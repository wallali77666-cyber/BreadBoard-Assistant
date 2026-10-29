// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Design/CircuitDraft.cs
// PURPOSE: The editable circuit behind the X-ray builder. It owns the parts
//          placed on real tie points and converts them into the same
//          DetectedComponent[] the vision loop produces, so the KiCad DRC engine,
//          the ratsnest and the tutor cannot tell a hand-built board from a
//          camera frame.
//
// MULTI-PIN: a DIP or 3-pin module stores one hole per pin in pin-number order.
//            Pins 1 and 2 stay in Lead1/Lead2 for the two-lead DRC fast path;
//            pins 3..N travel in ExtraLeads.
//
// THREADING: UI thread only. The coordinator receives an immutable snapshot.
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.Design;

/// <summary>One part in the draft, identified by the tie points its pins occupy.</summary>
public readonly record struct DraftPart(
    int TrackId,
    ComponentKind Kind,
    string[] Pins,
    string? Value,
    string? SymbolId = null)
{
    public string Pin1HoleId => Pins.Length > 0 ? Pins[0] : string.Empty;

    public string Pin2HoleId => Pins.Length > 1 ? Pins[1] : Pin1HoleId;

    public int PinCount => Pins.Length;

    public string KindLabel => Kind switch
    {
        ComponentKind.JumperWire => "JUMPER",
        ComponentKind.Resistor => "RESISTOR",
        ComponentKind.Led => "LED",
        ComponentKind.Capacitor => "CAPACITOR",
        ComponentKind.IntegratedCircuit => "IC",
        ComponentKind.PushButton => "BUTTON",
        _ => "PART"
    };

    public string Display => PinCount <= 2
        ? $"{KindLabel} #{TrackId}   {Pin1HoleId}  \u2192  {Pin2HoleId}"
        : $"{KindLabel} #{TrackId}   {PinCount}P   {Pins[0]}..{Pins[PinCount - 1]}";

    public string PinSummary => PinCount <= 2
        ? $"{Pin1HoleId}  \u2192  {Pin2HoleId}"
        : string.Join(' ', Pins);
}

/// <summary>
/// Mutable, validated working copy of the circuit on the board. Every mutation
/// bumps <see cref="Revision"/> and invalidates the cached detection array.
/// </summary>
public sealed class CircuitDraft
{
    private const float BoxPadding = 12f;

    private readonly PinMatrixLookup _matrix;
    private readonly List<DraftPart> _parts = new(32);
    private DetectedComponent[] _detections = Array.Empty<DetectedComponent>();
    private int _nextTrackId = 1;
    private bool _dirty;

    public CircuitDraft(PinMatrixLookup? matrix = null)
    {
        _matrix = matrix ?? PinMatrixLookup.Shared;
    }

    public PinMatrixLookup Matrix => _matrix;

    public IReadOnlyList<DraftPart> Parts => _parts;

    public int Count => _parts.Count;

    /// <summary>Monotonic version counter; the coordinator only re-reads on change.</summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Adds a part between two tie points. Both leads may share one hole only when
    /// the caller is placing a single-lead jumper; two identical holes are rejected
    /// because a part cannot short onto itself through one pin.
    /// </summary>
    public bool TryAdd(
        ComponentKind kind,
        string? pin1HoleId,
        string? pin2HoleId,
        out DraftPart part,
        out string? error)
    {
        return TryAdd(kind, pin1HoleId, pin2HoleId, value: null, out part, out error);
    }

    public bool TryAdd(
        ComponentKind kind,
        string? pin1HoleId,
        string? pin2HoleId,
        string? value,
        out DraftPart part,
        out string? error)
    {
        return TryAddFootprint(kind, new[] { pin1HoleId ?? string.Empty, pin2HoleId ?? string.Empty }, value, symbolId: null, out part, out error);
    }

    /// <summary>
    /// Adds a part with any number of pins (2 = lead, 4..40 = DIP/adapter,
    /// 3..20 = TO-92 or regulator row). <paramref name="pins"/> is in pin order.
    /// </summary>
    public bool TryAddFootprint(
        ComponentKind kind,
        string[]? pins,
        string? value,
        string? symbolId,
        out DraftPart part,
        out string? error)
    {
        part = default;

        if (kind == ComponentKind.Unknown)
        {
            error = "Choose the kind of part to place first.";
            return false;
        }

        if (pins is null || pins.Length < 2)
        {
            error = "A part needs at least two pins on tie points.";
            return false;
        }

        var resolved = new string[pins.Length];
        for (int i = 0; i < pins.Length; i++)
        {
            if (!_matrix.TryGetHole(pins[i], out HoleCoord hole))
            {
                error = $"Pin {i + 1} ('{pins[i]}') is not a tie point. Use A15, F20 or TOP_GND_12.";
                return false;
            }

            resolved[i] = hole.HoleId;
        }

        for (int i = 0; i < resolved.Length; i++)
        {
            for (int j = i + 1; j < resolved.Length; j++)
            {
                if (string.Equals(resolved[i], resolved[j], StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Pins {i + 1} and {j + 1} both sit in {resolved[i]}; a part cannot short onto itself.";
                    return false;
                }
            }
        }

        string? normalizedSymbol = string.IsNullOrWhiteSpace(symbolId) ? null : symbolId.Trim();

        part = new DraftPart(
            _nextTrackId++,
            kind,
            resolved,
            ResolveValue(kind, resolved.Length, value),
            normalizedSymbol);

        _parts.Add(part);
        _dirty = true;
        Revision++;
        error = null;
        return true;
    }

    /// <summary>Re-adds a vision detection (used by "load demo into builder").</summary>
    public bool TryAddDetection(in DetectedComponent detection, out string? error)
    {
        if (!_matrix.TrySnapToHole(detection.Lead1Px, detection.Lead1Py, out HoleCoord hole1)
            || !_matrix.TrySnapToHole(detection.Lead2Px, detection.Lead2Py, out HoleCoord hole2))
        {
            error = "A detected lead was not close enough to a tie point to copy.";
            return false;
        }

        var pins = new string[detection.PinCount];
        pins[0] = hole1.HoleId;
        pins[1] = hole2.HoleId;

        ReadOnlySpan<LeadPoint> extras = detection.ExtraLeads.Span;
        for (int i = 0; i < extras.Length; i++)
        {
            if (!_matrix.TrySnapToHole(extras[i].Px, extras[i].Py, out HoleCoord hole))
            {
                error = $"Pin {i + 3} of the detected part was not close enough to a tie point.";
                return false;
            }

            pins[i + 2] = hole.HoleId;
        }

        return TryAddFootprint(
            detection.Kind,
            pins,
            detection.DecodedValueLabel,
            detection.SymbolId,
            out _,
            out error);
    }

    /// <summary>
    /// Appends every detection whose leads land on valid tie points. Used by the
    /// image importer; parts that cannot be snapped are counted, never invented.
    /// When <paramref name="icPinCount"/> is 4 or more every IC detection becomes a
    /// DIP of that size, starting at its first lead instead of a bogus two-lead box.
    /// </summary>
    public int TryImportDetections(
        ReadOnlySpan<DetectedComponent> detections,
        bool replaceExisting,
        out int skipped,
        int icPinCount = 0,
        string? icSymbolId = null)
    {
        skipped = 0;

        if (replaceExisting && _parts.Count > 0)
        {
            _parts.Clear();
            _dirty = true;
            Revision++;
        }

        int added = 0;
        for (int i = 0; i < detections.Length; i++)
        {
            DetectedComponent detection = detections[i];

            if (detection.Kind == ComponentKind.IntegratedCircuit
                && icPinCount >= 4
                && detection.ExtraLeads.IsEmpty)
            {
                if (TryAddImportedIc(detection, icPinCount, icSymbolId))
                {
                    added++;
                    continue;
                }

                skipped++;
                continue;
            }

            if (TryAddDetection(detection, out _))
                added++;
            else
                skipped++;
        }

        return added;
    }

    private bool TryAddImportedIc(in DetectedComponent detection, int pinCount, string? symbolId)
    {
        if (!_matrix.TrySnapToHole(detection.Lead1Px, detection.Lead1Py, out HoleCoord anchor, maxRadius: 24f)
            && !_matrix.TrySnapToHole(detection.Lead2Px, detection.Lead2Py, out anchor, maxRadius: 24f))
            return false;

        if (!FootprintPlacer.TryPlaceDualRow(_matrix, anchor.HoleId, pinCount, out string[] pins, out _))
            return false;

        string? value = detection.DecodedValueLabel;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "DIP", StringComparison.OrdinalIgnoreCase))
            value = $"DIP-{pinCount}";

        return TryAddFootprint(ComponentKind.IntegratedCircuit, pins, value, symbolId, out _, out _);
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _parts.Count)
            return false;

        _parts.RemoveAt(index);
        _dirty = true;
        Revision++;
        return true;
    }

    public bool RemoveLast()
    {
        if (_parts.Count == 0)
            return false;

        _parts.RemoveAt(_parts.Count - 1);
        _dirty = true;
        Revision++;
        return true;
    }

    /// <summary>Removes the last part placed on the given tie point (mouse right-click undo).</summary>
    public bool RemoveLastTouching(string? holeId)
    {
        if (string.IsNullOrWhiteSpace(holeId))
            return false;

        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            string[] pins = _parts[i].Pins;
            for (int p = 0; p < pins.Length; p++)
            {
                if (!string.Equals(pins[p], holeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return RemoveAt(i);
            }
        }

        return false;
    }

    /// <summary>Changes the printed value of a placed part.</summary>
    public bool TrySetValue(int trackId, string? value)
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            DraftPart part = _parts[i];
            if (part.TrackId != trackId)
                continue;

            string resolved = ResolveValue(part.Kind, part.PinCount, value);
            if (string.Equals(part.Value, resolved, StringComparison.Ordinal))
                return true;

            _parts[i] = part with { Value = resolved };
            _dirty = true;
            Revision++;
            return true;
        }

        return false;
    }

    /// <summary>Attaches (or clears) the KiCad symbol used by the schematic view.</summary>
    public bool TrySetSymbol(int trackId, string? symbolId)
    {
        string? normalized = string.IsNullOrWhiteSpace(symbolId) ? null : symbolId.Trim();

        for (int i = 0; i < _parts.Count; i++)
        {
            DraftPart part = _parts[i];
            if (part.TrackId != trackId)
                continue;

            if (string.Equals(part.SymbolId, normalized, StringComparison.Ordinal))
                return true;

            _parts[i] = part with { SymbolId = normalized };
            _dirty = true;
            Revision++;
            return true;
        }

        return false;
    }

    /// <summary>Moves a part onto a new set of holes, keeping its value and symbol.</summary>
    public bool TryReplacePins(int trackId, string[]? pins, out string? error)
    {
        if (pins is null || pins.Length < 2)
        {
            error = "A part needs at least two pins.";
            return false;
        }

        for (int i = 0; i < _parts.Count; i++)
        {
            DraftPart part = _parts[i];
            if (part.TrackId != trackId)
                continue;

            if (!TryAddFootprint(part.Kind, pins, part.Value, part.SymbolId, out DraftPart replacement, out error))
                return false;

            // Keep the original track id so selection and the tutor stay stable.
            _parts[i] = replacement with { TrackId = part.TrackId, Value = part.Value };
            _dirty = true;
            Revision++;
            error = null;
            return true;
        }

        error = $"Part #{trackId} is no longer on the board.";
        return false;
    }

    public void Clear()
    {
        if (_parts.Count == 0)
            return;

        _parts.Clear();
        _dirty = true;
        Revision++;
    }

    /// <summary>Immutable-ish snapshot for the reflex loop. Do not mutate the result.</summary>
    public DetectedComponent[] BuildDetections()
    {
        if (!_dirty)
            return _detections;

        var detections = new DetectedComponent[_parts.Count];
        for (int i = 0; i < _parts.Count; i++)
            detections[i] = ToDetection(_parts[i]);

        _detections = detections;
        _dirty = false;
        return detections;
    }

    public string Describe()
    {
        if (_parts.Count == 0)
            return "EMPTY BOARD";

        int jumpers = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            if (_parts[i].Kind == ComponentKind.JumperWire)
                jumpers++;
        }

        int parts = _parts.Count - jumpers;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_parts.Count} PLACED  \u00b7  {jumpers} JUMPER{(jumpers == 1 ? string.Empty : "S")}  \u00b7  {parts} PART{(parts == 1 ? string.Empty : "S")}");
    }

    public static string DefaultValue(ComponentKind kind) => kind switch
    {
        ComponentKind.Resistor => "220R",
        ComponentKind.Led => "LED_RED_5MM",
        ComponentKind.Capacitor => "10uF",
        ComponentKind.IntegratedCircuit => "NE555P",
        ComponentKind.PushButton => "TACT_6MM",
        _ => "WIRE"
    };

    private static string ResolveValue(ComponentKind kind, int pinCount, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        if (kind == ComponentKind.IntegratedCircuit && pinCount > 2)
            return $"DIP-{pinCount}";

        return DefaultValue(kind);
    }

    private DetectedComponent ToDetection(in DraftPart part)
    {
        _matrix.TryGetHole(part.Pin1HoleId, out HoleCoord pin1);
        _matrix.TryGetHole(part.Pin2HoleId, out HoleCoord pin2);

        float minX = pin1.Px;
        float minY = pin1.Py;
        float maxX = pin1.Px;
        float maxY = pin1.Py;

        for (int i = 1; i < part.Pins.Length; i++)
        {
            _matrix.TryGetHole(part.Pins[i], out HoleCoord hole);
            minX = MathF.Min(minX, hole.Px);
            minY = MathF.Min(minY, hole.Py);
            maxX = MathF.Max(maxX, hole.Px);
            maxY = MathF.Max(maxY, hole.Py);
        }

        float width = MathF.Max(18f, maxX - minX + BoxPadding * 2f);
        float height = MathF.Max(18f, maxY - minY + BoxPadding * 2f);

        float left = Math.Clamp(minX - BoxPadding, 0f, BreadboardSpec.CanvasWidth - width);
        float top = Math.Clamp(minY - BoxPadding, 0f, BreadboardSpec.CanvasHeight - height);

        LeadPoint[]? extraLeads = null;
        if (part.Pins.Length > 2)
        {
            extraLeads = new LeadPoint[part.Pins.Length - 2];
            for (int i = 2; i < part.Pins.Length; i++)
            {
                _matrix.TryGetHole(part.Pins[i], out HoleCoord hole);
                extraLeads[i - 2] = new LeadPoint(hole.Px, hole.Py);
            }
        }

        return new DetectedComponent(
            TrackId: part.TrackId,
            Kind: part.Kind,
            Confidence: 1f,
            BoxX: left,
            BoxY: top,
            BoxW: width,
            BoxH: height,
            Lead1Px: pin1.Px,
            Lead1Py: pin1.Py,
            Lead2Px: pin2.Px,
            Lead2Py: pin2.Py,
            DecodedValueLabel: part.Value,
            SymbolId: part.SymbolId,
            ExtraLeads: extraLeads is null ? default : extraLeads);
    }
}
