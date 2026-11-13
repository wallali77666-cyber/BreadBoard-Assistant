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

    /// <summary>Board instance ID currently targeted by the hardware programmer cable (e.g., MCU_1 or MCU_2).</summary>
    public string TargetProgrammerBoardInstanceId { get; set; } = "MCU_1";

    /// <summary>True when a universal USB adapter is enabled to bridge mismatched plug types.</summary>
    public bool IsCableAdapterEnabled { get; set; }

    /// <summary>True when the selected cable does not natively fit the target board without an adapter.</summary>
    public bool IsCableMismatchWarning { get; set; }

    /// <summary>Compact telemetry line shown inside the CAD Programmer chassis (board/FQBN/baud/port).</summary>
    public string ActiveBoardPortSummaryText { get; set; } = "Ready · 115200 baud";

    /// <summary>Non-None while user is dragging or click-placing a USB cable onto a Dev Board receptacle.</summary>
    public UsbCableKind PendingCableDragKind { get; set; } = UsbCableKind.None;

    /// <summary>Current cursor position in canvas pixels during manual USB cable placement.</summary>
    public SKPoint CableDragCursorCanvas { get; set; }

    /// <summary>Cable card currently hovered in the 2.5D Cable Rack.</summary>
    public UsbCableKind HoveredCableRackKind { get; set; } = UsbCableKind.None;

    /// <summary>Board instance ID whose USB receptacle is currently hovered, if any.</summary>
    public string? HoveredUsbReceptacleBoardId { get; set; }

    /// <summary>True when cursor hovers over the Hardware Programmer USB Jack.</summary>
    public bool HoveredProgrammerJack { get; set; }

    /// <summary>Live virtual firmware runtime telemetry (LED/LCD/Buzzer/TX-RX states).</summary>
    public VirtualFirmwareRuntimeState? ActiveFirmwareState { get; set; }

    /// <summary>Active thermal hotspots rendered as IR overlays in board coordinates.</summary>
    public IReadOnlyList<ThermalHotspot> ActiveThermalHotspots { get; set; } = Array.Empty<ThermalHotspot>();

    /// <summary>Screen-space bounding box of the CAD [UPLOAD FIRMWARE] button for viewport click hit-testing (only active when connected).</summary>
    public SKRect LastProgrammerUploadButtonRect { get; private set; } = SKRect.Empty;

    /// <summary>Always-accessible Send / Open Programmer Dialog button bounding box on the CAD programmer chassis.</summary>
    public SKRect LastProgrammerSendButtonRect { get; private set; } = SKRect.Empty;

    /// <summary>Screen-space bounding box of the external hardware programmer chassis.</summary>
    public SKRect LastProgrammerBodyRect { get; private set; } = SKRect.Empty;

    /// <summary>Screen-space bounding box of the Hardware Programmer's USB output jack.</summary>
    public SKRect LastProgrammerJackRect { get; private set; } = SKRect.Empty;

    /// <summary>Screen-space bounding box of the Adapter toggle pill in the 2.5D Cable Rack.</summary>
    public SKRect LastProgrammerAdapterToggleRect { get; private set; } = SKRect.Empty;

    /// <summary>Screen-space bounding box of the 2.5D Cable Rack tray.</summary>
    public SKRect LastCableRackBodyRect { get; private set; } = SKRect.Empty;

    private readonly Dictionary<UsbCableKind, SKRect> _cableRackCardRects = new();
    private readonly Dictionary<string, (SKRect UsbRect, UsbCableKind NativeKind, bool IsVertical)> _boardUsbReceptacles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SKPoint> _connectedCableSamples = new();
    private SKPoint _lastProgrammerPortAnchor;

    public IReadOnlyDictionary<UsbCableKind, SKRect> LastCableRackCardRects => _cableRackCardRects;

    public IReadOnlyDictionary<string, (SKRect UsbRect, UsbCableKind NativeKind, bool IsVertical)> LastBoardUsbReceptacles => _boardUsbReceptacles;

    public SKPoint LastProgrammerPortAnchor => _lastProgrammerPortAnchor;

    /// <summary>Returns true when viewport click coordinates land on the CAD Upload Firmware button.</summary>
    public bool TryHitTestProgrammerUploadButton(float canvasX, float canvasY)
        => !LastProgrammerUploadButtonRect.IsEmpty && LastProgrammerUploadButtonRect.Contains(canvasX, canvasY);

    /// <summary>Returns true when viewport click coordinates land on the always-accessible CAD Send/Dialog button.</summary>
    public bool TryHitTestProgrammerSendButton(float canvasX, float canvasY)
        => !LastProgrammerSendButtonRect.IsEmpty && LastProgrammerSendButtonRect.Contains(canvasX, canvasY);

    /// <summary>Returns true when click/hover lands on the Hardware Programmer's USB jack socket.</summary>
    public bool TryHitTestProgrammerJack(float canvasX, float canvasY)
        => !LastProgrammerJackRect.IsEmpty && InflateRect(LastProgrammerJackRect, 4f).Contains(canvasX, canvasY);

    /// <summary>Returns true when click lands on the 2.5D Cable Rack's USB Adapter toggle pill.</summary>
    public bool TryHitTestProgrammerAdapterToggle(float canvasX, float canvasY)
        => !LastProgrammerAdapterToggleRect.IsEmpty && LastProgrammerAdapterToggleRect.Contains(canvasX, canvasY);

    /// <summary>Hit-tests the tactile 2.5D cable cards mounted in the Cable Rack.</summary>
    public bool TryHitTestCableRackCard(float canvasX, float canvasY, out UsbCableKind cableKind)
    {
        foreach (var kv in _cableRackCardRects)
        {
            if (!kv.Value.IsEmpty && kv.Value.Contains(canvasX, canvasY))
            {
                cableKind = kv.Key;
                return true;
            }
        }

        cableKind = UsbCableKind.None;
        return false;
    }

    /// <summary>Hit-tests any docked microcontroller's USB receptacle port (inflated slightly for easy manual plugging).</summary>
    public bool TryHitTestBoardUsbReceptacle(float canvasX, float canvasY, out string? boardInstanceId, out UsbCableKind nativeUsbKind)
    {
        foreach (var kv in _boardUsbReceptacles)
        {
            if (!kv.Value.UsbRect.IsEmpty && InflateRect(kv.Value.UsbRect, 5f).Contains(canvasX, canvasY))
            {
                boardInstanceId = kv.Key;
                nativeUsbKind = kv.Value.NativeKind;
                return true;
            }
        }

        boardInstanceId = null;
        nativeUsbKind = UsbCableKind.None;
        return false;
    }

    /// <summary>Hit-tests the currently connected USB cable path or either plugged USB boot for right-click unplugging.</summary>
    public bool TryHitTestConnectedCableOrPlug(float canvasX, float canvasY)
    {
        if (!IsProgrammerConnected)
            return false;

        if (TryHitTestProgrammerJack(canvasX, canvasY))
            return true;

        if (_boardUsbReceptacles.TryGetValue(TargetProgrammerBoardInstanceId, out var targetRec) &&
            !targetRec.UsbRect.IsEmpty &&
            InflateRect(targetRec.UsbRect, 6f).Contains(canvasX, canvasY))
        {
            return true;
        }

        if (_connectedCableSamples.Count < 2)
            return false;

        const float maxDistSq = 12f * 12f;
        for (int i = 0; i < _connectedCableSamples.Count - 1; i++)
        {
            if (DistanceSqToSegment(new SKPoint(canvasX, canvasY), _connectedCableSamples[i], _connectedCableSamples[i + 1]) <= maxDistSq)
                return true;
        }

        return false;
    }

    private static SKRect InflateRect(SKRect rect, float pad) =>
        new(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad);

    private static float DistanceSqToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 0.0001f)
        {
            float px = p.X - a.X;
            float py = p.Y - a.Y;
            return (px * px) + (py * py);
        }

        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0f, 1f);
        float projX = a.X + (t * dx);
        float projY = a.Y + (t * dy);
        float distX = p.X - projX;
        float distY = p.Y - projY;
        return (distX * distX) + (distY * distY);
    }

    internal void ClearProgrammerHitRects()
    {
        LastProgrammerUploadButtonRect = SKRect.Empty;
        LastProgrammerSendButtonRect = SKRect.Empty;
        LastProgrammerBodyRect = SKRect.Empty;
        LastProgrammerJackRect = SKRect.Empty;
        LastProgrammerAdapterToggleRect = SKRect.Empty;
        LastCableRackBodyRect = SKRect.Empty;
        _cableRackCardRects.Clear();
        _boardUsbReceptacles.Clear();
        _connectedCableSamples.Clear();
    }

    public bool TryHitTestDevBoardPin(float canvasX, float canvasY, out DevBoardPinHit? hit)
    {
        hit = null;
        if (ActiveMicrocontrollers.Count == 0 || !_hasTransform)
            return false;

        float pixelsPerMm = _boardPixelWidth / BreadboardWidthMm;
        var boardRect = new SKRect(_boardLeft, _boardTop, _boardLeft + _boardPixelWidth, _boardTop + _boardPixelHeight);

        // Always recompute non-overlapping layouts if layout cache is out of date or bounds shifted
        var expectedLayouts = McuRenderer.ComputeBoardLayouts(ActiveMicrocontrollers, boardRect, pixelsPerMm);
        if (_activeMcuLayouts.Count != expectedLayouts.Count ||
            (expectedLayouts.Count > 0 &&
             (MathF.Abs(_activeMcuLayouts[0].BoardBoundsCanvas.Left - expectedLayouts[0].BoardBoundsCanvas.Left) > 0.5f ||
              MathF.Abs(_activeMcuLayouts[0].BoardBoundsCanvas.Top - expectedLayouts[0].BoardBoundsCanvas.Top) > 0.5f)))
        {
            _activeMcuLayouts = expectedLayouts;
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
        using var ferruleShader = SKShader.CreateLinearGradient(
            new SKPoint(center.X - outerRadius, center.Y - outerRadius),
            new SKPoint(center.X + outerRadius, center.Y + outerRadius),
            new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#B0BEC5"), SKColor.Parse("#CFD8DC"), SKColor.Parse("#90A4AE") },
            new[] { 0f, 0.35f, 0.75f, 1f },
            SKShaderTileMode.Clamp);
        using var ferrulePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = ferruleShader
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
            Typeface = s_offBoardUiBoldTypeface,
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

    private static readonly SKTypeface s_offBoardUiBoldTypeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);

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

        _boardUsbReceptacles.Clear();
        _cableRackCardRects.Clear();
        _connectedCableSamples.Clear();

        foreach (RenderedMicrocontrollerLayout layout in mcuLayouts)
        {
            if (!layout.UsbReceptacleRectCanvas.IsEmpty)
            {
                _boardUsbReceptacles[layout.BoardInstanceId] = (
                    layout.UsbReceptacleRectCanvas,
                    layout.NativeUsbKind,
                    layout.IsUsbReceptacleVertical);
            }
        }

        float rightAnchor = breadboardRect.Right;
        float maxRightWithLabels = breadboardRect.Right;
        for (int i = 0; i < mcuLayouts.Count; i++)
        {
            SKRect bounds = mcuLayouts[i].BoardBoundsCanvas;
            if (bounds.Right > rightAnchor)
                rightAnchor = bounds.Right;

            McuLabelExtents ext = McuRenderer.MeasureLabelExtents(mcuLayouts[i].BoardType, pxPerMm);
            float labelRight = bounds.Right + ext.RightPx;
            if (labelRight > maxRightWithLabels)
                maxRightWithLabels = labelRight;
        }

        float progLeft = MathF.Max(rightAnchor + (12.6f * pxPerMm), maxRightWithLabels + (7.6f * pxPerMm));
        float progWidth = 35.0f * pxPerMm;
        float progHeight = 31.5f * pxPerMm;
        float progTop = breadboardRect.Top;
        var progRect = new SKRect(progLeft, progTop, progLeft + progWidth, progTop + progHeight);
        LastProgrammerBodyRect = progRect;

        float rackTop = progRect.Bottom + (2.3f * pxPerMm);
        float rackHeight = 27.0f * pxPerMm;
        var rackRect = new SKRect(progLeft, rackTop, progLeft + progWidth, rackTop + rackHeight);
        LastCableRackBodyRect = rackRect;

        UsbCableSpec cableSpec = ProgrammerCatalog.GetCableSpec(ActiveUsbCableKind);
        SKColor accentColor = TryParseColor(cableSpec.AccentHex, new SKColor(0x00, 0xE5, 0xFF));
        SKColor neonGreenCyan = new SKColor(0x00, 0xFF, 0xC8);
        bool connected = IsProgrammerConnected;
        bool firmwareLive = ActiveFirmwareState is { IsRunning: true };
        long tickMs = Environment.TickCount64;
        float pulse01 = 0.5f + (0.5f * MathF.Sin((tickMs % 2000L) * (MathF.PI * 2f / 2000f)));

        var progPortAnchor = new SKPoint(progRect.Left, progRect.Top + (progRect.Height * 0.36f));
        // Seat the Programmer USB plug barrel cleanly outside jackHousing.Left (-0.55mm) so
        // the brushed silver neck inserts into the socket without clipping ANKER/orange branding.
        var progPlugAnchor = new SKPoint(progPortAnchor.X - (1.25f * pxPerMm), progPortAnchor.Y);
        _lastProgrammerPortAnchor = progPortAnchor;
        LastProgrammerJackRect = new SKRect(
            progPortAnchor.X - (2.6f * pxPerMm),
            progPortAnchor.Y - (2.4f * pxPerMm),
            progPortAnchor.X + (2.2f * pxPerMm),
            progPortAnchor.Y + (2.4f * pxPerMm));

        RenderedMicrocontrollerLayout? targetMcu = mcuLayouts.FirstOrDefault(m =>
            string.Equals(m.BoardInstanceId, TargetProgrammerBoardInstanceId, StringComparison.OrdinalIgnoreCase))
            ?? (mcuLayouts.Count > 0 ? mcuLayouts[0] : null);

        // 1. Draw USB receptacle placement indicators on every docked MCU so manual drop/click targets stand out
        foreach (RenderedMicrocontrollerLayout layout in mcuLayouts)
        {
            if (layout.UsbReceptacleRectCanvas.IsEmpty)
                continue;

            bool isTargeted = targetMcu is not null && string.Equals(layout.BoardInstanceId, targetMcu.BoardInstanceId, StringComparison.OrdinalIgnoreCase);
            bool isHoveredPort = string.Equals(HoveredUsbReceptacleBoardId, layout.BoardInstanceId, StringComparison.OrdinalIgnoreCase);
            UsbCableKind evalKind = PendingCableDragKind != UsbCableKind.None ? PendingCableDragKind : ActiveUsbCableKind;
            bool compat = ProgrammerCatalog.IsCableCompatible(evalKind, layout.BoardType, IsCableAdapterEnabled, out bool isNativeMatch, out _);

            if ((connected && isTargeted) || PendingCableDragKind != UsbCableKind.None || isHoveredPort || !connected)
            {
                SKColor portRingColor = connected && isTargeted
                    ? neonGreenCyan
                    : (PendingCableDragKind != UsbCableKind.None || isHoveredPort)
                        ? (compat ? (isNativeMatch ? neonGreenCyan : new SKColor(0xFB, 0xBF, 0x24)) : new SKColor(0xEF, 0x44, 0x44))
                        : new SKColor(0x38, 0xBD, 0xF8);

                byte alpha = (connected && isTargeted)
                    ? (byte)(130 + (95 * pulse01))
                    : (isHoveredPort || PendingCableDragKind != UsbCableKind.None)
                        ? (byte)230
                        : (byte)(85 + (45 * pulse01));

                SKRect ringRect = InflateRect(layout.UsbReceptacleRectCanvas, 1.15f * pxPerMm);
                using var portGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.85f * pxPerMm);
                using var portDashEffect = (connected && isTargeted)
                    ? null
                    : SKPathEffect.CreateDash(new[] { 1.6f * pxPerMm, 1.1f * pxPerMm }, 0f);
                using var portRingPaint = new SKPaint
                {
                    Color = portRingColor.WithAlpha(alpha),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (isHoveredPort || PendingCableDragKind != UsbCableKind.None ? 0.8f : 0.55f) * pxPerMm,
                    PathEffect = portDashEffect,
                    IsAntialias = true,
                    MaskFilter = (connected && isTargeted) || isHoveredPort ? portGlowBlur : null
                };
                canvas.DrawRoundRect(ringRect, 1.1f * pxPerMm, 1.1f * pxPerMm, portRingPaint);
            }
        }

        // 2. Draw Connected USB Cable Path & Realistic Plugs (with vertical S2-Mini or horizontal Uno/ESP32/STM32 entry)
        if (connected && ActiveUsbCableKind != UsbCableKind.None && targetMcu is not null)
        {
            SKRect usbRect = targetMcu.UsbReceptacleRectCanvas;
            bool verticalPort = targetMcu.IsUsbReceptacleVertical;
            SKPoint targetAnchor = !usbRect.IsEmpty
                ? verticalPort
                    ? new SKPoint(usbRect.MidX, usbRect.Bottom - (0.35f * pxPerMm))
                    : new SKPoint(usbRect.Left + (0.85f * pxPerMm), usbRect.MidY)
                : new SKPoint(targetMcu.BoardBoundsCanvas.Left + (1.2f * pxPerMm), targetMcu.BoardBoundsCanvas.MidY);

            float tailOffset = (ActiveUsbCableKind == UsbCableKind.UsbB ? 5.6f : 5.4f) * pxPerMm;
            SKPoint progTail = new(progPlugAnchor.X - tailOffset, progPlugAnchor.Y);
            float rightCorridorX = MathF.Max(maxRightWithLabels + (1.8f * pxPerMm), progTail.X - (0.6f * pxPerMm));
            if (rightCorridorX > progTail.X - (0.2f * pxPerMm))
                rightCorridorX = progTail.X - (0.2f * pxPerMm);

            using var cablePath = BuildCleanUsbCablePath(
                progPlugAnchor,
                progTail,
                targetAnchor,
                tailOffset,
                verticalPort,
                targetMcu,
                breadboardRect,
                mcuLayouts,
                McuRenderer,
                rightCorridorX,
                pxPerMm);

            using (var meas = new SKPathMeasure(cablePath, forceClosed: false))
            {
                float len = meas.Length;
                const int samples = 24;
                for (int s = 0; s <= samples; s++)
                {
                    if (meas.GetPosition((len * s) / samples, out SKPoint pt))
                        _connectedCableSamples.Add(pt);
                }
            }

            DrawRealisticUsbCablePath(canvas, cablePath, cableSpec, pxPerMm, connected: true, IsProgrammerUploading, tickMs, pulse01);

            DrawRealisticPlugBarrel(
                canvas,
                progPlugAnchor,
                ActiveUsbCableKind,
                cableSpec.ShellStyleId,
                isVerticalBottomPort: false,
                isBoardEnd: false,
                pxPerMm,
                connected: true,
                accentColor);

            DrawRealisticPlugBarrel(
                canvas,
                targetAnchor,
                ActiveUsbCableKind,
                cableSpec.ShellStyleId,
                isVerticalBottomPort: verticalPort,
                isBoardEnd: true,
                pxPerMm,
                connected: true,
                accentColor);
        }

        // 4. Hardware Programmer Chassis (Bright Neon Connected Glow + IDE Telemetry + USB Jack)
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
            using var boxAuraBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f * pxPerMm);
            using var boxAuraPaint = new SKPaint
            {
                Color = (firmwareLive ? neonGreenCyan : accentColor).WithAlpha((byte)(125 + (90 * pulse01))),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f * pxPerMm,
                IsAntialias = true,
                MaskFilter = boxAuraBlur
            };
            canvas.DrawRoundRect(progRect, 1.6f * pxPerMm, 1.6f * pxPerMm, boxAuraPaint);
        }

        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(progRect.Left, progRect.Top),
            new SKPoint(progRect.Left, progRect.Bottom),
            connected
                ? new[] { SKColor.Parse("#0F2A2E"), SKColor.Parse("#0B1726"), SKColor.Parse("#070D17") }
                : new[] { SKColor.Parse("#1F2937"), SKColor.Parse("#111827"), SKColor.Parse("#0B0F19") },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        using var bodyPaint = new SKPaint { Shader = bodyShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(progRect, 1.4f * pxPerMm, 1.4f * pxPerMm, bodyPaint);

        using var bezelPaint = new SKPaint
        {
            Color = connected ? neonGreenCyan : SKColor.Parse("#334155"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (connected ? 0.65f : 0.45f) * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(progRect, 1.4f * pxPerMm, 1.4f * pxPerMm, bezelPaint);

        // Programmer USB output jack socket on left edge (clickable to initiate cable drag)
        var jackHousing = new SKRect(
            progRect.Left - (0.55f * pxPerMm),
            progPortAnchor.Y - (1.8f * pxPerMm),
            progRect.Left + (1.55f * pxPerMm),
            progPortAnchor.Y + (1.8f * pxPerMm));
        using var jackFill = new SKPaint { Color = SKColor.Parse("#0F172A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var jackBorder = new SKPaint
        {
            Color = HoveredProgrammerJack ? SKColors.White : (connected ? neonGreenCyan : new SKColor(0x38, 0xBD, 0xF8)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (HoveredProgrammerJack ? 0.55f : 0.35f) * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(jackHousing, 0.5f * pxPerMm, 0.5f * pxPerMm, jackFill);
        canvas.DrawRoundRect(jackHousing, 0.5f * pxPerMm, 0.5f * pxPerMm, jackBorder);

        // Corner chassis screws
        float screwInset = 1.3f * pxPerMm;
        using var screwPaint = new SKPaint { Color = SKColor.Parse("#64748B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(progRect.Left + screwInset, progRect.Top + screwInset, 0.42f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Right - screwInset, progRect.Top + screwInset, 0.42f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Left + screwInset, progRect.Bottom - screwInset, 0.42f * pxPerMm, screwPaint);
        canvas.DrawCircle(progRect.Right - screwInset, progRect.Bottom - screwInset, 0.42f * pxPerMm, screwPaint);

        // Header Silkscreen
        using var titlePaint = new SKPaint
        {
            Color = connected ? neonGreenCyan : SKColor.Parse("#E2E8F0"),
            TextSize = Math.Max(6.5f, 1.32f * pxPerMm),
            Typeface = _uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(connected ? "USB PROG · CONNECTED" : "USB-ISP / UART PROG", progRect.MidX, progRect.Top + (3.1f * pxPerMm), titlePaint);

        using var subPaint = new SKPaint
        {
            Color = connected ? SKColor.Parse("#A7F3D0") : SKColor.Parse("#94A3B8"),
            TextSize = Math.Max(5.5f, 0.95f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        string subTelemetry = string.IsNullOrWhiteSpace(ActiveBoardPortSummaryText)
            ? $"TARGET: {TargetProgrammerBoardInstanceId}"
            : $"{TargetProgrammerBoardInstanceId} · {ActiveBoardPortSummaryText}";
        if (subTelemetry.Length > 28)
            subTelemetry = subTelemetry[..28];
        canvas.DrawText(subTelemetry, progRect.MidX, progRect.Top + (5.0f * pxPerMm), subPaint);

        // Status LED Array: PWR, LINK, TX, RX
        float ledRowY = progRect.Top + (7.6f * pxPerMm);
        float ledSpacing = progWidth * 0.20f;
        float ledStartX = progRect.MidX - (1.5f * ledSpacing);
        bool txActive = IsProgrammerUploading || (firmwareLive && ((ActiveFirmwareState!.TickCount % 2) == 0));
        bool rxActive = firmwareLive && ((ActiveFirmwareState!.TickCount % 3) == 1);

        DrawProgrammerLed(canvas, new SKPoint(ledStartX, ledRowY), "PWR", true, new SKColor(0x10, 0xB9, 0x81), pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + ledSpacing, ledRowY), "LINK", connected, neonGreenCyan, pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + (ledSpacing * 2f), ledRowY), "TX", txActive, new SKColor(0xF5, 0x9E, 0x0B), pxPerMm);
        DrawProgrammerLed(canvas, new SKPoint(ledStartX + (ledSpacing * 3f), ledRowY), "RX", rxActive, new SKColor(0x34, 0xD3, 0x99), pxPerMm);

        // Active USB Cable Telemetry Pill
        float pillTop = progRect.Top + (10.7f * pxPerMm);
        float pillH = 3.5f * pxPerMm;
        var cablePill = new SKRect(progRect.Left + (2.0f * pxPerMm), pillTop, progRect.Right - (2.0f * pxPerMm), pillTop + pillH);
        using var pillBg = new SKPaint { Color = SKColor.Parse("#0F172A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var pillBorder = new SKPaint
        {
            Color = IsCableMismatchWarning ? new SKColor(0xF5, 0x9E, 0x0B) : accentColor.WithAlpha(205),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.35f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(cablePill, 0.7f * pxPerMm, 0.7f * pxPerMm, pillBg);
        canvas.DrawRoundRect(cablePill, 0.7f * pxPerMm, 0.7f * pxPerMm, pillBorder);

        using var pillText = new SKPaint
        {
            Color = IsCableMismatchWarning ? new SKColor(0xFB, 0xBF, 0x24) : (connected ? neonGreenCyan : accentColor),
            TextSize = Math.Max(6.0f, 1.05f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        string statusTag = connected ? "CONNECTED" : (IsCableMismatchWarning ? "MISMATCH" : "UNPLUGGED");
        canvas.DrawText($"{cableSpec.ShortTag} · {statusTag}", cablePill.MidX, cablePill.MidY + (0.38f * pxPerMm), pillText);

        // Flash progress bar
        float barTop = cablePill.Bottom + (1.4f * pxPerMm);
        var barTrack = new SKRect(progRect.Left + (2.0f * pxPerMm), barTop, progRect.Right - (2.0f * pxPerMm), barTop + (1.7f * pxPerMm));
        using var barTrackPaint = new SKPaint { Color = SKColor.Parse("#1E293B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(barTrack, 0.5f * pxPerMm, 0.5f * pxPerMm, barTrackPaint);

        float fillFraction = IsProgrammerUploading
            ? Math.Clamp(ProgrammerUploadProgress, 0.05f, 1.0f)
            : firmwareLive ? 1.0f : (connected ? 1.0f : 0f);

        if (fillFraction > 0f)
        {
            var barFill = new SKRect(barTrack.Left, barTrack.Top, barTrack.Left + (barTrack.Width * fillFraction), barTrack.Bottom);
            using var barFillPaint = new SKPaint
            {
                Color = IsProgrammerUploading ? new SKColor(0xF5, 0x9E, 0x0B) : neonGreenCyan,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(barFill, 0.5f * pxPerMm, 0.5f * pxPerMm, barFillPaint);
        }

        // Clickable Action Button at bottom of Programmer Chassis:
        // - LastProgrammerUploadButtonRect stays Empty when disconnected (preserving existing regression assertions)
        // - LastProgrammerSendButtonRect is ALWAYS active so user can open Send/IDE Programmer dialog anytime
        float pad = 1.8f * pxPerMm;
        float btnH = Math.Max(13.5f, 4.7f * pxPerMm);
        var btnRect = new SKRect(progRect.Left + pad, progRect.Bottom - btnH - pad, progRect.Right - pad, progRect.Bottom - pad);

        LastProgrammerUploadButtonRect = connected ? btnRect : SKRect.Empty;
        LastProgrammerSendButtonRect = btnRect;

        SKColor btnFillColor = !connected
            ? SKColor.Parse("#111C2D")
            : IsProgrammerUploading
                ? SKColor.Parse("#78350F")
                : SKColor.Parse("#064E3B");
        SKColor btnBorderColor = !connected
            ? SKColor.Parse("#38BDF8")
            : IsProgrammerUploading
                ? SKColor.Parse("#FBBF24")
                : neonGreenCyan;
        SKColor btnTextColor = connected ? SKColors.White : SKColor.Parse("#7DD3FC");
        float btnStrokeWidth = (connected ? 0.55f : 0.38f) * pxPerMm;
        float btnTextSize = connected ? Math.Max(6.4f, 1.06f * pxPerMm) : Math.Max(5.4f, 0.84f * pxPerMm);
        string btnCaption = !connected
            ? "PLUG CABLE TO PORT"
            : IsProgrammerUploading
                ? $"FLASHING {Math.Round(ProgrammerUploadProgress * 100):0}%"
                : firmwareLive
                    ? "SEND / RE-UPLOAD"
                    : "SEND / UPLOAD FW";

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
            Typeface = _uiBoldTypeface,
            TextAlign = SKTextAlign.Left,
            IsAntialias = true
        };
        float textWidth = btnLabelPaint.MeasureText(btnCaption);
        float iconSize = 1.9f * pxPerMm;
        float iconGap = 0.85f * pxPerMm;
        float maxInnerWidth = btnRect.Width - (1.8f * pxPerMm);
        if (iconSize + iconGap + textWidth > maxInnerWidth && textWidth > 1f)
        {
            float scaleFit = MathF.Max(0.70f, (maxInnerWidth - iconSize - iconGap) / textWidth);
            btnLabelPaint.TextSize *= scaleFit;
            textWidth = btnLabelPaint.MeasureText(btnCaption);
        }

        float groupLeft = btnRect.MidX - ((iconSize + iconGap + textWidth) * 0.5f);
        float iconCx = groupLeft + (iconSize * 0.5f);
        float iconCy = btnRect.MidY;
        using var iconPaint = new SKPaint
        {
            Color = btnTextColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 0.34f * pxPerMm),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };
        using var iconPath = new SKPath();
        if (!connected)
        {
            // Plug + downward chevron pointing toward 2.5D Cable Rack
            float r = iconSize * 0.42f;
            iconPath.MoveTo(iconCx - (r * 0.45f), iconCy - (r * 0.92f));
            iconPath.LineTo(iconCx - (r * 0.45f), iconCy - (r * 0.32f));
            iconPath.MoveTo(iconCx + (r * 0.45f), iconCy - (r * 0.92f));
            iconPath.LineTo(iconCx + (r * 0.45f), iconCy - (r * 0.32f));
            iconPath.MoveTo(iconCx, iconCy - (r * 0.32f));
            iconPath.LineTo(iconCx, iconCy + (r * 0.84f));
            iconPath.MoveTo(iconCx - (r * 0.72f), iconCy + (r * 0.16f));
            iconPath.LineTo(iconCx, iconCy + (r * 0.88f));
            iconPath.LineTo(iconCx + (r * 0.72f), iconCy + (r * 0.16f));
        }
        else
        {
            // Upload arrow + dock tray
            float r = iconSize * 0.44f;
            iconPath.MoveTo(iconCx, iconCy + (r * 0.44f));
            iconPath.LineTo(iconCx, iconCy - (r * 0.88f));
            iconPath.MoveTo(iconCx - (r * 0.72f), iconCy - (r * 0.16f));
            iconPath.LineTo(iconCx, iconCy - (r * 0.90f));
            iconPath.LineTo(iconCx + (r * 0.72f), iconCy - (r * 0.16f));
            iconPath.MoveTo(iconCx - (r * 0.85f), iconCy + (r * 0.54f));
            iconPath.LineTo(iconCx - (r * 0.85f), iconCy + (r * 0.90f));
            iconPath.LineTo(iconCx + (r * 0.85f), iconCy + (r * 0.90f));
            iconPath.LineTo(iconCx + (r * 0.85f), iconCy + (r * 0.54f));
        }
        canvas.DrawPath(iconPath, iconPaint);
        canvas.DrawText(btnCaption, groupLeft + iconSize + iconGap, btnRect.MidY + (btnLabelPaint.TextSize * 0.34f), btnLabelPaint);

        // 5. 2.5D CABLE RACK (Mounted right underneath Hardware Programmer)
        DrawCableRack25D(canvas, rackRect, pxPerMm, connected, neonGreenCyan);

        // 6. Floating Rubber-Band Cable Drag Preview (rendered after Programmer/Rack so it is never obscured)
        if (PendingCableDragKind != UsbCableKind.None)
        {
            UsbCableSpec dragSpec = ProgrammerCatalog.GetCableSpec(PendingCableDragKind);
            SKPoint cursorEnd = CableDragCursorCanvas;
            var snapBoard = !string.IsNullOrWhiteSpace(HoveredUsbReceptacleBoardId)
                ? mcuLayouts.FirstOrDefault(m => string.Equals(m.BoardInstanceId, HoveredUsbReceptacleBoardId, StringComparison.OrdinalIgnoreCase))
                : null;
            bool isHoverSnap = snapBoard is not null &&
                               _boardUsbReceptacles.TryGetValue(HoveredUsbReceptacleBoardId!, out _);
            bool dragVerticalSnap = false;
            bool dragCompat = true;

            if (isHoverSnap && snapBoard is not null)
            {
                dragVerticalSnap = snapBoard.IsUsbReceptacleVertical;
                dragCompat = ProgrammerCatalog.IsCableCompatible(PendingCableDragKind, snapBoard.BoardType, IsCableAdapterEnabled, out _, out _);

                if (_boardUsbReceptacles.TryGetValue(HoveredUsbReceptacleBoardId!, out var sRect))
                {
                    cursorEnd = dragVerticalSnap
                        ? new SKPoint(sRect.UsbRect.MidX, sRect.UsbRect.Bottom - (0.35f * pxPerMm))
                        : new SKPoint(sRect.UsbRect.Left + (0.85f * pxPerMm), sRect.UsbRect.MidY);
                }
            }

            float dragTailOffset = (PendingCableDragKind == UsbCableKind.UsbB ? 5.6f : 5.4f) * pxPerMm;
            SKPoint dragProgTail = new(progPlugAnchor.X - dragTailOffset, progPlugAnchor.Y);
            float dragCorridorX = MathF.Max(maxRightWithLabels + (1.8f * pxPerMm), dragProgTail.X - (0.6f * pxPerMm));
            if (dragCorridorX > dragProgTail.X - (0.2f * pxPerMm))
                dragCorridorX = dragProgTail.X - (0.2f * pxPerMm);

            SKPath dragPath;
            float? floatingPlugAngleDeg = null;
            if (isHoverSnap && snapBoard is not null)
            {
                dragPath = BuildCleanUsbCablePath(
                    progPlugAnchor,
                    dragProgTail,
                    cursorEnd,
                    dragTailOffset,
                    dragVerticalSnap,
                    snapBoard,
                    breadboardRect,
                    mcuLayouts,
                    McuRenderer,
                    dragCorridorX,
                    pxPerMm);
            }
            else
            {
                // Mid-air free drag: keep vertical transition inside the empty dragCorridorX outside all MCUs
                // before bending toward the cursor, and aim the floating plug tip cleanly away from the cable tail.
                dragPath = new SKPath();
                dragPath.MoveTo(progPlugAnchor);
                dragPath.LineTo(dragProgTail);

                bool cursorLeftOfCorridor = cursorEnd.X < dragCorridorX - (2.0f * pxPerMm);
                SKPoint approachAnchor;
                if (cursorLeftOfCorridor)
                {
                    SKPoint bendCorner = new(dragCorridorX, cursorEnd.Y);
                    float dropMidY = dragProgTail.Y + ((cursorEnd.Y - dragProgTail.Y) * 0.55f);
                    SKPoint corridorMid = new(dragCorridorX, dropMidY);
                    SKPoint dragTail = new(MathF.Min(dragCorridorX - (1.0f * pxPerMm), cursorEnd.X + (5.8f * pxPerMm)), cursorEnd.Y);
                    dragPath.CubicTo(
                        new SKPoint(dragCorridorX, dragProgTail.Y),
                        new SKPoint(dragCorridorX, dragProgTail.Y + ((corridorMid.Y - dragProgTail.Y) * 0.5f)),
                        corridorMid);
                    dragPath.CubicTo(
                        bendCorner,
                        new SKPoint(dragTail.X + ((bendCorner.X - dragTail.X) * 0.45f), cursorEnd.Y),
                        dragTail);
                    dragPath.LineTo(cursorEnd);
                    approachAnchor = dragTail;
                }
                else
                {
                    float sweep = MathF.Max(6.0f * pxPerMm, SKPoint.Distance(dragProgTail, cursorEnd) * 0.35f);
                    SKPoint c1 = new(dragProgTail.X - (sweep * 0.5f), dragProgTail.Y);
                    SKPoint c2 = new(cursorEnd.X - (sweep * 0.45f), cursorEnd.Y);
                    dragPath.CubicTo(c1, c2, cursorEnd);
                    approachAnchor = c2;
                }

                float dx = cursorEnd.X - approachAnchor.X;
                float dy = cursorEnd.Y - approachAnchor.Y;
                if ((dx * dx) + (dy * dy) > 1.0f)
                    floatingPlugAngleDeg = MathF.Atan2(dy, dx) * (180f / MathF.PI);
                else if (cursorLeftOfCorridor)
                    floatingPlugAngleDeg = 180f;
            }

            using (dragPath)
            {
                SKColor dragAccent = dragCompat ? neonGreenCyan : new SKColor(0xEF, 0x44, 0x44);
                DrawRealisticUsbCablePath(canvas, dragPath, dragSpec, pxPerMm, connected: dragCompat, uploading: false, tickMs, pulse01);

                DrawRealisticPlugBarrel(
                    canvas,
                    progPlugAnchor,
                    PendingCableDragKind,
                    dragSpec.ShellStyleId,
                    isVerticalBottomPort: false,
                    isBoardEnd: false,
                    pxPerMm,
                    connected: dragCompat,
                    dragAccent);

                DrawRealisticPlugBarrel(
                    canvas,
                    cursorEnd,
                    PendingCableDragKind,
                    dragSpec.ShellStyleId,
                    isVerticalBottomPort: isHoverSnap && dragVerticalSnap,
                    isBoardEnd: true,
                    pxPerMm,
                    connected: dragCompat,
                    dragAccent,
                    overrideAngleDeg: isHoverSnap ? null : floatingPlugAngleDeg);
            }
        }
    }

    private void DrawCableRack25D(
        SKCanvas canvas,
        SKRect rackRect,
        float pxPerMm,
        bool isCableConnected,
        SKColor neonGreenCyan)
    {
        using var rackBgShader = SKShader.CreateLinearGradient(
            new SKPoint(rackRect.Left, rackRect.Top),
            new SKPoint(rackRect.Left, rackRect.Bottom),
            new[] { SKColor.Parse("#18202C"), SKColor.Parse("#0E141F") },
            null,
            SKShaderTileMode.Clamp);
        using var rackBg = new SKPaint
        {
            Shader = rackBgShader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var rackBorder = new SKPaint
        {
            Color = SKColor.Parse("#2D3B4E"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.4f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rackRect, 1.3f * pxPerMm, 1.3f * pxPerMm, rackBg);
        canvas.DrawRoundRect(rackRect, 1.3f * pxPerMm, 1.3f * pxPerMm, rackBorder);

        // Rack Title + [ADAPTER] toggle pill
        using var rackHdr = new SKPaint
        {
            Color = SKColor.Parse("#CBD5E1"),
            TextSize = Math.Max(5.8f, 1.05f * pxPerMm),
            Typeface = _uiBoldTypeface,
            TextAlign = SKTextAlign.Left,
            IsAntialias = true
        };
        canvas.DrawText("2.5D CABLES (DRAG TO PORT)", rackRect.Left + (1.6f * pxPerMm), rackRect.Top + (2.55f * pxPerMm), rackHdr);

        var adapterRect = new SKRect(
            rackRect.Right - (9.8f * pxPerMm),
            rackRect.Top + (0.85f * pxPerMm),
            rackRect.Right - (1.2f * pxPerMm),
            rackRect.Top + (3.25f * pxPerMm));
        LastProgrammerAdapterToggleRect = adapterRect;

        using var adpFill = new SKPaint
        {
            Color = IsCableAdapterEnabled ? SKColor.Parse("#78350F") : SKColor.Parse("#1E293B"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var adpStroke = new SKPaint
        {
            Color = IsCableAdapterEnabled ? new SKColor(0xFB, 0xBF, 0x24) : SKColor.Parse("#475569"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.3f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawRoundRect(adapterRect, 0.55f * pxPerMm, 0.55f * pxPerMm, adpFill);
        canvas.DrawRoundRect(adapterRect, 0.55f * pxPerMm, 0.55f * pxPerMm, adpStroke);

        using var adpTxt = new SKPaint
        {
            Color = IsCableAdapterEnabled ? new SKColor(0xFD, 0xE6, 0x8A) : SKColor.Parse("#94A3B8"),
            TextSize = Math.Max(5.0f, 0.80f * pxPerMm),
            Typeface = _monoTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(IsCableAdapterEnabled ? "ADPT ON" : "ADAPTER", adapterRect.MidX, adapterRect.MidY + (0.28f * pxPerMm), adpTxt);

        // 3 tactile cable bays
        UsbCableKind[] bayOrder = { UsbCableKind.UsbB, UsbCableKind.UsbTypeC, UsbCableKind.MicroUsb };
        float bayLeft = rackRect.Left + (1.4f * pxPerMm);
        float bayRight = rackRect.Right - (1.4f * pxPerMm);
        float bayStartY = rackRect.Top + (4.1f * pxPerMm);
        float bayGap = 1.15f * pxPerMm;
        float bayH = (rackRect.Bottom - bayStartY - (1.3f * pxPerMm) - (bayGap * 2f)) / 3f;

        for (int i = 0; i < bayOrder.Length; i++)
        {
            UsbCableKind kind = bayOrder[i];
            UsbCableSpec spec = ProgrammerCatalog.GetCableSpec(kind);
            float top = bayStartY + (i * (bayH + bayGap));
            var cardRect = new SKRect(bayLeft, top, bayRight, top + bayH);
            _cableRackCardRects[kind] = cardRect;

            bool isSelected = ActiveUsbCableKind == kind;
            bool isDragged = PendingCableDragKind == kind;
            bool isHovered = HoveredCableRackKind == kind;
            SKColor accent = TryParseColor(spec.AccentHex, new SKColor(0x00, 0xE5, 0xFF));

            using var cardBg = new SKPaint
            {
                Color = isDragged
                    ? SKColor.Parse("#1E293B")
                    : isSelected && isCableConnected
                        ? SKColor.Parse("#082F2E")
                        : isSelected
                            ? SKColor.Parse("#172536")
                            : SKColor.Parse("#0D131D"),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var cardBorder = new SKPaint
            {
                Color = isSelected && isCableConnected
                    ? neonGreenCyan
                    : (isDragged || isHovered)
                        ? SKColors.White
                        : isSelected
                            ? accent
                            : SKColor.Parse("#263447"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (isSelected || isHovered ? 0.45f : 0.28f) * pxPerMm,
                IsAntialias = true
            };
            canvas.DrawRoundRect(cardRect, 0.8f * pxPerMm, 0.8f * pxPerMm, cardBg);
            canvas.DrawRoundRect(cardRect, 0.8f * pxPerMm, 0.8f * pxPerMm, cardBorder);

            // Mini 2.5D coiled cable graphic inside left 34% of bay card
            float glyphLeft = cardRect.Left + (1.1f * pxPerMm);
            float glyphRight = cardRect.Left + (cardRect.Width * 0.34f);
            float glyphMidY = cardRect.MidY;
            DrawMiniRackCableGlyph(canvas, new SKRect(glyphLeft, cardRect.Top + (0.7f * pxPerMm), glyphRight, cardRect.Bottom - (0.7f * pxPerMm)), spec, pxPerMm);

            // Title & Connector Badge inside right 64%
            float textX = glyphRight + (1.2f * pxPerMm);
            string shortTitle = kind switch
            {
                UsbCableKind.UsbB => "USB-A / USB-B",
                UsbCableKind.UsbTypeC => "ANKER 100W C",
                UsbCableKind.MicroUsb => "MICRO-USB",
                _ => spec.ShortTag
            };
            string compatLine = kind switch
            {
                UsbCableKind.UsbB => "Black/Orange · Uno/Mega",
                UsbCableKind.UsbTypeC => "Braided · S2 Mini",
                UsbCableKind.MicroUsb => "ESP32 DevKit / STM32",
                _ => string.Empty
            };

            using var namePaint = new SKPaint
            {
                Color = isSelected && isCableConnected ? neonGreenCyan : SKColor.Parse("#F1F5F9"),
                TextSize = Math.Max(5.8f, 0.96f * pxPerMm),
                Typeface = _uiBoldTypeface,
                TextAlign = SKTextAlign.Left,
                IsAntialias = true
            };
            canvas.DrawText(shortTitle, textX, glyphMidY - (0.25f * pxPerMm), namePaint);

            using var subCardPaint = new SKPaint
            {
                Color = SKColor.Parse("#94A3B8"),
                TextSize = Math.Max(4.8f, 0.74f * pxPerMm),
                Typeface = _monoTypeface,
                TextAlign = SKTextAlign.Left,
                IsAntialias = true
            };
            canvas.DrawText(compatLine, textX, glyphMidY + (1.55f * pxPerMm), subCardPaint);

            // State dot
            SKColor dotColor = isSelected && isCableConnected
                ? neonGreenCyan
                : isSelected
                    ? accent
                    : SKColor.Parse("#334155");
            using var dotPaint = new SKPaint { Color = dotColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(cardRect.Right - (1.6f * pxPerMm), cardRect.Top + (1.5f * pxPerMm), 0.52f * pxPerMm, dotPaint);
        }
    }

    private static void DrawMiniRackCableGlyph(SKCanvas canvas, SKRect box, UsbCableSpec spec, float pxPerMm)
    {
        // Mini 2.5D coiled cable + recognizable twin/primary connector inside the bay
        float loopW = box.Width * 0.54f;
        float loopH = box.Height * 0.78f;
        var loopRect = new SKRect(box.Left + (0.45f * pxPerMm), box.MidY - (loopH * 0.5f), box.Left + loopW, box.MidY + (loopH * 0.5f));
        var innerLoopRect = new SKRect(
            loopRect.Left + (0.55f * pxPerMm),
            loopRect.Top + (0.55f * pxPerMm),
            loopRect.Right - (0.35f * pxPerMm),
            loopRect.Bottom - (0.55f * pxPerMm));

        if (spec.ShellStyleId == "anker_100w_braided")
        {
            using var braidBase = new SKPaint
            {
                Color = SKColor.Parse("#1E222B"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.85f * pxPerMm,
                IsAntialias = true
            };
            using var silverWeaveDash = SKPathEffect.CreateDash(new[] { 0.65f * pxPerMm, 0.70f * pxPerMm }, 0f);
            using var silverWeave = new SKPaint
            {
                Color = SKColor.Parse("#D1D5DB"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.52f * pxPerMm,
                PathEffect = silverWeaveDash,
                IsAntialias = true
            };
            canvas.DrawOval(loopRect, braidBase);
            canvas.DrawOval(innerLoopRect, braidBase);
            canvas.DrawOval(loopRect, silverWeave);
            canvas.DrawOval(innerLoopRect, silverWeave);

            // Stepped 100W metallic neck collar + charcoal ANKER barrel + rounded double-sided Type-C nose
            var collar = new SKRect(loopRect.Right - (0.2f * pxPerMm), box.MidY - (0.62f * pxPerMm), loopRect.Right + (0.75f * pxPerMm), box.MidY + (0.62f * pxPerMm));
            var barrel = new SKRect(collar.Right - (0.1f * pxPerMm), box.MidY - (1.05f * pxPerMm), box.Right - (1.25f * pxPerMm), box.MidY + (1.05f * pxPerMm));
            using var collarPaint = new SKPaint { Color = SKColor.Parse("#1F2937"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var brFill = new SKPaint { Color = SKColor.Parse("#374151"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var silverBand = new SKPaint { Color = SKColor.Parse("#E5E7EB"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var tipFill = new SKPaint { Color = SKColor.Parse("#CBD5E1"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(collar, 0.2f * pxPerMm, 0.2f * pxPerMm, collarPaint);
            canvas.DrawRoundRect(barrel, 0.35f * pxPerMm, 0.35f * pxPerMm, brFill);
            canvas.DrawRect(barrel.Left + (0.28f * pxPerMm), barrel.Top + (0.18f * pxPerMm), 0.36f * pxPerMm, barrel.Height - (0.36f * pxPerMm), silverBand);
            canvas.DrawRect(barrel.Left + (0.85f * pxPerMm), box.MidY - (0.16f * pxPerMm), barrel.Width - (1.20f * pxPerMm), 0.32f * pxPerMm, silverBand);
            canvas.DrawRoundRect(new SKRect(barrel.Right - (0.1f * pxPerMm), box.MidY - (0.48f * pxPerMm), box.Right - (0.15f * pxPerMm), box.MidY + (0.48f * pxPerMm)), 0.26f * pxPerMm, 0.26f * pxPerMm, tipFill);
        }
        else if (spec.ShellStyleId == "black_orange_emblem")
        {
            using var glossyBlack = new SKPaint
            {
                Color = SKColor.Parse("#0B0E14"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.90f * pxPerMm,
                IsAntialias = true
            };
            using var rimShine = new SKPaint
            {
                Color = SKColor.Parse("#9CA3AF"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.24f * pxPerMm,
                IsAntialias = true
            };
            canvas.DrawOval(loopRect, glossyBlack);
            canvas.DrawOval(innerLoopRect, glossyBlack);
            canvas.DrawOval(new SKRect(loopRect.Left + (0.12f * pxPerMm), loopRect.Top + (0.12f * pxPerMm), loopRect.Right - (0.12f * pxPerMm), loopRect.Bottom - (0.12f * pxPerMm)), rimShine);

            // Stacked Type-A (top) & Type-B (bottom) charcoal molded barrels each with vivid #FF6B00 orange oval emblem
            using var brFill = new SKPaint { Color = SKColor.Parse("#27272A"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var orangeEmblem = new SKPaint { Color = SKColor.Parse("#FF6B00"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var tipFill = new SKPaint { Color = SKColor.Parse("#D1D5DB"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var slotFill = new SKPaint { Color = SKColor.Parse("#334155"), Style = SKPaintStyle.Fill, IsAntialias = true };

            float plugsLeft = loopRect.Right - (0.15f * pxPerMm);
            float plugBarRight = box.Right - (1.25f * pxPerMm);

            // Top: Rectangular Type-A plug
            float topCy = box.MidY - (1.05f * pxPerMm);
            var barrelA = new SKRect(plugsLeft, topCy - (0.72f * pxPerMm), plugBarRight, topCy + (0.72f * pxPerMm));
            canvas.DrawRoundRect(barrelA, 0.22f * pxPerMm, 0.22f * pxPerMm, brFill);
            canvas.DrawOval(new SKRect(barrelA.Left + (0.65f * pxPerMm), topCy - (0.38f * pxPerMm), barrelA.Right - (0.55f * pxPerMm), topCy + (0.38f * pxPerMm)), orangeEmblem);
            var tipA = new SKRect(barrelA.Right, topCy - (0.50f * pxPerMm), box.Right - (0.15f * pxPerMm), topCy + (0.50f * pxPerMm));
            canvas.DrawRect(tipA, tipFill);
            canvas.DrawRect(tipA.Left + (0.35f * pxPerMm), topCy - (0.28f * pxPerMm), 0.35f * pxPerMm, 0.22f * pxPerMm, slotFill);
            canvas.DrawRect(tipA.Left + (0.35f * pxPerMm), topCy + (0.06f * pxPerMm), 0.35f * pxPerMm, 0.22f * pxPerMm, slotFill);

            // Bottom: Beveled Square Type-B plug
            float botCy = box.MidY + (1.05f * pxPerMm);
            var barrelB = new SKRect(plugsLeft, botCy - (0.82f * pxPerMm), plugBarRight, botCy + (0.82f * pxPerMm));
            canvas.DrawRoundRect(barrelB, 0.25f * pxPerMm, 0.25f * pxPerMm, brFill);
            canvas.DrawOval(new SKRect(barrelB.Left + (0.65f * pxPerMm), botCy - (0.42f * pxPerMm), barrelB.Right - (0.55f * pxPerMm), botCy + (0.42f * pxPerMm)), orangeEmblem);
            canvas.DrawRect(barrelB.Right, botCy - (0.58f * pxPerMm), 1.05f * pxPerMm, 1.16f * pxPerMm, tipFill);
        }
        else
        {
            using var microCoil = new SKPaint
            {
                Color = SKColor.Parse("#1F2937"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.8f * pxPerMm,
                IsAntialias = true
            };
            using var microAccent = new SKPaint
            {
                Color = SKColor.Parse("#00BCD4"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.25f * pxPerMm,
                IsAntialias = true
            };
            canvas.DrawOval(loopRect, microCoil);
            canvas.DrawOval(loopRect, microAccent);

            var barrel = new SKRect(loopRect.Right - (0.2f * pxPerMm), box.MidY - (0.85f * pxPerMm), box.Right - (1.2f * pxPerMm), box.MidY + (0.85f * pxPerMm));
            using var brFill = new SKPaint { Color = SKColor.Parse("#1E293B"), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var tipFill = new SKPaint { Color = SKColor.Parse("#94A3B8"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(barrel, 0.3f * pxPerMm, 0.3f * pxPerMm, brFill);
            canvas.DrawRect(barrel.Right, box.MidY - (0.45f * pxPerMm), 0.9f * pxPerMm, 0.9f * pxPerMm, tipFill);
        }
    }

    private static SKPath BuildCleanUsbCablePath(
        SKPoint progPlugAnchor,
        SKPoint progTail,
        SKPoint targetAnchor,
        float tailOffset,
        bool verticalPort,
        RenderedMicrocontrollerLayout targetMcu,
        SKRect breadboardRect,
        IReadOnlyList<RenderedMicrocontrollerLayout> mcuLayouts,
        OffBoardMicrocontrollerRenderer mcuRenderer,
        float rightCorridorX,
        float pxPerMm)
    {
        var cablePath = new SKPath();
        cablePath.MoveTo(progPlugAnchor);
        cablePath.LineTo(progTail);

        if (verticalPort)
        {
            // Vertical bottom USB-C receptacle (e.g., Wemos S2 Mini):
            // Exit stepped 100W collar downward (+Y), stay cleanly clear of board caption and any sibling
            // BottomMargin boards between boardTail.X and rightCorridorX, then climb up into programmer jack (-X -> +X).
            SKPoint boardTail = new(targetAnchor.X, targetAnchor.Y + (5.4f * pxPerMm));
            float obsBottom = boardTail.Y;
            float firstBlockerLeft = rightCorridorX;
            bool blockedLaterally = false;
            foreach (RenderedMicrocontrollerLayout other in mcuLayouts)
            {
                if (ReferenceEquals(other, targetMcu))
                    continue;

                McuLabelExtents otherExt = mcuRenderer.MeasureLabelExtents(other.BoardType, pxPerMm);
                float otherBottomWithCaption = other.BoardBoundsCanvas.Bottom + otherExt.BottomPx;
                float otherLeftBound = other.BoardBoundsCanvas.Left - otherExt.LeftPx;
                if (other.BoardBoundsCanvas.Right >= boardTail.X - (4.0f * pxPerMm)
                    && other.BoardBoundsCanvas.Left <= rightCorridorX
                    && other.BoardBoundsCanvas.Top <= boardTail.Y + (12.0f * pxPerMm)
                    && otherBottomWithCaption >= boardTail.Y - (3.0f * pxPerMm))
                {
                    blockedLaterally = true;
                    obsBottom = MathF.Max(obsBottom, otherBottomWithCaption);
                    firstBlockerLeft = MathF.Min(firstBlockerLeft, otherLeftBound);
                }
            }

            if (blockedLaterally)
            {
                // First sweep rightward strictly ABOVE targetMcu's bottom caption into the open gap
                // between targetMcu and the sibling board, then drop below obsBottom and sweep right.
                McuLabelExtents targetExt = mcuRenderer.MeasureLabelExtents(targetMcu.BoardType, pxPerMm);
                float targetRightClear = MathF.Max(
                    boardTail.X + (14.5f * pxPerMm),
                    targetMcu.BoardBoundsCanvas.Right + targetExt.RightPx + (2.2f * pxPerMm));
                float gapLaneX = firstBlockerLeft - (2.6f * pxPerMm) >= targetRightClear - (2.0f * pxPerMm)
                    ? MathF.Min(firstBlockerLeft - (2.6f * pxPerMm), MathF.Max(targetRightClear, (targetMcu.BoardBoundsCanvas.Right + firstBlockerLeft) * 0.5f))
                    : MathF.Max(boardTail.X + (15.0f * pxPerMm), firstBlockerLeft - (2.0f * pxPerMm));

                float topTurnY = boardTail.Y + (2.7f * pxPerMm);
                float underY = obsBottom + (6.8f * pxPerMm);
                float cornerR = MathF.Min(5.2f * pxPerMm, MathF.Max(2.0f * pxPerMm, (rightCorridorX - gapLaneX) * 0.25f));
                float topR = MathF.Min(3.2f * pxPerMm, MathF.Max(1.4f * pxPerMm, (gapLaneX - boardTail.X) * 0.35f));

                SKPoint topTurnMid = new((boardTail.X + gapLaneX) * 0.5f, topTurnY);
                SKPoint gapTopExit = new(gapLaneX, MathF.Min(underY - cornerR, topTurnY + topR));
                SKPoint gapDropBot = new(gapLaneX, MathF.Max(gapTopExit.Y + (1.0f * pxPerMm), underY - cornerR));
                SKPoint underLeft = new(gapLaneX + cornerR, underY);
                SKPoint underRight = new(rightCorridorX - cornerR, underY);
                SKPoint rightDrop = new(rightCorridorX, MathF.Max(progTail.Y + (3.5f * pxPerMm), underY - cornerR));

                cablePath.CubicTo(
                    new SKPoint(rightCorridorX, progTail.Y),
                    new SKPoint(rightCorridorX, progTail.Y + ((rightDrop.Y - progTail.Y) * 0.55f)),
                    rightDrop);
                cablePath.CubicTo(
                    new SKPoint(rightCorridorX, underY),
                    new SKPoint(underRight.X + (cornerR * 0.45f), underY),
                    underRight);
                cablePath.LineTo(underLeft);
                cablePath.CubicTo(
                    new SKPoint(underLeft.X - (cornerR * 0.45f), underY),
                    new SKPoint(gapLaneX, underY),
                    gapDropBot);
                cablePath.LineTo(gapTopExit);
                cablePath.CubicTo(
                    new SKPoint(gapLaneX, topTurnY),
                    new SKPoint(topTurnMid.X + ((gapLaneX - topTurnMid.X) * 0.45f), topTurnY),
                    topTurnMid);
                cablePath.CubicTo(
                    new SKPoint(boardTail.X, topTurnY),
                    new SKPoint(boardTail.X, boardTail.Y + (2.0f * pxPerMm)),
                    boardTail);
            }
            else
            {
                float turnY = boardTail.Y + (2.9f * pxPerMm);
                float sideMidY = turnY <= progTail.Y + (4.0f * pxPerMm)
                    ? progTail.Y + (3.0f * pxPerMm)
                    : progTail.Y + ((turnY - progTail.Y) * 0.52f);

                SKPoint sideMid = new(rightCorridorX, sideMidY);
                SKPoint bottomTurn = new((boardTail.X + rightCorridorX) * 0.52f, turnY);

                cablePath.CubicTo(
                    new SKPoint(rightCorridorX, progTail.Y),
                    new SKPoint(rightCorridorX, progTail.Y + ((sideMid.Y - progTail.Y) * 0.5f)),
                    sideMid);
                cablePath.CubicTo(
                    new SKPoint(rightCorridorX, turnY),
                    new SKPoint(bottomTurn.X + ((rightCorridorX - bottomTurn.X) * 0.45f), turnY),
                    bottomTurn);
                cablePath.CubicTo(
                    new SKPoint(boardTail.X, turnY),
                    new SKPoint(boardTail.X, boardTail.Y + (2.1f * pxPerMm)),
                    boardTail);
            }

            cablePath.LineTo(targetAnchor);
        }
        else
        {
            // Left-edge horizontal USB-B / Micro-USB receptacle:
            // Exit board strain-relief boot straight LEFT (-X), route beneath PCB/headers/board caption,
            // climb rightCorridorX outside right PCB edges, and enter programmer jack horizontally (-X -> +X).
            SKPoint boardTail = new(targetAnchor.X - tailOffset, targetAnchor.Y);
            float preferredLeft = targetMcu.BoardBoundsCanvas.Left > breadboardRect.Right
                ? MathF.Max(breadboardRect.Right + (2.2f * pxPerMm), boardTail.X - (4.2f * pxPerMm))
                : boardTail.X - (4.6f * pxPerMm);
            float leftCorridorX = MathF.Min(boardTail.X - (2.4f * pxPerMm), preferredLeft);

            McuLabelExtents targetExtents = mcuRenderer.MeasureLabelExtents(targetMcu.BoardType, pxPerMm);
            float obsBottom = targetMcu.BoardBoundsCanvas.Bottom + targetExtents.BottomPx;
            foreach (RenderedMicrocontrollerLayout other in mcuLayouts)
            {
                if (other.BoardBoundsCanvas.Right >= leftCorridorX && other.BoardBoundsCanvas.Left <= rightCorridorX)
                {
                    McuLabelExtents otherExt = mcuRenderer.MeasureLabelExtents(other.BoardType, pxPerMm);
                    obsBottom = MathF.Max(obsBottom, other.BoardBoundsCanvas.Bottom + otherExt.BottomPx);
                }
            }
            float underY = MathF.Max(obsBottom + (7.4f * pxPerMm), MathF.Max(progTail.Y, boardTail.Y) + (12.0f * pxPerMm));
            float cornerR = MathF.Min(6.5f * pxPerMm, (rightCorridorX - leftCorridorX) * 0.25f);
            float rightTopY = MathF.Max(progTail.Y + (3.5f * pxPerMm), underY - cornerR);
            float leftTopY = MathF.Max(boardTail.Y + (3.5f * pxPerMm), underY - cornerR);

            SKPoint rightDropStart = new(rightCorridorX, progTail.Y + ((rightTopY - progTail.Y) * 0.45f));
            SKPoint rightCornerTop = new(rightCorridorX, rightTopY);
            SKPoint bottomRight = new(rightCorridorX - cornerR, underY);
            SKPoint bottomLeft = new(leftCorridorX + cornerR, underY);
            SKPoint leftCornerTop = new(leftCorridorX, leftTopY);

            cablePath.CubicTo(
                new SKPoint(rightCorridorX, progTail.Y),
                new SKPoint(rightCorridorX, progTail.Y + ((rightDropStart.Y - progTail.Y) * 0.55f)),
                rightDropStart);
            cablePath.LineTo(rightCornerTop);
            cablePath.CubicTo(
                new SKPoint(rightCorridorX, underY),
                new SKPoint(bottomRight.X + (cornerR * 0.45f), underY),
                bottomRight);
            cablePath.LineTo(bottomLeft);
            cablePath.CubicTo(
                new SKPoint(bottomLeft.X - (cornerR * 0.45f), underY),
                new SKPoint(leftCorridorX, underY),
                leftCornerTop);
            cablePath.CubicTo(
                new SKPoint(leftCorridorX, MathF.Max(boardTail.Y, leftTopY - ((leftTopY - boardTail.Y) * 0.55f))),
                new SKPoint(leftCorridorX, boardTail.Y),
                boardTail);
            cablePath.LineTo(targetAnchor);
        }

        return cablePath;
    }

    private static void DrawRealisticUsbCablePath(
        SKCanvas canvas,
        SKPath cablePath,
        UsbCableSpec spec,
        float pxPerMm,
        bool connected,
        bool uploading,
        long tickMs,
        float pulse01)
    {
        SKColor accentColor = TryParseColor(spec.AccentHex, new SKColor(0x00, 0xE5, 0xFF));
        SKColor baseJacket = spec.ShellStyleId switch
        {
            "black_orange_emblem" => SKColor.Parse("#0B0D12"),
            "anker_100w_braided" => SKColor.Parse("#232730"),
            _ => SKColor.Parse("#1F2937")
        };

        using var cableShadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f * pxPerMm);
        using var cableShadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 105),
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
            SKColor neonAura = new(0x00, 0xFF, 0xC8);
            using var cableGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.35f * pxPerMm);
            using var cableGlowPaint = new SKPaint
            {
                Color = (uploading ? accentColor : neonAura).WithAlpha((byte)(85 + (75 * pulse01))),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3.4f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true,
                MaskFilter = cableGlowBlur
            };
            canvas.DrawPath(cablePath, cableGlowPaint);
        }

        using var jacketPaint = new SKPaint
        {
            Color = connected ? baseJacket : SKColor.Parse("#374151"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.75f * pxPerMm,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawPath(cablePath, jacketPaint);

        if (spec.ShellStyleId == "anker_100w_braided")
        {
            // Signature Anker 100W black & silver diamond braided sleeve texture along path
            using var silverBraidDash = SKPathEffect.CreateDash(new[] { 1.15f * pxPerMm, 1.25f * pxPerMm }, 0f);
            using var silverBraid = new SKPaint
            {
                Color = new SKColor(0xD1, 0xD5, 0xDB, connected ? (byte)165 : (byte)95),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.20f * pxPerMm,
                StrokeCap = SKStrokeCap.Butt,
                PathEffect = silverBraidDash,
                IsAntialias = true
            };
            using var innerCoreDash = SKPathEffect.CreateDash(new[] { 0.7f * pxPerMm, 1.7f * pxPerMm }, 0.6f * pxPerMm);
            using var innerCore = new SKPaint
            {
                Color = new SKColor(0x11, 0x13, 0x18, 175),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.55f * pxPerMm,
                PathEffect = innerCoreDash,
                IsAntialias = true
            };
            canvas.DrawPath(cablePath, silverBraid);
            canvas.DrawPath(cablePath, innerCore);
        }
        else if (spec.ShellStyleId == "black_orange_emblem")
        {
            // Glossy pitch-black PVC jacket with twin highlight sheen and faint orange accent trim
            using var glossRail = new SKPaint
            {
                Color = new SKColor(255, 255, 255, connected ? (byte)95 : (byte)45),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.42f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };
            canvas.DrawPath(cablePath, glossRail);
        }
        else
        {
            using var highlightPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, connected ? (byte)75 : (byte)35),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.45f * pxPerMm,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };
            canvas.DrawPath(cablePath, highlightPaint);
        }

        // Flowing telemetry photon packets when USB is linked
        if (connected)
        {
            using var pathMeasure = new SKPathMeasure(cablePath, forceClosed: false);
            float totalLen = pathMeasure.Length;
            if (totalLen > 1f)
            {
                double speedPeriodMs = uploading ? 650.0 : 1400.0;
                float phase = (float)((tickMs % (long)speedPeriodMs) / speedPeriodMs);
                SKColor packetColor = uploading ? new SKColor(0xFB, 0xBF, 0x24) : new SKColor(0x00, 0xFF, 0xC8);
                using var packetHaloBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.1f * pxPerMm);
                using var packetHalo = new SKPaint
                {
                    Color = packetColor.WithAlpha(145),
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

    private static void DrawRealisticPlugBarrel(
        SKCanvas canvas,
        SKPoint anchor,
        UsbCableKind cableKind,
        string shellStyleId,
        bool isVerticalBottomPort,
        bool isBoardEnd,
        float pxPerMm,
        bool connected,
        SKColor accent,
        float? overrideAngleDeg = null)
    {
        canvas.Save();
        canvas.Translate(anchor.X, anchor.Y);
        float effectiveAngleDeg = overrideAngleDeg ?? (isVerticalBottomPort && isBoardEnd ? -90f : 0f);
        if (MathF.Abs(effectiveAngleDeg) > 0.01f)
            canvas.RotateDegrees(effectiveAngleDeg);

        // Local coordinate frame: Tip enters at +X (toward 0..+1.5mm), barrel extends -X (-3.5..0mm)

        float barrelLen = cableKind == UsbCableKind.UsbB ? 4.4f * pxPerMm : 4.2f * pxPerMm;
        float halfH = cableKind == UsbCableKind.UsbB ? 1.55f * pxPerMm : cableKind == UsbCableKind.MicroUsb ? 1.0f * pxPerMm : 1.50f * pxPerMm;
        var barrelRect = new SKRect(-barrelLen, -halfH, 0.4f * pxPerMm, halfH);

        SKColor barrelFill = shellStyleId == "black_orange_emblem"
            ? SKColor.Parse("#23252C")
            : shellStyleId == "anker_100w_braided"
                ? SKColor.Parse("#374151")
                : SKColor.Parse("#1E293B");

        using var bootFill = new SKPaint { Color = barrelFill, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var bootStroke = new SKPaint
        {
            Color = connected ? new SKColor(0x00, 0xFF, 0xC8) : accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.34f * pxPerMm,
            IsAntialias = true
        };
        float radius = cableKind == UsbCableKind.UsbB ? 0.3f * pxPerMm : 0.65f * pxPerMm;
        canvas.DrawRoundRect(barrelRect, radius, radius, bootFill);
        canvas.DrawRoundRect(barrelRect, radius, radius, bootStroke);

        // Stepped strain-relief boot collar at -X tail
        var bootTail = new SKRect(barrelRect.Left - (1.8f * pxPerMm), -halfH * 0.66f, barrelRect.Left + (0.2f * pxPerMm), halfH * 0.66f);
        using var tailPaint = new SKPaint { Color = SKColor.Parse("#18181B"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bootTail, 0.25f * pxPerMm, 0.25f * pxPerMm, tailPaint);

        if (shellStyleId == "black_orange_emblem")
        {
            // 3 molded relief ribs + vivid orange oval USB emblem on barrel face
            using var ribPaint = new SKPaint { Color = SKColor.Parse("#3F3F46"), StrokeWidth = 0.24f * pxPerMm, Style = SKPaintStyle.Stroke };
            for (int r = 1; r <= 3; r++)
            {
                float rx = barrelRect.Left + (0.6f * pxPerMm) + (r * 0.45f * pxPerMm);
                canvas.DrawLine(rx, barrelRect.Top + (0.25f * pxPerMm), rx, barrelRect.Bottom - (0.25f * pxPerMm), ribPaint);
            }
            var emblem = new SKRect(barrelRect.MidX - (0.2f * pxPerMm), -0.68f * pxPerMm, barrelRect.Right - (0.5f * pxPerMm), 0.68f * pxPerMm);
            using var orangePaint = new SKPaint { Color = SKColor.Parse("#FF6B00"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawOval(emblem, orangePaint);
        }
        else if (shellStyleId == "anker_100w_braided")
        {
            // Stepped brushed metallic 100W bezel ring & crisp upright ANKER engraving
            using var silverBand = new SKPaint { Color = SKColor.Parse("#D1D5DB"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(barrelRect.Left + (0.45f * pxPerMm), -halfH * 0.56f, 0.40f * pxPerMm, halfH * 1.12f, silverBand);
            float brandTextSize = Math.Max(4.0f, 0.52f * pxPerMm);
            using var brandText = new SKPaint
            {
                Color = SKColor.Parse("#F3F4F6"),
                TextSize = brandTextSize,
                Typeface = s_offBoardUiBoldTypeface,
                TextAlign = SKTextAlign.Center,
                IsAntialias = true
            };
            float brandCx = barrelRect.MidX + (0.35f * pxPerMm);
            if (isVerticalBottomPort && isBoardEnd && overrideAngleDeg is null)
            {
                // Rotate +90 inside the -90 vertical frame so ANKER always reads horizontally upright
                canvas.Save();
                canvas.Translate(brandCx, 0f);
                canvas.RotateDegrees(90f);
                canvas.DrawText("ANKER", 0f, brandTextSize * 0.34f, brandText);
                canvas.Restore();
            }
            else if (MathF.Abs(effectiveAngleDeg) > 0.01f)
            {
                float rad = effectiveAngleDeg * (MathF.PI / 180f);
                float flipDeg = MathF.Abs(effectiveAngleDeg + 90f) < 1f
                    ? 90f
                    : (MathF.Cos(rad) < -0.15f ? 180f : 0f);
                if (flipDeg != 0f)
                {
                    canvas.Save();
                    canvas.Translate(brandCx, 0f);
                    canvas.RotateDegrees(flipDeg);
                    canvas.DrawText("ANKER", 0f, brandTextSize * 0.34f, brandText);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawText("ANKER", brandCx, brandTextSize * 0.34f, brandText);
                }
            }
            else
            {
                canvas.DrawText("ANKER", brandCx, brandTextSize * 0.34f, brandText);
            }
        }

        // Brushed nickel-steel connector tip inserting into receptacle (+X)
        float tipH = cableKind == UsbCableKind.UsbB ? 1.05f * pxPerMm : 0.72f * pxPerMm;
        var tipRect = new SKRect(0.2f * pxPerMm, -tipH, 1.55f * pxPerMm, tipH);
        using var tipPaint = new SKPaint { Color = SKColor.Parse("#CBD5E1"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(tipRect, 0.2f * pxPerMm, 0.2f * pxPerMm, tipPaint);

        canvas.Restore();
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
