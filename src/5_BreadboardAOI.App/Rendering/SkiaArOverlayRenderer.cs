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

    public bool ShowComponentBoxes { get; set; } = true;

    public bool ShowComponentLabels { get; set; } = true;

    public bool ShowHoverInspector { get; set; } = true;

    public bool ShowTelemetry { get; set; } = true;

    public bool ShowTitleBlock { get; set; } = true;

    public bool ShowLegend { get; set; } = true;

    /// <summary>Off by default: the vector board is the CAD drawing, not a photo.</summary>
    public bool ShowCameraUnderlay { get; set; }
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

public sealed class SkiaArOverlayRenderer : IDisposable
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

    // ---- hover state -------------------------------------------------------
    private HoleCoord _hoverHole;
    private bool _hoverValid;
    private float _hoverScreenX;
    private float _hoverScreenY;
    private string _hoverText = string.Empty;
    private string _hoverNetName = string.Empty;
    private DrcStatePacket? _lastRenderedState;

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

        BuildPinDotsPath();
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
    /// point. Returns false (and clears the highlight) when the cursor is off-board.
    /// </summary>
    public bool UpdateHover(float viewportX, float viewportY)
    {
        _hoverScreenX = viewportX;
        _hoverScreenY = viewportY;

        if (!_hasTransform)
            return ApplyHover(false, default);

        SKPoint board = Transform.ToBoard(viewportX, viewportY);
        bool inBounds = board.X >= -12f && board.X <= BoardWidth + 12f
                        && board.Y >= -12f && board.Y <= BoardHeight + 12f;

        HoleCoord hole = default;
        bool found = inBounds && PinMatrixLookup.Shared.TrySnapToHole(board.X, board.Y, out hole);
        return ApplyHover(found, hole);
    }

    public void ClearHover()
    {
        if (!_hoverValid && _hoverText.Length == 0)
            return;

        ApplyHover(false, default);
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
        _titleBlockTextPaint.TextSize = 8f * inverse;
        _titleBlockStrongTextPaint.TextSize = 10f * inverse;
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

        DrawPowerRails(canvas);
        DrawJumperLeads(canvas, state);

        if (Layers.ShowPinMatrixDots)
            canvas.DrawPath(_pinDotsPath, _pinDotPaint);

        if (Layers.ShowHoverInspector)
            DrawHoverStrip(canvas);

        DrawComponentBoxes(canvas, state);

        if (state is not null)
            DrawViolations(canvas, state);

        if (Layers.ShowRatsnestArrows)
            DrawTargetRatsnest(canvas, ratsnest);

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

            _jumperLinePath.Reset();
            _jumperLinePath.MoveTo(component.Pin1Hole.Px, component.Pin1Hole.Py);
            _jumperLinePath.LineTo(component.Pin2Hole.Px, component.Pin2Hole.Py);
            canvas.DrawPath(_jumperLinePath, _jumperLinePaint);
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
        if (state is null || !Layers.ShowComponentBoxes)
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

            canvas.DrawRect(box, boxPaint);

            if (!isJumper)
            {
                canvas.DrawCircle(component.Pin1Hole.Px, component.Pin1Hole.Py, 2.2f, _leadPaint);
                canvas.DrawCircle(component.Pin2Hole.Px, component.Pin2Hole.Py, 2.2f, _leadPaint);
            }

            if (!isJumper
                && Layers.ShowComponentLabels
                && _componentLabels.TryGetValue(detection.TrackId, out string? label))
            {
                float width = _boardSmallLabelPaint.MeasureText(label);
                float baseline = MathF.Max(_boardSmallLabelPaint.TextSize, box.Top - 4f / _scale);
                canvas.DrawText(label, box.MidX - width * 0.5f, baseline, _boardSmallLabelPaint);
            }
        }
    }

    private static bool IsFlagged(IReadOnlyList<DrcViolation> violations, in SnappedComponent component)
    {
        for (int i = 0; i < violations.Count; i++)
        {
            DrcViolation violation = violations[i];
            if (violation.OffendingStripId == component.Pin1Hole.StripId
                || violation.OffendingStripId == component.Pin2Hole.StripId)
            {
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

            if (Layers.ShowXRayStrips)
            {
                SKPaint fill = critical ? _xrayCriticalFillPaint : _xrayWarningFillPaint;
                SKPaint stroke = critical ? _xrayCriticalStrokePaint : _xrayWarningStrokePaint;

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
                    critical ? _xrayCriticalStrokePaint : _xrayWarningStrokePaint);
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

            DrawViolationBadge(canvas, violation, critical);
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

    private void DrawViolationBadge(SKCanvas canvas, in DrcViolation violation, bool critical)
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
        canvas.DrawRoundRect(badge, 2f, 2f, critical ? _xrayCriticalStrokePaint : _xrayWarningStrokePaint);
        canvas.DrawText(label, x + 6f, y + height - 4.5f, _telemetryLinePaint);
    }

    private static string ViolationLabel(ViolationCode code) => code switch
    {
        ViolationCode.ErcVccGndDirectShort => "ERC 100  VCC-GND SHORT",
        ViolationCode.ErcSameStripShort => "ERC 101  SAME-STRIP SHORT",
        ViolationCode.ErcFloatingLead => "ERC 102  FLOATING LEAD",
        ViolationCode.ErcReversedPolarity => "ERC 103  REVERSED POLARITY",
        ViolationCode.DrcSchematicNetMismatch => "DRC 200  NET MISMATCH",
        _ => "DRC VIOLATION"
    };

    private void UpdateHudCache(DrcStatePacket? state)
    {
        long bucket = (state?.FrameNumber ?? 0) / 15;
        if (_hudInitialized && state?.StateHash == _hudHash && bucket == _hudFrameBucket)
            return;

        _hudInitialized = true;
        _hudHash = state?.StateHash ?? int.MinValue;
        _hudFrameBucket = bucket;

        if (state is null)
        {
            _hudStatus = "AWAITING FRAME";
            _hudStatusColor = new SKColor(0x9A, 0x9A, 0x9A);
            _hudMetrics = "YOLO --.- MS   DRC --.--- MS";
            _hudFooter = "830 TIE POINTS   NO PACKET";
            return;
        }

        int critical = 0;
        int warnings = 0;
        for (int i = 0; i < state.Violations.Count; i++)
        {
            if (state.Violations[i].Severity == DrcSeverity.Critical)
                critical++;
            else
                warnings++;
        }

        if (critical > 0)
        {
            _hudStatus = critical == 1 ? "1 CRITICAL SHORT" : $"{critical} CRITICAL SHORTS";
            _hudStatusColor = new SKColor(0xD7, 0x37, 0x3F);
        }
        else if (warnings > 0)
        {
            _hudStatus = warnings == 1 ? "1 WARNING" : $"{warnings} WARNINGS";
            _hudStatusColor = new SKColor(0xE6, 0x86, 0x19);
        }
        else if (state.Components.Count == 0)
        {
            _hudStatus = "NO DETECTIONS";
            _hudStatusColor = new SKColor(0x9A, 0x9A, 0x9A);
        }
        else
        {
            _hudStatus = "DRC PASS - NETLIST CLEAN";
            _hudStatusColor = new SKColor(0x2D, 0x9D, 0x78);
        }

        _hudMetrics = string.Create(
            CultureInfo.InvariantCulture,
            $"YOLO {state.YoloInferenceMs:0.0} MS   DRC {state.DrcComputeMs:0.000} MS");

        _hudFooter = string.Create(
            CultureInfo.InvariantCulture,
            $"FRAME {state.FrameNumber}   PINS 830   PARTS {state.Components.Count}");
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

        float x = _hoverScreenX + 16f;
        float y = _hoverScreenY + 18f;

        if (x + width > viewportWidth - 8f)
            x = _hoverScreenX - width - 12f;
        if (y + height > viewportHeight - 8f)
            y = _hoverScreenY - height - 12f;

        x = MathF.Max(4f, x);
        y = MathF.Max(4f, y);

        var panel = new SKRect(x, y, x + width, y + height);
        canvas.DrawRoundRect(panel, 2f, 2f, _hoverPanelPaint);
        canvas.DrawRoundRect(panel, 2f, 2f, _hoverBorderPaint);
        canvas.DrawText(_hoverText, x + 8f, y + 16f, _hoverTextPaint);
    }

    private void ComputeLayout(int viewportWidth, int viewportHeight)
    {
        float rulerTop = Layers.ShowRulers ? RulerTopHeight : 0f;
        float rulerLeft = Layers.ShowRulers ? RulerLeftWidth : 0f;

        float availableWidth = MathF.Max(1f, viewportWidth - rulerLeft - CanvasMargin * 2f);
        float availableHeight = MathF.Max(1f, viewportHeight - rulerTop - CanvasMargin * 2f);

        float scale = MathF.Min(availableWidth / BoardWidth, availableHeight / BoardHeight);
        _scale = MathF.Max(0.05f, scale);
        _boardPixelWidth = BoardWidth * _scale;
        _boardPixelHeight = BoardHeight * _scale;
        _boardLeft = rulerLeft + CanvasMargin + (availableWidth - _boardPixelWidth) * 0.5f;
        _boardTop = rulerTop + CanvasMargin + (availableHeight - _boardPixelHeight) * 0.5f;
        _hasTransform = true;
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
            _componentLabels[detection.TrackId] = string.Create(
                CultureInfo.InvariantCulture,
                $"{KindLabel(detection.Kind)} #{detection.TrackId}  {component.Pin1Hole.HoleId}>{component.Pin2Hole.HoleId}");
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
        _ => "PART"
    };

    private void BuildPinDotsPath()
    {
        _pinDotsPath.Reset();
        ReadOnlySpan<HoleCoord> holes = PinMatrixLookup.Shared.Holes;
        for (int i = 0; i < holes.Length; i++)
            _pinDotsPath.AddCircle(holes[i].Px, holes[i].Py, 2.1f);
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
            _legendTextPaint);

        // Typefaces are process-cached native objects; paints release their refs above.
    }

    private static void DisposePaints(params SKPaint[] paints)
    {
        for (int i = 0; i < paints.Length; i++)
            paints[i].Dispose();
    }
}
