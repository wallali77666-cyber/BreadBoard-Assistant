// ============================================================================
// FILE: src/5_BreadboardAOI.App/Rendering/SkiaArOverlayRenderer.cs
// PURPOSE: 60 FPS Adobe Spectrum / KiCad-style CAD viewport. Draws the board
//          drawing sheet, top/left rulers, all 830 tie points, translucent red
//          X-Ray strip capsules, green dashed KiCad ratsnest vectors, component
//          boxes and a mouse-hover pin inspector.
//
// ALLOCATION POLICY: every SKPaint / SKPath / SKPathEffect is created once in
// the constructor. Per-frame string work is limited to a 4 Hz telemetry refresh
// and a hover tooltip that only rebuilds when the snapped hole changes.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using SkiaSharp;

namespace BreadboardAOI.App.Rendering;

/// <summary>Toggleable CAD layers driven by the left dock in Phase 5B.</summary>
public sealed class OverlayLayerSettings
{
    public bool ShowRulers { get; set; } = true;

    public bool ShowPinMatrixDots { get; set; } = true;

    public bool ShowXRayStrips { get; set; } = true;

    public bool ShowRatsnestArrows { get; set; } = true;

    public bool ShowComponentBoxes { get; set; }

    /// <summary>Draws each part as its physical 2D symbol instead of a CAD rectangle.</summary>
    public bool ShowRealisticParts { get; set; } = true;

    public bool ShowComponentLabels { get; set; } = true;

    /// <summary>Draws high-contrast micro pill pin labels (VCC, GND, TRIG, ECHO, A+, K-, etc.) on sensor and component pins.</summary>
    public bool ShowSensorPinLabels { get; set; } = true;

    public bool ShowHoverInspector { get; set; } = true;

    public bool ShowTelemetry { get; set; } = true;

    public bool ShowTitleBlock { get; set; } = true;

    public bool ShowLegend { get; set; } = true;

    /// <summary>Off by default: the vector board is the CAD drawing, not a photo.</summary>
    public bool ShowCameraUnderlay { get; set; }

    /// <summary>Draws all 126 conductive clips faintly so the board reads as an X-ray.</summary>
    public bool ShowAllStrips { get; set; }

    /// <summary>Renders dynamic 16x2 I2C LCD character display simulation (blue backlight + dot matrix text) when enabled.</summary>
    public bool ShowLcdSimulation { get; set; } = true;

    /// <summary>Live simulated text for 16x2 LCD Row 0.</summary>
    public string LcdSimulatedRow0 { get; set; } = "Breadboard AOI";

    /// <summary>Live simulated text for 16x2 LCD Row 1.</summary>
    public string LcdSimulatedRow1 { get; set; } = "LCD 1602 Active";
}

/// <summary>Result of snapping the mouse cursor through the pin matrix.</summary>
public readonly record struct PinHoverInfo(
    HoleCoord Hole,
    string NetName,
    int StripId,
    bool IsPowerRail);

/// <summary>Board-space to screen-space fit transform (no pan/zoom in Phase 5).</summary>
public readonly record struct BoardViewportTransform(float Scale, float OffsetX, float OffsetY)
{
    public SKPoint ToScreen(float boardX, float boardY) =>
        new(OffsetX + boardX * Scale, OffsetY + boardY * Scale);

    public SKPoint ToBoard(float screenX, float screenY) =>
        new((screenX - OffsetX) / Scale, (screenY - OffsetY) / Scale);
}

public sealed partial class SkiaArOverlayRenderer : IDisposable
{
    private const float BoardWidth = BreadboardSpec.CanvasWidth;
    private const float BoardHeight = BreadboardSpec.CanvasHeight;

    private const float RulerTopHeight = 24f;
    private const float RulerLeftWidth = 32f;
    private const float CanvasMargin = 16f;

    // ---- layout (screen space, refreshed every paint) ----------------------
    private float _scale = 1f;
    private float _boardLeft;
    private float _boardTop;
    private float _boardPixelWidth = BoardWidth;
    private float _boardPixelHeight = BoardHeight;
    private bool _hasTransform;

    // ---- selected part for visual highlighting -----------------------------
    public int? SelectedTrackId { get; set; }

    // ---- user zoom & pan ---------------------------------------------------
    public float UserZoomFactor { get; set; } = 1.0f;
    public float PanOffsetX { get; set; } = 0f;
    public float PanOffsetY { get; set; } = 0f;

    private int _lastViewportWidth = 1000;
    private int _lastViewportHeight = 600;
    private float _baseFitScale = 1f;

    public void ResetZoom()
    {
        UserZoomFactor = 1.0f;
        PanOffsetX = 0f;
        PanOffsetY = 0f;
    }

    public void PanBy(float deltaX, float deltaY)
    {
        PanOffsetX += deltaX;
        PanOffsetY += deltaY;
    }

    public void ZoomAtCenter(float factor)
    {
        float cx = _lastViewportWidth * 0.5f;
        float cy = _lastViewportHeight * 0.5f;
        ZoomAt(factor, cx, cy);
    }

    public void ZoomAt(float factor, float focusScreenX, float focusScreenY)
    {
        float oldZoom = UserZoomFactor;
        float newZoom = Math.Clamp(oldZoom * factor, 0.4f, 5.0f);
        if (MathF.Abs(newZoom - oldZoom) < 0.001f)
            return;

        if (!_hasTransform || _scale <= 0.001f || _baseFitScale <= 0.001f)
        {
            UserZoomFactor = newZoom;
            return;
        }

        // Exact point in board coordinates under the focus screen pixel
        float boardX = (focusScreenX - _boardLeft) / _scale;
        float boardY = (focusScreenY - _boardTop) / _scale;

        // Apply new zoom factor
        UserZoomFactor = newZoom;

        // Calculate new scale and unpanned base position
        float newScale = _baseFitScale * newZoom;
        float rulerTop = Layers.ShowRulers ? RulerTopHeight : 0f;
        float rulerLeft = Layers.ShowRulers ? RulerLeftWidth : 0f;
        float availableWidth = MathF.Max(1f, _lastViewportWidth - rulerLeft - CanvasMargin * 2f);
        float availableHeight = MathF.Max(1f, _lastViewportHeight - rulerTop - CanvasMargin * 2f);
        (float dockWidth, float dockHeight) = ComputeDockReserve(_baseFitScale);

        float newBoardPixelWidth = BoardWidth * newScale;
        float newBoardPixelHeight = BoardHeight * newScale;
        float newBaseLeft = rulerLeft + CanvasMargin + ((availableWidth - newBoardPixelWidth - (dockWidth * newScale)) * 0.5f);
        float newBaseTop = rulerTop + CanvasMargin + ((availableHeight - newBoardPixelHeight - (dockHeight * newScale)) * 0.5f);

        // Update pan offset so (boardX, boardY) remains exactly at (focusScreenX, focusScreenY)
        PanOffsetX = focusScreenX - (boardX * newScale) - newBaseLeft;
        PanOffsetY = focusScreenY - (boardY * newScale) - newBaseTop;
    }

    // ---- hover state -------------------------------------------------------
    private HoleCoord _hoverHole;
    private bool _hoverValid;
    private float _hoverScreenX;
    private float _hoverScreenY;
    private string _hoverText = string.Empty;
    private string _hoverNetName = string.Empty;
    private DrcStatePacket? _lastRenderedState;

    // ---- X-ray builder state ----------------------------------------------
    private SKPath? _allStripsPath;
    private HoleCoord _pendingHole;
    private bool _pendingValid;

    // ---- telemetry cache (rebuilt at ~4 Hz, never per frame) ---------------
    private int _hudHash = int.MinValue;
    private long _hudFrameBucket = -1;
    private bool _hudInitialized;
    private string _hudStatus = "AWAITING FRAME";
    private string _hudMetrics = string.Empty;
    private string _hudFooter = "830 TIE POINTS";
    private SKColor _hudStatusColor = new(0x9A, 0x9A, 0x9A);

    // ---- component label cache (rebuilt when the topology hash changes) ----
    private readonly Dictionary<int, string> _componentLabels = new(128);
    private int _labelHash = int.MinValue;

    // ---- reusable geometry -------------------------------------------------
    private readonly SKPath _pinDotsPath = new();
    private readonly SKPath _arrowHeadPath = new();
    private readonly SKPath _jumperLinePath = new();
    private readonly SKPaint _pasteboardPaint;
    private readonly SKPaint _boardFillPaint;
    private readonly SKPaint _boardBorderPaint;
    private readonly SKPaint _underlayPaint;
    private readonly SKPaint _trenchFillPaint;
    private readonly SKPaint _trenchEdgePaint;
    private readonly SKPaint _railVccPaint;
    private readonly SKPaint _railGndPaint;
    private readonly SKPaint _pinDotPaint;
    private readonly SKPaint _pinHoverFillPaint;
    private readonly SKPaint _pinHoverRingPaint;
    private readonly SKPaint _xrayCriticalFillPaint;
    private readonly SKPaint _xrayCriticalStrokePaint;
    private readonly SKPaint _xrayWarningFillPaint;
    private readonly SKPaint _xrayWarningStrokePaint;
    private readonly SKPaint _xrayOverloadFillPaint;
    private readonly SKPaint _xrayOverloadStrokePaint;
    private readonly SKPaint _xrayInputOnlyFillPaint;
    private readonly SKPaint _xrayInputOnlyStrokePaint;
    private readonly SKPaint _xrayCrossoverFillPaint;
    private readonly SKPaint _xrayCrossoverStrokePaint;
    private readonly SKPaint _componentBoxPaint;
    private readonly SKPaint _componentBoxFlaggedPaint;
    private readonly SKPaint _jumperBoxPaint;
    private readonly SKPaint _jumperLinePaint;
    private readonly SKPaint _leadPaint;
    private readonly SKPaint _ratsnestPaint;
    private readonly SKPaint _ratsnestHeadPaint;
    private readonly SKPaint _rulerBandPaint;
    private readonly SKPaint _rulerBorderPaint;
    private readonly SKPaint _rulerTickPaint;
    private readonly SKPaint _rulerTextPaint;
    private readonly SKPaint _rulerAccentPaint;
    private readonly SKPaint _boardLabelPaint;
    private readonly SKPaint _boardSmallLabelPaint;
    private readonly SKPaint _boardTinyLabelPaint;
    private readonly SKPaint _labelChipPaint;
    private readonly SKPaint _telemetryPanelPaint;
    private readonly SKPaint _telemetryBorderPaint;
    private readonly SKPaint _telemetryStatusPaint;
    private readonly SKPaint _telemetryLinePaint;
    private readonly SKPaint _telemetryFooterPaint;
    private readonly SKPaint _titleBlockFillPaint;
    private readonly SKPaint _titleBlockBorderPaint;
    private readonly SKPaint _titleBlockTextPaint;
    private readonly SKPaint _titleBlockStrongTextPaint;
    private readonly SKPaint _hoverPanelPaint;
    private readonly SKPaint _hoverBorderPaint;
    private readonly SKPaint _hoverTextPaint;
    private readonly SKPaint _legendTextPaint;
    private readonly SKPaint _stripGhostFillPaint;
    private readonly SKPaint _stripGhostStrokePaint;
    private readonly SKPaint _builderPendingFillPaint;
    private readonly SKPaint _builderPendingStrokePaint;
    private readonly SKPaint _builderLinkPaint;
    private readonly SKTypeface _monoTypeface;
    private readonly SKTypeface _uiTypeface;
    private readonly SKTypeface _uiBoldTypeface;
    private bool _disposed;

    public SkiaArOverlayRenderer()
    {
        _monoTypeface = ResolveTypeface("Cascadia Mono", "Consolas");
        _uiTypeface = ResolveTypeface("Segoe UI", "Tahoma");
        _uiBoldTypeface = SKTypeface.FromFamilyName(
            "Segoe UI",
            SKFontStyleWeight.SemiBold,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright) ?? _uiTypeface;

        _pasteboardPaint = Fill(0x14, 0x14, 0x14);
        _boardFillPaint = Fill(0xE9, 0xE7, 0xE2);
        _boardBorderPaint = Stroke(0x33, 0x33, 0x37, 1f);
        _underlayPaint = new SKPaint { Color = new SKColor(255, 255, 255, 64) };
        _trenchFillPaint = Fill(0xD2, 0xCF, 0xC8);
        _trenchEdgePaint = Stroke(0xB8, 0xB4, 0xAC, 1f);
        _railVccPaint = Stroke(0xC0, 0x39, 0x3F, 1.4f);
        _railGndPaint = Stroke(0x2F, 0x6F, 0xB3, 1.4f);
        _pinDotPaint = Fill(0x63, 0x63, 0x63);
        _pinHoverFillPaint = Fill(0x00, 0x7A, 0xCC, 220);
        _pinHoverRingPaint = Stroke(0x00, 0x7A, 0xCC, 1.2f);
        _xrayCriticalFillPaint = Fill(0xD7, 0x37, 0x3F, 74);
        _xrayCriticalStrokePaint = Stroke(0xD7, 0x37, 0x3F, 1.2f);
        _xrayWarningFillPaint = Fill(0xE6, 0x86, 0x19, 66);
        _xrayWarningStrokePaint = Stroke(0xE6, 0x86, 0x19, 1.2f);
        _xrayOverloadFillPaint = Fill(0xE0, 0x40, 0xFB, 80);
        _xrayOverloadStrokePaint = Stroke(0xE0, 0x40, 0xFB, 1.4f);
        _xrayInputOnlyFillPaint = Fill(0xFF, 0x6D, 0x00, 75);
        _xrayInputOnlyStrokePaint = Stroke(0xFF, 0x6D, 0x00, 1.3f);
        _xrayCrossoverFillPaint = Fill(0x00, 0xE5, 0xFF, 75);
        _xrayCrossoverStrokePaint = Stroke(0x00, 0xE5, 0xFF, 1.3f);
        _componentBoxPaint = Stroke(0x3A, 0x7D, 0xB8, 1f);
        _componentBoxFlaggedPaint = Stroke(0xD7, 0x37, 0x3F, 1.4f);
        _jumperBoxPaint = Stroke(0x84, 0x84, 0x80, 0.8f);
        _jumperLinePaint = Stroke(0x74, 0x74, 0x70, 1f);
        _jumperLinePaint.PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0f);
        _leadPaint = Fill(0x2F, 0x2F, 0x2F, 200);
        _ratsnestPaint = Stroke(0x2D, 0x9D, 0x78, 1.8f);
        _ratsnestPaint.PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0f);
        _ratsnestHeadPaint = Fill(0x2D, 0x9D, 0x78);
        _rulerBandPaint = Fill(0x25, 0x25, 0x26);
        _rulerBorderPaint = Stroke(0x33, 0x33, 0x37, 1f);
        _rulerTickPaint = Stroke(0x88, 0x88, 0x88, 1f);
        _rulerTextPaint = Text(0x9A, 0x9A, 0x9A, 8.5f, _monoTypeface);
        _rulerAccentPaint = Fill(0x00, 0x7A, 0xCC);
        _boardLabelPaint = Text(0x2E, 0x2E, 0x2E, 12f, _uiBoldTypeface);
        _boardSmallLabelPaint = Text(0x4A, 0x4A, 0x48, 8.5f, _monoTypeface);
        _boardTinyLabelPaint = Text(0x6E, 0x6E, 0x6A, 7.5f, _monoTypeface);
        _partPinNumberPaint = Text(0xA8, 0xA6, 0x9E, 7f, _monoTypeface);
        _partIcLabelPaint = Text(0xD8, 0xD6, 0xCE, 7.5f, _monoTypeface);
        _partCapCeramicTextPaint = Text(0x40, 0x22, 0x08, 6.5f, _monoTypeface);
        _partSensorLabelDarkPaint = Text(0x18, 0x18, 0x1C, 7.5f, _monoTypeface);
        _partSensorBadgeTextPaint = Text(0xFA, 0xFA, 0xFA, 6.5f, _monoTypeface);
        _pinVccTextPaint = Text(0xFF, 0x55, 0x55, 6.5f, _monoTypeface);
        _pinGndTextPaint = Text(0x50, 0xFA, 0x7B, 6.5f, _monoTypeface);
        _pinSignalTextPaint = Text(0xFF, 0xB8, 0x6C, 6.5f, _monoTypeface);
        _pinGeneralTextPaint = Text(0xEC, 0xEF, 0xF4, 6.5f, _monoTypeface);
        _pinI2cTextPaint = Text(0xBD, 0x93, 0xF9, 6.5f, _monoTypeface);
        _labelChipPaint = Fill(0xED, 0xEB, 0xE4, 214);
        _telemetryPanelPaint = Fill(0x1E, 0x1E, 0x1E);
        _telemetryBorderPaint = Stroke(0x33, 0x33, 0x37, 1f);
        _telemetryStatusPaint = Text(0xCC, 0xCC, 0xCC, 13f, _uiBoldTypeface);
        _telemetryLinePaint = Text(0xC8, 0xC8, 0xC8, 9f, _monoTypeface);
        _telemetryFooterPaint = Text(0x9A, 0x9A, 0x9A, 8.5f, _monoTypeface);
        _titleBlockFillPaint = Fill(0xF4, 0xF2, 0xED);
        _titleBlockBorderPaint = Stroke(0x66, 0x66, 0x62, 1f);
        _titleBlockTextPaint = Text(0x4A, 0x4A, 0x48, 8f, _monoTypeface);
        _titleBlockStrongTextPaint = Text(0x25, 0x25, 0x26, 10f, _uiBoldTypeface);
        _hoverPanelPaint = Fill(0x1E, 0x1E, 0x1E, 244);
        _hoverBorderPaint = Stroke(0x00, 0x7A, 0xCC, 1f);
        _hoverTextPaint = Text(0xE6, 0xE6, 0xE6, 10f, _monoTypeface);
        _legendTextPaint = Text(0x4A, 0x4A, 0x48, 8f, _monoTypeface);
        _stripGhostFillPaint = Fill(0x2F, 0x6F, 0xB3, 12);
        _stripGhostStrokePaint = Stroke(0x88, 0x88, 0x88, 0.55f);
        _builderPendingFillPaint = Fill(0x00, 0x7A, 0xCC, 210);
        _builderPendingStrokePaint = Stroke(0x00, 0x7A, 0xCC, 1.4f);
        _builderLinkPaint = Stroke(0x00, 0x7A, 0xCC, 1.2f);
        _builderLinkPaint.PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0f);

        BuildPinDotsPath();
        _allStripsPath = BuildAllStripsPath();
    }

    /// <summary>Layer switches bound by the Phase 5B left dock.</summary>
    public OverlayLayerSettings Layers { get; } = new();

    /// <summary>Raised only when the snapped hole changes (safe to marshal to the UI).</summary>
    public event EventHandler? HoverChanged;

    public BoardViewportTransform Transform => new(_scale, _boardLeft, _boardTop);

    public bool IsHoveringPin => _hoverValid;

    public PinHoverInfo? HoveredPin => _hoverValid
        ? new PinHoverInfo(_hoverHole, _hoverNetName, _hoverHole.StripId, BreadboardSpec.IsRailStrip(_hoverHole.StripId))
        : null;

    /// <summary>
    /// Snaps the mouse cursor (already in control-local pixels) to the nearest tie
    /// point or docked off-board microcontroller pin socket.
    /// </summary>
    public bool UpdateHover(float viewportX, float viewportY)
    {
        _hoverScreenX = viewportX;
        _hoverScreenY = viewportY;

        if (!_hasTransform)
        {
            ClearDevBoardHover();
            return ApplyHover(false, default);
        }

        // 1. Hit-test off-board microcontroller pin sockets first if docked
        if (TryHitTestDevBoardPin(viewportX, viewportY, out DevBoardPinHit? devBoardHit) && devBoardHit is not null)
        {
            ApplyHover(false, default);
            _hoverValid = false;
            _hoverText = $"[{devBoardHit.BoardProfile.DisplayName}] {devBoardHit.Pin.DisplayLabel} ({devBoardHit.Pin.Role}, {devBoardHit.Pin.NominalVoltage:F1}V) - Click to wire";
            return true;
        }

        ClearDevBoardHover();

        // 2. Snap to the 830-tie-point breadboard matrix
        SKPoint board = Transform.ToBoard(viewportX, viewportY);
        bool inBounds = board.X >= -12f && board.X <= BoardWidth + 12f
                        && board.Y >= -12f && board.Y <= BoardHeight + 12f;

        HoleCoord hole = default;
        bool found = inBounds && PinMatrixLookup.Shared.TrySnapToHole(board.X, board.Y, out hole);
        return ApplyHover(found, hole);
    }

    public void ClearHover()
    {
        ClearDevBoardHover();
        if (!_hoverValid && _hoverText.Length == 0)
            return;

        ApplyHover(false, default);
    }

    /// <summary>
    /// Renders an off-screen snapshot of the entire breadboard (1000x500 CAD sheet)
    /// and encodes it as a high-resolution PNG byte array for Qwen multimodal reasoning.
    /// </summary>
    public byte[] CaptureFullBoardPng(
        DrcStatePacket? drcState,
        IReadOnlyList<RatsnestVector>? ratsnest = null,
        int width = 1000,
        int height = 500)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
            return Array.Empty<byte>();

        SKCanvas canvas = surface.Canvas;
        RenderBoardViewport(canvas, width, height, warpedBoardFrame: null, drcState, ratsnest);
        canvas.Flush();

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    /// <summary>
    /// Draws the complete CAD sheet: pasteboard, light board body, rulers, 830-pin
    /// matrix, X-Ray strip capsules, component boxes, corrective arrows and HUD.
    /// </summary>
    public void RenderCadViewport(
        SKCanvas canvas,
        int viewportWidth,
        int viewportHeight,
        SKBitmap? warpedBoardFrame,
        DrcStatePacket? drcState,
        IReadOnlyList<RatsnestVector>? ratsnest = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        _lastRenderedState = drcState;

        RenderBoardViewport(canvas, viewportWidth, viewportHeight, warpedBoardFrame, drcState, ratsnest);
    }

    /// <summary>
    /// Paints the 2D board: pasteboard, rulers, 830-pin matrix, X-ray strips,
    /// realistic parts, corrective ratsnest arrows and the telemetry HUD.
    /// </summary>
    private void RenderBoardViewport(
        SKCanvas canvas,
        int viewportWidth,
        int viewportHeight,
        SKBitmap? warpedBoardFrame,
        DrcStatePacket? drcState,
        IReadOnlyList<RatsnestVector>? ratsnest)
    {
        EnsureComponentLabels(drcState);

        if (_hoverValid)
        {
            string net = ResolveNetName(_hoverHole.StripId);
            if (!string.Equals(net, _hoverNetName, StringComparison.Ordinal))
                RefreshHoverInfo();
        }

        canvas.DrawRect(0f, 0f, viewportWidth, viewportHeight, _pasteboardPaint);
        ComputeLayout(viewportWidth, viewportHeight);

        var boardRect = new SKRect(
            _boardLeft,
            _boardTop,
            _boardLeft + _boardPixelWidth,
            _boardTop + _boardPixelHeight);

        canvas.DrawRect(boardRect, _boardFillPaint);

        if (Layers.ShowCameraUnderlay && warpedBoardFrame is not null)
        {
            canvas.Save();
            canvas.ClipRect(boardRect);
            canvas.DrawBitmap(
                warpedBoardFrame,
                new SKRect(0f, 0f, warpedBoardFrame.Width, warpedBoardFrame.Height),
                boardRect,
                _underlayPaint);
            canvas.Restore();
        }

        canvas.DrawRect(boardRect, _boardBorderPaint);

        canvas.Save();
        canvas.Translate(_boardLeft, _boardTop);
        canvas.Scale(_scale);
        DrawBoardContent(canvas, drcState, ratsnest);
        canvas.Restore();

        // Render any docked off-board microcontrollers outside the breadboard.
        // Wires are painted before the PCB so they disappear under the board and
        // its labels, exactly like a cable plugged into a socket at the edge.
        if (ActiveMicrocontrollers.Count > 0)
        {
            float pixelsPerMm = _boardPixelWidth / BreadboardWidthMm;
            var mcuLayouts = new List<RenderedMicrocontrollerLayout>(ActiveMicrocontrollers.Count);
            foreach (var mcu in ActiveMicrocontrollers)
                mcuLayouts.Add(McuRenderer.ComputeBoardLayout(mcu, boardRect, pixelsPerMm));
            _activeMcuLayouts = mcuLayouts;

            // 1. Paint microcontroller boards first so the PCB, hardware ICs, and headers form the foundation
            foreach (var mcu in ActiveMicrocontrollers)
                McuRenderer.RenderMicrocontroller(canvas, mcu, boardRect, pixelsPerMm);

            // 2. Paint 3D jumper wires on top so they arch gracefully into the headers
            if (ActiveDevBoardWires.Count > 0)
                DrawDevBoardJumperWires(canvas, mcuLayouts, boardRect, pixelsPerMm);

            if (ActiveDevBoardWires.Count > 0)
                DrawDevBoardWirePlugs(canvas, mcuLayouts, ActiveDevBoardWires, pixelsPerMm);

            // 3. Paint DRC violation callouts on top of all physical elements
            if (ActiveDevBoardViolations.Count > 0)
                DrawDrcViolationHud(canvas, boardRect, ActiveDevBoardViolations, pixelsPerMm);
        }

        // Live interactive 2.5D/3D jumper wire dragging (from breadboard hole or MCU pin to cursor)
        DrawActiveWireDrag(canvas, boardRect, _boardPixelWidth / BreadboardWidthMm);

        // Telemetry/title/legend are screen HUDs, not copper. Repaint them above
        // the jumpers so a wire never obscures a label or a readout.
        canvas.Save();
        canvas.Translate(_boardLeft, _boardTop);
        canvas.Scale(_scale);
        DrawBoardOverlays(canvas, drcState);
        canvas.Restore();

        if (Layers.ShowRulers)
            DrawRulers(canvas);

        if (Layers.ShowHoverInspector)
            DrawHoverTooltip(canvas, viewportWidth, viewportHeight);
    }

    private void ApplyPaintsForScale()
    {
        float inverse = 1f / _scale;

        _boardBorderPaint.StrokeWidth = 1f * inverse;
        _trenchEdgePaint.StrokeWidth = 1f * inverse;
        _railVccPaint.StrokeWidth = 1.4f * inverse;
        _railGndPaint.StrokeWidth = 1.4f * inverse;
        _pinHoverRingPaint.StrokeWidth = 1.2f * inverse;
        _xrayCriticalStrokePaint.StrokeWidth = 1.2f * inverse;
        _xrayWarningStrokePaint.StrokeWidth = 1.2f * inverse;
        _xrayOverloadStrokePaint.StrokeWidth = 1.4f * inverse;
        _xrayInputOnlyStrokePaint.StrokeWidth = 1.3f * inverse;
        _xrayCrossoverStrokePaint.StrokeWidth = 1.3f * inverse;
        _componentBoxPaint.StrokeWidth = 1f * inverse;
        _componentBoxFlaggedPaint.StrokeWidth = 1.4f * inverse;
        _jumperBoxPaint.StrokeWidth = 0.8f * inverse;
        _jumperLinePaint.StrokeWidth = 1f * inverse;
        _ratsnestPaint.StrokeWidth = 1.8f * inverse;
        _telemetryBorderPaint.StrokeWidth = 1f * inverse;
        _titleBlockBorderPaint.StrokeWidth = 1f * inverse;

        _boardLabelPaint.TextSize = 12f * inverse;
        _boardSmallLabelPaint.TextSize = 8.5f * inverse;
        _boardTinyLabelPaint.TextSize = 7.5f * inverse;
        _telemetryStatusPaint.TextSize = 13f * inverse;
        _telemetryLinePaint.TextSize = 9f * inverse;
        _telemetryFooterPaint.TextSize = 8.5f * inverse;
        // The title block is a fixed-size board-space panel. At small viewport
        // scales the constant 8 px text no longer fits its 312-unit width, so
        // ease the HUD font down with the board instead of clipping the labels.
        float titleBlockTextScale = Math.Clamp(_scale / 0.85f, 0.72f, 1f);
        _titleBlockTextPaint.TextSize = 8f * inverse * titleBlockTextScale;
        _titleBlockStrongTextPaint.TextSize = 10f * inverse * titleBlockTextScale;
        _legendTextPaint.TextSize = 8f * inverse;
    }

    private void DrawBoardContent(
        SKCanvas canvas,
        DrcStatePacket? state,
        IReadOnlyList<RatsnestVector>? ratsnest)
    {
        ApplyPaintsForScale();

        // Central IC trench (non-conductive divider between rows E and F).
        canvas.DrawRect(new SKRect(0f, 175f, BoardWidth, 225f), _trenchFillPaint);
        canvas.DrawLine(0f, 175f, BoardWidth, 175f, _trenchEdgePaint);
        canvas.DrawLine(0f, 225f, BoardWidth, 225f, _trenchEdgePaint);

        if (Layers.ShowAllStrips && _allStripsPath is not null)
        {
            canvas.DrawPath(_allStripsPath, _stripGhostFillPaint);
            canvas.DrawPath(_allStripsPath, _stripGhostStrokePaint);
        }

        DrawPowerRails(canvas);
        DrawJumperLeads(canvas, state);

        if (Layers.ShowPinMatrixDots)
            canvas.DrawPath(_pinDotsPath, _pinDotPaint);

        if (Layers.ShowHoverInspector)
            DrawHoverStrip(canvas);

        if (_pendingValid)
            DrawPendingLead(canvas);

        DrawComponentBoxes(canvas, state);

        if (state is not null)
            DrawViolations(canvas, state);

        if (Layers.ShowRatsnestArrows)
            DrawTargetRatsnest(canvas, ratsnest);

        DrawAvometerProbes(canvas);
    }

    /// <summary>
    /// HUD panels that must stay above physical jumper wires. Kept separate
    /// from <see cref="DrawBoardContent"/> so the viewport can paint wires and
    /// docked MCUs in between the breadboard and these screen overlays.
    /// </summary>
    private void DrawBoardOverlays(SKCanvas canvas, DrcStatePacket? state)
    {
        if (Layers.ShowTelemetry)
            DrawTelemetryPanel(canvas, state);

        if (Layers.ShowTitleBlock)
            DrawTitleBlock(canvas);

        if (Layers.ShowLegend)
            DrawLegend(canvas);
    }

    private void DrawPowerRails(SKCanvas canvas)
    {
        canvas.DrawLine(20f, BreadboardSpec.TopVccRailY, BoardWidth - 20f, BreadboardSpec.TopVccRailY, _railVccPaint);
        canvas.DrawLine(20f, BreadboardSpec.TopGndRailY, BoardWidth - 20f, BreadboardSpec.TopGndRailY, _railGndPaint);
        canvas.DrawLine(20f, BreadboardSpec.BottomVccRailY, BoardWidth - 20f, BreadboardSpec.BottomVccRailY, _railVccPaint);
        canvas.DrawLine(20f, BreadboardSpec.BottomGndRailY, BoardWidth - 20f, BreadboardSpec.BottomGndRailY, _railGndPaint);

        canvas.DrawText("VCC", 4f, BreadboardSpec.TopVccRailY + 3f, _boardTinyLabelPaint);
        canvas.DrawText("GND", 4f, BreadboardSpec.TopGndRailY + 3f, _boardTinyLabelPaint);
        canvas.DrawText("VCC", 4f, BreadboardSpec.BottomVccRailY + 3f, _boardTinyLabelPaint);
        canvas.DrawText("GND", 4f, BreadboardSpec.BottomGndRailY + 3f, _boardTinyLabelPaint);
    }

    private void DrawJumperLeads(SKCanvas canvas, DrcStatePacket? state)
    {
        if (state is null)
            return;

        foreach (SnappedComponent component in state.Components)
        {
            if (component.RawDetection.Kind != ComponentKind.JumperWire)
                continue;

            bool isFlagged = IsFlagged(state.Violations, component);
            DrawJumperWire(canvas, component, isFlagged);
        }
    }

    private void DrawHoverStrip(SKCanvas canvas)
    {
        if (!_hoverValid)
            return;

        int stripId = _hoverHole.StripId;
        if (BreadboardSpec.IsRailStrip(stripId))
        {
            float railY = BreadboardSpec.RailY(stripId);
            canvas.DrawLine(20f, railY, BoardWidth - 20f, railY, _pinHoverRingPaint);
            return;
        }

        ReadOnlySpan<HoleCoord> holes = PinMatrixLookup.Shared.GetStripHoles(stripId);
        for (int i = 0; i < holes.Length; i++)
        {
            canvas.DrawCircle(holes[i].Px, holes[i].Py, 4.4f, _pinHoverFillPaint);
            canvas.DrawCircle(holes[i].Px, holes[i].Py, 7.4f, _pinHoverRingPaint);
        }
    }

    private void DrawComponentBoxes(SKCanvas canvas, DrcStatePacket? state)
    {
        if (state is null)
            return;

        bool drawBoxes = Layers.ShowComponentBoxes;
        bool drawSymbols = Layers.ShowRealisticParts;
        if (!drawBoxes && !drawSymbols)
            return;

        foreach (SnappedComponent component in state.Components)
        {
            DetectedComponent detection = component.RawDetection;
            bool isJumper = detection.Kind == ComponentKind.JumperWire;
            bool isFlagged = IsFlagged(state.Violations, component);

            SKPaint boxPaint = isFlagged
                ? _componentBoxFlaggedPaint
                : isJumper ? _jumperBoxPaint : _componentBoxPaint;

            var box = new SKRect(
                detection.BoxX,
                detection.BoxY,
                detection.BoxX + detection.BoxW,
                detection.BoxY + detection.BoxH);

            if (drawBoxes)
            {
                canvas.DrawRect(box, boxPaint);

                if (!isJumper)
                {
                    for (int pin = 0; pin < component.PinCount; pin++)
                    {
                        HoleCoord hole = component.GetHole(pin);
                        if (string.IsNullOrEmpty(hole.HoleId))
                            continue;

                        canvas.DrawCircle(hole.Px, hole.Py, 2.2f, _leadPaint);
                    }
                }
            }

            // Jumpers are already drawn as bowed wires by DrawJumperLeads.
            if (drawSymbols && !isJumper)
                DrawRealisticPart(canvas, component);

            // A realistic glyph hides the plain box, so a fault still needs its
            // dashed outline when the boxes layer is switched off.
            if (isFlagged && drawSymbols && !drawBoxes)
                canvas.DrawRoundRect(box, 4f, 4f, _partFlagPaint);

            if (SelectedTrackId == detection.TrackId && !isJumper)
            {
                var selBox = box;
                selBox.Inflate(3.5f, 3.5f);
                canvas.DrawRoundRect(selBox, 4f, 4f, _partSelectionPaint);
            }

            if (!isJumper
                && Layers.ShowComponentLabels
                && _componentLabels.TryGetValue(detection.TrackId, out string? label))
            {
                float width = _boardSmallLabelPaint.MeasureText(label);
                float textSize = _boardSmallLabelPaint.TextSize;

                // Alternate above/below so two neighbouring parts never stack their
                // labels, and paint a paper chip so the text survives the camera underlay.
                float baseline = (detection.TrackId & 1) == 0
                    ? box.Bottom + textSize + 3f / _scale
                    : MathF.Max(textSize, box.Top - 4f / _scale);

                float left = box.MidX - width * 0.5f;
                float pad = 2f / _scale;

                var chip = new SKRect(left - pad, baseline - textSize, left + width + pad, baseline + pad);
                canvas.DrawRoundRect(chip, 2f, 2f, _labelChipPaint);
                canvas.DrawText(label, left, baseline, _boardSmallLabelPaint);
            }
        }
    }

    private static bool IsFlagged(IReadOnlyList<DrcViolation> violations, in SnappedComponent component)
    {
        for (int i = 0; i < violations.Count; i++)
        {
            int strip = violations[i].OffendingStripId;

            for (int pin = 0; pin < component.PinCount; pin++)
            {
                HoleCoord hole = component.GetHole(pin);
                if (!string.IsNullOrEmpty(hole.HoleId) && hole.StripId == strip)
                    return true;
            }
        }

        return false;
    }

    private void DrawViolations(SKCanvas canvas, DrcStatePacket state)
    {
        IReadOnlyList<DrcViolation> violations = state.Violations;
        for (int i = 0; i < violations.Count; i++)
        {
            DrcViolation violation = violations[i];
            bool critical = violation.Severity == DrcSeverity.Critical;

            SKPaint fill;
            SKPaint stroke;
            switch (violation.Code)
            {
                case ViolationCode.ErcVoltageOverload:
                    fill = _xrayOverloadFillPaint;
                    stroke = _xrayOverloadStrokePaint;
                    break;
                case ViolationCode.ErcInputOnlyViolation:
                    fill = _xrayInputOnlyFillPaint;
                    stroke = _xrayInputOnlyStrokePaint;
                    break;
                case ViolationCode.ErcProtocolCrossoverError:
                    fill = _xrayCrossoverFillPaint;
                    stroke = _xrayCrossoverStrokePaint;
                    break;
                default:
                    fill = critical ? _xrayCriticalFillPaint : _xrayWarningFillPaint;
                    stroke = critical ? _xrayCriticalStrokePaint : _xrayWarningStrokePaint;
                    break;
            }

            if (Layers.ShowXRayStrips)
            {
                DrawStripCapsule(canvas, violation.OffendingStripId, fill, stroke);

                // A VCC/GND bridge shorts both rails: highlight the partner rail too.
                if (violation.Code == ViolationCode.ErcVccGndDirectShort)
                {
                    int strip = violation.OffendingStripId;
                    int partner = strip switch
                    {
                        BreadboardSpec.StripTopVcc => BreadboardSpec.StripTopGnd,
                        BreadboardSpec.StripTopGnd => BreadboardSpec.StripTopVcc,
                        BreadboardSpec.StripBotVcc => BreadboardSpec.StripBotGnd,
                        BreadboardSpec.StripBotGnd => BreadboardSpec.StripBotVcc,
                        _ => 0
                    };

                    if (partner != 0)
                        DrawStripCapsule(canvas, partner, fill, stroke);
                }
            }

            if (!string.IsNullOrEmpty(violation.BadHole.HoleId))
            {
                DrawCrosshair(
                    canvas,
                    violation.BadHole.Px,
                    violation.BadHole.Py,
                    stroke);
            }

            if (Layers.ShowRatsnestArrows && !string.IsNullOrEmpty(violation.SuggestedTargetHole.HoleId))
            {
                DrawCorrectiveArrow(
                    canvas,
                    violation.BadHole.Px,
                    violation.BadHole.Py,
                    violation.SuggestedTargetHole.Px,
                    violation.SuggestedTargetHole.Py);
            }

            if (Layers.ShowComponentLabels)
                DrawViolationBadge(canvas, violation, stroke);
        }
    }

    private void DrawStripCapsule(SKCanvas canvas, int stripId, SKPaint fill, SKPaint stroke)
    {
        if (BreadboardSpec.IsRailStrip(stripId))
        {
            float railY = BreadboardSpec.RailY(stripId);
            var railRect = new SKRect(20f, railY - 7f, BoardWidth - 20f, railY + 7f);
            canvas.DrawRoundRect(railRect, 7f, 7f, fill);
            canvas.DrawRoundRect(railRect, 7f, 7f, stroke);
            return;
        }

        ReadOnlySpan<HoleCoord> holes = PinMatrixLookup.Shared.GetStripHoles(stripId);
        if (holes.Length == 0)
            return;

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        for (int i = 0; i < holes.Length; i++)
        {
            minX = MathF.Min(minX, holes[i].Px);
            maxX = MathF.Max(maxX, holes[i].Px);
            minY = MathF.Min(minY, holes[i].Py);
            maxY = MathF.Max(maxY, holes[i].Py);
        }

        var capsule = new SKRect(minX - 8f, minY - 9f, maxX + 8f, maxY + 9f);
        canvas.DrawRoundRect(capsule, 5f, 5f, fill);
        canvas.DrawRoundRect(capsule, 5f, 5f, stroke);
    }

    private void DrawCorrectiveArrow(SKCanvas canvas, float fromX, float fromY, float toX, float toY)
    {
        canvas.DrawLine(fromX, fromY, toX, toY, _ratsnestPaint);

        float deltaX = toX - fromX;
        float deltaY = toY - fromY;
        float length = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length < 0.001f)
            return;

        float unitX = deltaX / length;
        float unitY = deltaY / length;
        float headLength = 9f / _scale;
        float halfWidth = 4f / _scale;

        _arrowHeadPath.Reset();
        _arrowHeadPath.MoveTo(toX, toY);
        _arrowHeadPath.LineTo(
            toX - unitX * headLength - unitY * halfWidth,
            toY - unitY * headLength + unitX * halfWidth);
        _arrowHeadPath.LineTo(
            toX - unitX * headLength + unitY * halfWidth,
            toY - unitY * headLength - unitX * halfWidth);
        _arrowHeadPath.Close();

        canvas.DrawPath(_arrowHeadPath, _ratsnestHeadPaint);
        canvas.DrawCircle(toX, toY, 3.2f, _ratsnestHeadPaint);
    }

    private void DrawTargetRatsnest(SKCanvas canvas, IReadOnlyList<RatsnestVector>? ratsnest)
    {
        if (ratsnest is not { Count: > 0 })
            return;

        int limit = Math.Min(ratsnest.Count, 24);
        for (int i = 0; i < limit; i++)
        {
            RatsnestVector vector = ratsnest[i];
            DrawCorrectiveArrow(canvas, vector.From.Px, vector.From.Py, vector.To.Px, vector.To.Py);
        }
    }

    private void DrawCrosshair(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        canvas.DrawCircle(x, y, 6f, paint);
        canvas.DrawLine(x - 10f, y, x + 10f, y, paint);
        canvas.DrawLine(x, y - 10f, x, y + 10f, paint);
    }

    private void DrawViolationBadge(SKCanvas canvas, in DrcViolation violation, SKPaint strokePaint)
    {
        string label = ViolationLabel(violation.Code);
        float textWidth = _telemetryLinePaint.MeasureText(label);
        float width = textWidth + 12f;
        float height = 15f;

        float x = Math.Clamp(violation.BadHole.Px - width * 0.5f, 4f, BoardWidth - width - 4f);
        float y = violation.BadHole.Py - height - 12f;
        if (y < 4f)
            y = violation.BadHole.Py + 12f;
        if (y > BoardHeight - height - 4f)
            y = BoardHeight - height - 4f;

        var badge = new SKRect(x, y, x + width, y + height);
        canvas.DrawRoundRect(badge, 2f, 2f, _telemetryPanelPaint);
        canvas.DrawRoundRect(badge, 2f, 2f, strokePaint);
        canvas.DrawText(label, x + 6f, y + height - 4.5f, _telemetryLinePaint);
    }

    private static string ViolationLabel(ViolationCode code) => code switch
    {
        ViolationCode.ErcVccGndDirectShort => "ERC 100  VCC-GND SHORT",
        ViolationCode.ErcSameStripShort => "ERC 101  SAME-STRIP SHORT",
        ViolationCode.ErcFloatingLead => "ERC 102  FLOATING LEAD",
        ViolationCode.ErcReversedPolarity => "ERC 103  REVERSED POLARITY",
        ViolationCode.ErcRedundantJumper => "ERC 104  REDUNDANT JUMPER",
        ViolationCode.ErcHoleCollision => "ERC 105  OVERFILLED HOLE",
        ViolationCode.ErcVoltageOverload => "ERC 106  VOLTAGE OVERLOAD",
        ViolationCode.ErcInputOnlyViolation => "ERC 107  INPUT-ONLY PIN",
        ViolationCode.ErcProtocolCrossoverError => "ERC 108  PROTOCOL CROSSOVER",
        ViolationCode.DrcSchematicNetMismatch => "DRC 200  OPEN NET",
        ViolationCode.DrcUnexpectedNetLink => "DRC 202  UNEXPECTED BRIDGE",
        _ => "DRC VIOLATION"
    };

    private void UpdateHudCache(DrcStatePacket? state)
    {
        int devBoardHash = 17;
        int devBoardCritical = 0;
        int devBoardWarnings = 0;
        for (int i = 0; i < ActiveDevBoardViolations.Count; i++)
        {
            var v = ActiveDevBoardViolations[i];
            devBoardHash = HashCode.Combine(devBoardHash, v.RuleCode.GetHashCode(), v.NetName.GetHashCode());
            if (v.Severity is McuDrcSeverity.CriticalDamageRisk or McuDrcSeverity.Error)
                devBoardCritical++;
            else
                devBoardWarnings++;
        }

        int currentHash = HashCode.Combine(state?.StateHash ?? int.MinValue, devBoardHash, ActiveDevBoardWires.Count);
        long bucket = (state?.FrameNumber ?? 0) / 15;
        if (_hudInitialized && currentHash == _hudHash && bucket == _hudFrameBucket)
            return;

        _hudInitialized = true;
        _hudHash = currentHash;
        _hudFrameBucket = bucket;

        int critical = devBoardCritical;
        int warnings = devBoardWarnings;
        if (state is not null)
        {
            for (int i = 0; i < state.Violations.Count; i++)
            {
                if (state.Violations[i].Severity == DrcSeverity.Critical)
                    critical++;
                else
                    warnings++;
            }
        }

        if (critical > 0)
        {
            _hudStatus = critical == 1 ? "1 CRITICAL VIOLATION" : $"{critical} CRITICAL VIOLATIONS";
            _hudStatusColor = new SKColor(0xD7, 0x37, 0x3F);
        }
        else if (warnings > 0)
        {
            _hudStatus = warnings == 1 ? "1 WARNING" : $"{warnings} WARNINGS";
            _hudStatusColor = new SKColor(0xE6, 0x86, 0x19);
        }
        else if (state is null || (state.Components.Count == 0 && ActiveDevBoardWires.Count == 0))
        {
            _hudStatus = state is null ? "AWAITING FRAME" : "NO DETECTIONS";
            _hudStatusColor = new SKColor(0x9A, 0x9A, 0x9A);
        }
        else
        {
            _hudStatus = "DRC PASS - NETLIST CLEAN";
            _hudStatusColor = new SKColor(0x2D, 0x9D, 0x78);
        }

        if (state is not null)
        {
            _hudMetrics = string.Create(
                CultureInfo.InvariantCulture,
                $"YOLO {state.YoloInferenceMs:0.0} MS   DRC {state.DrcComputeMs:0.000} MS");

            string partsText = $"PARTS {state.Components.Count}";
            string wiresText = ActiveDevBoardWires.Count > 0 ? $"   WIRES {ActiveDevBoardWires.Count}" : "";
            _hudFooter = string.Create(
                CultureInfo.InvariantCulture,
                $"FRAME {state.FrameNumber}   PINS 830   {partsText}{wiresText}");
        }
        else
        {
            _hudMetrics = "YOLO --.- MS   DRC --.--- MS";
            string wiresText = ActiveDevBoardWires.Count > 0 ? $"   WIRES {ActiveDevBoardWires.Count}" : "";
            _hudFooter = $"830 TIE POINTS   CAD DOCKED{wiresText}";
        }
    }

    private void DrawTelemetryPanel(SKCanvas canvas, DrcStatePacket? state)
    {
        UpdateHudCache(state);

        var panel = new SKRect(24f, 318f, 300f, 414f);
        canvas.DrawRect(panel, _telemetryPanelPaint);
        canvas.DrawRect(panel, _telemetryBorderPaint);
        canvas.DrawLine(panel.Left, 344f, panel.Right, 344f, _telemetryBorderPaint);

        _telemetryStatusPaint.Color = _hudStatusColor;
        canvas.DrawText(_hudStatus, 34f, 338f, _telemetryStatusPaint);
        canvas.DrawText(_hudMetrics, 34f, 365f, _telemetryLinePaint);
        canvas.DrawText(_hudFooter, 34f, 383f, _telemetryFooterPaint);
        canvas.DrawText(_hoverValid ? _hoverText : "HOVER: --", 34f, 401f, _telemetryFooterPaint);
    }

    private void DrawTitleBlock(SKCanvas canvas)
    {
        var block = new SKRect(664f, 330f, 976f, 438f);
        canvas.DrawRect(block, _titleBlockFillPaint);
        canvas.DrawRect(block, _titleBlockBorderPaint);

        float rowHeight = (block.Bottom - block.Top) / 4f;
        for (int row = 1; row < 4; row++)
        {
            float y = block.Top + row * rowHeight;
            canvas.DrawLine(block.Left, y, block.Right, y, _titleBlockBorderPaint);
        }

        canvas.DrawText("BREADBOARD AOI", block.Left + 10f, block.Top + 17f, _titleBlockStrongTextPaint);
        canvas.DrawText("VIEW: X-RAY OVERLAY / TOP", block.Left + 10f, block.Top + rowHeight + 14f, _titleBlockTextPaint);
        canvas.DrawText("MATRIX: 63 COLUMNS x 10 ROWS + 4 RAILS", block.Left + 10f, block.Top + rowHeight * 2f + 14f, _titleBlockTextPaint);
        canvas.DrawText("SHEET: DRC / RATSNEST    PITCH: 14.84 PX", block.Left + 10f, block.Top + rowHeight * 3f + 14f, _titleBlockTextPaint);
    }

    private void DrawLegend(SKCanvas canvas)
    {
        float x = 24f;
        const float baseline = 489f;

        DrawLegendEntry(canvas, ref x, baseline, _xrayCriticalFillPaint, "CRITICAL");
        DrawLegendEntry(canvas, ref x, baseline, _xrayWarningFillPaint, "WARNING");
        DrawLegendEntry(canvas, ref x, baseline, _xrayOverloadFillPaint, "OVERLOAD");
        DrawLegendEntry(canvas, ref x, baseline, _xrayCrossoverFillPaint, "CROSSOVER");
        DrawLegendEntry(canvas, ref x, baseline, _ratsnestHeadPaint, "RATSNEST");
        DrawLegendEntry(canvas, ref x, baseline, _pinDotPaint, "TIE POINT");
    }

    private void DrawLegendEntry(SKCanvas canvas, ref float x, float baseline, SKPaint swatch, string label)
    {
        canvas.DrawRect(new SKRect(x, baseline - 6f, x + 6f, baseline), swatch);
        x += 10f;
        canvas.DrawText(label, x, baseline, _legendTextPaint);
        x += _legendTextPaint.MeasureText(label) + 16f;
    }

    private void DrawRulers(SKCanvas canvas)
    {
        float top = _boardTop - RulerTopHeight;
        float left = _boardLeft - RulerLeftWidth;

        canvas.DrawRect(new SKRect(left, top, _boardLeft, _boardTop), _rulerBandPaint);
        canvas.DrawRect(new SKRect(_boardLeft, top, _boardLeft + _boardPixelWidth, _boardTop), _rulerBandPaint);
        canvas.DrawRect(new SKRect(left, _boardTop, _boardLeft, _boardTop + _boardPixelHeight), _rulerBandPaint);

        canvas.DrawLine(left, top, _boardLeft + _boardPixelWidth, top, _rulerBorderPaint);
        canvas.DrawLine(left, top, left, _boardTop + _boardPixelHeight, _rulerBorderPaint);
        canvas.DrawLine(_boardLeft, _boardTop, _boardLeft + _boardPixelWidth, _boardTop, _rulerBorderPaint);
        canvas.DrawLine(_boardLeft, _boardTop, _boardLeft, _boardTop + _boardPixelHeight, _rulerBorderPaint);

        float labelBaseline = top + 10f;

        for (int column = 1; column <= BreadboardSpec.ColumnCount; column++)
        {
            float x = _boardLeft + BreadboardSpec.ColumnX(column) * _scale;
            bool showLabel = column == 1 || column % 5 == 0 || column == BreadboardSpec.ColumnCount;

            canvas.DrawLine(x, _boardTop - (showLabel ? 6f : 3f), x, _boardTop, _rulerTickPaint);

            if (!showLabel)
                continue;

            string label = column.ToString(CultureInfo.InvariantCulture);
            float width = _rulerTextPaint.MeasureText(label);
            canvas.DrawText(label, x - width * 0.5f, labelBaseline, _rulerTextPaint);
        }

        DrawLeftRulerLabel(canvas, left, BreadboardSpec.TopVccRailY, "+", ticked: true);
        DrawLeftRulerLabel(canvas, left, BreadboardSpec.TopGndRailY, "-", ticked: true);

        for (int row = 0; row < BreadboardSpec.AllRowLabels.Length; row++)
        {
            char rowChar = BreadboardSpec.AllRowLabels[row][0];
            DrawLeftRulerLabel(canvas, left, BreadboardSpec.RowY(rowChar), BreadboardSpec.AllRowLabels[row], ticked: true);
        }

        DrawLeftRulerLabel(canvas, left, BreadboardSpec.BottomVccRailY, "+", ticked: true);
        DrawLeftRulerLabel(canvas, left, BreadboardSpec.BottomGndRailY, "-", ticked: true);

        SKColor previous = _rulerTextPaint.Color;
        if (_hoverValid)
            _rulerTextPaint.Color = new SKColor(0x4F, 0xA8, 0xE8);

        string corner = _hoverValid ? _hoverHole.HoleId : "830";
        canvas.DrawText(corner, left + 4f, top + 16f, _rulerTextPaint);
        _rulerTextPaint.Color = previous;

        if (!_hoverValid)
            return;

        float markerX = _boardLeft + _hoverHole.Px * _scale;
        float markerY = _boardTop + _hoverHole.Py * _scale;
        canvas.DrawRect(new SKRect(markerX - 1f, top, markerX + 1f, _boardTop), _rulerAccentPaint);
        canvas.DrawRect(new SKRect(left, markerY - 1f, _boardLeft, markerY + 1f), _rulerAccentPaint);
    }

    private void DrawLeftRulerLabel(SKCanvas canvas, float left, float boardY, string label, bool ticked)
    {
        float y = _boardTop + boardY * _scale;

        if (ticked)
            canvas.DrawLine(left + RulerLeftWidth - 6f, y, left + RulerLeftWidth, y, _rulerTickPaint);

        float width = _rulerTextPaint.MeasureText(label);
        canvas.DrawText(label, left + (RulerLeftWidth - 8f - width), y + 3f, _rulerTextPaint);
    }

    private void DrawHoverTooltip(SKCanvas canvas, int viewportWidth, int viewportHeight)
    {
        if (!_hoverValid || _hoverText.Length == 0)
            return;

        float textWidth = _hoverTextPaint.MeasureText(_hoverText);
        float width = textWidth + 16f;
        const float height = 24f;

        // Anchor the inspector to the snapped pin, not the raw cursor: the panel
        // then always labels the blue ring the operator can actually see.
        float anchorX = _boardLeft + _hoverHole.Px * _scale;
        float anchorY = _boardTop + _hoverHole.Py * _scale;

        float x = anchorX + 16f;
        float y = anchorY + 18f;

        if (x + width > viewportWidth - 8f)
            x = anchorX - width - 16f;
        if (y + height > viewportHeight - 8f)
            y = anchorY - height - 18f;

        x = MathF.Max(4f, x);
        y = MathF.Max(4f, y);

        var panel = new SKRect(x, y, x + width, y + height);

        // Thin leader so a flipped tooltip still points at its hole.
        float leaderX = Math.Clamp(anchorX, panel.Left, panel.Right);
        float leaderY = Math.Clamp(anchorY, panel.Top, panel.Bottom);
        canvas.DrawLine(anchorX, anchorY, leaderX, leaderY, _hoverBorderPaint);

        canvas.DrawRoundRect(panel, 2f, 2f, _hoverPanelPaint);
        canvas.DrawRoundRect(panel, 2f, 2f, _hoverBorderPaint);
        canvas.DrawText(_hoverText, x + 8f, y + 16f, _hoverTextPaint);
    }

    private void ComputeLayout(int viewportWidth, int viewportHeight)
    {
        _lastViewportWidth = Math.Max(100, viewportWidth);
        _lastViewportHeight = Math.Max(100, viewportHeight);

        float rulerTop = Layers.ShowRulers ? RulerTopHeight : 0f;
        float rulerLeft = Layers.ShowRulers ? RulerLeftWidth : 0f;

        float availableWidth = MathF.Max(1f, viewportWidth - rulerLeft - CanvasMargin * 2f);
        float availableHeight = MathF.Max(1f, viewportHeight - rulerTop - CanvasMargin * 2f);

        float scale = MathF.Max(
            0.05f,
            MathF.Min(availableWidth / BoardWidth, availableHeight / BoardHeight));

        // The label and title clearance is measured in screen pixels, so it has
        // to be converted back into board units after each fit. Two passes are
        // enough for the dock margin to converge without oscillating.
        for (int pass = 0; pass < 2; pass++)
        {
            (float extraWidth, float extraHeight) = ComputeDockReserve(scale);
            scale = MathF.Max(
                0.05f,
                MathF.Min(
                    availableWidth / (BoardWidth + extraWidth),
                    availableHeight / (BoardHeight + extraHeight)));
        }

        (float dockWidth, float dockHeight) = ComputeDockReserve(scale);

        _baseFitScale = scale;
        float effectiveScale = Math.Clamp(scale * UserZoomFactor, 0.02f, 25f);

        _scale = effectiveScale;
        _boardPixelWidth = BoardWidth * _scale;
        _boardPixelHeight = BoardHeight * _scale;
        _boardLeft = rulerLeft + CanvasMargin + ((availableWidth - _boardPixelWidth - (dockWidth * _scale)) * 0.5f) + PanOffsetX;
        _boardTop = rulerTop + CanvasMargin + ((availableHeight - _boardPixelHeight - (dockHeight * _scale)) * 0.5f) + PanOffsetY;
        _hasTransform = true;
    }

    /// <summary>
    /// Board-space margin needed to keep every docked MCU, its rotated pin
    /// labels and its title caption inside the viewport.
    /// </summary>
    private (float ExtraWidth, float ExtraHeight) ComputeDockReserve(float scale)
    {
        if (ActiveMicrocontrollers.Count == 0)
            return (0f, 0f);

        const float unitsPerMm = BoardWidth / BreadboardWidthMm;
        float safeScale = MathF.Max(scale, 0.05f);
        float pixelsPerMm = (BoardWidth * safeScale) / BreadboardWidthMm;
        float gapUnits = OffBoardMicrocontrollerRenderer.DockGapMm * unitsPerMm;
        float titleUnits = OffBoardMicrocontrollerRenderer.TitleClearanceMm * unitsPerMm;
        float slotGapUnits = 14.0f * unitsPerMm;
        float extraWidth = 0f;
        float extraHeight = 0f;

        foreach (OffBoardMicrocontrollerVisualState mcu in ActiveMicrocontrollers)
        {
            MicrocontrollerBoardProfile profile = McuRuleEngine.GetBoardProfile(mcu.BoardType);
            McuLabelExtents labels = McuRenderer.MeasureLabelExtents(profile, pixelsPerMm);
            float boardWidthUnits = profile.WidthMm * unitsPerMm;
            float boardHeightUnits = profile.HeightMm * unitsPerMm;

            if (mcu.DockPlacement == OffBoardDockPlacement.BottomMargin)
            {
                float slotOffset = mcu.DockSlotIndex * (boardWidthUnits + slotGapUnits);
                extraHeight = MathF.Max(
                    extraHeight,
                    gapUnits + boardHeightUnits + (labels.BottomPx / safeScale) + titleUnits);
                extraWidth = MathF.Max(
                    extraWidth,
                    MathF.Max(0f, slotOffset + boardWidthUnits - BoardWidth));
            }
            else
            {
                float slotOffset = mcu.DockSlotIndex * (boardHeightUnits + slotGapUnits);
                extraWidth = MathF.Max(
                    extraWidth,
                    gapUnits + boardWidthUnits + (labels.RightPx / safeScale));
                extraHeight = MathF.Max(
                    extraHeight,
                    MathF.Max(
                        0f,
                        slotOffset + boardHeightUnits
                        + ((labels.TopPx + labels.BottomPx) / safeScale)
                        + titleUnits
                        - BoardHeight));
            }
        }

        return (extraWidth, extraHeight);
    }

    private bool ApplyHover(bool found, HoleCoord hole)
    {
        bool changed = found != _hoverValid
                       || (found && !string.Equals(hole.HoleId, _hoverHole.HoleId, StringComparison.Ordinal));

        _hoverValid = found;
        _hoverHole = found ? hole : default;

        if (changed)
        {
            RefreshHoverInfo();
            HoverChanged?.Invoke(this, EventArgs.Empty);
        }

        return found;
    }

    private void RefreshHoverInfo()
    {
        if (!_hoverValid)
        {
            _hoverNetName = string.Empty;
            _hoverText = string.Empty;
            return;
        }

        _hoverNetName = ResolveNetName(_hoverHole.StripId);
        _hoverText = string.Create(
            CultureInfo.InvariantCulture,
            $"{_hoverHole.HoleId}  |  STRIP {_hoverHole.StripId}  |  {_hoverNetName}");
    }

    private string ResolveNetName(int stripId)
    {
        if (stripId is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc)
            return "NET_VCC";

        if (stripId is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd)
            return "NET_GND";

        DrcStatePacket? state = _lastRenderedState;
        if (state is not null)
        {
            foreach (SnappedComponent component in state.Components)
            {
                if (component.Pin1Hole.StripId == stripId && component.Net1Name.Length > 0)
                    return component.Net1Name;

                if (component.Pin2Hole.StripId == stripId && component.Net2Name.Length > 0)
                    return component.Net2Name;
            }
        }

        return "UNROUTED";
    }

    private void EnsureComponentLabels(DrcStatePacket? state)
    {
        if (state is null)
        {
            _componentLabels.Clear();
            _labelHash = int.MinValue;
            return;
        }

        if (state.StateHash == _labelHash)
            return;

        _labelHash = state.StateHash;
        _componentLabels.Clear();

        foreach (SnappedComponent component in state.Components)
        {
            DetectedComponent detection = component.RawDetection;
            string value = string.IsNullOrWhiteSpace(detection.DecodedValueLabel)
                ? string.Empty
                : "  " + detection.DecodedValueLabel;

            string spanStr = component.PinCount > 2
                ? $"{component.Pin1Hole.HoleId}..{component.GetHole(component.PinCount - 1).HoleId}"
                : $"{component.Pin1Hole.HoleId}>{component.Pin2Hole.HoleId}";

            _componentLabels[detection.TrackId] = string.Create(
                CultureInfo.InvariantCulture,
                $"{KindLabel(detection.Kind)} #{detection.TrackId}{value}  {spanStr}");
        }
    }

    private static string KindLabel(ComponentKind kind) => kind switch
    {
        ComponentKind.JumperWire => "JUMPER",
        ComponentKind.Resistor => "RESISTOR",
        ComponentKind.Led => "LED",
        ComponentKind.Capacitor => "CAPACITOR",
        ComponentKind.IntegratedCircuit => "IC",
        ComponentKind.PushButton => "BUTTON",
        ComponentKind.Diode => "DIODE",
        ComponentKind.Transistor => "TRANSISTOR",
        ComponentKind.Potentiometer => "POT",
        ComponentKind.VoltageRegulator => "REGULATOR",
        ComponentKind.Buzzer => "BUZZER",
        ComponentKind.Sensor => "SENSOR",
        ComponentKind.Battery => "BATTERY",
        _ => "PART"
    };

    private void BuildPinDotsPath()
    {
        _pinDotsPath.Reset();
        ReadOnlySpan<HoleCoord> holes = PinMatrixLookup.Shared.Holes;
        for (int i = 0; i < holes.Length; i++)
            _pinDotsPath.AddCircle(holes[i].Px, holes[i].Py, 2.1f);
    }

    /// <summary>One cached path containing every conductive clip, for the X-ray backdrop.</summary>
    private static SKPath BuildAllStripsPath()
    {
        var path = new SKPath();

        for (int strip = BreadboardSpec.UpperStripBase; strip <= BreadboardSpec.MaxStripId; strip++)
        {
            ReadOnlySpan<HoleCoord> holes = PinMatrixLookup.Shared.GetStripHoles(strip);
            if (holes.Length == 0)
                continue;

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            for (int i = 0; i < holes.Length; i++)
            {
                minX = MathF.Min(minX, holes[i].Px);
                maxX = MathF.Max(maxX, holes[i].Px);
                minY = MathF.Min(minY, holes[i].Py);
                maxY = MathF.Max(maxY, holes[i].Py);
            }

            path.AddRoundRect(new SKRect(minX - 8f, minY - 9f, maxX + 8f, maxY + 9f), 5f, 5f);
        }

        for (int strip = BreadboardSpec.StripTopVcc; strip <= BreadboardSpec.StripBotGnd; strip++)
        {
            float railY = BreadboardSpec.RailY(strip);
            path.AddRoundRect(new SKRect(20f, railY - 7f, BoardWidth - 20f, railY + 7f), 7f, 7f);
        }

        return path;
    }

    private DevBoardPinHit? _pendingDevBoardPin;
    private string _pendingWireColor = "#00E5FF";

    /// <summary>Marks the first lead of a part being placed in the X-ray builder.</summary>
    public void SetPendingLead(HoleCoord hole, string? wireColor = null)
    {
        _pendingHole = hole;
        _pendingValid = true;
        _pendingDevBoardPin = null;
        if (!string.IsNullOrWhiteSpace(wireColor))
            _pendingWireColor = wireColor;
    }

    /// <summary>Marks an off-board MCU pin as the first lead of an interactive jumper wire.</summary>
    public void SetPendingDevBoardPin(DevBoardPinHit mcuPinHit, string? wireColor = null)
    {
        _pendingDevBoardPin = mcuPinHit;
        _pendingValid = false;
        if (!string.IsNullOrWhiteSpace(wireColor))
            _pendingWireColor = wireColor;
    }

    public void ClearPendingLead()
    {
        _pendingValid = false;
        _pendingDevBoardPin = null;
    }

    public bool HasPendingLead => _pendingValid || _pendingDevBoardPin is not null;

    /// <summary>Anchor-on-hover indicator so the first lead location is clear on the breadboard.</summary>
    private void DrawPendingLead(SKCanvas canvas)
    {
        canvas.DrawCircle(_pendingHole.Px, _pendingHole.Py, 5.5f, _builderPendingFillPaint);
        canvas.DrawCircle(_pendingHole.Px, _pendingHole.Py, 10f, _builderPendingStrokePaint);
    }

    /// <summary>
    /// Renders the active interactive 2.5D/3D jumper wire dragging between a breadboard hole
    /// or MCU socket and the mouse cursor / hovered target pin at 60 FPS.
    /// </summary>
    private void DrawActiveWireDrag(SKCanvas canvas, SKRect boardRect, float pixelsPerMm)
    {
        SKPoint? startPt = null;
        if (_pendingDevBoardPin is not null)
        {
            startPt = _pendingDevBoardPin.CanvasCenter;
        }
        else if (_pendingValid)
        {
            startPt = new SKPoint(
                boardRect.Left + ((_pendingHole.Px / BoardWidth) * boardRect.Width),
                boardRect.Top + ((_pendingHole.Py / BoardHeight) * boardRect.Height));
        }

        if (startPt is not { } start)
            return;

        SKPoint end;
        bool isSnapped = false;

        if (HoveredDevBoardPin is { } hoveredMcu)
        {
            end = hoveredMcu.CanvasCenter;
            isSnapped = true;
        }
        else if (_hoverValid)
        {
            end = new SKPoint(
                boardRect.Left + ((_hoverHole.Px / BoardWidth) * boardRect.Width),
                boardRect.Top + ((_hoverHole.Py / BoardHeight) * boardRect.Height));
            isSnapped = true;
        }
        else if (_hoverScreenX > 0 && _hoverScreenY > 0)
        {
            end = new SKPoint(_hoverScreenX, _hoverScreenY);
        }
        else
        {
            return;
        }

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        if (((dx * dx) + (dy * dy)) < 16f)
            return;

        Draw3DJumperWire(canvas, start, end, _pendingWireColor, hasViolation: false, pxPerMm: pixelsPerMm);

        if (isSnapped)
        {
            SKColor ringColor = SKColor.TryParse(_pendingWireColor, out SKColor parsed) ? parsed : SKColor.Parse("#00E5FF");
            using var snapGlow = new SKPaint
            {
                Color = ringColor.WithAlpha(210),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = MathF.Max(2.0f, 0.55f * pixelsPerMm),
                IsAntialias = true
            };
            canvas.DrawCircle(end.X, end.Y, MathF.Max(4.5f, 1.8f * pixelsPerMm), snapGlow);
        }
    }

    private static SKPaint Fill(byte red, byte green, byte blue, byte alpha = 255) =>
        new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(red, green, blue, alpha),
            IsAntialias = true
        };

    private static SKPaint Stroke(byte red, byte green, byte blue, float width) =>
        new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(red, green, blue),
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

    private static SKPaint Text(byte red, byte green, byte blue, float size, SKTypeface typeface) =>
        new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(red, green, blue),
            TextSize = size,
            Typeface = typeface,
            TextAlign = SKTextAlign.Left,
            IsAntialias = true
        };

    private static SKTypeface ResolveTypeface(string family, string fallback)
    {
        SKTypeface? typeface = SKTypeface.FromFamilyName(family);
        if (typeface is null)
            typeface = SKTypeface.FromFamilyName(fallback);

        return typeface ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _pinDotsPath.Dispose();
        _arrowHeadPath.Dispose();
        _jumperLinePath.Dispose();
        _partWirePath.Dispose();
        _allStripsPath?.Dispose();

        DisposePaints(
            _pasteboardPaint,
            _boardFillPaint,
            _boardBorderPaint,
            _underlayPaint,
            _trenchFillPaint,
            _trenchEdgePaint,
            _railVccPaint,
            _railGndPaint,
            _pinDotPaint,
            _pinHoverFillPaint,
            _pinHoverRingPaint,
            _xrayCriticalFillPaint,
            _xrayCriticalStrokePaint,
            _xrayWarningFillPaint,
            _xrayWarningStrokePaint,
            _xrayOverloadFillPaint,
            _xrayOverloadStrokePaint,
            _xrayInputOnlyFillPaint,
            _xrayInputOnlyStrokePaint,
            _xrayCrossoverFillPaint,
            _xrayCrossoverStrokePaint,
            _componentBoxPaint,
            _componentBoxFlaggedPaint,
            _jumperBoxPaint,
            _jumperLinePaint,
            _leadPaint,
            _ratsnestPaint,
            _ratsnestHeadPaint,
            _rulerBandPaint,
            _rulerBorderPaint,
            _rulerTickPaint,
            _rulerTextPaint,
            _rulerAccentPaint,
            _boardLabelPaint,
            _boardSmallLabelPaint,
            _boardTinyLabelPaint,
            _labelChipPaint,
            _telemetryPanelPaint,
            _telemetryBorderPaint,
            _telemetryStatusPaint,
            _telemetryLinePaint,
            _telemetryFooterPaint,
            _titleBlockFillPaint,
            _titleBlockBorderPaint,
            _titleBlockTextPaint,
            _titleBlockStrongTextPaint,
            _hoverPanelPaint,
            _hoverBorderPaint,
            _hoverTextPaint,
            _legendTextPaint,
            _stripGhostFillPaint,
            _stripGhostStrokePaint,
            _builderPendingFillPaint,
            _builderPendingStrokePaint,
            _builderLinkPaint,
            _partLeadPaint,
            _partHighlightPaint,
            _partResistorBodyPaint,
            _partResistorEdgePaint,
            _partLedBodyPaint,
            _partLedEdgePaint,
            _partLedEmissionPaint,
            _partCapBodyPaint,
            _partCapStripePaint,
            _partCapEdgePaint,
            _partIcBodyPaint,
            _partIcMarkPaint,
            _partIcPinPaint,
            _partIcLegPaint,
            _partButtonBasePaint,
            _partButtonCapPaint,
            _partButtonEdgePaint,
            _partJumperPaint,
            _partJumperPlugPaint,
            _partFlagPaint,
            _partSelectionPaint,
            _partPinNumberPaint,
            _partIcLabelPaint,
            _partCapCeramicTextPaint,
            _partSensorLabelDarkPaint,
            _partSensorBadgeTextPaint,
            _pinPillBgPaint,
            _pinPillBorderPaint,
            _pinVccTextPaint,
            _pinGndTextPaint,
            _pinSignalTextPaint,
            _pinGeneralTextPaint,
            _pinI2cTextPaint,
            _partHeaterGlowPaint,
            _partTempBulbPaint,
            _partEyeletPaint,
            _partUltrasonicBezelPaint,
            _partDiodeGlassBodyPaint,
            _partDiodeRectifierBodyPaint,
            _partDiodeCathodeSilverPaint,
            _partDiodeCathodeBlackPaint,
            _partDiodeEdgePaint,
            _partBuzzerBodyPaint,
            _partBuzzerHolePaint,
            _partBuzzerEdgePaint,
            _partBuzzerPlusPaint,
            _partSensorLdrBodyPaint,
            _partSensorLdrTrackPaint,
            _partSensorThermistorPaint,
            _partSensorMetalPaint,
            _partTiltGoldPaint,
            _partTiltCollarPaint,
            _partPhotodiodeDiePaint,
            _partBondWirePaint,
            _partGlassFramePaint,
            _partIrFilterDomePaint,
            _partIrRayPaint,
            _partMicMetalRimPaint,
            _partMicFeltPortPaint,
            _partMicRingPaint,
            _partPirDomePaint,
            _partPirFacetPaint,
            _partPirTrimpotPaint,
            _partUltrasonicMeshPaint,
            _partCrystalCanPaint,
            _partDht11BodyPaint,
            _partDht11SlotPaint,
            _partDht22BodyPaint,
            _partDht22MeshPaint,
            _partGasMeshPaint,
            _partGasBezelPaint,
            _partGasGauzeBasePaint,
            _partSoundPcbPaint,
            _partBlueTrimpotPaint,
            _partGoldScrewPaint,
            _partHallBodyPaint,
            _partFluxPaint,
            _partPotBodyPaint,
            _partPotDialPaint,
            _partPotSlotPaint,
            _partRegTabPaint,
            _partRegTabHolePaint,
            _partModulePcbPaint,
            _partModuleBluePcbPaint,
            _partUltrasonicCanPaint,
            _partDhtGrillPaint,
            _partShadowPaint,
            _partBatteryBodyPaint,
            _partBatteryGoldPaint,
            _partBatteryTerminalPaint,
            _partBatteryPlusLeadPaint,
            _partBatteryMinusLeadPaint,
            _dmmRedProbePaint,
            _dmmBlackProbePaint,
            _dmmNeedlePaint,
            _dmmWireRedPaint,
            _dmmWireBlackPaint,
            _lcdPcbPaint,
            _lcdBezelPaint,
            _lcdBezelBorderPaint,
            _lcdUnlitScreenPaint,
            _lcdBacklightBluePaint,
            _lcdGlassGlintPaint,
            _lcdCellGridPaint,
            _lcdCharTextPaint,
            _lcdPinHeaderPaint,
            _lcdSilkscreenPaint);

        for (int i = 0; i < _bandPaints.Length; i++)
            _bandPaints[i].Dispose();

        // Typefaces are process-cached native objects; paints release their refs above.
    }

    private static void DisposePaints(params SKPaint[] paints)
    {
        for (int i = 0; i < paints.Length; i++)
            paints[i].Dispose();
    }
}
