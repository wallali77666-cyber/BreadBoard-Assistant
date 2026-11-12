using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.Geometry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BreadboardAOI.Core.DrcEngine;
using SkiaSharp;

namespace BreadboardAOI.App.Rendering;

/// <summary>
/// Represents a jumper wire routed between an off-board microcontroller pin socket
/// and a breadboard tie-point (or another microcontroller pin socket).
/// </summary>
public sealed record OffBoardJumperWire(
    string WireId,
    string NetName,
    string SourceEndpointKey,
    string TargetEndpointKey,
    string InsulationHexColor)
{
    /// <summary>Human-readable source label for the connected-wire list.</summary>
    public string SourceLabel => FormatEndpoint(SourceEndpointKey);

    /// <summary>Human-readable target label for the connected-wire list.</summary>
    public string TargetLabel => FormatEndpoint(TargetEndpointKey);

    private static string FormatEndpoint(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "--";

        string trimmed = key.Trim();
        if (trimmed.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(3);

        int colon = trimmed.LastIndexOf(':');
        return colon >= 0 && colon < trimmed.Length - 1
            ? trimmed.Substring(colon + 1)
            : trimmed;
    }
}

/// <summary>
/// Result of a single CAD viewport render pass, containing exact pin and tie-point coordinates
/// for interactive mouse hover, wire snapping, and DRC overlays.
/// </summary>
public sealed record CadViewportRenderSnapshot(
    SKRect BreadboardBoundsCanvas,
    IReadOnlyList<RenderedMicrocontrollerLayout> MicrocontrollerLayouts,
    IReadOnlyDictionary<string, SKPoint> AllSnapPointsCanvas,
    IReadOnlyList<McuDrcViolation> ActiveViolations);

/// <summary>
/// Hit-test result for an off-board microcontroller pin socket in viewport canvas coordinates.
/// </summary>
public sealed record DevBoardPinHit(
    string BoardInstanceId,
    MicrocontrollerBoardProfile BoardProfile,
    McuPinDefinition Pin,
    SKPoint CanvasCenter);

public sealed partial class SkiaArOverlayRenderer
{
    private readonly DevBoardAndModuleRuleEngine? _mcuRuleEngine;
    private readonly OffBoardMicrocontrollerRenderer? _offBoardMcuRenderer;
    private OffBoardMicrocontrollerRenderer? _lazyMcuRenderer;
    private DevBoardAndModuleRuleEngine? _lazyMcuRuleEngine;
    private List<RenderedMicrocontrollerLayout> _activeMcuLayouts = new();

    public const float BreadboardWidthMm = 165.1f;
    public const float BreadboardHeightMm = 54.6f;
    public const float TiePointPitchMm = 2.54f;

    /// <summary>Active off-board microcontroller instances docked outside the breadboard.</summary>
    public IReadOnlyList<OffBoardMicrocontrollerVisualState> ActiveMicrocontrollers { get; set; } = Array.Empty<OffBoardMicrocontrollerVisualState>();

    /// <summary>Active jumper wires routed between off-board microcontrollers and breadboard tie-points.</summary>
    public IReadOnlyList<OffBoardJumperWire> ActiveDevBoardWires { get; set; } = Array.Empty<OffBoardJumperWire>();

    /// <summary>Active DRC violations detected on off-board nets.</summary>
    public IReadOnlyList<McuDrcViolation> ActiveDevBoardViolations { get; set; } = Array.Empty<McuDrcViolation>();

    /// <summary>Latest computed 2D geometry layouts for all docked microcontrollers.</summary>
    public IReadOnlyList<RenderedMicrocontrollerLayout> ActiveMcuLayouts => _activeMcuLayouts;

    /// <summary>Currently hovered off-board microcontroller pin socket, if any.</summary>
    public DevBoardPinHit? HoveredDevBoardPin { get; private set; }

    /// <summary>Selected external USB cable type attached to the hardware programmer.</summary>
    public UsbCableKind ActiveUsbCableKind { get; set; } = UsbCableKind.UsbB;

    /// <summary>True when the USB programmer cable is actively linked to the docked dev board.</summary>
    public bool IsProgrammerConnected { get; set; }

    /// <summary>True while a firmware compile/flash sequence is animated along the USB cable.</summary>
    public bool IsProgrammerUploading { get; set; }

    /// <summary>Normalized 0..1 firmware flash progress rendered inside the CAD programmer box.</summary>
    public float ProgrammerUploadProgress { get; set; }

    /// <summary>Live virtual firmware runtime telemetry (LED/LCD/Buzzer/TX-RX states).</summary>
    public VirtualFirmwareRuntimeState? ActiveFirmwareState { get; set; }

    /// <summary>Active thermal hotspots rendered as IR overlays in board coordinates.</summary>
    public IReadOnlyList<ThermalHotspot> ActiveThermalHotspots { get; set; } = Array.Empty<ThermalHotspot>();

    /// <summary>Screen-space bounding box of the CAD [UPLOAD FIRMWARE] button for viewport click hit-testing.</summary>
    public SKRect LastProgrammerUploadButtonRect { get; private set; } = SKRect.Empty;

    /// <summary>Screen-space bounding box of the external hardware programmer chassis.</summary>
    public SKRect LastProgrammerBodyRect { get; private set; } = SKRect.Empty;

    /// <summary>Returns true when viewport click coordinates land on the CAD Upload Firmware button.</summary>
    public bool TryHitTestProgrammerUploadButton(float canvasX, float canvasY)
        => !LastProgrammerUploadButtonRect.IsEmpty && LastProgrammerUploadButtonRect.Contains(canvasX, canvasY);

    internal void ClearProgrammerHitRects()
    {
        LastProgrammerUploadButtonRect = SKRect.Empty;
        LastProgrammerBodyRect = SKRect.Empty;
    }

    public bool TryHitTestDevBoardPin(float canvasX, float canvasY, out DevBoardPinHit? hit)
    {
        hit = null;
        if (ActiveMicrocontrollers.Count == 0 || !_hasTransform)
            return false;

        float pixelsPerMm = _boardPixelWidth / BreadboardWidthMm;
        var boardRect = new SKRect(_boardLeft, _boardTop, _boardLeft + _boardPixelWidth, _boardTop + _boardPixelHeight);

        // Always recompute if layout cache is out of date or bounds shifted
        if (_activeMcuLayouts.Count != ActiveMicrocontrollers.Count ||
            _activeMcuLayouts.Count == 0 ||
            MathF.Abs(_activeMcuLayouts[0].BoardBoundsCanvas.Left - (McuRenderer.ComputeBoardLayout(ActiveMicrocontrollers[0], boardRect, pixelsPerMm).BoardBoundsCanvas.Left)) > 0.5f)
        {
            _activeMcuLayouts = ActiveMicrocontrollers
                .Select(mcu => McuRenderer.ComputeBoardLayout(mcu, boardRect, pixelsPerMm))
                .ToList();
        }

        float bestDistSq = float.MaxValue;
        string? bestBoardId = null;
        string? bestPinId = null;
        MicrocontrollerBoardProfile? bestProfile = null;
        McuPinDefinition? bestPinDef = null;
        SKPoint bestCenter = default;

        foreach (RenderedMicrocontrollerLayout layout in _activeMcuLayouts)
        {
            MicrocontrollerBoardProfile profile = McuRuleEngine.GetBoardProfile(layout.BoardType);
            foreach (var (pinId, hitBox) in layout.PinHitBoxesCanvas)
            {
                SKPoint center = layout.PinSocketCanvasCenters.TryGetValue(pinId, out SKPoint pt)
                    ? pt
                    : new SKPoint(hitBox.MidX, hitBox.MidY);

                float dx = canvasX - center.X;
                float dy = canvasY - center.Y;
                float distSq = (dx * dx) + (dy * dy);

                float pinPitchPx = 2.54f * pixelsPerMm;
                float maxRadius = MathF.Max(4.5f, MathF.Min(pinPitchPx * 0.65f, 16.0f));
                if (distSq <= (maxRadius * maxRadius) && distSq < bestDistSq)
                {
                    McuPinDefinition? pinDef = profile.FindPin(pinId);
                    if (pinDef is not null)
                    {
                        bestDistSq = distSq;
                        bestBoardId = layout.BoardInstanceId;
                        bestPinId = pinId;
                        bestProfile = profile;
                        bestPinDef = pinDef;
                        bestCenter = center;
                    }
                }
            }
        }

        if (bestPinDef is not null && bestProfile is not null && bestBoardId is not null && bestPinId is not null)
        {
            hit = new DevBoardPinHit(
                bestBoardId,
                bestProfile,
                bestPinDef,
                bestCenter);
            HoveredDevBoardPin = hit;
            UpdateHoveredMcuPin(bestBoardId, bestPinId);
            return true;
        }

        return false;
    }

    public void ClearDevBoardHover()
    {
        if (HoveredDevBoardPin is not null)
        {
            HoveredDevBoardPin = null;
            UpdateHoveredMcuPin(null, null);
        }
    }

    public void UpdateHoveredMcuPin(string? boardInstanceId, string? pinId)
    {
        if (ActiveMicrocontrollers.Count == 0)
            return;

        bool changed = false;
        var updated = new List<OffBoardMicrocontrollerVisualState>(ActiveMicrocontrollers.Count);
        foreach (var mcu in ActiveMicrocontrollers)
        {
            string? targetPin = (boardInstanceId == null || string.Equals(mcu.BoardInstanceId, boardInstanceId, StringComparison.OrdinalIgnoreCase))
                ? pinId
                : null;
            if (!string.Equals(mcu.HoveredPinCanonicalId, targetPin, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                updated.Add(mcu with { HoveredPinCanonicalId = targetPin });
            }
            else
            {
                updated.Add(mcu);
            }
        }

        if (changed)
            ActiveMicrocontrollers = updated;
    }

    public void DrawDevBoardJumperWires(
        SKCanvas canvas,
        IReadOnlyList<RenderedMicrocontrollerLayout> mcuLayouts,
        SKRect breadboardRect,
        float pxPerMm)
    {
        if (ActiveDevBoardWires.Count == 0)
            return;

        var violatingNets = new HashSet<string>(
            ActiveDevBoardViolations.Select(v => v.NetName),
            StringComparer.OrdinalIgnoreCase);

        var violatingPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in ActiveDevBoardViolations)
        {
            foreach (var op in v.OffendingPins)
            {
                violatingPins.Add($"{op.BoardInstanceId}:{op.PinId}");
                violatingPins.Add(op.PinId);
            }
        }

        var snapPoints = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in mcuLayouts)
        {
            string inst = layout.BoardInstanceId;
            foreach (var (pinId, pt) in layout.PinSocketCanvasCenters)
            {
                snapPoints[$"{inst}:{pinId}"] = pt;
                snapPoints[pinId] = pt;
            }
        }

        foreach (var wire in ActiveDevBoardWires)
        {
            if (!TryResolveEndpointCanvasPoint(wire.SourceEndpointKey, snapPoints, breadboardRect, out SKPoint start))
                continue;
            if (!TryResolveEndpointCanvasPoint(wire.TargetEndpointKey, snapPoints, breadboardRect, out SKPoint end))
                continue;

            bool hasViolation = violatingNets.Contains(wire.NetName) ||
                violatingPins.Contains(wire.SourceEndpointKey) ||
                violatingPins.Contains(wire.TargetEndpointKey) ||
                (wire.SourceEndpointKey.Contains(':') && violatingPins.Contains(wire.SourceEndpointKey.Substring(wire.SourceEndpointKey.LastIndexOf(':') + 1))) ||
                (wire.TargetEndpointKey.Contains(':') && violatingPins.Contains(wire.TargetEndpointKey.Substring(wire.TargetEndpointKey.LastIndexOf(':') + 1)));

            PinLabelAlignment? startAlign = null;
            PinLabelAlignment? endAlign = null;
            SKRect? mcuBounds = null;

            if (TryResolveMcuPinContext(wire.SourceEndpointKey, mcuLayouts, out var sLayout, out var sPin))
            {
                startAlign = sPin?.LabelAlignment;
                mcuBounds = sLayout?.BoardBoundsCanvas;
            }

            if (TryResolveMcuPinContext(wire.TargetEndpointKey, mcuLayouts, out var tLayout, out var tPin))
            {
                endAlign = tPin?.LabelAlignment;
                mcuBounds ??= tLayout?.BoardBoundsCanvas;
            }

            Draw3DJumperWire(
                canvas,
                start,
                end,
                wire.InsulationHexColor,
                hasViolation,
                pxPerMm,
                startAlign,
                endAlign,
                mcuBounds,
                breadboardRect);
        }
    }

    /// <summary>
    /// Draws the colored plug inside each MCU socket after the PCB has been
    /// painted. The wire itself runs under the board; this plug makes the
    /// electrical connection visible at the pin instead of at the board edge.
    /// </summary>
    public void DrawDevBoardWirePlugs(
        SKCanvas canvas,
        IReadOnlyList<RenderedMicrocontrollerLayout> mcuLayouts,
        IReadOnlyList<OffBoardJumperWire> wires,
        float pxPerMm)
    {
        if (wires.Count == 0 || mcuLayouts.Count == 0)
            return;

        var pinPoints = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var layout in mcuLayouts)
        {
            string inst = layout.BoardInstanceId;
            foreach (var (pinId, pt) in layout.PinSocketCanvasCenters)
            {
                pinPoints[$"{inst}:{pinId}"] = pt;
                pinPoints[pinId] = pt;
            }
        }

        foreach (var wire in wires)
        {
            SKColor color = SKColor.TryParse(wire.InsulationHexColor, out SKColor parsed)
                ? parsed
                : SKColor.Parse("#FF9800");
            DrawMcuPlug(canvas, wire.SourceEndpointKey, pinPoints, color, pxPerMm);
            DrawMcuPlug(canvas, wire.TargetEndpointKey, pinPoints, color, pxPerMm);
        }
    }

    private static void DrawMcuPlug(
        SKCanvas canvas,
        string key,
        Dictionary<string, SKPoint> pinPoints,
        SKColor color,
        float pxPerMm)
    {
        if (!TryResolveMcuPin(key, pinPoints, out SKPoint point))
            return;

        Draw3DMetaFerrule(canvas, point, color, pxPerMm, radiusMm: 0.65f);
    }

    private static bool TryResolveMcuPin(
        string key,
        Dictionary<string, SKPoint> pinPoints,
        out SKPoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string normalized = key.Trim();
        if (pinPoints.TryGetValue(normalized, out point))
            return true;

        int colon = normalized.LastIndexOf(':');
        return colon >= 0
            && colon < normalized.Length - 1
            && pinPoints.TryGetValue(normalized.Substring(colon + 1), out point);
    }

    /// <summary>
    /// Resolves an endpoint key to a canvas point. MCU pins are looked up by
    /// their full "instance:pin" key, the legacy "MCU:pin" key, or a bare pin
    /// id; breadboard holes use the actual board rect so the same resolver works
    /// in both the viewport and the standalone scene renderer.
    /// </summary>
    public bool TryResolveEndpointCanvasPoint(
        string key,
        Dictionary<string, SKPoint> pinCoords,
        SKRect breadboardRect,
        out SKPoint canvasPoint)
    {
        canvasPoint = default;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string normalized = key.Trim();

        // 1. Direct MCU pin coordinates (e.g. "MCU_1:D13" or "D13").
        if (pinCoords.TryGetValue(normalized, out canvasPoint))
            return true;

        int colon = normalized.LastIndexOf(':');
        if (colon >= 0 && colon < normalized.Length - 1)
        {
            string tail = normalized.Substring(colon + 1);
            if (pinCoords.TryGetValue(tail, out canvasPoint))
                return true;
        }

        // 2. Breadboard holes (e.g. "BB:E15", "BB:TOP_VCC_1", "E15", "TOP_VCC_1").
        string holeId = normalized;
        if (holeId.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            holeId = holeId.Substring(3);

        if (PinMatrixLookup.Shared.TryGetHole(holeId, out HoleCoord hole))
        {
            canvasPoint = new SKPoint(
                breadboardRect.Left + ((hole.Px / BoardWidth) * breadboardRect.Width),
                breadboardRect.Top + ((hole.Py / BoardHeight) * breadboardRect.Height));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves an endpoint key to its microcontroller layout and pin definition if the key represents an MCU socket.
    /// </summary>
    public bool TryResolveMcuPinContext(
        string key,
        IReadOnlyList<RenderedMicrocontrollerLayout> mcuLayouts,
        out RenderedMicrocontrollerLayout? matchedLayout,
        out McuPinDefinition? matchedPin)
    {
        matchedLayout = null;
        matchedPin = null;
        if (string.IsNullOrWhiteSpace(key) || mcuLayouts.Count == 0)
            return false;

        string normalized = key.Trim();
        if (normalized.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            return false;

        string boardId = string.Empty;
        string pinId = normalized;
        int colon = normalized.LastIndexOf(':');
        if (colon >= 0)
        {
            boardId = normalized.Substring(0, colon);
            pinId = normalized.Substring(colon + 1);
        }

        foreach (RenderedMicrocontrollerLayout layout in mcuLayouts)
        {
            if (!string.IsNullOrEmpty(boardId) &&
                !string.Equals(layout.BoardInstanceId, boardId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals("MCU", boardId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals("MCU_1", boardId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MicrocontrollerBoardProfile profile = McuRuleEngine.GetBoardProfile(layout.BoardType);
            McuPinDefinition? pinDef = profile.FindPin(pinId);
            if (pinDef is not null)
            {
                matchedLayout = layout;
                matchedPin = pinDef;
                return true;
            }
        }

        return false;
    }

    public DevBoardAndModuleRuleEngine McuRuleEngine => _mcuRuleEngine ?? (_lazyMcuRuleEngine ??= new DevBoardAndModuleRuleEngine());
    public OffBoardMicrocontrollerRenderer McuRenderer => _offBoardMcuRenderer ?? (_lazyMcuRenderer ??= new OffBoardMicrocontrollerRenderer(McuRuleEngine));

    public SkiaArOverlayRenderer(
        DevBoardAndModuleRuleEngine ruleEngine,
        OffBoardMicrocontrollerRenderer mcuRenderer) : this()
    {
        _mcuRuleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _offBoardMcuRenderer = mcuRenderer ?? throw new ArgumentNullException(nameof(mcuRenderer));
    }

    /// <summary>
    /// Renders the complete CAD scene: 830-tie-point breadboard, docked off-board microcontrollers,
    /// routed jumper wires, and DRC violation callouts.
    /// </summary>
    public CadViewportRenderSnapshot RenderCadScene(
        SKCanvas canvas,
        SKPoint breadboardTopLeftCanvas,
        float pixelsPerMm,
        IReadOnlyList<OffBoardMicrocontrollerVisualState> microcontrollers,
        IReadOnlyList<OffBoardJumperWire> jumperWires,
        IReadOnlyList<OffBoardElectricalNet> electricalNets)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(microcontrollers);
        ArgumentNullException.ThrowIfNull(jumperWires);
        ArgumentNullException.ThrowIfNull(electricalNets);

        DevBoardAndModuleRuleEngine engine = _mcuRuleEngine ?? new DevBoardAndModuleRuleEngine();
        OffBoardMicrocontrollerRenderer renderer = _offBoardMcuRenderer ?? new OffBoardMicrocontrollerRenderer(engine);

        // 1. Evaluate DRC violations across all nets
        IReadOnlyList<McuDrcViolation> violations = engine.EvaluateCircuit(electricalNets);
        var violatingNetNames = violations
            .Select(v => v.NetName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var violatingPinsInScene = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in violations)
        {
            foreach (var op in v.OffendingPins)
            {
                violatingPinsInScene.Add($"{op.BoardInstanceId}:{op.PinId}");
                violatingPinsInScene.Add(op.PinId);
            }
        }

        // 2. Render 830-tie-point breadboard and collect tie-point coordinates
        var breadboardRect = new SKRect(
            breadboardTopLeftCanvas.X,
            breadboardTopLeftCanvas.Y,
            breadboardTopLeftCanvas.X + (BreadboardWidthMm * pixelsPerMm),
            breadboardTopLeftCanvas.Y + (BreadboardHeightMm * pixelsPerMm));

        var snapPoints = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
        Draw830TiePointBreadboard(canvas, breadboardRect, pixelsPerMm, snapPoints);

        // 3. Compute every MCU layout first, then paint jumper wires under the
        //    PCBs so sockets and labels stay legible.
        var mcuLayouts = new List<RenderedMicrocontrollerLayout>(microcontrollers.Count);
        foreach (OffBoardMicrocontrollerVisualState mcuState in microcontrollers)
        {
            RenderedMicrocontrollerLayout layout = renderer.ComputeBoardLayout(
                mcuState,
                breadboardRect,
                pixelsPerMm);

            mcuLayouts.Add(layout);

            foreach ((string pinId, SKPoint socketPt) in layout.PinSocketCanvasCenters)
            {
                snapPoints[$"{mcuState.BoardInstanceId}:{pinId}"] = socketPt;
                snapPoints[pinId] = socketPt;
            }
        }

        // 4. Paint the microcontroller boards first so headers and substrate form the baseline
        foreach (OffBoardMicrocontrollerVisualState mcuState in microcontrollers)
        {
            renderer.RenderMicrocontroller(canvas, mcuState, breadboardRect, pixelsPerMm);
        }

        // 5. Render realistic 2.5D/3D Bezier jumper wires ON TOP connecting MCU pin sockets to breadboard holes
        foreach (OffBoardJumperWire wire in jumperWires)
        {
            if (TryResolveEndpointCanvasPoint(wire.SourceEndpointKey, snapPoints, breadboardRect, out SKPoint p0) &&
                TryResolveEndpointCanvasPoint(wire.TargetEndpointKey, snapPoints, breadboardRect, out SKPoint p1))
            {
                bool hasViolation = violatingNetNames.Contains(wire.NetName) ||
                    violatingPinsInScene.Contains(wire.SourceEndpointKey) ||
                    violatingPinsInScene.Contains(wire.TargetEndpointKey) ||
                    (wire.SourceEndpointKey.Contains(':') && violatingPinsInScene.Contains(wire.SourceEndpointKey.Substring(wire.SourceEndpointKey.LastIndexOf(':') + 1))) ||
                    (wire.TargetEndpointKey.Contains(':') && violatingPinsInScene.Contains(wire.TargetEndpointKey.Substring(wire.TargetEndpointKey.LastIndexOf(':') + 1)));

                PinLabelAlignment? startAlign = null;
                PinLabelAlignment? endAlign = null;
                SKRect? mcuBounds = null;

                if (TryResolveMcuPinContext(wire.SourceEndpointKey, mcuLayouts, out var sLayout, out var sPin))
                {
                    startAlign = sPin?.LabelAlignment;
                    mcuBounds = sLayout?.BoardBoundsCanvas;
                }

                if (TryResolveMcuPinContext(wire.TargetEndpointKey, mcuLayouts, out var tLayout, out var tPin))
                {
                    endAlign = tPin?.LabelAlignment;
                    mcuBounds ??= tLayout?.BoardBoundsCanvas;
                }

                Draw3DJumperWire(
                    canvas,
                    p0,
                    p1,
                    wire.InsulationHexColor,
                    hasViolation,
                    pixelsPerMm,
                    startAlign,
                    endAlign,
                    mcuBounds,
                    breadboardRect);
            }
        }

        if (jumperWires.Count > 0)
            DrawDevBoardWirePlugs(canvas, mcuLayouts, jumperWires, pixelsPerMm);

        // 6. Render DRC violation callouts on top of the viewport
        if (violations.Count > 0)
        {
            DrawDrcViolationHud(canvas, breadboardRect, violations, pixelsPerMm);
        }

        return new CadViewportRenderSnapshot(
            BreadboardBoundsCanvas: breadboardRect,
            MicrocontrollerLayouts: mcuLayouts,
            AllSnapPointsCanvas: snapPoints,
            ActiveViolations: violations);
    }

    /// <summary>
    /// Hit-tests a canvas point against all off-board microcontroller pin sockets and breadboard tie points.
    /// Returns the endpoint key (e.g., "Uno1:D13" or "BB:E15") if within snap radius.
    /// </summary>
    public string? HitTestEndpoint(
        CadViewportRenderSnapshot snapshot,
        SKPoint cursorCanvasPoint,
        float snapRadiusPx)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? closestKey = null;
        float bestDistSq = snapRadiusPx * snapRadiusPx;

        foreach ((string key, SKPoint pt) in snapshot.AllSnapPointsCanvas)
        {
            float dx = cursorCanvasPoint.X - pt.X;
            float dy = cursorCanvasPoint.Y - pt.Y;
            float distSq = (dx * dx) + (dy * dy);
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                closestKey = key;
            }
        }

        return closestKey;
    }

    private static void Draw830TiePointBreadboard(
        SKCanvas canvas,
        SKRect rect,
        float pxPerMm,
        Dictionary<string, SKPoint> snapPoints)
    {
        using var bodyPaint = new SKPaint
        {
            Color = SKColor.Parse("#F5F5F0"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Color = SKColor.Parse("#CFD8DC"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 0.3f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 1.5f * pxPerMm, 1.5f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 1.5f * pxPerMm, 1.5f * pxPerMm, borderPaint);

        var ravineRect = new SKRect(rect.Left + (2.0f * pxPerMm), rect.MidY - (1.8f * pxPerMm), rect.Right - (2.0f * pxPerMm), rect.MidY + (1.8f * pxPerMm));
        using var ravinePaint = new SKPaint { Color = SKColor.Parse("#E0E0D8"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(ravineRect, ravinePaint);

        using var redLinePaint = new SKPaint { Color = SKColor.Parse("#D32F2F"), StrokeWidth = 0.35f * pxPerMm, IsAntialias = true };
        using var blueLinePaint = new SKPaint { Color = SKColor.Parse("#1976D2"), StrokeWidth = 0.35f * pxPerMm, IsAntialias = true };
        canvas.DrawLine(rect.Left + (4f * pxPerMm), rect.Top + (1.5f * pxPerMm), rect.Right - (4f * pxPerMm), rect.Top + (1.5f * pxPerMm), redLinePaint);
        canvas.DrawLine(rect.Left + (4f * pxPerMm), rect.Top + (7.5f * pxPerMm), rect.Right - (4f * pxPerMm), rect.Top + (7.5f * pxPerMm), blueLinePaint);
        canvas.DrawLine(rect.Left + (4f * pxPerMm), rect.Bottom - (7.5f * pxPerMm), rect.Right - (4f * pxPerMm), rect.Bottom - (7.5f * pxPerMm), redLinePaint);
        canvas.DrawLine(rect.Left + (4f * pxPerMm), rect.Bottom - (1.5f * pxPerMm), rect.Right - (4f * pxPerMm), rect.Bottom - (1.5f * pxPerMm), blueLinePaint);

        char[] rowLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J'];
        float[] rowYOffsetsMm = [11.4f, 13.94f, 16.48f, 19.02f, 21.56f, 33.04f, 35.58f, 38.12f, 40.66f, 43.20f];

        using var holePaint = new SKPaint { Color = SKColor.Parse("#2B2B2B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float holeHalf = 0.48f * pxPerMm;

        for (int col = 1; col <= 63; col++)
        {
            float x = rect.Left + ((2.54f + ((col - 1) * TiePointPitchMm)) * pxPerMm);
            for (int r = 0; r < rowLetters.Length; r++)
            {
                float y = rect.Top + (rowYOffsetsMm[r] * pxPerMm);
                var pt = new SKPoint(x, y);
                snapPoints[$"BB:{rowLetters[r]}{col}"] = pt;
                snapPoints[$"{rowLetters[r]}{col}"] = pt;
                canvas.DrawRect(x - holeHalf, y - holeHalf, holeHalf * 2f, holeHalf * 2f, holePaint);
            }
        }

        // Power rail tie points (50 holes per rail)
        float topVccY = rect.Top + (2.75f * pxPerMm);
        float topGndY = rect.Top + (5.0f * pxPerMm);
        float botVccY = rect.Bottom - (5.0f * pxPerMm);
        float botGndY = rect.Bottom - (2.75f * pxPerMm);

        for (int i = 1; i <= 50; i++)
        {
            // Groups of 5 holes with gap
            int group = (i - 1) / 5;
            int idxInGroup = (i - 1) % 5;
            float rx = rect.Left + ((5.0f + (group * 16.0f) + (idxInGroup * TiePointPitchMm)) * pxPerMm);

            var ptTopVcc = new SKPoint(rx, topVccY);
            var ptTopGnd = new SKPoint(rx, topGndY);
            var ptBotVcc = new SKPoint(rx, botVccY);
            var ptBotGnd = new SKPoint(rx, botGndY);

            snapPoints[$"BB:TOP_VCC_{i}"] = ptTopVcc;
            snapPoints[$"TOP_VCC_{i}"] = ptTopVcc;
            snapPoints[$"BB:TOP_GND_{i}"] = ptTopGnd;
            snapPoints[$"TOP_GND_{i}"] = ptTopGnd;
            snapPoints[$"BB:BOT_VCC_{i}"] = ptBotVcc;
            snapPoints[$"BOT_VCC_{i}"] = ptBotVcc;
            snapPoints[$"BB:BOT_GND_{i}"] = ptBotGnd;
            snapPoints[$"BOT_GND_{i}"] = ptBotGnd;

            canvas.DrawCircle(rx, topVccY, holeHalf * 0.9f, holePaint);
            canvas.DrawCircle(rx, topGndY, holeHalf * 0.9f, holePaint);
            canvas.DrawCircle(rx, botVccY, holeHalf * 0.9f, holePaint);
            canvas.DrawCircle(rx, botGndY, holeHalf * 0.9f, holePaint);
        }
    }

    /// <summary>
    /// Renders realistic 2.5D/3D curved jumper wire with vertical socket departure tangents,
    /// distance-proportional 3D catenary arch, anchored physical drop shadow, 3D DuPont connectors
    /// at tie points, 3D cylindrical insulation with specular highlights, and 60 FPS animated current pulses.
    /// </summary>
    public static void Draw3DJumperWire(
        SKCanvas canvas,
        SKPoint start,
        SKPoint end,
        string hexColor,
        bool hasViolation,
        float pxPerMm,
        PinLabelAlignment? startAlignment = null,
        PinLabelAlignment? endAlignment = null,
        SKRect? mcuBoardBounds = null,
        SKRect? breadboardRect = null)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy));
        if (dist < 0.01f)
            return;

        // Deterministic pseudo-random variation based on endpoints so parallel wires drape naturally without colliding
        int x1 = (int)MathF.Round(start.X * 10f);
        int y1 = (int)MathF.Round(start.Y * 10f);
        int x2 = (int)MathF.Round(end.X * 10f);
        int y2 = (int)MathF.Round(end.Y * 10f);
        if (x1 > x2 || (x1 == x2 && y1 > y2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }
        int wireHash = HashCode.Combine(x1, y1, x2, y2);
        float variance = (((wireHash & 0x7F) / 127.0f) - 0.5f) * 0.35f; // +/- 17.5% natural slack variation

        SKPoint c1 = default, c2 = default, c3 = default, c4 = default, midFlank = default;
        bool isFlankRoute = false;
        float normalX = 0f;
        float normalY = -1f;

        // ---------------------------------------------------------------------
        // 1. Check if flank clearance routing is needed around an off-board MCU board:
        //    Case A: Target is on MCU bottom header (BelowSocket), start is above MCU (e.g. on breadboard)
        //    Case B: Source is on MCU bottom header (BelowSocket), target is above MCU (e.g. on breadboard)
        //    In both cases, straight Beziers cut across the top header sockets and IC packages.
        //    We route gracefully around the left or right flank of the MCU PCB.
        // ---------------------------------------------------------------------
        if (mcuBoardBounds.HasValue)
        {
            SKRect board = mcuBoardBounds.Value;

            bool endIsBelowHeader = endAlignment == PinLabelAlignment.BelowSocket ||
                (end.Y > board.MidY && end.Y <= board.Bottom + (6.0f * pxPerMm) && end.X >= board.Left - (6.0f * pxPerMm) && end.X <= board.Right + (6.0f * pxPerMm));

            bool startIsBelowHeader = startAlignment == PinLabelAlignment.BelowSocket ||
                (start.Y > board.MidY && start.Y <= board.Bottom + (6.0f * pxPerMm) && start.X >= board.Left - (6.0f * pxPerMm) && start.X <= board.Right + (6.0f * pxPerMm));

            if (endIsBelowHeader && start.Y < board.Top + (2.0f * pxPerMm))
            {
                isFlankRoute = true;
                bool useLeftFlank = end.X < board.MidX;
                float flankMargin = Math.Clamp(14.0f * pxPerMm + (MathF.Abs(variance) * 4.0f * pxPerMm), 9.0f * pxPerMm, 26.0f * pxPerMm);
                float flankX = useLeftFlank
                    ? Math.Min(board.Left - flankMargin, Math.Min(start.X, end.X) - (4.0f * pxPerMm))
                    : Math.Max(board.Right + flankMargin, Math.Max(start.X, end.X) + (4.0f * pxPerMm));

                float bottomClearance = Math.Clamp(7.5f * pxPerMm + (MathF.Abs(variance) * 3.0f * pxPerMm), 5.0f * pxPerMm, 16.0f * pxPerMm);
                float bottomY = board.Bottom + bottomClearance;
                float approachLead = Math.Clamp(9.0f * pxPerMm, 6.0f * pxPerMm, 18.0f * pxPerMm);

                midFlank = new SKPoint(flankX, board.MidY + (variance * 4.0f * pxPerMm));

                // Segment 1: start curving downwards and outwards past the flank
                float dropLead = MathF.Max(14.0f * pxPerMm, MathF.Abs(midFlank.Y - start.Y) * 0.45f);
                c1 = new SKPoint(start.X, start.Y + dropLead);
                c2 = new SKPoint(midFlank.X, midFlank.Y - (board.Height * 0.28f));

                // Segment 2: from flank, underneath bottom edge, entering socket vertically from below
                c3 = new SKPoint(midFlank.X, bottomY);
                c4 = new SKPoint(end.X, end.Y + approachLead); // c4 is below end -> tangent points strictly UP into socket!

                normalX = useLeftFlank ? -0.85f : 0.85f;
                normalY = -0.52f;
            }
            else if (startIsBelowHeader && end.Y < board.Top + (2.0f * pxPerMm))
            {
                isFlankRoute = true;
                bool useLeftFlank = start.X < board.MidX;
                float flankMargin = Math.Clamp(14.0f * pxPerMm + (MathF.Abs(variance) * 4.0f * pxPerMm), 9.0f * pxPerMm, 26.0f * pxPerMm);
                float flankX = useLeftFlank
                    ? Math.Min(board.Left - flankMargin, Math.Min(start.X, end.X) - (4.0f * pxPerMm))
                    : Math.Max(board.Right + flankMargin, Math.Max(start.X, end.X) + (4.0f * pxPerMm));

                float bottomClearance = Math.Clamp(7.5f * pxPerMm + (MathF.Abs(variance) * 3.0f * pxPerMm), 5.0f * pxPerMm, 16.0f * pxPerMm);
                float bottomY = board.Bottom + bottomClearance;
                float approachLead = Math.Clamp(9.0f * pxPerMm, 6.0f * pxPerMm, 18.0f * pxPerMm);

                midFlank = new SKPoint(flankX, board.MidY + (variance * 4.0f * pxPerMm));

                // Segment 1: leaves start heading downwards into loop under board
                c1 = new SKPoint(start.X, start.Y + approachLead);
                c2 = new SKPoint(midFlank.X, bottomY);

                // Segment 2: leaves flank heading upwards to breadboard end
                float riseLead = MathF.Max(14.0f * pxPerMm, MathF.Abs(end.Y - midFlank.Y) * 0.45f);
                c3 = new SKPoint(midFlank.X, midFlank.Y - (board.Height * 0.28f));
                c4 = new SKPoint(end.X, end.Y + riseLead);

                normalX = useLeftFlank ? -0.85f : 0.85f;
                normalY = -0.52f;
            }
        }

        if (!isFlankRoute)
        {
            // Predominantly vertical routing across margins/ravine (e.g. Breadboard to docked MCU top header or cross-dock jumpers)
            bool isVerticalSpan = MathF.Abs(dy) > (MathF.Abs(dx) * 0.45f) && dist > (10.0f * pxPerMm);

            if (isVerticalSpan)
            {
                float direction = MathF.Sign(dy); // +1 if start is top and end is bottom; -1 if start is bottom (MCU) and end is top (breadboard)
                float verticalLead = Math.Clamp(MathF.Abs(dy) * (0.34f + (variance * 0.08f)), 14.0f * pxPerMm, 65.0f * pxPerMm);

                // Natural lateral bow so parallel wires form distinct graceful 3D cable runs
                float lateralSlack = (((wireHash & 0x3F) - 31) / 31.0f) * MathF.Min(22.0f * pxPerMm, dist * 0.16f);

                // Departure at start: leaves heading along Y towards target with natural lateral arch
                c1 = new SKPoint(start.X + (dx * 0.12f) + lateralSlack, start.Y + (direction * verticalLead));

                // Entry at end: approaches strictly along Y (vertical socket arrival), avoiding horizontal collision with adjacent header pins
                c2 = new SKPoint(end.X - (dx * 0.12f) + (lateralSlack * 0.45f), end.Y - (direction * verticalLead));

                normalX = -MathF.Sign(dx) * 0.65f;
                normalY = -0.76f;
            }
            else
            {
                // Horizontal or shallow diagonal catenary (e.g. Breadboard tie-point to tie-point)
                float boardMidY = breadboardRect?.MidY ?? ((start.Y + end.Y) * 0.5f);
                bool isTopHalf = start.Y < boardMidY && end.Y < boardMidY;
                bool isBottomHalf = start.Y >= boardMidY && end.Y >= boardMidY;
                bool isTrenchCrossing = (start.Y < boardMidY && end.Y >= boardMidY) || (start.Y >= boardMidY && end.Y < boardMidY);

                if (isTrenchCrossing)
                {
                    // Crossing central ravine: smooth vertical S-curve across the divider
                    float direction = MathF.Sign(dy);
                    float trenchLead = Math.Clamp(MathF.Abs(dy) * 0.42f, 6.0f * pxPerMm, 22.0f * pxPerMm);
                    c1 = new SKPoint(start.X + (dx * 0.15f), start.Y + (direction * trenchLead));
                    c2 = new SKPoint(end.X - (dx * 0.15f), end.Y - (direction * trenchLead));
                    normalX = -MathF.Sign(dx) * 0.5f;
                    normalY = -0.86f;
                }
                else
                {
                    float bowDirection = isTopHalf ? -1.0f : +1.0f;

                    normalX = -dy / dist;
                    normalY = dx / dist;

                    if (bowDirection < 0f)
                    {
                        // Bow UPWARD (negative canvas Y towards top rails)
                        if (normalY > 0f || (MathF.Abs(normalY) < 0.001f && normalX < 0f))
                        {
                            normalX = -normalX;
                            normalY = -normalY;
                        }
                    }
                    else
                    {
                        // Bow DOWNWARD (positive canvas Y towards bottom rails)
                        if (normalY < 0f || (MathF.Abs(normalY) < 0.001f && normalX > 0f))
                        {
                            normalX = -normalX;
                            normalY = -normalY;
                        }
                    }

                    float bow = Math.Clamp(dist * (0.24f + variance), 4.0f * pxPerMm, 34.0f * pxPerMm);
                    c1 = new SKPoint(start.X + (dx * 0.25f) + (normalX * bow), start.Y + (dy * 0.25f) + (normalY * bow));
                    c2 = new SKPoint(start.X + (dx * 0.75f) + (normalX * bow), start.Y + (dy * 0.75f) + (normalY * bow));
                }
            }
        }

        using var wirePath = new SKPath();
        using var shadowPath = new SKPath();

        float shadowElevation = Math.Clamp(dist * 0.09f, 2.2f * pxPerMm, 14.0f * pxPerMm);
        float shadowOffsetX = 1.6f * pxPerMm;

        wirePath.MoveTo(start);
        shadowPath.MoveTo(start.X + (0.5f * pxPerMm), start.Y + (0.8f * pxPerMm));

        if (isFlankRoute)
        {
            wirePath.CubicTo(c1, c2, midFlank);
            wirePath.CubicTo(c3, c4, end);

            shadowPath.CubicTo(
                c1.X + shadowOffsetX, c1.Y + shadowElevation,
                c2.X + shadowOffsetX, c2.Y + shadowElevation,
                midFlank.X + shadowOffsetX, midFlank.Y + shadowElevation);
            shadowPath.CubicTo(
                c3.X + shadowOffsetX, c3.Y + shadowElevation,
                c4.X + shadowOffsetX, c4.Y + shadowElevation,
                end.X + (0.5f * pxPerMm), end.Y + (0.8f * pxPerMm));
        }
        else
        {
            wirePath.CubicTo(c1, c2, end);
            shadowPath.CubicTo(
                c1.X + shadowOffsetX, c1.Y + shadowElevation,
                c2.X + shadowOffsetX, c2.Y + shadowElevation,
                end.X + (0.5f * pxPerMm), end.Y + (0.8f * pxPerMm));
        }

        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(2.2f, 2.6f * pxPerMm));
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 110),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(2.2f, 1.7f * pxPerMm),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
            MaskFilter = shadowBlur
        };
        canvas.DrawPath(shadowPath, shadowPaint);

        // 2. Alert violation intense glow aura
        if (hasViolation)
        {
            using var alertGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.2f * pxPerMm);
            using var alertGlowPaint = new SKPaint
            {
                Color = new SKColor(255, 23, 68, 210),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = MathF.Max(4.0f, 3.2f * pxPerMm),
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true,
                MaskFilter = alertGlowBlur
            };
            canvas.DrawPath(wirePath, alertGlowPaint);
        }

        SKColor wireColor = SKColor.TryParse(hexColor, out SKColor parsed) ? parsed : SKColor.Parse("#FF9800");
        float wireWidth = MathF.Max(2.2f, 1.55f * pxPerMm);

        // 3. Faint ambient contrast halo keeping dark jumpers visible against dark backgrounds
        using var haloPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 45),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = wireWidth + MathF.Max(1.0f, 0.45f * pxPerMm),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, haloPaint);

        // 4. Shaded underside stroke (darkened 3D bottom bevel of cylinder)
        SKColor darkUnderside = new SKColor(
            (byte)(wireColor.Red * 0.45f),
            (byte)(wireColor.Green * 0.45f),
            (byte)(wireColor.Blue * 0.45f));
        using var underPaint = new SKPaint
        {
            Color = darkUnderside,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = wireWidth,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, underPaint);

        // 5. Main cylindrical insulation core
        using var wirePaint = new SKPaint
        {
            Color = wireColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = wireWidth * 0.86f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, wirePaint);

        // 6. 3D cylindrical longitudinal specular highlight stroke along the curve crest
        using var specularPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 125),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(0.6f, wireWidth * 0.26f),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, specularPaint);

        // 7. Sleek, high-tech animated electrical current flow pulses (smooth glowing energy capsules)
        double timestampSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        float pulseSpeed = hasViolation ? 75.0f * pxPerMm : 45.0f * pxPerMm;
        float wavelength = 28.0f * pxPerMm;
        float phase = (float)((timestampSec * pulseSpeed) % wavelength);

        // 7a. Outer luminous current pulse aura
        using var dashEffect = SKPathEffect.CreateDash(new float[] { 3.2f * pxPerMm, 24.8f * pxPerMm }, -phase);
        SKColor auraColor = hasViolation ? new SKColor(255, 23, 68, 180) :
                            wireColor.Red > 180 && wireColor.Green < 80 ? new SKColor(255, 152, 0, 160) :
                            new SKColor(100, 220, 240, 170);

        using var pulseAuraPaint = new SKPaint
        {
            PathEffect = dashEffect,
            Color = auraColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1.4f, wireWidth * 0.54f),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, pulseAuraPaint);

        // 7b. Inner intense electrical photon packet core
        using var pulseCorePaint = new SKPaint
        {
            PathEffect = dashEffect,
            Color = hasViolation ? new SKColor(255, 200, 200, 220) : new SKColor(255, 255, 255, 220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(0.6f, wireWidth * 0.22f),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(wirePath, pulseCorePaint);

        // 8. 3D DuPont connector housings at both ends
        Draw3DMetaFerrule(canvas, start, wireColor, pxPerMm);
        Draw3DMetaFerrule(canvas, end, wireColor, pxPerMm);
    }

    /// <summary>
    /// Renders a realistic 3D DuPont connector ferrule, metallic crimp sleeve,
    /// strain-relief collar, and polished terminal plug at a socket or tie point.
    /// </summary>
    public static void Draw3DMetaFerrule(
        SKCanvas canvas,
        SKPoint center,
        SKColor wireColor,
        float pxPerMm,
        float radiusMm = 1.15f)
    {
        float outerRadius = MathF.Max(1.6f, radiusMm * pxPerMm);

        // 1. 2.5D Drop shadow under the DuPont ferrule collar
        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(1.2f, 0.8f * pxPerMm));
        using var collarShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 90),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = shadowBlur
        };
        canvas.DrawCircle(center.X + (0.5f * pxPerMm), center.Y + (0.6f * pxPerMm), outerRadius * 1.35f, collarShadow);

        // 2. DuPont molded black thermoplastic connector housing (rounded square collar with beveled rim)
        using var bootPaint = new SKPaint
        {
            Color = SKColor.Parse("#1A1A1D"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var bootRim = new SKPaint
        {
            Color = SKColor.Parse("#455A64"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.8f, 0.25f * pxPerMm),
            IsAntialias = true
        };
        var collarRect = new SKRect(
            center.X - (outerRadius * 1.22f),
            center.Y - (outerRadius * 1.22f),
            center.X + (outerRadius * 1.22f),
            center.Y + (outerRadius * 1.22f));
        float cr = outerRadius * 0.40f;
        canvas.DrawRoundRect(collarRect, cr, cr, bootPaint);
        canvas.DrawRoundRect(collarRect, cr, cr, bootRim);

        // 3. Shiny metallic crimp sleeve with 4-stop linear metallic gradient
        using var ferrulePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(center.X - outerRadius, center.Y - outerRadius),
                new SKPoint(center.X + outerRadius, center.Y + outerRadius),
                new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#B0BEC5"), SKColor.Parse("#CFD8DC"), SKColor.Parse("#90A4AE") },
                new[] { 0f, 0.35f, 0.75f, 1f },
                SKShaderTileMode.Clamp)
        };
        using var ferruleStroke = new SKPaint
        {
            Color = SKColor.Parse("#546E7A"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.7f, 0.20f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawCircle(center, outerRadius * 0.92f, ferrulePaint);
        canvas.DrawCircle(center, outerRadius * 0.92f, ferruleStroke);

        // 4. Colored wire core dielectric plug with 3D specular gleam
        using var plugPaint = new SKPaint
        {
            Color = wireColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var shinePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 215),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(center, outerRadius * 0.58f, plugPaint);
        canvas.DrawCircle(center.X - (outerRadius * 0.18f), center.Y - (outerRadius * 0.18f), outerRadius * 0.22f, shinePaint);
    }

    private static void DrawDrcViolationHud(
        SKCanvas canvas,
        SKRect breadboardRect,
        IReadOnlyList<McuDrcViolation> violations,
        float pxPerMm)
    {
        float bannerX = breadboardRect.Left;
        float bannerY = Math.Max(4.0f, breadboardRect.Top - (12.0f * pxPerMm));

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(183, 28, 28, 235),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 82, 82, 220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f * pxPerMm,
            IsAntialias = true
        };
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = Math.Max(9.0f, 1.5f * pxPerMm),
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
            TextAlign = SKTextAlign.Left,
            IsAntialias = true
        };

        string summary = $"DRC ALERT ({violations.Count}): {violations[0].RuleCode} - {violations[0].Title}";
        float textW = textPaint.MeasureText(summary);
        var hudRect = new SKRect(bannerX, bannerY, bannerX + textW + (4.0f * pxPerMm), bannerY + (4.2f * pxPerMm));

        canvas.DrawRoundRect(hudRect, 0.8f * pxPerMm, 0.8f * pxPerMm, bgPaint);
        canvas.DrawRoundRect(hudRect, 0.8f * pxPerMm, 0.8f * pxPerMm, borderPaint);
        canvas.DrawText(summary, hudRect.Left + (2.0f * pxPerMm), hudRect.MidY + (0.55f * pxPerMm), textPaint);
    }

    private void DrawHardwareProgrammerAndUsbCable(
        SKCanvas canvas,
        SKRect breadboardRect,
        IReadOnlyList<RenderedMicrocontrollerLayout> mcuLayouts,
        float pxPerMm)
    {
        if (pxPerMm <= 0.01f)
        {
            ClearProgrammerHitRects();
            return;
        }

        float rightAnchor = breadboardRect.Right;
        for (int i = 0; i < mcuLayouts.Count; i++)
        {
            SKRect bounds = mcuLayouts[i].BoardBoundsCanvas;
            if (bounds.Right > rightAnchor && bounds.Left > breadboardRect.Right - (2.0f * pxPerMm))
                rightAnchor = bounds.Right;
        }

        float progLeft = rightAnchor + (12.0f * pxPerMm);
        float progWidth = 28.0f * pxPerMm;
        float progHeight = Math.Clamp(breadboardRect.Height * 0.72f, 24.0f * pxPerMm, 42.0f * pxPerMm);
        float progTop = breadboardRect.Top + ((breadboardRect.Height - progHeight) * 0.5f);
        var progRect = new SKRect(progLeft, progTop, progLeft + progWidth, progTop + progHeight);
        LastProgrammerBodyRect = progRect;

        UsbCableSpec cableSpec = ProgrammerCatalog.GetCableSpec(ActiveUsbCableKind);
        SKColor accentColor = TryParseColor(cableSpec.AccentHex, new SKColor(0x00, 0xBC, 0xD4));
        SKColor jacketColor = TryParseColor(cableSpec.JacketHex, new SKColor(0x00, 0x83, 0x8F));
        bool connected = IsProgrammerConnected;
        bool firmwareLive = ActiveFirmwareState is { IsRunning: true };
        long tickMs = Environment.TickCount64;
        float pulse01 = 0.5f + (0.5f * MathF.Sin((tickMs % 2000L) * (MathF.PI * 2f / 2000f)));

        // 1. Determine USB cable endpoints (Programmer left port -> Docked MCU USB connector)
        var progPortAnchor = new SKPoint(progRect.Left, progRect.Top + (progRect.Height * 0.36f));
        RenderedMicrocontrollerLayout? targetMcu = mcuLayouts.Count > 0 ? mcuLayouts[0] : null;
        SKPoint targetAnchor;
        SKPoint ctrl1;
        SKPoint ctrl2;

        if (targetMcu is not null)
        {
            SKRect mr = targetMcu.BoardBoundsCanvas;
            targetAnchor = targetMcu.BoardType switch
            {
                MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560
                    => new SKPoint(mr.Left + (2.0f * pxPerMm), mr.Top + (15.25f * pxPerMm)),
                MicrocontrollerBoardType.WemosEsp32S2Mini
                    => new SKPoint(mr.MidX, mr.Bottom - (0.8f * pxPerMm)),
                _ => new SKPoint(mr.Left + (1.2f * pxPerMm), mr.MidY)
            };

            if (!connected)
            {
                // Unplugged cable stops slightly short of the MCU USB receptacle so visual detachment is obvious.
                targetAnchor = new SKPoint(targetAnchor.X - (5.5f * pxPerMm), targetAnchor.Y + (4.5f * pxPerMm));
            }

            float midX = (progPortAnchor.X + targetAnchor.X) * 0.5f;
            float sag = MathF.Max(12.0f * pxPerMm, MathF.Abs(progPortAnchor.Y - targetAnchor.Y) * 0.45f);
            ctrl1 = new SKPoint(progPortAnchor.X - (14.0f * pxPerMm), progPortAnchor.Y + (sag * 0.35f));
            ctrl2 = new SKPoint(midX - (6.0f * pxPerMm), MathF.Max(progPortAnchor.Y, targetAnchor.Y) + (sag * 0.72f));
        }
        else
        {
            targetAnchor = new SKPoint(progRect.Left - (9.0f * pxPerMm), progRect.Bottom - (3.0f * pxPerMm));
            ctrl1 = new SKPoint(progPortAnchor.X - (11.0f * pxPerMm), progPortAnchor.Y + (4.0f * pxPerMm));
            ctrl2 = new SKPoint(targetAnchor.X - (4.0f * pxPerMm), targetAnchor.Y + (6.0f * pxPerMm));
        }

        // Draw realistic 3D Bezier USB cable & photon pulses underneath the programmer housing
        using (var cablePath = new SKPath())
        {
            cablePath.MoveTo(progPortAnchor);
            cablePath.CubicTo(ctrl1, ctrl2, targetAnchor);

            using var cableShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f * pxPerMm);
            using var cableShadowPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 95),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.4f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true,
                MaskFilter = cableShadowBlur
            };
            canvas.Save();
            canvas.Translate(0f, 1.0f * pxPerMm);
            canvas.DrawPath(cablePath, cableShadowPaint);
            canvas.Restore();

            if (connected)
            {
                using var cableGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f * pxPerMm);
                using var cableGlowPaint = new SKPaint
                {
                    Color = accentColor.WithAlpha((byte)(70 + (60 * pulse01))),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3.2f * pxPerMm,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                    MaskFilter = cableGlowBlur
                };
                canvas.DrawPath(cablePath, cableGlowPaint);
            }

            using var jacketPaint = new SKPaint
            {
                Color = connected ? jacketColor : new SKColor(0x37, 0x41, 0x51),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.65f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };
            canvas.DrawPath(cablePath, jacketPaint);

            using var highlightPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, connected ? (byte)75 : (byte)35),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.45f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };
            canvas.DrawPath(cablePath, highlightPaint);

            // Flowing telemetry photon packets when USB is linked (faster amber pulses during upload)
            if (connected)
            {
                using var pathMeasure = new SKPathMeasure(cablePath, forceClosed: false);
                float totalLen = pathMeasure.Length;
                if (totalLen > 1f)
                {
                    double speedPeriodMs = IsProgrammerUploading ? 650.0 : 1400.0;
                    float phase = (float)((tickMs % (long)speedPeriodMs) / speedPeriodMs);
                    SKColor packetColor = IsProgrammerUploading ? new SKColor(0xFB, 0xBF, 0x24) : new SKColor(0x00, 0xE5, 0xFF);
                    using var packetHaloBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.1f * pxPerMm);
                    using var packetHalo = new SKPaint
                    {
                        Color = packetColor.WithAlpha(135),
                        Style = SKPaintStyle.Fill,
                        IsAntialias = true,
                        MaskFilter = packetHaloBlur
                    };
                    using var packetCore = new SKPaint
                    {
                        Color = SKColors.White,
                        Style = SKPaintStyle.Fill,
                        IsAntialias = true
                    };

                    const int packetCount = 4;
                    for (int p = 0; p < packetCount; p++)
                    {
                        float dist = ((phase + (p / (float)packetCount)) % 1.0f) * totalLen;
                        if (pathMeasure.GetPosition(dist, out SKPoint pt))
                        {
                            canvas.DrawCircle(pt, 1.25f * pxPerMm, packetHalo);
                            canvas.DrawCircle(pt, 0.58f * pxPerMm, packetCore);
                        }
                    }
                }
            }
        }

        // Glow aura ring on docked MCU's USB receptacle when linked
        if (connected && targetMcu is not null)
        {
            using var portAuraBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.8f * pxPerMm);
            using var portAuraPaint = new SKPaint
            {
                Color = accentColor.WithAlpha((byte)(110 + (85 * pulse01))),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.85f * pxPerMm,
                IsAntialias = true,
                MaskFilter = portAuraBlur
            };
            canvas.DrawCircle(targetAnchor, 3.2f * pxPerMm, portAuraPaint);
        }

        // Molded connector boots at both ends of USB cable
        DrawUsbConnectorBoot(canvas, progPortAnchor, ActiveUsbCableKind, pxPerMm, connected, accentColor);
        DrawUsbConnectorBoot(canvas, targetAnchor, ActiveUsbCableKind, pxPerMm, connected, accentColor);

        // 2. Hardware Programmer Chassis
        using var chassisShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.0f * pxPerMm);
        using var chassisShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 125),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = chassisShadowBlur
        };
        canvas.DrawRoundRect(
            new SKRect(progRect.Left + (0.9f * pxPerMm), progRect.Top + (1.3f * pxPerMm), progRect.Right + (0.9f * pxPerMm), progRect.Bottom + (1.3f * pxPerMm)),
            1.6f * pxPerMm,
            1.6f * pxPerMm,
            chassisShadow);

        if (connected)
        {
            using var boxAuraBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f * pxPerMm);
            using var boxAuraPaint = new SKPaint
            {
                Color = (firmwareLive ? new SKColor(0x10, 0xB9, 0x81) : accentColor).WithAlpha((byte)(85 + (65 * pulse01))),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.3f * pxPerMm,
                IsAntialias = true,
                MaskFilter = boxAuraBlur
            };
            canvas.DrawRoundRect(progRect, 1.6f * pxPerMm, 1.6f * pxPerMm, boxAuraPaint);
        }

        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(progRect.Left, progRect.Top),
            new SKPoint(progRect.Left, progRect.Bottom),
            new[] { SKColor.Parse("#1F2937"), SKColor.Parse("#111827"), SKColor.Parse("#0B0F19") },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        using var bodyPaint = new SKPaint { Shader = bodyShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(progRect, 1.4f * pxPerMm, 1.4f * pxPerMm, bodyPaint);

        using var bezelPaint = new SKPaint
        {
            Color = connected ? accentColor : SKColor.Parse("#334155"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.45f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(progRect, 1.4f * pxPerMm, 1.4f * pxPerMm, bezelPaint);

        // Corner chassis screws
        float screwInset = 1.4f * pxPerMm;
        using var screwPaint = new SKPaint { Color = SKColor.Parse("#64748B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(progRect.Left + screwInset, progRect.Top + screwInset, 0.45f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Right - screwInset, progRect.Top + screwInset, 0.45f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Left + screwInset, progRect.Bottom - screwInset, 0.45f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Right - screwInset, progRect.Bottom - screwInset, 0.45f * pxPerMm, screwPaint);

        // Header Silkscreen
        using var titlePaint = new SKPaint
        {
            Color = SKColor.Parse("#E2E8F0"),
            TextSize = Math.Max(7.0f, 1.45f * pxPerMm),
            Typeface = _uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("USB-ISP / UART", progRect.MidX, progRect.Top + (3.2f * pxPerMm), titlePaint);

        using var subPaint = new SKPaint
        {
            Color = SKColor.Parse("#94A3B8"),
            TextSize = Math.Max(6.0f, 1.05f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("HARDWARE PROG", progRect.MidX, progRect.Top + (5.1f * pxPerMm), subPaint);

        // Status LED Array: PWR, LINK, TX, RX
        float ledRowY = progRect.Top + (8.0f * pxPerMm);
        float ledSpacing = progWidth * 0.20f;
        float ledStartX = progRect.MidX - (1.5f * ledSpacing);
        bool txActive = IsProgrammerUploading || (firmwareLive && ((ActiveFirmwareState!.TickCount % 2) == 0));
        bool rxActive = firmwareLive && ((ActiveFirmwareState!.TickCount % 3) == 1);

        DrawProgrammerLed(canvas, new SKPoint(ledStartX, ledRowY), "PWR", true, new SKColor(0x10, 0xB9, 0x81), pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + ledSpacing, ledRowY), "LINK", connected, new SKColor(0x00, 0xE5, 0xFF), pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + (ledSpacing * 2f), ledRowY), "TX", txActive, new SKColor(0xF5, 0x9E, 0x0B), pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + (ledSpacing * 3f), ledRowY), "RX", rxActive, new SKColor(0x34, 0xD3, 0x99), pxPerMm);

        // Active USB Cable Pill
        float pillTop = progRect.Top + (11.2f * pxPerMm);
        float pillH = 3.8f * pxPerMm;
        var cablePill = new SKRect(progRect.Left + (2.0f * pxPerMm), pillTop, progRect.Right - (2.0f * pxPerMm), pillTop + pillH);
        using var pillBg = new SKPaint { Color = SKColor.Parse("#0F172A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var pillBorder = new SKPaint { Color = accentColor.WithAlpha(185), Style = SKPaintStyle.Stroke, StrokeWidth = 0.35f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(cablePill, 0.7f * pxPerMm, 0.7f * pxPerMm, pillBg);
        canvas.DrawRoundRect(cablePill, 0.7f * pxPerMm, 0.7f * pxPerMm, pillBorder);

        using var pillText = new SKPaint
        {
            Color = accentColor,
            TextSize = Math.Max(6.5f, 1.15f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        string cableBadgeLabel = $"{cableSpec.ShortTag} · {(connected ? "LINKED" : "IDLE")}";
        canvas.DrawText(cableBadgeLabel, cablePill.MidX, cablePill.MidY + (0.42f * pxPerMm), pillText);

        // Flash progress bar inside programmer box (visible while uploading or live)
        float barTop = cablePill.Bottom + (1.6f * pxPerMm);
        var barTrack = new SKRect(progRect.Left + (2.0f * pxPerMm), barTop, progRect.Right - (2.0f * pxPerMm), barTop + (1.8f * pxPerMm));
        using var barTrackPaint = new SKPaint { Color = SKColor.Parse("#1E293B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(barTrack, 0.5f * pxPerMm, 0.5f * pxPerMm, barTrackPaint);

        float fillFraction = IsProgrammerUploading
            ? Math.Clamp(ProgrammerUploadProgress, 0.05f, 1.0f)
            : firmwareLive ? 1.0f : 0f;

        if (fillFraction > 0f)
        {
            var barFill = new SKRect(barTrack.Left, barTrack.Top, barTrack.Left + (barTrack.Width * fillFraction), barTrack.Bottom);
            using var barFillPaint = new SKPaint
            {
                Color = IsProgrammerUploading ? new SKColor(0xF5, 0x9E, 0x0B) : new SKColor(0x10, 0xB9, 0x81),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(barFill, 0.5f * pxPerMm, 0.5f * pxPerMm, barFillPaint);
        }

        // Clickable [ ⬆ UPLOAD FIRMWARE ] Button at bottom of programmer chassis (only when connected)
        float pad = 1.9f * pxPerMm;
        float btnH = Math.Max(14.0f, 4.8f * pxPerMm);
        var btnRect = new SKRect(progRect.Left + pad, progRect.Bottom - btnH - pad, progRect.Right - pad, progRect.Bottom - pad);

        LastProgrammerUploadButtonRect = connected ? btnRect : SKRect.Empty;

        SKColor btnFillColor = !connected
            ? SKColor.Parse("#0F172A")
            : IsProgrammerUploading
                ? SKColor.Parse("#78350F")
                : SKColor.Parse("#065F46");
        SKColor btnBorderColor = !connected
            ? SKColor.Parse("#334155")
            : IsProgrammerUploading
                ? SKColor.Parse("#FBBF24")
                : SKColor.Parse("#00E5FF");
        SKColor btnTextColor = connected ? SKColors.White : SKColor.Parse("#64748B");
        float btnStrokeWidth = (connected ? 0.45f : 0.35f) * pxPerMm;
        float btnTextSize = connected ? Math.Max(7.0f, 1.15f * pxPerMm) : Math.Max(6.0f, 0.88f * pxPerMm);
        SKTypeface? btnTypeface = connected ? _uiBoldTypeface : _monoTypeface;
        string btnCaption = !connected
            ? "CONNECT CABLE TO UPLOAD"
            : IsProgrammerUploading
                ? $"FLASHING {Math.Round(ProgrammerUploadProgress * 100):0}%"
                : firmwareLive
                    ? "⬆ RE-UPLOAD FW"
                    : "⬆ UPLOAD FIRMWARE";

        using var btnFill = new SKPaint { Color = btnFillColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var btnBorder = new SKPaint
        {
            Color = btnBorderColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = btnStrokeWidth,
            IsAntialias = true
        };
        canvas.DrawRoundRect(btnRect, 0.9f * pxPerMm, 0.9f * pxPerMm, btnFill);
        canvas.DrawRoundRect(btnRect, 0.9f * pxPerMm, 0.9f * pxPerMm, btnBorder);

        using var btnLabelPaint = new SKPaint
        {
            Color = btnTextColor,
            TextSize = btnTextSize,
            Typeface = btnTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        float baselineOffset = connected ? (0.42f * pxPerMm) : (0.35f * pxPerMm);
        canvas.DrawText(btnCaption, btnRect.MidX, btnRect.MidY + baselineOffset, btnLabelPaint);
    }

    private void DrawProgrammerLed(
        SKCanvas canvas,
        SKPoint center,
        string label,
        bool isOn,
        SKColor onColor,
        float pxPerMm)
    {
        float r = 0.65f * pxPerMm;
        if (isOn)
        {
            using var haloBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.9f * pxPerMm);
            using var haloPaint = new SKPaint
            {
                Color = onColor.WithAlpha(155),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                MaskFilter = haloBlur
            };
            canvas.DrawCircle(center, r * 2.1f, haloPaint);
        }

        using var ledPaint = new SKPaint
        {
            Color = isOn ? onColor : SKColor.Parse("#334155"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(center, r, ledPaint);

        using var textPaint = new SKPaint
        {
            Color = isOn ? SKColor.Parse("#E2E8F0") : SKColor.Parse("#64748B"),
            TextSize = Math.Max(5.5f, 0.82f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, center.X, center.Y + (1.75f * pxPerMm), textPaint);
    }

    private static void DrawUsbConnectorBoot(
        SKCanvas canvas,
        SKPoint anchor,
        UsbCableKind cableKind,
        float pxPerMm,
        bool connected,
        SKColor accent)
    {
        float halfW = cableKind == UsbCableKind.UsbB ? 1.5f * pxPerMm : 1.15f * pxPerMm;
        float halfH = cableKind == UsbCableKind.MicroUsb ? 0.85f * pxPerMm : 1.25f * pxPerMm;
        var bootRect = new SKRect(anchor.X - halfW, anchor.Y - halfH, anchor.X + halfW, anchor.Y + halfH);

        using var bootFill = new SKPaint
        {
            Color = SKColor.Parse("#1E293B"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var bootStroke = new SKPaint
        {
            Color = connected ? accent : SKColor.Parse("#64748B"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.35f * pxPerMm,
            IsAntialias = true
        };
        float radius = cableKind == UsbCableKind.UsbB ? 0.25f * pxPerMm : 0.65f * pxPerMm;
        canvas.DrawRoundRect(bootRect, radius, radius, bootFill);
        canvas.DrawRoundRect(bootRect, radius, radius, bootStroke);
    }

    private void DrawThermalHeatmapOverlay(SKCanvas canvas)
    {
        float invScale = 1f / MathF.Max(_scale, 0.05f);

        foreach (ThermalHotspot hotspot in ActiveThermalHotspots)
        {
            float t = Math.Clamp(hotspot.NormalizedHeat01, 0f, 1f);
            SKColor heatColor = t < 0.5f
                ? LerpColor(new SKColor(0x00, 0xE6, 0x76), new SKColor(0xFF, 0xEA, 0x00), t * 2f)
                : LerpColor(new SKColor(0xFF, 0xEA, 0x00), new SKColor(0xFF, 0x17, 0x44), (t - 0.5f) * 2f);

            float radius = 24f + (t * 28f);
            var center = new SKPoint(hotspot.CenterPx, hotspot.CenterPy);

            using var shader = SKShader.CreateRadialGradient(
                center,
                radius,
                new[]
                {
                    heatColor.WithAlpha((byte)(110 + (115 * t))),
                    heatColor.WithAlpha(85),
                    SKColors.Transparent
                },
                new[] { 0f, 0.45f, 1f },
                SKShaderTileMode.Clamp);
            using var heatPaint = new SKPaint
            {
                Shader = shader,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawCircle(center, radius, heatPaint);

            if (hotspot.IsOverloaded)
            {
                using var ringDash = SKPathEffect.CreateDash(new[] { 4f * invScale, 3f * invScale }, 0f);
                using var ringPaint = new SKPaint
                {
                    Color = new SKColor(0xFF, 0x17, 0x44, 225),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f * invScale,
                    PathEffect = ringDash,
                    IsAntialias = true
                };
                canvas.DrawCircle(center, radius * 0.78f, ringPaint);
            }

            if (_scale >= 0.45f)
            {
                string pillText = hotspot.IsOverloaded
                    ? string.Create(CultureInfo.InvariantCulture, $"! {hotspot.EstimatedCurrentMa:0.#}mA {hotspot.PowerMilliwatts:0.#}mW")
                    : string.Create(CultureInfo.InvariantCulture, $"{hotspot.EstimatedCurrentMa:0.#}mA · {hotspot.PowerMilliwatts:0.#}mW");

                using var txtPaint = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 8.5f * invScale,
                    Typeface = _monoTypeface,
                    TextAlign = SKTextAlign.Center,
                    IsAntialias = true
                };
                float txtW = txtPaint.MeasureText(pillText);
                float padX = 5f * invScale;
                float pillH = 13f * invScale;
                float pillCy = hotspot.CenterPy - (16f + (t * 6f));
                var pillRect = new SKRect(
                    hotspot.CenterPx - (txtW * 0.5f) - padX,
                    pillCy - (pillH * 0.5f),
                    hotspot.CenterPx + (txtW * 0.5f) + padX,
                    pillCy + (pillH * 0.5f));

                using var bgPaint = new SKPaint
                {
                    Color = hotspot.IsOverloaded ? new SKColor(0x7F, 0x1D, 0x1D, 232) : new SKColor(0x11, 0x18, 0x27, 225),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                using var borderPaint = new SKPaint
                {
                    Color = heatColor,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 0.9f * invScale,
                    IsAntialias = true
                };

                canvas.DrawRoundRect(pillRect, 3f * invScale, 3f * invScale, bgPaint);
                canvas.DrawRoundRect(pillRect, 3f * invScale, 3f * invScale, borderPaint);
                canvas.DrawText(pillText, pillRect.MidX, pillRect.MidY + (3f * invScale), txtPaint);
            }
        }
    }

    private static SKColor LerpColor(SKColor a, SKColor b, float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + ((b.Red - a.Red) * clamped)),
            (byte)(a.Green + ((b.Green - a.Green) * clamped)),
            (byte)(a.Blue + ((b.Blue - a.Blue) * clamped)),
            (byte)(a.Alpha + ((b.Alpha - a.Alpha) * clamped)));
    }

    private static SKColor TryParseColor(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;

        try
        {
            return SKColor.Parse(hex);
        }
        catch
        {
            return fallback;
        }
    }
}
