using System;
using System.Collections.Generic;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.DrcEngine;
using SkiaSharp;

namespace BreadboardAOI.App.Rendering;

/// <summary>
/// Docking margin outside the 830-tie-point breadboard where off-board microcontrollers are placed.
/// </summary>
public enum OffBoardDockPlacement
{
    BottomMargin,
    RightMargin
}

/// <summary>
/// Interactive and placement state for a single off-board microcontroller rendered in the CAD viewport.
/// </summary>
public sealed record OffBoardMicrocontrollerVisualState(
    string BoardInstanceId,
    MicrocontrollerBoardType BoardType,
    OffBoardDockPlacement DockPlacement,
    int DockSlotIndex,
    string? HoveredPinCanonicalId,
    IReadOnlySet<string> ConnectedPinCanonicalIds,
    IReadOnlySet<string> ViolatingPinCanonicalIds);

/// <summary>
/// Computed 2D canvas geometry for a rendered microcontroller board and every pin socket on it.
/// </summary>
public sealed record RenderedMicrocontrollerLayout(
    string BoardInstanceId,
    MicrocontrollerBoardType BoardType,
    SKRect BoardBoundsCanvas,
    IReadOnlyDictionary<string, SKPoint> PinSocketCanvasCenters,
    IReadOnlyDictionary<string, SKRect> PinHitBoxesCanvas,
    SKRect UsbReceptacleRectCanvas = default,
    UsbCableKind NativeUsbKind = UsbCableKind.None,
    bool IsUsbReceptacleVertical = false);

/// <summary>
/// How far the rotated pin-label badges and the silkscreen title reach outside
/// each edge of a docked microcontroller PCB, in screen pixels.
/// </summary>
public readonly record struct McuLabelExtents(
    float TopPx,
    float BottomPx,
    float LeftPx,
    float RightPx);

/// <summary>
/// High-precision 2.5D/3D SkiaSharp vector CAD renderer for off-board microcontrollers
/// (Arduino Uno R3/R4, Arduino Mega 2560, ESP32 DevKit V1, WEMOS ESP32-S2 Mini, STM32F103C8T6 Blue Pill).
/// Draws realistic multi-layer FR4 PCB substrates with bevels and shadows, 3D connectors (USB-B, micro-USB, USB-C, DC barrel jack),
/// 3D silicon packages (DIP-28, TQFP-100, LQFP-48, RF shield), crystal resonators, 3D female headers with leaf-spring contacts,
/// and crisp high-legibility color-coded pin labels.
/// </summary>
public sealed class OffBoardMicrocontrollerRenderer
{
    /// <summary>Gap between the breadboard edge and a docked MCU PCB, in millimetres.</summary>
    public const float DockGapMm = 18.0f;

    /// <summary>Extra clearance below a docked MCU for its silkscreen title, in millimetres.</summary>
    public const float TitleClearanceMm = 6.0f;

    private static readonly SKTypeface s_uiBoldTypeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
    private static readonly SKTypeface s_uiRegularTypeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    private static readonly SKTypeface s_monoBoldTypeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold);
    private static readonly SKTypeface s_serifBoldTypeface = SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold);

    private readonly DevBoardAndModuleRuleEngine _ruleEngine;

    // Color palette for pin labels per specification
    private static readonly SKColor PowerRedBadge = SKColor.Parse("#D32F2F");
    private static readonly SKColor PowerRedText = SKColor.Parse("#FFCDD2");
    private static readonly SKColor GroundDarkBadge = SKColor.Parse("#1B1F23");
    private static readonly SKColor GroundWhiteText = SKColor.Parse("#F5F5F5");
    private static readonly SKColor CommCyanBadge = SKColor.Parse("#006064");
    private static readonly SKColor CommCyanText = SKColor.Parse("#84FFFF");
    private static readonly SKColor GeneralIoBadge = SKColor.Parse("#263238");
    private static readonly SKColor GeneralIoWhiteText = SKColor.Parse("#ECEFF1");

    public OffBoardMicrocontrollerRenderer(DevBoardAndModuleRuleEngine ruleEngine)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
    }

    /// <summary>
    /// Computes the exact canvas coordinates of the microcontroller PCB and every pin socket without drawing,
    /// enabling jumper wire snapping and hit-testing before or during rendering.
    /// </summary>
    public RenderedMicrocontrollerLayout ComputeBoardLayout(
        OffBoardMicrocontrollerVisualState state,
        SKRect breadboardBounds,
        float pixelsPerMm)
        => ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, overrideOriginX: null, overrideOriginY: null);

    /// <summary>
    /// Computes non-overlapping canvas layouts for all docked microcontrollers together so dual-board
    /// configurations (e.g., Mega 2560 + Uno, or Uno + Wemos S2 Mini) never collide horizontally or vertically.
    /// </summary>
    public List<RenderedMicrocontrollerLayout> ComputeBoardLayouts(
        IReadOnlyList<OffBoardMicrocontrollerVisualState> states,
        SKRect breadboardBounds,
        float pixelsPerMm)
    {
        ArgumentNullException.ThrowIfNull(states);
        var layouts = new List<RenderedMicrocontrollerLayout>(states.Count);
        if (states.Count == 0)
            return layouts;

        int bottomCount = 0;
        int rightCount = 0;
        foreach (OffBoardMicrocontrollerVisualState s in states)
        {
            if (s.BoardType == MicrocontrollerBoardType.None) continue;
            if (s.DockPlacement == OffBoardDockPlacement.BottomMargin) bottomCount++;
            else rightCount++;
        }

        float bottomCursorX = breadboardBounds.Left;
        float rightCursorY = breadboardBounds.Top;
        float marginGapPx = DockGapMm * pixelsPerMm;
        float slotGapPx = 14.0f * pixelsPerMm;
        float titlePx = TitleClearanceMm * pixelsPerMm;

        foreach (OffBoardMicrocontrollerVisualState state in states)
        {
            if (state.BoardType == MicrocontrollerBoardType.None)
            {
                layouts.Add(ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, null, null));
                continue;
            }

            MicrocontrollerBoardProfile profile = _ruleEngine.GetBoardProfile(state.BoardType);
            McuLabelExtents ext = MeasureLabelExtents(profile, pixelsPerMm);
            float boardWidthPx = profile.WidthMm * pixelsPerMm;
            float boardHeightPx = profile.HeightMm * pixelsPerMm;

            if (state.DockPlacement == OffBoardDockPlacement.BottomMargin)
            {
                if (bottomCount <= 1)
                {
                    layouts.Add(ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, null, null));
                }
                else
                {
                    float originX = bottomCursorX + ext.LeftPx;
                    float originY = breadboardBounds.Bottom + marginGapPx + ext.TopPx;
                    bottomCursorX = originX + boardWidthPx + ext.RightPx + slotGapPx;
                    layouts.Add(ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, originX, originY));
                }
            }
            else
            {
                if (rightCount <= 1)
                {
                    layouts.Add(ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, null, null));
                }
                else
                {
                    float originX = breadboardBounds.Right + marginGapPx + ext.LeftPx;
                    float originY = rightCursorY + ext.TopPx;
                    rightCursorY = originY + boardHeightPx + ext.BottomPx + slotGapPx + titlePx;
                    layouts.Add(ComputeBoardLayoutCore(state, breadboardBounds, pixelsPerMm, originX, originY));
                }
            }
        }

        return layouts;
    }

    private RenderedMicrocontrollerLayout ComputeBoardLayoutCore(
        OffBoardMicrocontrollerVisualState state,
        SKRect breadboardBounds,
        float pixelsPerMm,
        float? overrideOriginX,
        float? overrideOriginY)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.BoardType == MicrocontrollerBoardType.None)
            return new RenderedMicrocontrollerLayout(state.BoardInstanceId, state.BoardType, SKRect.Empty, new Dictionary<string, SKPoint>(), new Dictionary<string, SKRect>());

        MicrocontrollerBoardProfile profile = _ruleEngine.GetBoardProfile(state.BoardType);
        float boardWidthPx = profile.WidthMm * pixelsPerMm;
        float boardHeightPx = profile.HeightMm * pixelsPerMm;
        float marginGapPx = DockGapMm * pixelsPerMm;
        McuLabelExtents labelExtents = MeasureLabelExtents(profile, pixelsPerMm);

        float originX;
        float originY;

        if (overrideOriginX.HasValue && overrideOriginY.HasValue)
        {
            originX = overrideOriginX.Value;
            originY = overrideOriginY.Value;
        }
        else if (state.DockPlacement == OffBoardDockPlacement.BottomMargin)
        {
            float centeredLeft = state.DockSlotIndex == 0
                ? breadboardBounds.Left + ((breadboardBounds.Width - boardWidthPx) * 0.5f)
                : breadboardBounds.Left;
            originX = centeredLeft + labelExtents.LeftPx
                + (state.DockSlotIndex * (boardWidthPx + (14.0f * pixelsPerMm)));
            originY = breadboardBounds.Bottom + marginGapPx + labelExtents.TopPx;
        }
        else
        {
            originX = breadboardBounds.Right + marginGapPx + labelExtents.LeftPx;
            originY = breadboardBounds.Top + labelExtents.TopPx
                + (state.DockSlotIndex * (boardHeightPx + (14.0f * pixelsPerMm)));
        }

        var boardRect = new SKRect(originX, originY, originX + boardWidthPx, originY + boardHeightPx);
        var pinCenters = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
        var pinHitBoxes = new Dictionary<string, SKRect>(StringComparer.OrdinalIgnoreCase);
        float hitRadius = 1.45f * pixelsPerMm;

        foreach (McuPinDefinition pin in profile.Pins)
        {
            float cx = originX + (pin.LocalXMm * pixelsPerMm);
            float cy = originY + (pin.LocalYMm * pixelsPerMm);
            var pt = new SKPoint(cx, cy);
            var hitRect = new SKRect(cx - hitRadius, cy - hitRadius, cx + hitRadius, cy + hitRadius);

            pinCenters[pin.CanonicalId] = pt;
            pinHitBoxes[pin.CanonicalId] = hitRect;
        }

        var usbGeom = ComputeUsbReceptacleGeometry(state.BoardType, boardRect, pixelsPerMm);

        return new RenderedMicrocontrollerLayout(
            BoardInstanceId: state.BoardInstanceId,
            BoardType: state.BoardType,
            BoardBoundsCanvas: boardRect,
            PinSocketCanvasCenters: pinCenters,
            PinHitBoxesCanvas: pinHitBoxes,
            UsbReceptacleRectCanvas: usbGeom.UsbRect,
            NativeUsbKind: usbGeom.NativeKind,
            IsUsbReceptacleVertical: usbGeom.IsVertical);
    }

    /// <summary>
    /// Computes the exact USB receptacle rectangle, native connector standard, and
    /// physical insertion orientation (vertical bottom-edge vs horizontal left-edge)
    /// for any supported microcontroller PCB.
    /// </summary>
    public static (SKRect UsbRect, UsbCableKind NativeKind, bool IsVertical) ComputeUsbReceptacleGeometry(
        MicrocontrollerBoardType boardType,
        SKRect rect,
        float pxPerMm)
    {
        if (rect.IsEmpty || pxPerMm <= 0.001f || boardType == MicrocontrollerBoardType.None)
            return (SKRect.Empty, UsbCableKind.None, false);

        return boardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 =>
            (
                new SKRect(
                    rect.Left - (2.6f * pxPerMm),
                    rect.Top + (9.0f * pxPerMm),
                    rect.Left + (12.5f * pxPerMm),
                    rect.Top + (21.5f * pxPerMm)),
                UsbCableKind.UsbB,
                false
            ),
            MicrocontrollerBoardType.WemosEsp32S2Mini =>
            (
                new SKRect(
                    rect.MidX - (4.4f * pxPerMm),
                    rect.Bottom - (5.5f * pxPerMm),
                    rect.MidX + (4.4f * pxPerMm),
                    rect.Bottom + (1.5f * pxPerMm)),
                UsbCableKind.UsbTypeC,
                true
            ),
            MicrocontrollerBoardType.Stm32BluePillF103C8 =>
            (
                new SKRect(
                    rect.Left - (1.4f * pxPerMm),
                    rect.MidY - (3.8f * pxPerMm),
                    rect.Left + (5.2f * pxPerMm),
                    rect.MidY + (3.8f * pxPerMm)),
                UsbCableKind.MicroUsb,
                false
            ),
            _ =>
            (
                new SKRect(
                    rect.Left - (1.6f * pxPerMm),
                    rect.MidY - (4.0f * pxPerMm),
                    rect.Left + (5.8f * pxPerMm),
                    rect.MidY + (4.0f * pxPerMm)),
                UsbCableKind.MicroUsb,
                false
            )
        };
    }

    /// <summary>
    /// Measures how far the rotated pin-label badges and the silkscreen title
    /// reach outside each edge of the PCB. The viewport layout uses this to
    /// reserve a dock margin that never clips a label or the board caption.
    /// </summary>
    public McuLabelExtents MeasureLabelExtents(MicrocontrollerBoardType boardType, float pixelsPerMm) =>
        MeasureLabelExtents(_ruleEngine.GetBoardProfile(boardType), pixelsPerMm);

    public McuLabelExtents MeasureLabelExtents(MicrocontrollerBoardProfile profile, float pixelsPerMm)
    {
        ArgumentNullException.ThrowIfNull(profile);

        float fontSize = GetPinLabelFontSize(pixelsPerMm);
        float padX = 0.50f * pixelsPerMm;
        float offsetFromSocket = (2.40f * 0.65f * pixelsPerMm) + (0.60f * pixelsPerMm);
        float boardWidthPx = profile.WidthMm * pixelsPerMm;
        float boardHeightPx = profile.HeightMm * pixelsPerMm;
        float top = 0f;
        float bottom = 0f;
        float left = 0f;
        float right = 0f;

        foreach (McuPinDefinition pin in profile.Pins)
        {
            float badgeWidth = EstimateLabelWidth(pin.DisplayLabel, fontSize) + (padX * 2f);
            float reach = offsetFromSocket + badgeWidth;

            // In Wemos S2 Mini, outer pins extend 2 badge widths out into margin
            if (profile.BoardType == MicrocontrollerBoardType.WemosEsp32S2Mini)
            {
                bool isOuter = pin.CanonicalId is "RST" or "IO3" or "IO5" or "IO7" or "IO9" or "IO11" or "IO12" or "3V3" or
                                                  "IO39" or "IO37" or "IO35" or "IO33" or "IO18" or "IO16" or "GND_2" or "VBUS";
                if (isOuter)
                    reach = offsetFromSocket + (badgeWidth * 2f) + (1.8f * pixelsPerMm);
            }

            float px = pin.LocalXMm * pixelsPerMm;
            float py = pin.LocalYMm * pixelsPerMm;

            switch (pin.LabelAlignment)
            {
                case PinLabelAlignment.AboveSocket:
                    top = MathF.Max(top, reach - py);
                    break;
                case PinLabelAlignment.BelowSocket:
                    bottom = MathF.Max(bottom, reach - (boardHeightPx - py));
                    break;
                case PinLabelAlignment.LeftOfSocket:
                    left = MathF.Max(left, reach - px);
                    break;
                case PinLabelAlignment.RightOfSocket:
                    right = MathF.Max(right, reach - (boardWidthPx - px));
                    break;
            }
        }

        if (profile.BoardType == MicrocontrollerBoardType.WemosEsp32S2Mini)
        {
            // Reserve clearance below the vertical bottom USB-C receptacle, plugged ANKER 100W barrel,
            // and braided cable turn so the silkscreen caption never overlaps the connector or cable.
            bottom = MathF.Max(bottom, 10.6f * pixelsPerMm);
        }

        return new McuLabelExtents(
            MathF.Max(0f, top),
            MathF.Max(0f, bottom),
            MathF.Max(0f, left),
            MathF.Max(0f, right));
    }

    /// <summary>Pin label size, with a floor that still fits the 2.54 mm header pitch.</summary>
    public static float GetPinLabelFontSize(float pixelsPerMm) =>
        MathF.Max(5.8f, 1.20f * pixelsPerMm);

    private static float EstimateLabelWidth(string label, float fontSize) =>
        string.IsNullOrEmpty(label) ? 0f : label.Length * fontSize * 0.62f;

    /// <summary>
    /// Renders the complete 2.5D/3D vector CAD illustration of an off-board microcontroller onto the SkiaSharp canvas
    /// and returns its computed pin socket coordinates.
    /// </summary>
    public RenderedMicrocontrollerLayout RenderMicrocontroller(
        SKCanvas canvas,
        OffBoardMicrocontrollerVisualState state,
        SKRect breadboardBounds,
        float pixelsPerMm)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(state);

        if (state.BoardType == MicrocontrollerBoardType.None)
            return new RenderedMicrocontrollerLayout(state.BoardInstanceId, state.BoardType, SKRect.Empty, new Dictionary<string, SKPoint>(), new Dictionary<string, SKRect>());

        RenderedMicrocontrollerLayout layout = ComputeBoardLayout(state, breadboardBounds, pixelsPerMm);
        return RenderMicrocontroller(canvas, state, layout, pixelsPerMm);
    }

    /// <summary>
    /// Renders an off-board microcontroller at a precomputed non-overlapping layout.
    /// </summary>
    public RenderedMicrocontrollerLayout RenderMicrocontroller(
        SKCanvas canvas,
        OffBoardMicrocontrollerVisualState state,
        RenderedMicrocontrollerLayout layout,
        float pixelsPerMm)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(layout);

        if (state.BoardType == MicrocontrollerBoardType.None || layout.BoardBoundsCanvas.IsEmpty)
            return layout;

        MicrocontrollerBoardProfile profile = _ruleEngine.GetBoardProfile(state.BoardType);
        SKRect rect = layout.BoardBoundsCanvas;

        canvas.Save();

        // 1. Draw 3D drop shadow and FR4 substrate with bevel extrusion
        DrawPcbSubstrate(canvas, profile, rect, pixelsPerMm);

        // 2. Draw board-specific authentic 3D hardware components
        switch (state.BoardType)
        {
            case MicrocontrollerBoardType.ArduinoUnoR3R4:
                DrawArduinoUnoHardware(canvas, rect, pixelsPerMm);
                break;
            case MicrocontrollerBoardType.ArduinoMega2560:
                DrawArduinoMegaHardware(canvas, rect, pixelsPerMm);
                break;
            case MicrocontrollerBoardType.Esp32DevKitV1:
                DrawEsp32DevKitHardware(canvas, rect, pixelsPerMm);
                break;
            case MicrocontrollerBoardType.WemosEsp32S2Mini:
                DrawWemosEsp32S2MiniHardware(canvas, rect, pixelsPerMm);
                break;
            case MicrocontrollerBoardType.Stm32BluePillF103C8:
                DrawStm32BluePillHardware(canvas, rect, pixelsPerMm);
                break;
        }

        // 3. Draw 3D female header socket strips with internal leaf-spring contacts and crisp labels
        DrawPinSocketsAndLabels(canvas, profile, state, layout, pixelsPerMm);

        // 4. Draw board title silkscreen banner
        DrawSilkscreenBoardTitle(canvas, profile, state.BoardInstanceId, rect, pixelsPerMm);

        canvas.Restore();
        return layout;
    }

    private static SKPath BuildAuthenticPcbContourPath(
        MicrocontrollerBoardProfile profile,
        SKRect rect,
        float pxPerMm)
    {
        var path = new SKPath();
        float r = 1.8f * pxPerMm;

        if (profile.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560)
        {
            float stepIn = 3.2f * pxPerMm;
            float stepTopY = rect.Top + (7.0f * pxPerMm);
            float stepBotY = rect.Bottom - (9.5f * pxPerMm);
            float rightInner = rect.Right - stepIn;

            path.MoveTo(rect.Left + r, rect.Top);
            path.LineTo(rightInner - r, rect.Top);
            path.LineTo(rect.Right, stepTopY);
            path.LineTo(rect.Right, stepBotY);
            path.LineTo(rightInner - r, rect.Bottom);
            path.LineTo(rect.Left + r, rect.Bottom);
            path.QuadTo(rect.Left, rect.Bottom, rect.Left, rect.Bottom - r);
            path.LineTo(rect.Left, rect.Top + r);
            path.QuadTo(rect.Left, rect.Top, rect.Left + r, rect.Top);
            path.Close();
            return path;
        }

        path.AddRoundRect(rect, r, r);
        return path;
    }

    private static void DrawPcbSubstrate(
        SKCanvas canvas,
        MicrocontrollerBoardProfile profile,
        SKRect rect,
        float pxPerMm)
    {
        using SKPath contourPath = BuildAuthenticPcbContourPath(profile, rect, pxPerMm);

        // 1. Soft multi-layer diffuse drop shadow
        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.8f * pxPerMm);
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 110),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = shadowBlur
        };
        canvas.Save();
        canvas.Translate(1.6f * pxPerMm, 2.6f * pxPerMm);
        canvas.DrawPath(contourPath, shadowPaint);
        canvas.Restore();

        // 2. 3D FR4 substrate thickness extrusion (bottom & right beveled edge)
        SKColor primaryColor = SKColor.Parse(profile.PcbPrimaryHexColor);
        SKColor secondaryColor = SKColor.Parse(profile.PcbSecondaryHexColor);
        SKColor edgeDarkColor = new SKColor(
            (byte)Math.Max(0, primaryColor.Red - 50),
            (byte)Math.Max(0, primaryColor.Green - 50),
            (byte)Math.Max(0, primaryColor.Blue - 50));

        using var extrusionPaint = new SKPaint
        {
            Color = edgeDarkColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        float depthOffset = MathF.Max(1.8f, 1.1f * pxPerMm);
        canvas.Save();
        canvas.Translate(depthOffset * 0.65f, depthOffset);
        canvas.DrawPath(contourPath, extrusionPaint);
        canvas.Restore();

        // 3. Solder mask surface gradient
        using var pcbShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { primaryColor, secondaryColor },
            null,
            SKShaderTileMode.Clamp);
        using var pcbPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = pcbShader
        };
        canvas.DrawPath(contourPath, pcbPaint);

        // 4. Subtle copper ground hatching and copper pour trace accents
        DrawPcbCopperArtwork(canvas, rect, pxPerMm);

        // 5. Polished metallic edge highlight
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 70),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 0.28f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawPath(contourPath, borderPaint);

        // 6. Gold-plated mounting holes with annular rings and thermal relief spokes
        if (profile.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560)
        {
            DrawMountingHole(canvas, rect.Left + (14.0f * pxPerMm), rect.Top + (2.5f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Left + (15.2f * pxPerMm), rect.Bottom - (2.5f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Left + (66.0f * pxPerMm), rect.Top + (7.6f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Left + (66.0f * pxPerMm), rect.Bottom - (15.2f * pxPerMm), pxPerMm);
        }
        else if (profile.BoardType == MicrocontrollerBoardType.Esp32DevKitV1)
        {
            DrawMountingHole(canvas, rect.Left + (3.0f * pxPerMm), rect.Top + (3.0f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Left + (3.0f * pxPerMm), rect.Bottom - (3.0f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Right - (3.0f * pxPerMm), rect.Top + (3.0f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Right - (3.0f * pxPerMm), rect.Bottom - (3.0f * pxPerMm), pxPerMm);
        }
        else if (profile.BoardType == MicrocontrollerBoardType.WemosEsp32S2Mini)
        {
            DrawMountingHole(canvas, rect.Left + (2.8f * pxPerMm), rect.Top + (3.5f * pxPerMm), pxPerMm);
            DrawMountingHole(canvas, rect.Right - (2.8f * pxPerMm), rect.Top + (3.5f * pxPerMm), pxPerMm);
        }
    }

    private static void DrawPcbCopperArtwork(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        using var tracePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 18),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.8f, 0.20f * pxPerMm),
            IsAntialias = true
        };

        // Draw elegant decorative silkscreen trace lines across board margin
        float y1 = rect.Top + (6f * pxPerMm);
        float y2 = rect.Bottom - (8f * pxPerMm);
        canvas.DrawLine(rect.Left + (18f * pxPerMm), y1, rect.Right - (12f * pxPerMm), y1, tracePaint);
        canvas.DrawLine(rect.Left + (22f * pxPerMm), y2, rect.Right - (8f * pxPerMm), y2, tracePaint);

        // Subtle ground vias with annular rings
        DrawGoldVia(canvas, rect.Left + (28f * pxPerMm), rect.Top + (4.5f * pxPerMm), pxPerMm);
        DrawGoldVia(canvas, rect.Right - (18f * pxPerMm), rect.Top + (4.5f * pxPerMm), pxPerMm);
        DrawGoldVia(canvas, rect.Left + (32f * pxPerMm), rect.Bottom - (4.5f * pxPerMm), pxPerMm);
    }

    private static void DrawGoldVia(SKCanvas canvas, float cx, float cy, float pxPerMm)
    {
        using var ring = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var hole = new SKPaint { Color = SKColor.Parse("#1A1A1A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 0.9f * pxPerMm, ring);
        canvas.DrawCircle(cx, cy, 0.45f * pxPerMm, hole);
    }

    private static void DrawMountingHole(SKCanvas canvas, float cx, float cy, float pxPerMm)
    {
        // Drop shadow for mounting hole
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx + (0.3f * pxPerMm), cy + (0.3f * pxPerMm), 2.0f * pxPerMm, shadow);

        // Gold-plated outer annular ring
        using var ringPaint = new SKPaint
        {
            Color = SKColor.Parse("#D4AF37"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        // Outer shiny rim
        using var rimPaint = new SKPaint
        {
            Color = SKColor.Parse("#FFF176"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.8f, 0.22f * pxPerMm),
            IsAntialias = true
        };
        // Dark interior drill barrel
        using var drillPaint = new SKPaint
        {
            Color = SKColor.Parse("#121212"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        canvas.DrawCircle(cx, cy, 2.0f * pxPerMm, ringPaint);
        canvas.DrawCircle(cx, cy, 1.95f * pxPerMm, rimPaint);
        canvas.DrawCircle(cx, cy, 1.15f * pxPerMm, drillPaint);

        // Metallic interior drill shadow reflection
        using var depthShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.35f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawCircle(cx, cy, 1.0f * pxPerMm, depthShadow);
    }

    private static void DrawArduinoUnoHardware(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        var usbRect = new SKRect(
            rect.Left - (2.6f * pxPerMm),
            rect.Top + (9.0f * pxPerMm),
            rect.Left + (12.5f * pxPerMm),
            rect.Top + (21.5f * pxPerMm));
        DrawUsbBConnector(canvas, usbRect, pxPerMm);

        var barrelRect = new SKRect(
            rect.Left - (2.2f * pxPerMm),
            rect.Bottom - (17.0f * pxPerMm),
            rect.Left + (13.0f * pxPerMm),
            rect.Bottom - (3.0f * pxPerMm));
        DrawDarkBarrelJack(canvas, barrelRect, "7-12V DC", pxPerMm);

        var crystalRect = new SKRect(
            rect.Left + (20.5f * pxPerMm),
            rect.Top + (18.5f * pxPerMm),
            rect.Left + (30.5f * pxPerMm),
            rect.Top + (23.2f * pxPerMm));
        DrawCrystalCan(canvas, crystalRect, "16.000MHz", pxPerMm);

        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (6.2f * pxPerMm), rect.Top + (4.5f * pxPerMm)),
            "RESET",
            SKColor.Parse("#E53935"),
            pxPerMm);

        // ATmega328P DIP-28 package with 3D lead legs, dual-wipe socket, and laser etch
        var dipRect = new SKRect(
            rect.Left + (30.5f * pxPerMm),
            rect.Top + (30.5f * pxPerMm),
            rect.Left + (64.5f * pxPerMm),
            rect.Top + (41.0f * pxPerMm));
        DrawDipIcPackage(canvas, dipRect, "ATMEGA328P-PU", 14, pxPerMm);

        // Power and L indicator LEDs
        DrawStatusLed(canvas, rect.Left + (28.0f * pxPerMm), rect.Top + (11.0f * pxPerMm), "ON", SKColor.Parse("#00E676"), pxPerMm);
        DrawStatusLed(canvas, rect.Left + (28.0f * pxPerMm), rect.Top + (15.0f * pxPerMm), "L", SKColor.Parse("#FFD600"), pxPerMm);

        // SOT-223 3.3V / 5V voltage regulator IC
        var regRect = new SKRect(
            rect.Left + (15.5f * pxPerMm),
            rect.Bottom - (12.0f * pxPerMm),
            rect.Left + (21.5f * pxPerMm),
            rect.Bottom - (6.5f * pxPerMm));
        DrawSot223Regulator(canvas, regRect, "AMS1117", pxPerMm);

        // Authentic Arduino Uno PCB silkscreen artwork & pin markings
        DrawUnoSilkscreen(canvas, rect, pxPerMm);
    }

    private static void DrawUnoSilkscreen(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        using var whitePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 230),
            TextSize = Math.Max(5.2f, 0.88f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        using var subTitlePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 200),
            TextSize = Math.Max(5.2f, 0.85f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            IsAntialias = true
        };
        using var linePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 120),
            StrokeWidth = Math.Max(0.6f, 0.18f * pxPerMm),
            IsAntialias = true
        };

        // Brand logo
        canvas.DrawText("ARDUINO UNO R3", rect.Left + (34.0f * pxPerMm), rect.Top + (13.5f * pxPerMm), subTitlePaint);

        // Top Digital Section Silkscreen
        canvas.DrawText("DIGITAL (PWM ~)", rect.Left + (32.0f * pxPerMm), rect.Top + (7.0f * pxPerMm), subTitlePaint);
        canvas.DrawLine(rect.Left + (15.0f * pxPerMm), rect.Top + (7.4f * pxPerMm), rect.Left + (62.0f * pxPerMm), rect.Top + (7.4f * pxPerMm), linePaint);

        (string name, float xMm)[] topPins =
        [
            ("SCL", 15.46f), ("SDA", 18.00f), ("ARF", 20.54f), ("GND", 23.08f),
            ("13", 25.62f), ("12", 28.16f), ("~11", 30.70f), ("~10", 33.24f), ("~9", 35.78f), ("8", 38.32f),
            ("7", 43.40f), ("~6", 45.94f), ("~5", 48.48f), ("4", 51.02f), ("~3", 53.56f), ("2", 56.10f),
            ("TX", 58.64f), ("RX", 61.18f)
        ];
        float topTextY = rect.Top + (5.3f * pxPerMm);
        foreach (var (lbl, xMm) in topPins)
        {
            canvas.DrawText(lbl, rect.Left + (xMm * pxPerMm), topTextY, whitePaint);
        }

        // Bottom Power & Analog Sections Silkscreen
        canvas.DrawText("POWER", rect.Left + (18.0f * pxPerMm), rect.Bottom - (7.0f * pxPerMm), subTitlePaint);
        canvas.DrawText("ANALOG IN", rect.Left + (38.0f * pxPerMm), rect.Bottom - (7.0f * pxPerMm), subTitlePaint);
        canvas.DrawLine(rect.Left + (17.5f * pxPerMm), rect.Bottom - (6.4f * pxPerMm), rect.Left + (52.0f * pxPerMm), rect.Bottom - (6.4f * pxPerMm), linePaint);

        (string name, float xMm)[] botPins =
        [
            ("IO", 18.00f), ("RST", 20.54f), ("3V3", 23.08f), ("5V", 25.62f), ("GND", 28.16f), ("GND", 30.70f), ("VIN", 33.24f),
            ("A0", 38.32f), ("A1", 40.86f), ("A2", 43.40f), ("A3", 45.94f), ("A4", 48.48f), ("A5", 51.02f)
        ];
        float botTextY = rect.Bottom - (4.9f * pxPerMm);
        foreach (var (lbl, xMm) in botPins)
        {
            canvas.DrawText(lbl, rect.Left + (xMm * pxPerMm), botTextY, whitePaint);
        }
    }

    private static void DrawArduinoMegaHardware(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        var usbRect = new SKRect(
            rect.Left - (2.6f * pxPerMm),
            rect.Top + (9.0f * pxPerMm),
            rect.Left + (12.5f * pxPerMm),
            rect.Top + (21.5f * pxPerMm));
        DrawUsbBConnector(canvas, usbRect, pxPerMm);

        var barrelRect = new SKRect(
            rect.Left - (2.2f * pxPerMm),
            rect.Bottom - (17.0f * pxPerMm),
            rect.Left + (13.0f * pxPerMm),
            rect.Bottom - (3.0f * pxPerMm));
        DrawDarkBarrelJack(canvas, barrelRect, "7-12V DC", pxPerMm);

        // ATmega2560 TQFP-100 square IC with 100 gull-wing metallic leads
        var qfpRect = new SKRect(
            rect.Left + (38.0f * pxPerMm),
            rect.Top + (17.0f * pxPerMm),
            rect.Left + (54.5f * pxPerMm),
            rect.Top + (33.5f * pxPerMm));
        DrawQfpPackage(canvas, qfpRect, "ATMEGA2560", "16AU", pxPerMm, leadsPerSide: 25);

        var crystalRect = new SKRect(
            rect.Left + (24.0f * pxPerMm),
            rect.Top + (23.0f * pxPerMm),
            rect.Left + (33.0f * pxPerMm),
            rect.Top + (27.5f * pxPerMm));
        DrawCrystalCan(canvas, crystalRect, "16.000MHz", pxPerMm);

        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (6.2f * pxPerMm), rect.Top + (4.5f * pxPerMm)),
            "RESET",
            SKColor.Parse("#E53935"),
            pxPerMm);

        DrawStatusLed(canvas, rect.Left + (28.0f * pxPerMm), rect.Top + (11.0f * pxPerMm), "ON", SKColor.Parse("#00E676"), pxPerMm);
        DrawStatusLed(canvas, rect.Left + (28.0f * pxPerMm), rect.Top + (15.0f * pxPerMm), "L", SKColor.Parse("#FFD600"), pxPerMm);

        DrawMegaSilkscreen(canvas, rect, pxPerMm);
    }

    private static void DrawMegaSilkscreen(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        using var subTitlePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 210),
            TextSize = Math.Max(5.2f, 0.90f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            IsAntialias = true
        };
        canvas.DrawText("ARDUINO MEGA 2560", rect.Left + (58.0f * pxPerMm), rect.Top + (14.0f * pxPerMm), subTitlePaint);
        canvas.DrawText("COMMUNICATION", rect.Left + (66.0f * pxPerMm), rect.Top + (7.0f * pxPerMm), subTitlePaint);
        canvas.DrawText("DIGITAL 22-53", rect.Right - (18.0f * pxPerMm), rect.MidY, subTitlePaint);
    }

    private static void DrawEsp32DevKitHardware(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // 1. 4 Corner mounting holes with gold annular grounding rings
        DrawMountingHole(canvas, rect.Left + (3.0f * pxPerMm), rect.Top + (3.0f * pxPerMm), pxPerMm);
        DrawMountingHole(canvas, rect.Left + (3.0f * pxPerMm), rect.Bottom - (3.0f * pxPerMm), pxPerMm);
        DrawMountingHole(canvas, rect.Right - (3.0f * pxPerMm), rect.Top + (3.0f * pxPerMm), pxPerMm);
        DrawMountingHole(canvas, rect.Right - (3.0f * pxPerMm), rect.Bottom - (3.0f * pxPerMm), pxPerMm);

        // 2. 3D Micro-USB connector on left edge
        var usbRect = new SKRect(
            rect.Left - (1.6f * pxPerMm),
            rect.MidY - (4.0f * pxPerMm),
            rect.Left + (5.8f * pxPerMm),
            rect.MidY + (4.0f * pxPerMm));
        DrawMicroUsbConnector(canvas, usbRect, pxPerMm);

        // 3. Tactile switches: EN (top) and BOOT (bottom)
        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (4.5f * pxPerMm), rect.Top + (6.5f * pxPerMm)),
            "EN",
            SKColor.Parse("#CFD8DC"),
            pxPerMm);
        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (4.5f * pxPerMm), rect.Bottom - (6.5f * pxPerMm)),
            "BOOT",
            SKColor.Parse("#CFD8DC"),
            pxPerMm);

        // 4. CP2102 USB-to-UART bridge IC
        var cp2102Rect = new SKRect(
            rect.Left + (10.5f * pxPerMm),
            rect.MidY - (3.4f * pxPerMm),
            rect.Left + (17.2f * pxPerMm),
            rect.MidY + (3.4f * pxPerMm));
        DrawQfpPackage(canvas, cp2102Rect, "CP2102", "", pxPerMm, leadsPerSide: 7);

        // 5. AMS1117-3.3 SOT-223 Voltage Regulator
        var regRect = new SKRect(
            rect.Left + (18.2f * pxPerMm),
            rect.Top + (6.0f * pxPerMm),
            rect.Left + (21.5f * pxPerMm),
            rect.Top + (11.0f * pxPerMm));
        DrawSot223Regulator(canvas, regRect, "AMS1117", pxPerMm);

        // 6. Yellow SMD Tantalum Capacitor
        var capRect = new SKRect(
            rect.Left + (18.2f * pxPerMm),
            rect.Bottom - (11.0f * pxPerMm),
            rect.Left + (21.5f * pxPerMm),
            rect.Bottom - (6.5f * pxPerMm));
        DrawTantalumCapacitor(canvas, capRect, pxPerMm);

        // 7. Status LEDs (PWR Red, IO2 Blue)
        DrawStatusLed(canvas, rect.Left + (17.0f * pxPerMm), rect.MidY - (1.8f * pxPerMm), "PWR", SKColor.Parse("#D32F2F"), pxPerMm);
        DrawStatusLed(canvas, rect.Left + (17.0f * pxPerMm), rect.MidY + (1.8f * pxPerMm), "IO2", SKColor.Parse("#0288D1"), pxPerMm);

        // 8. ESP-WROOM-32 metal RF shield can with FCC/CE laser etch
        var shieldRect = new SKRect(
            rect.Left + (22.0f * pxPerMm),
            rect.Top + (5.5f * pxPerMm),
            rect.Left + (43.0f * pxPerMm),
            rect.Bottom - (5.5f * pxPerMm));
        DrawRfShieldCan(canvas, shieldRect, "ESP-WROOM-32", pxPerMm);

        // 9. Gold-plated Inverted-F PCB Antenna on dark substrate
        DrawPcbMeanderAntenna(
            canvas,
            new SKRect(rect.Left + (43.2f * pxPerMm), rect.Top + (6.0f * pxPerMm), rect.Right - (1.2f * pxPerMm), rect.Bottom - (6.0f * pxPerMm)),
            pxPerMm);

        // 10. Crisp Silkscreen
        DrawEsp32Silkscreen(canvas, rect, pxPerMm);
    }

    private static void DrawTantalumCapacitor(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // 3D yellow/gold tantalum capacitor body
        using var bodyPaint = new SKPaint
        {
            Color = SKColor.Parse("#FFC107"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Color = SKColor.Parse("#FFA000"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.7f, 0.20f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 0.25f * pxPerMm, 0.25f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 0.25f * pxPerMm, 0.25f * pxPerMm, borderPaint);

        // Dark brown positive polarity stripe at one end
        using var polPaint = new SKPaint { Color = SKColor.Parse("#4E342E"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(rect.Left, rect.Top, rect.Width * 0.25f, rect.Height, polPaint);

        // Solder metal end terminals
        using var termPaint = new SKPaint { Color = SKColor.Parse("#CFD8DC"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(rect.Left - (0.3f * pxPerMm), rect.Top + (0.2f * pxPerMm), 0.3f * pxPerMm, rect.Height - (0.4f * pxPerMm), termPaint);
        canvas.DrawRect(rect.Right, rect.Top + (0.2f * pxPerMm), 0.3f * pxPerMm, rect.Height - (0.4f * pxPerMm), termPaint);
    }

    private static void DrawEsp32Silkscreen(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        using var logoPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 220),
            TextSize = Math.Max(5.2f, 0.85f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("ESP32 DEVKIT V1", rect.Left + (13.5f * pxPerMm), rect.Top + (4.5f * pxPerMm), logoPaint);
    }

    private static void DrawWemosEsp32S2MiniHardware(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // 1. Two top gold mounting holes with annular rings (diameter 2.0mm hole, 3.2mm ring)
        DrawMountingHole(canvas, rect.Left + (2.8f * pxPerMm), rect.Top + (3.5f * pxPerMm), pxPerMm);
        DrawMountingHole(canvas, rect.Right - (2.8f * pxPerMm), rect.Top + (3.5f * pxPerMm), pxPerMm);

        // 2. Top serpentine trace PCB antenna
        var antennaRect = new SKRect(
            rect.Left + (6.0f * pxPerMm),
            rect.Top + (1.2f * pxPerMm),
            rect.Right - (6.0f * pxPerMm),
            rect.Top + (5.5f * pxPerMm));
        DrawPcbMeanderAntenna(canvas, antennaRect, pxPerMm);

        // 3. 40.000 MHz quartz crystal oscillator above the chip
        var xtalRect = new SKRect(
            rect.MidX - (1.8f * pxPerMm),
            rect.Top + (8.5f * pxPerMm),
            rect.MidX + (1.8f * pxPerMm),
            rect.Top + (11.5f * pxPerMm));
        DrawCrystalCan(canvas, xtalRect, "40.0MHz", pxPerMm);

        // 4. ESP32-S2FN4R2 QFN chip in the center of the board
        var s2ChipRect = new SKRect(
            rect.MidX - (3.8f * pxPerMm),
            rect.Top + (13.5f * pxPerMm),
            rect.MidX + (3.8f * pxPerMm),
            rect.Top + (21.1f * pxPerMm));
        DrawQfpPackage(canvas, s2ChipRect, "ESP32-S2", "FN4R2", pxPerMm, leadsPerSide: 14);

        // 5. White silkscreen branding
        using (var s2TextPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 230),
            TextSize = Math.Max(5.2f, 0.88f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            IsAntialias = true
        })
        {
            // Vertical "S2 Mini" silkscreen next to chip
            canvas.Save();
            canvas.Translate(rect.MidX + (4.6f * pxPerMm), rect.Top + (18.5f * pxPerMm));
            canvas.RotateDegrees(-90f);
            canvas.DrawText("S2 Mini", 0, 0, s2TextPaint);
            canvas.Restore();
        }

        // 6. 3D Stainless Steel Type-C Receptacle at bottom edge
        var usbRect = new SKRect(
            rect.MidX - (4.4f * pxPerMm),
            rect.Bottom - (5.5f * pxPerMm),
            rect.MidX + (4.4f * pxPerMm),
            rect.Bottom + (1.5f * pxPerMm));
        DrawVerticalUsbCConnector(canvas, usbRect, pxPerMm);

        // 7. Micro Tactile Buttons flanking USB-C
        // RST on left
        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (4.5f * pxPerMm), rect.Bottom - (3.8f * pxPerMm)),
            "RST",
            SKColor.Parse("#ECEFF1"),
            pxPerMm);
        // '0' (BOOT) on right
        DrawTactileButton(
            canvas,
            new SKPoint(rect.Right - (4.5f * pxPerMm), rect.Bottom - (3.8f * pxPerMm)),
            "0",
            SKColor.Parse("#ECEFF1"),
            pxPerMm);

        // 8. Status LED (GPIO15) near button '0'
        DrawStatusLed(canvas, rect.Right - (8.5f * pxPerMm), rect.Bottom - (4.2f * pxPerMm), "15", SKColor.Parse("#00E676"), pxPerMm);
    }

    private static void DrawStm32BluePillHardware(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        var usbRect = new SKRect(
            rect.Left - (1.4f * pxPerMm),
            rect.MidY - (3.8f * pxPerMm),
            rect.Left + (5.2f * pxPerMm),
            rect.MidY + (3.8f * pxPerMm));
        DrawMicroUsbConnector(canvas, usbRect, pxPerMm);

        var mcuRect = new SKRect(
            rect.Left + (17.5f * pxPerMm),
            rect.MidY - (4.6f * pxPerMm),
            rect.Left + (26.8f * pxPerMm),
            rect.MidY + (4.6f * pxPerMm));
        DrawQfpPackage(canvas, mcuRect, "STM32F103", "C8T6", pxPerMm, leadsPerSide: 12);

        var xtal8Rect = new SKRect(
            rect.Left + (9.8f * pxPerMm),
            rect.MidY - (2.2f * pxPerMm),
            rect.Left + (16.2f * pxPerMm),
            rect.MidY + (2.2f * pxPerMm));
        DrawCrystalCan(canvas, xtal8Rect, "8.000MHz", pxPerMm);

        var rtcRect = new SKRect(
            rect.Left + (9.5f * pxPerMm),
            rect.Top + (4.6f * pxPerMm),
            rect.Left + (15.5f * pxPerMm),
            rect.Top + (7.4f * pxPerMm));
        DrawRtcCrystalCylinder(canvas, rtcRect, "32.768k", pxPerMm);

        DrawYellowBootJumpers(
            canvas,
            new SKPoint(rect.Left + (34.5f * pxPerMm), rect.MidY - (2.4f * pxPerMm)),
            "BOOT0",
            pxPerMm);
        DrawYellowBootJumpers(
            canvas,
            new SKPoint(rect.Left + (34.5f * pxPerMm), rect.MidY + (2.4f * pxPerMm)),
            "BOOT1",
            pxPerMm);

        DrawTactileButton(
            canvas,
            new SKPoint(rect.Left + (45.0f * pxPerMm), rect.MidY),
            "RST",
            SKColor.Parse("#CFD8DC"),
            pxPerMm);

        DrawStatusLed(canvas, rect.Left + (40.0f * pxPerMm), rect.Top + (4.5f * pxPerMm), "PWR", SKColor.Parse("#D32F2F"), pxPerMm);
        DrawStatusLed(canvas, rect.Left + (40.0f * pxPerMm), rect.Bottom - (4.5f * pxPerMm), "PC13", SKColor.Parse("#00E676"), pxPerMm);

        using var logoPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 210),
            TextSize = Math.Max(5.2f, 0.85f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("STM32F103C8T6 BluePill", rect.MidX, rect.Top + (3.8f * pxPerMm), logoPaint);
    }

    private static void DrawStatusLed(SKCanvas canvas, float cx, float cy, string label, SKColor ledColor, float pxPerMm)
    {
        float r = 0.85f * pxPerMm;

        // Radiant soft glow cone
        using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f * pxPerMm);
        using var glowPaint = new SKPaint
        {
            Color = ledColor.WithAlpha(95),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = glowBlur
        };
        canvas.DrawCircle(cx, cy, r * 1.6f, glowPaint);

        // Plastic SMD LED body
        using var body = new SKPaint { Color = SKColor.Parse("#1A1A1A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(cx - r * 1.2f, cy - r, cx + r * 1.2f, cy + r), 0.2f * pxPerMm, 0.2f * pxPerMm, body);

        // Core lit diode lens
        using var core = new SKPaint { Color = ledColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r * 0.7f, core);

        // Specular dome glint
        using var glint = new SKPaint { Color = new SKColor(255, 255, 255, 210), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx - (r * 0.25f), cy - (r * 0.25f), r * 0.28f, glint);

        if (pxPerMm >= 4.0f)
        {
            using var labelPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 175),
                TextSize = Math.Max(5.0f, 0.75f * pxPerMm),
                TextAlign = SKTextAlign.Center,
                IsAntialias = true
            };
            canvas.DrawText(label, cx, cy + (1.6f * pxPerMm), labelPaint);
        }
    }

    private static void DrawSot223Regulator(SKCanvas canvas, SKRect rect, string label, float pxPerMm)
    {
        // Top thermal copper/silver tab
        using var tabPaint = new SKPaint { Color = SKColor.Parse("#CFD8DC"), Style = SKPaintStyle.Fill, IsAntialias = true };
        var tabRect = new SKRect(rect.Left + (0.8f * pxPerMm), rect.Top - (1.0f * pxPerMm), rect.Right - (0.8f * pxPerMm), rect.Top);
        canvas.DrawRoundRect(tabRect, 0.2f * pxPerMm, 0.2f * pxPerMm, tabPaint);

        // Molded body
        using var bodyPaint = new SKPaint { Color = SKColor.Parse("#1C1C1C"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = SKColor.Parse("#37474F"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.2f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.35f * pxPerMm, 0.35f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 0.35f * pxPerMm, 0.35f * pxPerMm, borderPaint);

        // 3 bottom leads
        float step = rect.Width / 4f;
        for (int i = 1; i <= 3; i++)
        {
            float lx = rect.Left + (i * step);
            canvas.DrawRect(lx - (0.35f * pxPerMm), rect.Bottom, 0.7f * pxPerMm, 0.8f * pxPerMm, tabPaint);
        }

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#B0BEC5"),
            TextSize = Math.Max(5.0f, 0.85f * pxPerMm),
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, rect.MidX, rect.MidY + (0.3f * pxPerMm), textPaint);
    }

    private static void DrawPinSocketsAndLabels(
        SKCanvas canvas,
        MicrocontrollerBoardProfile profile,
        OffBoardMicrocontrollerVisualState state,
        RenderedMicrocontrollerLayout layout,
        float pxPerMm)
    {
        float housingSize = 2.45f * pxPerMm;
        float wellSize = 1.15f * pxPerMm;

        using var housingPaint = new SKPaint
        {
            Color = SKColor.Parse("#18191A"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var goldPadPaint = new SKPaint
        {
            Color = SKColor.Parse("#C5A030"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 0.24f * pxPerMm),
            IsAntialias = true
        };
        using var wellCavityPaint = new SKPaint
        {
            Color = SKColor.Parse("#080808"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var leafSpringPaint = new SKPaint
        {
            Color = SKColor.Parse("#FFD54F"), // Authentic gold-leaf phosphor-bronze contact
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.8f, 0.22f * pxPerMm),
            IsAntialias = true
        };
        using var hoverRingPaint = new SKPaint
        {
            Color = SKColor.Parse("#00E676"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.8f, 0.44f * pxPerMm),
            IsAntialias = true
        };
        using var connectedRingPaint = new SKPaint
        {
            Color = SKColor.Parse("#00E5FF"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.4f, 0.36f * pxPerMm),
            IsAntialias = true
        };
        using var violationRingPaint = new SKPaint
        {
            Color = SKColor.Parse("#FF1744"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2.2f, 0.54f * pxPerMm),
            IsAntialias = true
        };

        // Measure uniform badge width for top and bottom rows so badge extents form a perfectly aligned edge
        float maxBadgeH = MathF.Max(3.8f, 2.18f * pxPerMm);
        float padY = MathF.Max(0.6f, 0.20f * pxPerMm);
        float padX = MathF.Max(1.2f, 0.45f * pxPerMm);
        float calcFontSize = MathF.Max(3.8f, MathF.Min(GetPinLabelFontSize(pxPerMm), maxBadgeH - (padY * 2f)));

        using var measurePaint = new SKPaint
        {
            TextSize = calcFontSize,
            Typeface = s_monoBoldTypeface
        };

        float uniformAboveW = 0f;
        float uniformBelowW = 0f;
        foreach (McuPinDefinition pin in profile.Pins)
        {
            float w = measurePaint.MeasureText(pin.DisplayLabel) + (padX * 2f);
            if (pin.LabelAlignment == PinLabelAlignment.AboveSocket)
                uniformAboveW = MathF.Max(uniformAboveW, w);
            else if (pin.LabelAlignment == PinLabelAlignment.BelowSocket)
                uniformBelowW = MathF.Max(uniformBelowW, w);
        }

        foreach (McuPinDefinition pin in profile.Pins)
        {
            SKPoint center = layout.PinSocketCanvasCenters[pin.CanonicalId];
            bool isHovered = string.Equals(state.HoveredPinCanonicalId, pin.CanonicalId, StringComparison.OrdinalIgnoreCase);
            bool isConnected = state.ConnectedPinCanonicalIds.Contains(pin.CanonicalId);
            bool isViolating = state.ViolatingPinCanonicalIds.Contains(pin.CanonicalId);

            var housingRect = new SKRect(
                center.X - (housingSize * 0.5f),
                center.Y - (housingSize * 0.5f),
                center.X + (housingSize * 0.5f),
                center.Y + (housingSize * 0.5f));

            // 1. PCB annular solder pad
            canvas.DrawRect(housingRect, goldPadPaint);

            // 2. Plastic shroud socket body
            canvas.DrawRect(housingRect, housingPaint);

            // 3. 3D chamfered bevels (light top/left, dark bottom/right)
            using (var lightBevel = new SKPaint { Color = new SKColor(255, 255, 255, 45), StrokeWidth = 1f, IsAntialias = true })
            {
                canvas.DrawLine(housingRect.Left, housingRect.Top, housingRect.Right, housingRect.Top, lightBevel);
                canvas.DrawLine(housingRect.Left, housingRect.Top, housingRect.Left, housingRect.Bottom, lightBevel);
            }
            using (var darkBevel = new SKPaint { Color = new SKColor(0, 0, 0, 95), StrokeWidth = 1f, IsAntialias = true })
            {
                canvas.DrawLine(housingRect.Left, housingRect.Bottom, housingRect.Right, housingRect.Bottom, darkBevel);
                canvas.DrawLine(housingRect.Right, housingRect.Top, housingRect.Right, housingRect.Bottom, darkBevel);
            }

            // 4. Interior square socket well
            var wellRect = new SKRect(
                center.X - (wellSize * 0.5f),
                center.Y - (wellSize * 0.5f),
                center.X + (wellSize * 0.5f),
                center.Y + (wellSize * 0.5f));
            canvas.DrawRect(wellRect, wellCavityPaint);

            // 5. Dual shiny internal gold leaf spring contacts!
            float contactInset = wellSize * 0.28f;
            canvas.DrawLine(wellRect.Left + contactInset, wellRect.Top + contactInset, wellRect.Left + contactInset, wellRect.Bottom - contactInset, leafSpringPaint);
            canvas.DrawLine(wellRect.Right - contactInset, wellRect.Top + contactInset, wellRect.Right - contactInset, wellRect.Bottom - contactInset, leafSpringPaint);

            // 6. Interactive highlight rings
            if (isViolating)
            {
                canvas.DrawCircle(center.X, center.Y, housingSize * 0.74f, violationRingPaint);
            }
            else if (isHovered)
            {
                canvas.DrawCircle(center.X, center.Y, housingSize * 0.72f, hoverRingPaint);
            }
            else if (isConnected)
            {
                canvas.DrawCircle(center.X, center.Y, housingSize * 0.64f, connectedRingPaint);
            }

            // 7. Crisp non-overlapping vector pin label badge
            float uniformW = pin.LabelAlignment == PinLabelAlignment.AboveSocket ? uniformAboveW :
                             pin.LabelAlignment == PinLabelAlignment.BelowSocket ? uniformBelowW : 0f;
            DrawPinLabelBadge(canvas, pin, center, housingSize, isHovered, isConnected, isViolating, pxPerMm, profile.BoardType, uniformW);
        }

        // 8. Sleek floating 3D glass HUD card for currently hovered pin
        if (!string.IsNullOrEmpty(state.HoveredPinCanonicalId))
        {
            McuPinDefinition? hoveredPin = profile.FindPin(state.HoveredPinCanonicalId);
            if (hoveredPin is not null && layout.PinSocketCanvasCenters.TryGetValue(hoveredPin.CanonicalId, out SKPoint hCenter))
            {
                bool isConn = state.ConnectedPinCanonicalIds.Contains(hoveredPin.CanonicalId);
                bool isViol = state.ViolatingPinCanonicalIds.Contains(hoveredPin.CanonicalId);
                DrawHoveredPinHudCard(canvas, hoveredPin, hCenter, profile, isConn, isViol, pxPerMm);
            }
        }
    }

    private static void DrawHoveredPinHudCard(
        SKCanvas canvas,
        McuPinDefinition pin,
        SKPoint socketCenter,
        MicrocontrollerBoardProfile profile,
        bool isConnected,
        bool isViolating,
        float pxPerMm)
    {
        using var titlePaint = new SKPaint
        {
            Color = isViolating ? SKColor.Parse("#FF5252") : SKColor.Parse("#00E5FF"),
            TextSize = Math.Max(7.0f, 1.25f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            IsAntialias = true
        };
        using var rolePaint = new SKPaint
        {
            Color = SKColor.Parse("#ECEFF1"),
            TextSize = Math.Max(5.8f, 1.02f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            IsAntialias = true
        };
        using var metaPaint = new SKPaint
        {
            Color = SKColor.Parse("#B0BEC5"),
            TextSize = Math.Max(5.2f, 0.88f * pxPerMm),
            Typeface = s_uiRegularTypeface,
            IsAntialias = true
        };

        string title = $"{profile.DisplayName} :: {pin.CanonicalId} [{pin.DisplayLabel}]";
        string role = $"Role: {pin.Role} (Bus Ch {pin.BusChannel})";
        string meta = $"{pin.NominalVoltage:F1}V Max: {pin.MaxSourceCurrentMa}mA | {(isViolating ? "DRC ALERT" : isConnected ? "WIRED / ACTIVE" : "AVAILABLE")}";

        float tW = MathF.Max(titlePaint.MeasureText(title), MathF.Max(rolePaint.MeasureText(role), metaPaint.MeasureText(meta)));
        float pad = 3.5f * pxPerMm;
        float cardW = tW + (pad * 2f);
        float cardH = 15.5f * pxPerMm;

        float cardX = MathF.Max(4f, socketCenter.X - (cardW * 0.5f));
        float cardY = socketCenter.Y - cardH - (5.0f * pxPerMm);

        var cardRect = new SKRect(cardX, cardY, cardX + cardW, cardY + cardH);

        // Soft drop shadow
        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, MathF.Max(2.0f, 1.5f * pxPerMm));
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 140),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = shadowBlur
        };
        canvas.DrawRoundRect(new SKRect(cardRect.Left + 1f, cardRect.Top + 2f, cardRect.Right + 1f, cardRect.Bottom + 2f), 1.2f * pxPerMm, 1.2f * pxPerMm, shadowPaint);

        // Glass background with high-contrast gradient
        using var bgShader = SKShader.CreateLinearGradient(
            new SKPoint(cardRect.Left, cardRect.Top),
            new SKPoint(cardRect.Left, cardRect.Bottom),
            new[] { new SKColor(24, 28, 36, 250), new SKColor(12, 16, 22, 250) },
            null,
            SKShaderTileMode.Clamp);
        using var bgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = bgShader
        };
        using var borderPaint = new SKPaint
        {
            Color = isViolating ? SKColor.Parse("#FF1744") : isConnected ? SKColor.Parse("#00E5FF") : SKColor.Parse("#00E676"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 0.35f * pxPerMm),
            IsAntialias = true
        };

        canvas.DrawRoundRect(cardRect, 1.2f * pxPerMm, 1.2f * pxPerMm, bgPaint);
        canvas.DrawRoundRect(cardRect, 1.2f * pxPerMm, 1.2f * pxPerMm, borderPaint);

        canvas.DrawText(title, cardRect.Left + pad, cardRect.Top + (4.6f * pxPerMm), titlePaint);
        canvas.DrawText(role, cardRect.Left + pad, cardRect.Top + (9.2f * pxPerMm), rolePaint);
        canvas.DrawText(meta, cardRect.Left + pad, cardRect.Top + (13.4f * pxPerMm), metaPaint);
    }

    private static void DrawPinLabelBadge(
        SKCanvas canvas,
        McuPinDefinition pin,
        SKPoint socketCenter,
        float housingSize,
        bool isHovered,
        bool isConnected,
        bool isViolating,
        float pxPerMm,
        MicrocontrollerBoardType boardType = MicrocontrollerBoardType.ArduinoUnoR3R4,
        float uniformWidth = 0f)
    {
        (SKColor badgeFill, SKColor textColor) = pin.ColorCategory switch
        {
            PinColorCategory.PowerRed => (PowerRedBadge, PowerRedText),
            PinColorCategory.GroundDark => (GroundDarkBadge, GroundWhiteText),
            PinColorCategory.CommCyanTeal => (CommCyanBadge, CommCyanText),
            _ => (GeneralIoBadge, GeneralIoWhiteText)
        };

        if (isViolating)
        {
            badgeFill = SKColor.Parse("#B71C1C");
            textColor = SKColors.White;
        }
        else if (isHovered)
        {
            badgeFill = SKColor.Parse("#00C853");
            textColor = SKColors.Black;
        }
        else if (isConnected)
        {
            badgeFill = badgeFill.WithAlpha(245);
        }

        // Clamp transverse badge size to strictly less than the 2.54mm header pitch to guarantee no overlaps
        float maxBadgeH = MathF.Max(3.8f, 2.18f * pxPerMm);
        float padY = MathF.Max(0.6f, 0.20f * pxPerMm);
        float padX = MathF.Max(1.2f, 0.45f * pxPerMm);
        float fontSize = MathF.Max(3.8f, MathF.Min(GetPinLabelFontSize(pxPerMm), maxBadgeH - (padY * 2f)));
        float badgeH = MathF.Min(fontSize + (padY * 2f), maxBadgeH);

        using var textPaint = new SKPaint
        {
            Color = textColor,
            TextSize = fontSize,
            Typeface = s_monoBoldTypeface,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        using var bgPaint = new SKPaint
        {
            Color = badgeFill,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 55),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.8f,
            IsAntialias = true
        };

        float textWidth = textPaint.MeasureText(pin.DisplayLabel);
        float badgeW = uniformWidth > 0f ? uniformWidth : (textWidth + (padX * 2f));
        float offsetFromSocket = (housingSize * 0.64f) + (0.60f * pxPerMm);

        canvas.Save();

        if (pin.LabelAlignment is PinLabelAlignment.AboveSocket or PinLabelAlignment.BelowSocket)
        {
            canvas.Translate(socketCenter.X, socketCenter.Y);
            canvas.RotateDegrees(-90f);

            // In -90 degree space:
            // AboveSocket: positive startX maps to negative canvas Y (strictly upward / above socket, zero collision with board components!).
            // BelowSocket: negative startX maps to positive canvas Y (strictly downward / below socket).
            float startX = pin.LabelAlignment == PinLabelAlignment.AboveSocket
                ? offsetFromSocket
                : -(offsetFromSocket + badgeW);

            var badgeRect = new SKRect(startX, -badgeH * 0.5f, startX + badgeW, badgeH * 0.5f);

            // Subtle drop shadow under badge
            using var badgeShadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 50),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRect(badgeRect.Left + 0.4f, badgeRect.Top + 0.5f, badgeRect.Right + 0.4f, badgeRect.Bottom + 0.5f), 0.35f * pxPerMm, 0.35f * pxPerMm, badgeShadow);

            canvas.DrawRoundRect(badgeRect, 0.4f * pxPerMm, 0.4f * pxPerMm, bgPaint);
            canvas.DrawRoundRect(badgeRect, 0.4f * pxPerMm, 0.4f * pxPerMm, borderPaint);
            canvas.DrawText(
                pin.DisplayLabel,
                badgeRect.MidX,
                badgeRect.MidY + (fontSize * 0.35f),
                textPaint);
        }
        else
        {
            float startX;

            // WEMOS ESP32-S2 Mini has authentic staggered 2x8 dual header rows on Left and Right sides.
            // We draw 4 parallel columns of badges (2 on the left, 2 on the right) so inner and outer pins never collide.
            if (boardType == MicrocontrollerBoardType.WemosEsp32S2Mini)
            {
                bool isLeftOuter = pin.CanonicalId is "RST" or "IO3" or "IO5" or "IO7" or "IO9" or "IO11" or "IO12" or "3V3";
                bool isRightOuter = pin.CanonicalId is "IO39" or "IO37" or "IO35" or "IO33" or "IO18" or "IO16" or "GND_2" or "VBUS";

                if (pin.LabelAlignment == PinLabelAlignment.LeftOfSocket)
                {
                    float outerLeftX = isLeftOuter ? socketCenter.X : (socketCenter.X - (2.54f * pxPerMm));
                    startX = isLeftOuter
                        ? outerLeftX - offsetFromSocket - (badgeW * 2f) - (1.5f * pxPerMm)
                        : outerLeftX - offsetFromSocket - badgeW;
                }
                else
                {
                    float outerRightX = isRightOuter ? socketCenter.X : (socketCenter.X + (2.54f * pxPerMm));
                    startX = isRightOuter
                        ? outerRightX + offsetFromSocket + badgeW + (1.5f * pxPerMm)
                        : outerRightX + offsetFromSocket;
                }
            }
            else
            {
                startX = pin.LabelAlignment == PinLabelAlignment.RightOfSocket
                    ? socketCenter.X + offsetFromSocket
                    : socketCenter.X - offsetFromSocket - badgeW;
            }

            var badgeRect = new SKRect(startX, socketCenter.Y - (badgeH * 0.5f), startX + badgeW, socketCenter.Y + (badgeH * 0.5f));

            using var badgeShadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 50),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawRoundRect(new SKRect(badgeRect.Left + 0.4f, badgeRect.Top + 0.5f, badgeRect.Right + 0.4f, badgeRect.Bottom + 0.5f), 0.35f * pxPerMm, 0.35f * pxPerMm, badgeShadow);

            canvas.DrawRoundRect(badgeRect, 0.4f * pxPerMm, 0.4f * pxPerMm, bgPaint);
            canvas.DrawRoundRect(badgeRect, 0.4f * pxPerMm, 0.4f * pxPerMm, borderPaint);
            canvas.DrawText(
                pin.DisplayLabel,
                badgeRect.MidX,
                badgeRect.MidY + (fontSize * 0.35f),
                textPaint);
        }

        canvas.Restore();
    }

    private static void DrawUsbBConnector(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 75), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.6f * pxPerMm), rect.Top + (0.8f * pxPerMm), rect.Right + (0.6f * pxPerMm), rect.Bottom + (0.8f * pxPerMm)), 0.8f * pxPerMm, 0.8f * pxPerMm, shadow);

        // Brushed nickel-plated outer metal shell
        using var shellShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#CFD8DC"), SKColor.Parse("#90A4AE"), SKColor.Parse("#ECEFF1") },
            new[] { 0f, 0.6f, 1f },
            SKShaderTileMode.Clamp);
        using var shellFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = shellShader
        };
        using var shellStroke = new SKPaint
        {
            Color = SKColor.Parse("#546E7A"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 0.28f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, shellFill);
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, shellStroke);

        // Port cavity depth
        float inset = 1.6f * pxPerMm;
        var portRect = new SKRect(rect.Left - (0.4f * pxPerMm), rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
        using var cavityPaint = new SKPaint { Color = SKColor.Parse("#1A1A1A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(portRect, 0.5f * pxPerMm, 0.5f * pxPerMm, cavityPaint);

        // Center white/blue insulation tongue
        var tongueRect = new SKRect(portRect.Left + (0.8f * pxPerMm), portRect.MidY - (1.1f * pxPerMm), portRect.Right - (0.8f * pxPerMm), portRect.MidY + (1.1f * pxPerMm));
        using var tonguePaint = new SKPaint { Color = SKColor.Parse("#0288D1"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(tongueRect, tonguePaint);

        // 4 Gold spring contact pins
        using var pinPaint = new SKPaint { Color = SKColor.Parse("#FFD54F"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float pStep = tongueRect.Width / 5f;
        for (int p = 1; p <= 4; p++)
        {
            float px = tongueRect.Left + (p * pStep);
            canvas.DrawRect(px - (0.25f * pxPerMm), tongueRect.Top, 0.5f * pxPerMm, tongueRect.Height, pinPaint);
        }

        // Stamped USB icon text
        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(6.2f, 1.1f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("USB-B", rect.Right - (3.4f * pxPerMm), rect.MidY + (0.4f * pxPerMm), textPaint);
    }

    private static void DrawMicroUsbConnector(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.4f * pxPerMm), rect.Top + (0.6f * pxPerMm), rect.Right + (0.4f * pxPerMm), rect.Bottom + (0.6f * pxPerMm)), 0.6f * pxPerMm, 0.6f * pxPerMm, shadow);

        // Metallic shell
        using var fillShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#B0BEC5"), SKColor.Parse("#78909C"), SKColor.Parse("#ECEFF1") },
            new[] { 0f, 0.65f, 1f },
            SKShaderTileMode.Clamp);
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = fillShader
        };
        using var strokePaint = new SKPaint
        {
            Color = SKColor.Parse("#455A64"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.9f, 0.24f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 0.6f * pxPerMm, 0.6f * pxPerMm, fillPaint);
        canvas.DrawRoundRect(rect, 0.6f * pxPerMm, 0.6f * pxPerMm, strokePaint);

        // Internal trapezoidal chamber
        var portRect = new SKRect(rect.Left - (0.2f * pxPerMm), rect.Top + (1.0f * pxPerMm), rect.Right - (1.2f * pxPerMm), rect.Bottom - (1.0f * pxPerMm));
        using var cavityPaint = new SKPaint { Color = SKColor.Parse("#1A1A1A"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(portRect, 0.4f * pxPerMm, 0.4f * pxPerMm, cavityPaint);

        // 2 Spring latch holes
        using var latchPaint = new SKPaint { Color = SKColor.Parse("#263238"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(rect.Right - (2.4f * pxPerMm), rect.Top + (1.2f * pxPerMm), 1.2f * pxPerMm, 0.6f * pxPerMm, latchPaint);
        canvas.DrawRect(rect.Right - (2.4f * pxPerMm), rect.Bottom - (1.8f * pxPerMm), 1.2f * pxPerMm, 0.6f * pxPerMm, latchPaint);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(5.5f, 0.95f * pxPerMm),
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("uUSB", rect.Right - (2.8f * pxPerMm), rect.MidY + (0.35f * pxPerMm), textPaint);
    }

    private static void DrawUsbCConnector(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // Brushed metallic oval pill shell
        using var shellShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#CFD8DC"), SKColor.Parse("#90A4AE"), SKColor.Parse("#ECEFF1") },
            null,
            SKShaderTileMode.Clamp);
        using var shellFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = shellShader
        };
        using var shellStroke = new SKPaint
        {
            Color = SKColor.Parse("#455A64"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.9f, 0.25f * pxPerMm),
            IsAntialias = true
        };
        float pillR = rect.Height * 0.42f;
        canvas.DrawRoundRect(rect, pillR, pillR, shellFill);
        canvas.DrawRoundRect(rect, pillR, pillR, shellStroke);

        // Deep interior cavity
        float inset = 1.0f * pxPerMm;
        var cavityRect = new SKRect(rect.Left - (0.2f * pxPerMm), rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
        using var cavityPaint = new SKPaint { Color = SKColor.Parse("#141414"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(cavityRect, cavityRect.Height * 0.40f, cavityRect.Height * 0.40f, cavityPaint);

        // Center tongue with gold contacts
        var tongueRect = new SKRect(cavityRect.Left + (0.8f * pxPerMm), cavityRect.MidY - (0.5f * pxPerMm), cavityRect.Right - (0.8f * pxPerMm), cavityRect.MidY + (0.5f * pxPerMm));
        using var tonguePaint = new SKPaint { Color = SKColor.Parse("#B0BEC5"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(tongueRect, 0.2f * pxPerMm, 0.2f * pxPerMm, tonguePaint);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(5.6f, 0.95f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("TYPE-C", rect.Right - (3.2f * pxPerMm), rect.MidY + (0.35f * pxPerMm), textPaint);
    }

    private static void DrawVerticalUsbCConnector(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // 1. Soft drop shadow under USB-C shell
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 85), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.4f * pxPerMm), rect.Top + (0.5f * pxPerMm), rect.Right + (0.4f * pxPerMm), rect.Bottom + (0.5f * pxPerMm)), 1.2f * pxPerMm, 1.2f * pxPerMm, shadow);

        // 2. 4 Through-hole chassis grounding tabs with solder pads
        using var tabPaint = new SKPaint { Color = SKColor.Parse("#B0BEC5"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var padPaint = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float tabW = 0.9f * pxPerMm;
        float tabH = 1.4f * pxPerMm;

        // Top-left and bottom-left solder tabs
        canvas.DrawRect(rect.Left - tabW, rect.Top + (1.0f * pxPerMm), tabW, tabH, padPaint);
        canvas.DrawRect(rect.Left - tabW + (0.2f * pxPerMm), rect.Top + (1.2f * pxPerMm), tabW - (0.2f * pxPerMm), tabH - (0.4f * pxPerMm), tabPaint);
        canvas.DrawRect(rect.Left - tabW, rect.Bottom - (2.6f * pxPerMm), tabW, tabH, padPaint);
        canvas.DrawRect(rect.Left - tabW + (0.2f * pxPerMm), rect.Bottom - (2.4f * pxPerMm), tabW - (0.2f * pxPerMm), tabH - (0.4f * pxPerMm), tabPaint);

        // Top-right and bottom-right solder tabs
        canvas.DrawRect(rect.Right, rect.Top + (1.0f * pxPerMm), tabW, tabH, padPaint);
        canvas.DrawRect(rect.Right, rect.Top + (1.2f * pxPerMm), tabW - (0.2f * pxPerMm), tabH - (0.4f * pxPerMm), tabPaint);
        canvas.DrawRect(rect.Right, rect.Bottom - (2.6f * pxPerMm), tabW, tabH, padPaint);
        canvas.DrawRect(rect.Right, rect.Bottom - (2.4f * pxPerMm), tabW - (0.2f * pxPerMm), tabH - (0.4f * pxPerMm), tabPaint);

        // 3. Brushed stainless steel metallic outer shell
        using var shellShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#CFD8DC"), SKColor.Parse("#B0BEC5"), SKColor.Parse("#ECEFF1") },
            new[] { 0f, 0.35f, 0.7f, 1f },
            SKShaderTileMode.Clamp);
        using var shellFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = shellShader
        };
        using var shellStroke = new SKPaint
        {
            Color = SKColor.Parse("#546E7A"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.9f, 0.26f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 1.2f * pxPerMm, 1.2f * pxPerMm, shellFill);
        canvas.DrawRoundRect(rect, 1.2f * pxPerMm, 1.2f * pxPerMm, shellStroke);

        // 4. Stamped detention latch holes on top face
        using var dimplePaint = new SKPaint { Color = SKColor.Parse("#78909C"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.MidX - (2.0f * pxPerMm), rect.Top + (1.5f * pxPerMm), rect.MidX - (0.9f * pxPerMm), rect.Top + (2.7f * pxPerMm)), 0.2f * pxPerMm, 0.2f * pxPerMm, dimplePaint);
        canvas.DrawRoundRect(new SKRect(rect.MidX + (0.9f * pxPerMm), rect.Top + (1.5f * pxPerMm), rect.MidX + (2.0f * pxPerMm), rect.Top + (2.7f * pxPerMm)), 0.2f * pxPerMm, 0.2f * pxPerMm, dimplePaint);

        // 5. Deep interior cavity at the bottom edge
        float mouthH = 2.2f * pxPerMm;
        var mouthRect = new SKRect(rect.Left + (0.8f * pxPerMm), rect.Bottom - mouthH, rect.Right - (0.8f * pxPerMm), rect.Bottom);
        using var cavityPaint = new SKPaint { Color = SKColor.Parse("#101014"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(mouthRect, 0.9f * pxPerMm, 0.9f * pxPerMm, cavityPaint);

        // 6. Central gold contact tongue
        var tongueRect = new SKRect(rect.MidX - (2.4f * pxPerMm), mouthRect.MidY - (0.35f * pxPerMm), rect.MidX + (2.4f * pxPerMm), mouthRect.MidY + (0.35f * pxPerMm));
        using var tonguePaint = new SKPaint { Color = SKColor.Parse("#FFD54F"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(tongueRect, 0.2f * pxPerMm, 0.2f * pxPerMm, tonguePaint);
    }

    private static void DrawDarkBarrelJack(SKCanvas canvas, SKRect rect, string label, float pxPerMm)
    {
        // Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 85), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.6f * pxPerMm), rect.Top + (0.8f * pxPerMm), rect.Right + (0.6f * pxPerMm), rect.Bottom + (0.8f * pxPerMm)), 1.0f * pxPerMm, 1.0f * pxPerMm, shadow);

        // Molded cylindrical thermoplastic housing
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new[] { SKColor.Parse("#2C2C2C"), SKColor.Parse("#161616"), SKColor.Parse("#212121") },
            new[] { 0f, 0.5f, 1f },
            SKShaderTileMode.Clamp);
        using var bodyPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = bodyShader
        };
        using var bodyStroke = new SKPaint { Color = SKColor.Parse("#111111"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.3f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, bodyStroke);

        // Concentric outer beveled lip
        float lipW = 2.4f * pxPerMm;
        var lipRect = new SKRect(rect.Left - (0.5f * pxPerMm), rect.Top + (0.6f * pxPerMm), rect.Left + lipW, rect.Bottom - (0.6f * pxPerMm));
        using var lipPaint = new SKPaint { Color = SKColor.Parse("#181818"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(lipRect, 0.5f * pxPerMm, 0.5f * pxPerMm, lipPaint);

        // Center deep cylindrical socket well
        float wellR = (rect.Height - (3.0f * pxPerMm)) * 0.45f;
        float wellX = rect.Left + (5.5f * pxPerMm);
        float wellY = rect.MidY;
        using var wellPaint = new SKPaint { Color = SKColor.Parse("#080808"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(wellX, wellY, wellR, wellPaint);

        // Solid brass center positive terminal pin
        using var pinPaint = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var pinShine = new SKPaint { Color = SKColor.Parse("#FFF59D"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(wellX, wellY, wellR * 0.42f, pinPaint);
        canvas.DrawCircle(wellX - (wellR * 0.12f), wellY - (wellR * 0.12f), wellR * 0.18f, pinShine);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#B0BEC5"),
            TextSize = Math.Max(6.2f, 1.0f * pxPerMm),
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, rect.Right - (3.5f * pxPerMm), rect.MidY + (0.35f * pxPerMm), textPaint);
    }

    private static void DrawCrystalCan(SKCanvas canvas, SKRect rect, string label, float pxPerMm)
    {
        // Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), Style = SKPaintStyle.Fill, IsAntialias = true };
        float r = rect.Height * 0.48f;
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.4f * pxPerMm), rect.Top + (0.5f * pxPerMm), rect.Right + (0.4f * pxPerMm), rect.Bottom + (0.5f * pxPerMm)), r, r, shadow);

        // Brushed HC-49/S metal package with specular reflection
        using var fillShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#B0BEC5"), SKColor.Parse("#CFD8DC") },
            new[] { 0f, 0.5f, 1f },
            SKShaderTileMode.Clamp);
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = fillShader
        };
        using var strokePaint = new SKPaint
        {
            Color = SKColor.Parse("#607D8B"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(0.9f, 0.22f * pxPerMm),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, r, r, fillPaint);
        canvas.DrawRoundRect(rect, r, r, strokePaint);

        // Specular reflection stripe across the top crest
        using var shinePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 140),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.5f * pxPerMm,
            IsAntialias = true
        };
        canvas.DrawLine(rect.Left + r, rect.Top + (0.6f * pxPerMm), rect.Right - r, rect.Top + (0.6f * pxPerMm), shinePaint);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(6.0f, 0.95f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, rect.MidX, rect.MidY + (0.35f * pxPerMm), textPaint);
    }

    private static void DrawRtcCrystalCylinder(SKCanvas canvas, SKRect rect, string label, float pxPerMm)
    {
        using var fillShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#90A4AE"), SKColor.Parse("#CFD8DC") },
            null,
            SKShaderTileMode.Clamp);
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = fillShader
        };
        using var stroke = new SKPaint { Color = SKColor.Parse("#546E7A"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.2f * pxPerMm, IsAntialias = true };
        float r = rect.Height * 0.45f;
        canvas.DrawRoundRect(rect, r, r, fillPaint);
        canvas.DrawRoundRect(rect, r, r, stroke);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(5.2f, 0.85f * pxPerMm),
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, rect.MidX, rect.MidY + (0.30f * pxPerMm), textPaint);
    }

    private static void DrawTactileButton(SKCanvas canvas, SKPoint center, string label, SKColor capColor, float pxPerMm)
    {
        float half = 1.7f * pxPerMm;
        var baseRect = new SKRect(center.X - half, center.Y - half, center.X + half, center.Y + half);

        // 4 Metal solder legs
        using var legPaint = new SKPaint { Color = SKColor.Parse("#B0BEC5"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float legExt = 0.5f * pxPerMm;
        canvas.DrawRect(baseRect.Left - legExt, baseRect.Top + (0.3f * pxPerMm), legExt, 0.6f * pxPerMm, legPaint);
        canvas.DrawRect(baseRect.Left - legExt, baseRect.Bottom - (0.9f * pxPerMm), legExt, 0.6f * pxPerMm, legPaint);
        canvas.DrawRect(baseRect.Right, baseRect.Top + (0.3f * pxPerMm), legExt, 0.6f * pxPerMm, legPaint);
        canvas.DrawRect(baseRect.Right, baseRect.Bottom - (0.9f * pxPerMm), legExt, 0.6f * pxPerMm, legPaint);

        // Silver metal bracket
        using var basePaint = new SKPaint { Color = SKColor.Parse("#CFD8DC"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var baseStroke = new SKPaint { Color = SKColor.Parse("#78909C"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.2f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(baseRect, 0.35f * pxPerMm, 0.35f * pxPerMm, basePaint);
        canvas.DrawRoundRect(baseRect, 0.35f * pxPerMm, 0.35f * pxPerMm, baseStroke);

        // Actuator round button cap with 3D specular sheen
        using var capPaint = new SKPaint { Color = capColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var capShine = new SKPaint { Color = new SKColor(255, 255, 255, 120), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(center.X, center.Y, 1.0f * pxPerMm, capPaint);
        canvas.DrawCircle(center.X - (0.3f * pxPerMm), center.Y - (0.3f * pxPerMm), 0.45f * pxPerMm, capShine);

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = Math.Max(5.8f, 0.9f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, center.X, baseRect.Bottom + (1.1f * pxPerMm), textPaint);
    }

    private static void DrawDipIcPackage(SKCanvas canvas, SKRect rect, string partNumber, int pinsPerSide, float pxPerMm)
    {
        // 1. 28 Metallic lead legs with 3D shine and solder pads
        using var legPaint = new SKPaint { Color = SKColor.Parse("#CFD8DC"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var padPaint = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float pitch = rect.Width / (pinsPerSide + 1);

        for (int i = 1; i <= pinsPerSide; i++)
        {
            float lx = rect.Left + (i * pitch);
            // Solder pads on PCB
            canvas.DrawCircle(lx, rect.Top - (0.8f * pxPerMm), 0.55f * pxPerMm, padPaint);
            canvas.DrawCircle(lx, rect.Bottom + (0.8f * pxPerMm), 0.55f * pxPerMm, padPaint);

            // Lead legs extending from package
            canvas.DrawRect(lx - (0.35f * pxPerMm), rect.Top - (0.75f * pxPerMm), 0.7f * pxPerMm, 0.8f * pxPerMm, legPaint);
            canvas.DrawRect(lx - (0.35f * pxPerMm), rect.Bottom, 0.7f * pxPerMm, 0.8f * pxPerMm, legPaint);
        }

        // 2. Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.5f * pxPerMm), rect.Top + (0.8f * pxPerMm), rect.Right + (0.5f * pxPerMm), rect.Bottom + (0.8f * pxPerMm)), 0.5f * pxPerMm, 0.5f * pxPerMm, shadow);

        // 3. Molded dark epoxy package body with beveled edges
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new[] { SKColor.Parse("#262626"), SKColor.Parse("#161616"), SKColor.Parse("#222222") },
            null,
            SKShaderTileMode.Clamp);
        using var bodyPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = bodyShader
        };
        using var borderPaint = new SKPaint { Color = SKColor.Parse("#37474F"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.25f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.5f * pxPerMm, 0.5f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 0.5f * pxPerMm, 0.5f * pxPerMm, borderPaint);

        // 4. Semicircular pin-1 orientation notch at left edge
        using var notchPaint = new SKPaint { Color = SKColor.Parse("#111111"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float notchR = 1.2f * pxPerMm;
        canvas.DrawCircle(rect.Left, rect.MidY, notchR, notchPaint);

        // 5. Pin-1 circular indentation dimple dot
        using var dotPaint = new SKPaint { Color = SKColor.Parse("#0D0D0D"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(rect.Left + (2.8f * pxPerMm), rect.Top + (2.5f * pxPerMm), 0.75f * pxPerMm, dotPaint);

        // 6. Laser-etched silkscreen text
        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#BDBDBD"),
            TextSize = Math.Max(6.5f, 1.20f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(partNumber, rect.MidX + (1.0f * pxPerMm), rect.MidY + (0.42f * pxPerMm), textPaint);
    }

    private static void DrawQfpPackage(SKCanvas canvas, SKRect rect, string line1, string line2, float pxPerMm, int leadsPerSide = 12)
    {
        // 4-sided metallic gull-wing pin leads
        using var leadPaint = new SKPaint { Color = SKColor.Parse("#CFD8DC"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var padPaint = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float pitchX = rect.Width / (leadsPerSide + 1);
        float pitchY = rect.Height / (leadsPerSide + 1);
        float leadLen = 0.9f * pxPerMm;

        for (int i = 1; i <= leadsPerSide; i++)
        {
            float lx = rect.Left + (i * pitchX);
            float ly = rect.Top + (i * pitchY);
            // Top/Bottom leads
            canvas.DrawRect(lx - (0.22f * pxPerMm), rect.Top - leadLen, 0.44f * pxPerMm, leadLen, leadPaint);
            canvas.DrawRect(lx - (0.22f * pxPerMm), rect.Bottom, 0.44f * pxPerMm, leadLen, leadPaint);
            // Left/Right leads
            canvas.DrawRect(rect.Left - leadLen, ly - (0.22f * pxPerMm), leadLen, 0.44f * pxPerMm, leadPaint);
            canvas.DrawRect(rect.Right, ly - (0.22f * pxPerMm), leadLen, 0.44f * pxPerMm, leadPaint);
        }

        // Package drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 85), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.4f * pxPerMm), rect.Top + (0.6f * pxPerMm), rect.Right + (0.4f * pxPerMm), rect.Bottom + (0.6f * pxPerMm)), 0.6f * pxPerMm, 0.6f * pxPerMm, shadow);

        // Molded epoxy square package body
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#282828"), SKColor.Parse("#161616") },
            null,
            SKShaderTileMode.Clamp);
        using var bodyPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = bodyShader
        };
        using var borderPaint = new SKPaint { Color = SKColor.Parse("#424242"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.28f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.55f * pxPerMm, 0.55f * pxPerMm, bodyPaint);
        canvas.DrawRoundRect(rect, 0.55f * pxPerMm, 0.55f * pxPerMm, borderPaint);

        // Pin 1 chamfered corner / indentation dot
        using var dotPaint = new SKPaint { Color = SKColor.Parse("#0D0D0D"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(rect.Left + (1.8f * pxPerMm), rect.Top + (1.8f * pxPerMm), 0.7f * pxPerMm, dotPaint);

        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#E0E0E0"),
            TextSize = Math.Max(5.8f, 1.0f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(line1, rect.MidX, rect.MidY - (string.IsNullOrEmpty(line2) ? -0.35f * pxPerMm : 0.30f * pxPerMm), textPaint);
        if (!string.IsNullOrEmpty(line2))
        {
            canvas.DrawText(line2, rect.MidX, rect.MidY + (1.20f * pxPerMm), textPaint);
        }
    }

    private static void DrawRfShieldCan(SKCanvas canvas, SKRect rect, string title, float pxPerMm)
    {
        // 1. Drop shadow
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(rect.Left + (0.5f * pxPerMm), rect.Top + (0.7f * pxPerMm), rect.Right + (0.5f * pxPerMm), rect.Bottom + (0.7f * pxPerMm)), 0.8f * pxPerMm, 0.8f * pxPerMm, shadow);

        // 2. Nickel-silver RF shielding can with brushed sheen
        using var shieldShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Bottom),
            new[] { SKColor.Parse("#ECEFF1"), SKColor.Parse("#CFD8DC"), SKColor.Parse("#B0BEC5"), SKColor.Parse("#ECEFF1") },
            new[] { 0f, 0.35f, 0.75f, 1f },
            SKShaderTileMode.Clamp);
        using var shieldPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = shieldShader
        };
        using var borderPaint = new SKPaint { Color = SKColor.Parse("#78909C"), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.9f, 0.28f * pxPerMm), IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, shieldPaint);
        canvas.DrawRoundRect(rect, 0.8f * pxPerMm, 0.8f * pxPerMm, borderPaint);

        // 3. Stamped edge indentation notches
        using var notchPaint = new SKPaint { Color = SKColor.Parse("#90A4AE"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float notchW = 1.6f * pxPerMm;
        float notchH = 0.45f * pxPerMm;
        canvas.DrawRoundRect(new SKRect(rect.Left + (4.0f * pxPerMm), rect.Top - (0.15f * pxPerMm), rect.Left + (4.0f * pxPerMm) + notchW, rect.Top + notchH), 0.2f * pxPerMm, 0.2f * pxPerMm, notchPaint);
        canvas.DrawRoundRect(new SKRect(rect.Right - (6.0f * pxPerMm), rect.Top - (0.15f * pxPerMm), rect.Right - (6.0f * pxPerMm) + notchW, rect.Top + notchH), 0.2f * pxPerMm, 0.2f * pxPerMm, notchPaint);
        canvas.DrawRoundRect(new SKRect(rect.Left + (4.0f * pxPerMm), rect.Bottom - notchH, rect.Left + (4.0f * pxPerMm) + notchW, rect.Bottom + (0.15f * pxPerMm)), 0.2f * pxPerMm, 0.2f * pxPerMm, notchPaint);
        canvas.DrawRoundRect(new SKRect(rect.Right - (6.0f * pxPerMm), rect.Bottom - notchH, rect.Right - (6.0f * pxPerMm) + notchW, rect.Bottom + (0.15f * pxPerMm)), 0.2f * pxPerMm, 0.2f * pxPerMm, notchPaint);

        // 4. WiFi Pill Badge
        var wifiRect = new SKRect(rect.Left + (2.5f * pxPerMm), rect.Top + (2.2f * pxPerMm), rect.Left + (8.5f * pxPerMm), rect.Top + (5.0f * pxPerMm));
        using var wifiBg = new SKPaint { Color = SKColor.Parse("#455A64"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var wifiText = new SKPaint
        {
            Color = SKColors.White,
            TextSize = Math.Max(4.5f, 0.72f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawRoundRect(wifiRect, 0.7f * pxPerMm, 0.7f * pxPerMm, wifiBg);
        canvas.DrawText("WiFi", wifiRect.MidX, wifiRect.MidY + (0.24f * pxPerMm), wifiText);

        // 5. Espressif Circular (e) Logo
        float logoCx = rect.Right - (5.0f * pxPerMm);
        float logoCy = rect.Top + (3.6f * pxPerMm);
        using var logoCirclePaint = new SKPaint { Color = SKColor.Parse("#37474F"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.28f * pxPerMm, IsAntialias = true };
        canvas.DrawCircle(logoCx, logoCy, 1.8f * pxPerMm, logoCirclePaint);
        canvas.DrawCircle(logoCx, logoCy, 1.2f * pxPerMm, logoCirclePaint);
        using var logoLetter = new SKPaint { Color = SKColor.Parse("#37474F"), TextSize = Math.Max(4.8f, 0.75f * pxPerMm), Typeface = s_serifBoldTypeface, TextAlign = SKTextAlign.Center, IsAntialias = true };
        canvas.DrawText("e", logoCx, logoCy + (0.28f * pxPerMm), logoLetter);

        // 6. Laser-etched Part Title & Certification
        using var titlePaint = new SKPaint
        {
            Color = SKColor.Parse("#263238"),
            TextSize = Math.Max(6.8f, 1.20f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(title, rect.MidX, rect.MidY - (0.8f * pxPerMm), titlePaint);

        using var subPaint = new SKPaint
        {
            Color = SKColor.Parse("#455A64"),
            TextSize = Math.Max(4.8f, 0.76f * pxPerMm),
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("CE 0700   FCC ID: 2AC7Z-ESPWROOM32", rect.MidX, rect.MidY + (1.2f * pxPerMm), subPaint);
        using var rohsPaint = new SKPaint
        {
            Color = SKColor.Parse("#546E7A"),
            TextSize = Math.Max(4.4f, 0.68f * pxPerMm),
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText("RoHS Compliant - 2.4GHz Wi-Fi + BT BLE", rect.MidX, rect.MidY + (2.7f * pxPerMm), rohsPaint);
    }

    private static void DrawPcbMeanderAntenna(SKCanvas canvas, SKRect rect, float pxPerMm)
    {
        // Gold-plated inverted-F serpentine copper trace antenna
        using var tracePaint = new SKPaint
        {
            Color = SKColor.Parse("#D4AF37"),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.4f, 0.42f * pxPerMm),
            StrokeCap = SKStrokeCap.Square,
            IsAntialias = true
        };

        using var path = new SKPath();
        float stepY = rect.Height / 6f;
        path.MoveTo(rect.Left, rect.Top + stepY);
        for (int i = 0; i < 5; i++)
        {
            float y1 = rect.Top + ((i + 0.5f) * stepY);
            float y2 = rect.Top + ((i + 1.0f) * stepY);
            float xEdge = (i % 2 == 0) ? rect.Right - (0.6f * pxPerMm) : rect.Left + (0.9f * pxPerMm);
            path.LineTo(xEdge, y1);
            path.LineTo(xEdge, y2);
        }
        canvas.DrawPath(path, tracePaint);
    }

    private static void DrawYellowBootJumpers(SKCanvas canvas, SKPoint center, string label, float pxPerMm)
    {
        var rect = new SKRect(center.X - (2.8f * pxPerMm), center.Y - (1.1f * pxPerMm), center.X + (2.8f * pxPerMm), center.Y + (1.1f * pxPerMm));

        // 3D Yellow plastic jumper shunt with inner contacts
        using var yellowShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new[] { SKColor.Parse("#FFEE58"), SKColor.Parse("#FBC02D") },
            null,
            SKShaderTileMode.Clamp);
        using var yellowPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = yellowShader
        };
        using var borderPaint = new SKPaint { Color = SKColor.Parse("#F57F17"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.2f * pxPerMm, IsAntialias = true };
        canvas.DrawRoundRect(rect, 0.35f * pxPerMm, 0.35f * pxPerMm, yellowPaint);
        canvas.DrawRoundRect(rect, 0.35f * pxPerMm, 0.35f * pxPerMm, borderPaint);

        // Gold post heads
        using var goldPin = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(center.X - (1.4f * pxPerMm), center.Y, 0.35f * pxPerMm, goldPin);
        canvas.DrawCircle(center.X + (1.4f * pxPerMm), center.Y, 0.35f * pxPerMm, goldPin);

        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = Math.Max(5.8f, 0.88f * pxPerMm),
            Typeface = s_monoBoldTypeface,
            TextAlign = SKTextAlign.Center,
            IsAntialias = true
        };
        canvas.DrawText(label, rect.MidX, rect.MidY + (0.32f * pxPerMm), textPaint);
    }

    private void DrawSilkscreenBoardTitle(
        SKCanvas canvas,
        MicrocontrollerBoardProfile profile,
        string instanceId,
        SKRect rect,
        float pxPerMm)
    {
        float fontSize = Math.Max(7.5f, 1.35f * pxPerMm);
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 240),
            TextSize = fontSize,
            Typeface = s_uiBoldTypeface,
            TextAlign = SKTextAlign.Left,
            IsAntialias = true
        };

        McuLabelExtents extents = MeasureLabelExtents(profile, pxPerMm);
        float bottomReserve = MathF.Max(
            extents.BottomPx,
            profile.BoardType == MicrocontrollerBoardType.WemosEsp32S2Mini ? 10.6f * pxPerMm : 0f);
        float baseline = rect.Bottom + bottomReserve + (1.8f * pxPerMm) + fontSize;
        string caption = $"{profile.DisplayName} [{instanceId}]";
        float textWidth = paint.MeasureText(caption);
        float padX = 1.4f * pxPerMm;
        float padY = 0.60f * pxPerMm;
        var chip = new SKRect(
            rect.Left + (2.0f * pxPerMm) - padX,
            baseline - fontSize - padY,
            rect.Left + (2.0f * pxPerMm) + textWidth + padX,
            baseline + padY);

        using var chipPaint = new SKPaint
        {
            Color = new SKColor(16, 20, 24, 215),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var chipBorder = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 50),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRoundRect(chip, 0.7f * pxPerMm, 0.7f * pxPerMm, chipPaint);
        canvas.DrawRoundRect(chip, 0.7f * pxPerMm, 0.7f * pxPerMm, chipBorder);
        canvas.DrawText(caption, rect.Left + (2.0f * pxPerMm), baseline, paint);
    }
}
