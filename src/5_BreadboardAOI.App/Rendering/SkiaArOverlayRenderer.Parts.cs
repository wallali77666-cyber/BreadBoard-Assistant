// ============================================================================
// FILE: src/5_BreadboardAOI.App/Rendering/SkiaArOverlayRenderer.Parts.cs
// PURPOSE: Realistic 2D part symbols for the CAD viewport. Each detection is
//          drawn as the physical part you would solder: a carbon resistor with
//          colour bands decoded from its value, a 5 mm LED dome in its own
//          colour with a cathode flat, an electrolytic capacitor with a stripe,
//          a notched DIP package with pins and a pin-1 dot, a tact switch, and
//          a bowed jumper wire with socket ends.
//
// ALLOCATION POLICY: all paints, the band palette and the wire path are created
// once. Drawing works in board units (the canvas is already scaled), so every
// stroke width is divided by the current scale to stay 1 px on screen.
// ============================================================================

using System;
using System.Globalization;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using SkiaSharp;

namespace BreadboardAOI.App.Rendering;

public sealed partial class SkiaArOverlayRenderer
{
    private const float ResistorNominalLength = 44f;
    private const float LedNominalRadius = 10.2f;
    private const float CapacitorNominalLength = 30f;
    private const float IcNominalLength = 88f;
    private const float ButtonNominalSize = 25f;

    // ---- symbol paints (created once) --------------------------------------
    private readonly SKPaint _partLeadPaint = Stroke(0x3A, 0x38, 0x35, 1.6f);
    private readonly SKPaint _partHighlightPaint = Stroke(0xF2, 0xF0, 0xE8, 0.9f);
    private readonly SKPaint _partResistorBodyPaint = Fill(0xD9, 0xC7, 0x9E);
    private readonly SKPaint _partResistorEdgePaint = Stroke(0x4C, 0x45, 0x38, 1f);
    private readonly SKPaint _partLedBodyPaint = Fill(0xD9, 0x3A, 0x3A);
    private readonly SKPaint _partLedEdgePaint = Stroke(0x3E, 0x1D, 0x1D, 1.1f);
    private readonly SKPaint _partLedEmissionPaint = Stroke(0xB0, 0x8A, 0x8A, 0.9f);
    private readonly SKPaint _partCapBodyPaint = Fill(0x1E, 0x3A, 0x5F);
    private readonly SKPaint _partCapStripePaint = Fill(0xEC, 0xEC, 0xE8);
    private readonly SKPaint _partCapEdgePaint = Stroke(0x10, 0x20, 0x35, 1f);
    private readonly SKPaint _partCapCeramicBodyPaint = Fill(0xD9, 0x82, 0x2B);
    private readonly SKPaint _partCapCeramicEdgePaint = Stroke(0x60, 0x36, 0x10, 1f);
    private readonly SKPaint _partCapCeramicTextPaint;
    private readonly SKPaint _partIcBodyPaint = Fill(0x22, 0x22, 0x26);
    private readonly SKPaint _partIcMarkPaint = Fill(0xA8, 0xA6, 0x9E);
    private readonly SKPaint _partIcPinPaint = Stroke(0xC4, 0xC2, 0xB8, 1.1f);
    private readonly SKPaint _partIcLegPaint = Stroke(0x6A, 0x6A, 0x64, 1.2f);
    private readonly SKPaint _partIcLabelPaint;
    private readonly SKPaint _partButtonBasePaint = Fill(0x3A, 0x3A, 0x3F);
    private readonly SKPaint _partButtonCapPaint = Fill(0xBC, 0xB8, 0xAE);
    private readonly SKPaint _partButtonEdgePaint = Stroke(0x22, 0x22, 0x25, 1f);

    // ---- Diodes, Transistors, Regulators, Pots, Buzzers, Sensors -----------
    private readonly SKPaint _partDiodeGlassBodyPaint = Fill(0xE0, 0x68, 0x2A);
    private readonly SKPaint _partDiodeRectifierBodyPaint = Fill(0x22, 0x22, 0x26);
    private readonly SKPaint _partDiodeCathodeSilverPaint = Fill(0xE0, 0xE2, 0xE5);
    private readonly SKPaint _partDiodeCathodeBlackPaint = Fill(0x10, 0x10, 0x12);
    private readonly SKPaint _partDiodeEdgePaint = Stroke(0x38, 0x38, 0x38, 1f);

    private readonly SKPaint _partBuzzerBodyPaint = Fill(0x1A, 0x1A, 0x1E);
    private readonly SKPaint _partBuzzerHolePaint = Fill(0x0C, 0x0C, 0x0E);
    private readonly SKPaint _partBuzzerEdgePaint = Stroke(0x35, 0x35, 0x3D, 1.2f);
    private readonly SKPaint _partBuzzerPlusPaint = Stroke(0xEE, 0xEE, 0xEA, 1.3f);

    // ---- Specialized Sensor & Module Paints --------------------------------
    private readonly SKPaint _partSensorLdrBodyPaint = Fill(0xD6, 0x7C, 0x38);
    private readonly SKPaint _partSensorLdrTrackPaint = Stroke(0xC0, 0x24, 0x24, 1.2f);
    private readonly SKPaint _partSensorThermistorPaint = Fill(0x12, 0x48, 0x94);
    private readonly SKPaint _partSensorMetalPaint = Fill(0xCB, 0xAA, 0x38);
    private readonly SKPaint _partTiltGoldPaint = Fill(0xD8, 0xB4, 0x3A);
    private readonly SKPaint _partTiltCollarPaint = Fill(0x18, 0x18, 0x1C);
    private readonly SKPaint _partPhotodiodeDiePaint = Fill(0x18, 0x14, 0x36);
    private readonly SKPaint _partBondWirePaint = Stroke(0xD4, 0xAF, 0x37, 0.8f);
    private readonly SKPaint _partGlassFramePaint = Stroke(0xC0, 0xC6, 0xCE, 1f);
    private readonly SKPaint _partIrFilterDomePaint = Fill(0x14, 0x10, 0x1E);
    private readonly SKPaint _partIrRayPaint = Stroke(0xA0, 0x20, 0x20, 0.9f);
    private readonly SKPaint _partMicMetalRimPaint = Fill(0xD0, 0xD4, 0xDA);
    private readonly SKPaint _partMicFeltPortPaint = Fill(0x16, 0x16, 0x18);
    private readonly SKPaint _partMicRingPaint = Stroke(0x8A, 0x8E, 0x94, 0.9f);

    private readonly SKPaint _partPirDomePaint = Fill(0xF4, 0xF6, 0xFA);
    private readonly SKPaint _partPirFacetPaint = Stroke(0xBD, 0xC4, 0xCE, 0.8f);
    private readonly SKPaint _partPirTrimpotPaint = Fill(0xE0, 0x8A, 0x24);
    private readonly SKPaint _partUltrasonicMeshPaint = Stroke(0x50, 0x54, 0x5C, 0.8f);
    private readonly SKPaint _partCrystalCanPaint = Fill(0xC0, 0xC4, 0xC8);

    private readonly SKPaint _partDht11BodyPaint = Fill(0x02, 0x88, 0xD1);
    private readonly SKPaint _partDht11SlotPaint = Fill(0x01, 0x57, 0x9B);
    private readonly SKPaint _partDht22BodyPaint = Fill(0xF5, 0xF5, 0xF0);
    private readonly SKPaint _partDht22MeshPaint = Stroke(0xD0, 0xD0, 0xCA, 0.7f);

    private readonly SKPaint _partGasMeshPaint = Stroke(0x8A, 0x90, 0x9A, 0.8f);
    private readonly SKPaint _partGasBezelPaint = Fill(0x35, 0x38, 0x40);
    private readonly SKPaint _partGasGauzeBasePaint = Fill(0xC8, 0xCE, 0xD6);

    private readonly SKPaint _partSoundPcbPaint = Fill(0x8E, 0x18, 0x18);
    private readonly SKPaint _partBlueTrimpotPaint = Fill(0x19, 0x5B, 0x9E);
    private readonly SKPaint _partGoldScrewPaint = Fill(0xD4, 0xAF, 0x37);
    private readonly SKPaint _partHallBodyPaint = Fill(0x24, 0x24, 0x28);
    private readonly SKPaint _partFluxPaint = Stroke(0x28, 0x9D, 0x78, 1f);
    private readonly SKPaint _partSensorLabelDarkPaint;
    private readonly SKPaint _partSensorBadgeTextPaint;
    private readonly SKPaint _pinPillBgPaint = Fill(0x10, 0x14, 0x1D, 230);
    private readonly SKPaint _pinPillBorderPaint = Stroke(0x3B, 0x42, 0x52, 0.7f);
    private readonly SKPaint _pinVccTextPaint;
    private readonly SKPaint _pinGndTextPaint;
    private readonly SKPaint _pinSignalTextPaint;
    private readonly SKPaint _pinGeneralTextPaint;
    private readonly SKPaint _pinI2cTextPaint;
    private readonly SKPaint _partHeaterGlowPaint = Fill(0xFF, 0x70, 0x10);
    private readonly SKPaint _partTempBulbPaint = Fill(0x28, 0xD0, 0xB0);
    private readonly SKPaint _partEyeletPaint = Stroke(0xD4, 0xAF, 0x37, 1f);
    private readonly SKPaint _partUltrasonicBezelPaint = Stroke(0x9A, 0xA0, 0xAA, 1f);
    private readonly SKPaint _partShadowPaint = Fill(0x00, 0x00, 0x00, 55);
    private readonly SKPaint _partBatteryBodyPaint = Fill(0x0D, 0x1B, 0x2A);
    private readonly SKPaint _partBatteryGoldPaint = Fill(0xD4, 0xAF, 0x37);
    private readonly SKPaint _partBatteryTerminalPaint = Fill(0xD8, 0xDC, 0xE2);
    private readonly SKPaint _partBatteryPlusLeadPaint = Stroke(0xD7, 0x37, 0x3F, 1.8f);
    private readonly SKPaint _partBatteryMinusLeadPaint = Stroke(0x1E, 0x1E, 0x22, 1.8f);
    private readonly SKPaint _dmmRedProbePaint = Fill(0xD7, 0x37, 0x3F);
    private readonly SKPaint _dmmBlackProbePaint = Fill(0x1E, 0x1E, 0x22);
    private readonly SKPaint _dmmNeedlePaint = Stroke(0xC0, 0xC4, 0xC8, 1.4f);
    private readonly SKPaint _dmmWireRedPaint = DashedStroke(0xD7, 0x37, 0x3F, 1.6f);
    private readonly SKPaint _dmmWireBlackPaint = DashedStroke(0x22, 0x22, 0x26, 1.6f);

    public HoleCoord? DmmBlackProbe { get; set; }
    public HoleCoord? DmmRedProbe { get; set; }
    public bool IsDmmProbesVisible { get; set; }

    // ---- 16x2 I2C Character LCD Display Simulation Paints --------------------
    private readonly SKPaint _lcdPcbPaint = Fill(0x14, 0x5A, 0x32);          // Authentic HD44780 green PCB (#145A32)
    private readonly SKPaint _lcdBezelPaint = Fill(0x11, 0x14, 0x18);        // Black metal bezel frame
    private readonly SKPaint _lcdBezelBorderPaint = Stroke(0x28, 0x2C, 0x34, 1.2f);
    private readonly SKPaint _lcdUnlitScreenPaint = Fill(0x16, 0x22, 0x2A);   // Dark unlit LCD matrix (turned off)
    private readonly SKPaint _lcdBacklightBluePaint = Fill(0x0E, 0x62, 0xD4); // Vibrant blue illuminated backlight (#0E62D4)
    private readonly SKPaint _lcdGlassGlintPaint = Fill(0xFF, 0xFF, 0xFF, 30); // Soft glass specular highlight
    private readonly SKPaint _lcdCellGridPaint = Stroke(0x15, 0x4B, 0x9B, 0.5f); // Subtle 5x8 matrix dot grid
    private readonly SKPaint _lcdCharTextPaint = new()                         // Dot matrix white text
    {
        Color = new SKColor(0xF0, 0xF8, 0xFF),
        IsAntialias = true,
        TextSize = 5.2f,
        Typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private readonly SKPaint _lcdPinHeaderPaint = Fill(0xD4, 0xAF, 0x37);     // Gold top solder header pads
    private readonly SKPaint _lcdSilkscreenPaint = new()
    {
        Color = new SKColor(0xE0, 0xE5, 0xE0),
        IsAntialias = true,
        TextSize = 2.4f,
        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    private readonly SKPaint _partPotBodyPaint = Fill(0x15, 0x56, 0x9E);
    private readonly SKPaint _partPotDialPaint = Fill(0xF4, 0xF4, 0xEE);
    private readonly SKPaint _partPotSlotPaint = Stroke(0x40, 0x40, 0x40, 1.3f);

    private readonly SKPaint _partRegTabPaint = Fill(0xC8, 0xCC, 0xD0);
    private readonly SKPaint _partRegTabHolePaint = Fill(0x14, 0x14, 0x14);

    private readonly SKPaint _partModulePcbPaint = Fill(0x13, 0x58, 0x2B);
    private readonly SKPaint _partModuleBluePcbPaint = Fill(0x12, 0x48, 0x82);
    private readonly SKPaint _partUltrasonicCanPaint = Fill(0xD2, 0xD6, 0xDC);
    private readonly SKPaint _partDhtGrillPaint = Fill(0x28, 0x98, 0xC4);

    private readonly SKPaint _partJumperPaint = Stroke(0x7A, 0x8A, 0x74, 2.4f);
    private readonly SKPaint _partJumperPlugPaint = Fill(0x3A, 0x3A, 0x3D);
    private readonly SKPaint _jumperRedPaint = Stroke(0xD7, 0x37, 0x3F, 2.4f);
    private readonly SKPaint _jumperBlackPaint = Stroke(0x1E, 0x1E, 0x22, 2.4f);
    private readonly SKPaint _jumperBluePaint = Stroke(0x19, 0x76, 0xD2, 2.4f);
    private readonly SKPaint _jumperYellowPaint = Stroke(0xEB, 0xB4, 0x24, 2.4f);
    private readonly SKPaint _jumperGreenPaint = Stroke(0x2E, 0x8B, 0x46, 2.4f);
    private readonly SKPaint _jumperOrangePaint = Stroke(0xE0, 0x74, 0x1E, 2.4f);
    private readonly SKPaint _jumperPurplePaint = Stroke(0x7A, 0x3E, 0x8E, 2.4f);
    private readonly SKPaint _jumperWhitePaint = Stroke(0xF0, 0xF0, 0xEB, 2.4f);
    private readonly SKPaint _partFlagPaint = DashedStroke(0xD7, 0x37, 0x3F, 1.2f);
    private readonly SKPaint _partSelectionPaint = DashedStroke(0x00, 0xE5, 0xFF, 1.5f);
    private readonly SKPaint _partPinNumberPaint;

    /// <summary>Resistor colour code: black brown red orange yellow green blue violet grey white gold silver.</summary>
    private readonly SKPaint[] _bandPaints =
    [
        Fill(0x11, 0x11, 0x11), // 0: Black
        Fill(0x79, 0x40, 0x22), // 1: Brown
        Fill(0xD3, 0x2F, 0x2F), // 2: Red
        Fill(0xFF, 0x70, 0x10), // 3: Orange
        Fill(0xFF, 0xD6, 0x00), // 4: Yellow
        Fill(0x2E, 0x7D, 0x32), // 5: Green
        Fill(0x19, 0x76, 0xD2), // 6: Blue
        Fill(0x8E, 0x24, 0xAA), // 7: Violet
        Fill(0x75, 0x75, 0x75), // 8: Grey
        Fill(0xFF, 0xFF, 0xFF), // 9: White
        Fill(0xCF, 0xB5, 0x3B), // 10: Gold (±5%)
        Fill(0xC0, 0xC0, 0xC0)  // 11: Silver (±10%)
    ];

    private readonly SKPath _partWirePath = new();

    private bool _partPaintsScaled;

    /// <summary>Draws the physical part between its two snapped holes.</summary>
    private void DrawLcd1602Module(
        SKCanvas canvas,
        SKRect body,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        bool above,
        string? partValue)
    {
        // 1. Green PCB Board (80mm x 36mm form factor)
        canvas.DrawRoundRect(body, 3.5f, 3.5f, _lcdPcbPaint);
        canvas.DrawRoundRect(body, 3.5f, 3.5f, _partButtonEdgePaint);

        // 2. Four Corner Brass Mounting Holes (with inner dark screw hole)
        float holeInset = 3.5f;
        float holeRadius = 2.2f;
        float innerHole = 1.2f;
        canvas.DrawCircle(body.Left + holeInset, body.Top + holeInset, holeRadius, _partEyeletPaint);
        canvas.DrawCircle(body.Left + holeInset, body.Top + holeInset, innerHole, _partRegTabHolePaint);
        canvas.DrawCircle(body.Right - holeInset, body.Top + holeInset, holeRadius, _partEyeletPaint);
        canvas.DrawCircle(body.Right - holeInset, body.Top + holeInset, innerHole, _partRegTabHolePaint);
        canvas.DrawCircle(body.Left + holeInset, body.Bottom - holeInset, holeRadius, _partEyeletPaint);
        canvas.DrawCircle(body.Left + holeInset, body.Bottom - holeInset, innerHole, _partRegTabHolePaint);
        canvas.DrawCircle(body.Right - holeInset, body.Bottom - holeInset, holeRadius, _partEyeletPaint);
        canvas.DrawCircle(body.Right - holeInset, body.Bottom - holeInset, innerHole, _partRegTabHolePaint);

        // 3. Top 16-pin Header Silkscreen (1: VSS .. 16: K)
        float headerTop = body.Top + 2.0f;
        float headerPadW = (body.Width - 28f) / 16f;
        float startX = body.Left + 14f;
        for (int p = 0; p < 16; p++)
        {
            float padX = startX + p * headerPadW;
            canvas.DrawCircle(padX, headerTop + 1.2f, 0.85f, _lcdPinHeaderPaint);
            canvas.DrawCircle(padX, headerTop + 1.2f, 0.45f, _partRegTabHolePaint);
        }
        if (_scale >= 0.7f)
        {
            canvas.DrawText("1", startX - 3.5f, headerTop + 2.2f, _lcdSilkscreenPaint);
            canvas.DrawText("16", startX + 15 * headerPadW + 1.5f, headerTop + 2.2f, _lcdSilkscreenPaint);
        }

        // 4. Black Metal Bezel Frame surrounding the LCD Glass
        float bezelMarginX = 8.0f;
        float bezelTop = body.Top + 7.5f;
        float bezelHeight = body.Height - 11.0f;
        var bezelRect = new SKRect(body.Left + bezelMarginX, bezelTop, body.Right - bezelMarginX, bezelTop + bezelHeight);
        canvas.DrawRoundRect(bezelRect, 2.0f, 2.0f, _lcdBezelPaint);
        canvas.DrawRoundRect(bezelRect, 2.0f, 2.0f, _lcdBezelBorderPaint);

        // 5. LCD Glass Display Area (16x2 character grid)
        float glassInset = 2.8f;
        var glassRect = new SKRect(bezelRect.Left + glassInset, bezelRect.Top + glassInset, bezelRect.Right - glassInset, bezelRect.Bottom - glassInset);

        bool isSimulating = Layers.ShowLcdSimulation;
        if (!isSimulating)
        {
            // Dark turned off LCD Matrix (realistic unpowered nematic fluid)
            canvas.DrawRoundRect(glassRect, 1.2f, 1.2f, _lcdUnlitScreenPaint);

            // Faint unlit 16x2 character cell matrix outlines
            float cellW = glassRect.Width / 16f;
            float rowH = glassRect.Height / 2f;
            for (int r = 0; r < 2; r++)
            {
                for (int c = 0; c < 16; c++)
                {
                    var cell = new SKRect(glassRect.Left + c * cellW + 0.6f, glassRect.Top + r * rowH + 0.6f, glassRect.Left + (c + 1) * cellW - 0.6f, glassRect.Top + (r + 1) * rowH - 0.6f);
                    canvas.DrawRect(cell, _lcdCellGridPaint);
                }
            }
        }
        else
        {
            // Illuminated Blue LED Backlight
            canvas.DrawRoundRect(glassRect, 1.2f, 1.2f, _lcdBacklightBluePaint);

            // Subtle cell grid
            float cellW = glassRect.Width / 16f;
            float rowH = glassRect.Height / 2f;
            for (int r = 0; r < 2; r++)
            {
                for (int c = 0; c < 16; c++)
                {
                    var cell = new SKRect(glassRect.Left + c * cellW + 0.5f, glassRect.Top + r * rowH + 0.5f, glassRect.Left + (c + 1) * cellW - 0.5f, glassRect.Top + (r + 1) * rowH - 0.5f);
                    canvas.DrawRect(cell, _lcdCellGridPaint);
                }
            }

            // Dot Matrix White Text (Row 0 & Row 1)
            string row0 = Layers.LcdSimulatedRow0 ?? "Breadboard AOI";
            string row1 = Layers.LcdSimulatedRow1 ?? "LCD 1602 Active";
            if (row0.Length > 16) row0 = row0[..16];
            if (row1.Length > 16) row1 = row1[..16];

            float textY0 = glassRect.Top + rowH * 0.72f;
            float textY1 = glassRect.Top + rowH + rowH * 0.72f;
            float textX = glassRect.Left + 2.5f;

            canvas.DrawText(row0, textX, textY0, _lcdCharTextPaint);
            canvas.DrawText(row1, textX, textY1, _lcdCharTextPaint);

            // Specular glass reflection / glint across top portion
            using var glintPath = new SKPath();
            glintPath.MoveTo(glassRect.Left, glassRect.Top);
            glintPath.LineTo(glassRect.Right, glassRect.Top);
            glintPath.LineTo(glassRect.Right, glassRect.Top + 3.0f);
            glintPath.LineTo(glassRect.Left, glassRect.Top + 5.5f);
            glintPath.Close();
            canvas.DrawPath(glintPath, _lcdGlassGlintPaint);
        }
    }

    private void DrawRealisticPart(SKCanvas canvas, in SnappedComponent component)
    {
        ApplyPartPaintsForScale();

        if (component.PinCount > 2)
        {
            DrawMultiPinPart(canvas, component);
            return;
        }

        HoleCoord pin1 = component.Pin1Hole;
        HoleCoord pin2 = component.Pin2Hole;

        float dx = pin2.Px - pin1.Px;
        float dy = pin2.Py - pin1.Py;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 4f)
            return;

        float midX = (pin1.Px + pin2.Px) * 0.5f;
        float midY = (pin1.Py + pin2.Py) * 0.5f;
        float angle = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        float halfSpan = length * 0.5f;
        float clearance = 2.6f;

        canvas.Save();
        canvas.Translate(midX, midY);
        canvas.RotateDegrees(angle);

        switch (component.RawDetection.Kind)
        {
            case ComponentKind.Resistor:
                DrawResistorBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.Led:
                DrawLedBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel, GetLedFirmwareIntensity(in component));
                break;

            case ComponentKind.Capacitor:
                DrawCapacitorBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.IntegratedCircuit:
                DrawIcBody(canvas, halfSpan, clearance);
                break;

            case ComponentKind.PushButton:
                DrawButtonBody(canvas, halfSpan, clearance);
                break;

            case ComponentKind.Diode:
                DrawDiodeBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.Buzzer:
                DrawBuzzerBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.Sensor:
                DrawSensorBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.Battery:
                DrawBatteryBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            default:
                DrawUnknownBody(canvas, halfSpan);
                break;
        }

        canvas.Restore();
    }

    /// <summary>
    /// Draws a DIP or module from every snapped pin, not from the first two. A part
    /// whose pins straddle the trench becomes a notched DIP body with one leg per
    /// hole; a part sitting in a single row becomes a three-lead package above (or
    /// below, on the lower half) the row.
    /// </summary>
    private void DrawMultiPinPart(SKCanvas canvas, in SnappedComponent component)
    {
        Span<float> xs = stackalloc float[component.PinCount];
        Span<float> ys = stackalloc float[component.PinCount];

        int valid = 0;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int pin = 0; pin < component.PinCount; pin++)
        {
            HoleCoord hole = component.GetHole(pin);
            if (string.IsNullOrEmpty(hole.HoleId))
                continue;

            xs[pin] = hole.Px;
            ys[pin] = hole.Py;
            valid++;

            minX = MathF.Min(minX, hole.Px);
            minY = MathF.Min(minY, hole.Py);
            maxX = MathF.Max(maxX, hole.Px);
            maxY = MathF.Max(maxY, hole.Py);
        }

        if (valid < 3)
            return;

        bool dualRow = maxY - minY > 24f;

        if (dualRow)
            DrawDualRowBody(canvas, xs, ys, minX, maxX, minY, maxY, component.RawDetection.DecodedValueLabel);
        else
            DrawSingleRowBody(canvas, xs, ys, minX, maxX, minY, component.RawDetection.Kind, component.RawDetection.DecodedValueLabel);
    }

    private void DrawDualRowBody(
        SKCanvas canvas,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        float minX,
        float maxX,
        float minY,
        float maxY,
        string? partValue)
    {
        const float legInset = 9f;
        float bodyTop = minY + legInset;
        float bodyBottom = maxY - legInset;
        if (bodyBottom - bodyTop < 12f)
        {
            float mid = (minY + maxY) * 0.5f;
            bodyTop = mid - 8f;
            bodyBottom = mid + 8f;
        }

        float left = minX - 6f;
        float right = maxX + 6f;
        var body = new SKRect(left, bodyTop, right, bodyBottom);

        // 1. Soft diffuse drop shadow under the IC package body onto breadboard
        using var icShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 85),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f)
        };
        canvas.DrawRoundRect(new SKRect(left + 1.2f, bodyTop + 2.0f, right + 1.2f, bodyBottom + 2.0f), 2.8f, 2.8f, icShadow);

        // 2. PCB annular solder pads and 3D metallic lead legs entering the holes
        using var padPaint = new SKPaint { Color = SKColor.Parse("#D4AF37"), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var legShine = new SKPaint { Color = new SKColor(255, 255, 255, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };

        for (int pin = 0; pin < xs.Length; pin++)
        {
            float x = xs[pin];
            float y = ys[pin];
            canvas.DrawCircle(x, y, 2.5f, padPaint);

            if (y <= minY + 2f)
            {
                canvas.DrawLine(x, y, x, bodyTop, _partIcLegPaint);
                canvas.DrawLine(x - 0.4f, y, x - 0.4f, bodyTop, legShine);
            }
            else
            {
                canvas.DrawLine(x, y, x, bodyBottom, _partIcLegPaint);
                canvas.DrawLine(x - 0.4f, y, x - 0.4f, bodyBottom, legShine);
            }
        }

        // 3. Molded dark epoxy resin body with cylindrical lighting gradient
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, bodyTop),
            new SKPoint(0f, bodyBottom),
            new[] { SKColor.Parse("#2D2D2D"), SKColor.Parse("#1B1B1B"), SKColor.Parse("#252525") },
            new[] { 0f, 0.50f, 1f },
            SKShaderTileMode.Clamp);
        using var bodyFillPaint = new SKPaint { Shader = bodyShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(body, 2.4f, 2.4f, bodyFillPaint);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcPinPaint);

        // 4. Longitudinal center parting mold seam groove
        float midY = (bodyTop + bodyBottom) * 0.5f;
        using var groovePaint = new SKPaint { Color = new SKColor(10, 10, 10, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(left + 8f, midY, right - 8f, midY, groovePaint);

        // 5. 3D chamfered edge highlights (top highlight, bottom shadow)
        using var topBevel = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        using var botBevel = new SKPaint { Color = new SKColor(0, 0, 0, 95), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(left + 2.4f, bodyTop, right - 2.4f, bodyTop, topBevel);
        canvas.DrawLine(left, bodyTop + 2.4f, left, bodyBottom - 2.4f, topBevel);
        canvas.DrawLine(left + 2.4f, bodyBottom, right - 2.4f, bodyBottom, botBevel);
        canvas.DrawLine(right, bodyTop + 2.4f, right, bodyBottom - 2.4f, botBevel);

        // Print IC Part Name (e.g. NE555P, 74LS04, LM358, L293D) centered on the package
        if (_scale >= 0.62f && !string.IsNullOrWhiteSpace(partValue))
        {
            string label = partValue.Trim().ToUpperInvariant();
            float textWidth = _partIcLabelPaint.MeasureText(label);
            if (textWidth < (right - left - 12f))
            {
                float centerX = (left + right) * 0.5f;
                float centerY = midY + 2.5f;
                canvas.DrawText(label, centerX - textWidth * 0.5f, centerY, _partIcLabelPaint);
            }
        }

        // Pin 1 sits on the first snapped hole; put the index dot and notch beside it.
        bool pin1OnTop = ys[0] <= minY + 2f;
        float pin1X = Math.Clamp(xs[0], left + 5f, right - 5f);

        if (pin1OnTop)
        {
            canvas.DrawCircle(pin1X - 3.5f, bodyTop + 5f, 1.8f, _partIcMarkPaint);
            canvas.DrawArc(
                new SKRect(pin1X - 4.5f, bodyTop - 4.5f, pin1X + 4.5f, bodyTop + 4.5f),
                0f,
                180f,
                false,
                _partIcPinPaint);
        }
        else
        {
            canvas.DrawCircle(pin1X - 3.5f, bodyBottom - 5f, 1.8f, _partIcMarkPaint);
            canvas.DrawArc(
                new SKRect(pin1X - 4.5f, bodyBottom - 4.5f, pin1X + 4.5f, bodyBottom + 4.5f),
                180f,
                180f,
                false,
                _partIcPinPaint);
        }

        // Pin numbers are only legible on a large viewport; hiding them is better
        // than painting a grey smear over the die.
        if (_scale < 0.72f || right - left < 40f)
            return;

        for (int pin = 0; pin < xs.Length; pin++)
        {
            float x = xs[pin];
            bool top = ys[pin] <= minY + 2f;
            float baseline = top ? bodyTop + 7.5f : bodyBottom - 2.5f;

            string number = (pin + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            float width = _partPinNumberPaint.MeasureText(number);
            canvas.DrawText(number, x - width * 0.5f, baseline, _partPinNumberPaint);
        }
    }

    private void DrawPinLabelPill(SKCanvas canvas, float cx, float cy, string label)
    {
        if (string.IsNullOrEmpty(label))
            return;

        SKPaint textPaint = _pinGeneralTextPaint;
        string upper = label.ToUpperInvariant();
        if (upper.Contains("VCC") || upper.Contains("5V") || upper.Contains("3V") || upper.Contains("VIN") || upper.Contains("VDD") || upper == "+" || upper == "A" || upper == "A (+)")
            textPaint = _pinVccTextPaint;
        else if (upper.Contains("GND") || upper.Contains("0V") || upper.Contains("VSS") || upper.Contains("COM") || upper == "-" || upper == "K" || upper == "K (-)")
            textPaint = _pinGndTextPaint;
        else if (upper.Contains("SCL") || upper.Contains("SDA") || upper.Contains("I2C") || upper.Contains("SCK") || upper.Contains("MISO") || upper.Contains("MOSI"))
            textPaint = _pinI2cTextPaint;
        else if (upper.Contains("TRIG") || upper.Contains("ECHO") || upper.Contains("DATA") || upper.Contains("OUT") || upper.Contains("IN") || upper.Contains("AO") || upper.Contains("DO") || upper.Contains("WIPER") || upper.Contains("DQ") || upper.Contains("INT") || upper.Contains("AD0") || upper.Contains("SIG"))
            textPaint = _pinSignalTextPaint;

        float tw = textPaint.MeasureText(label);
        float padX = 2.4f;
        float padY = 1.4f;
        float halfW = tw * 0.5f + padX;
        float halfH = (textPaint.TextSize * 0.5f) + padY;

        var pillRect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
        canvas.DrawRoundRect(pillRect, 2f, 2f, _pinPillBgPaint);
        canvas.DrawRoundRect(pillRect, 2f, 2f, _pinPillBorderPaint);
        canvas.DrawText(label, cx - tw * 0.5f, cy + (textPaint.TextSize * 0.35f), textPaint);
    }

    private string[] ResolveSingleRowPinNames(ComponentKind kind, string label, int pinCount)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            if (BreadboardAOI.Core.DrcEngine.SensorPinoutCatalog.TryGetSensorOrBoard(label, out var profile) && profile != null && profile.Pins.Length >= pinCount)
            {
                var names = new string[pinCount];
                for (int i = 0; i < pinCount; i++)
                    names[i] = profile.Pins[i].Name;
                return names;
            }

            if (BreadboardAOI.Core.DrcEngine.SensorPinoutCatalog.TryGetDefinition(label, pinCount, out var def) && def != null && def.Pins.Count >= pinCount)
            {
                var names = new string[pinCount];
                for (int i = 0; i < pinCount; i++)
                    names[i] = def.Pins[i].Name;
                return names;
            }
        }

        if (label.Contains("LCD", StringComparison.OrdinalIgnoreCase) || label.Contains("1602", StringComparison.OrdinalIgnoreCase) || label.Contains("HD44780", StringComparison.OrdinalIgnoreCase))
            return ["GND", "VCC", "SDA", "SCL"];

        if (label.Contains("HC-SR04", StringComparison.Ordinal) || label.Contains("SR04", StringComparison.Ordinal) || label.Contains("ULTRASONIC", StringComparison.Ordinal))
            return ["VCC", "TRIG", "ECHO", "GND"];

        if (label.Contains("DHT11", StringComparison.Ordinal) || label.Contains("DHT22", StringComparison.Ordinal) || label.Contains("AM2302", StringComparison.Ordinal))
            return pinCount == 3 ? ["VCC", "DATA", "GND"] : ["VCC", "DATA", "NC", "GND"];

        if (label.Contains("MQ-2", StringComparison.Ordinal) || label.Contains("MQ2", StringComparison.Ordinal) || label.Contains("GAS", StringComparison.Ordinal))
            return ["VCC", "GND", "DO", "AO"];

        if (label.Contains("KY-038", StringComparison.Ordinal) || label.Contains("KY038", StringComparison.Ordinal) || label.Contains("SOUND", StringComparison.Ordinal))
            return ["AO", "GND", "VCC", "DO"];

        if (label.Contains("HC-SR501", StringComparison.Ordinal) || label.Contains("SR501", StringComparison.Ordinal) || label.Contains("PIR", StringComparison.Ordinal))
            return ["VCC", "OUT", "GND"];

        if (label.Contains("SSD1306", StringComparison.Ordinal) || label.Contains("OLED", StringComparison.Ordinal))
            return ["GND", "VCC", "SCL", "SDA"];

        if (label.Contains("MPU6050", StringComparison.Ordinal) || label.Contains("GY-521", StringComparison.Ordinal))
            return ["VCC", "GND", "SCL", "SDA", "XDA", "XCL", "AD0", "INT"];

        if (label.Contains("BME280", StringComparison.Ordinal) || label.Contains("BMP280", StringComparison.Ordinal))
            return pinCount == 4 ? ["VCC", "GND", "SCL", "SDA"] : ["VCC", "GND", "SCL", "SDA", "CSB", "SDO"];

        if (label.Contains("A3144", StringComparison.Ordinal) || label.Contains("HALL", StringComparison.Ordinal))
            return ["VCC", "GND", "OUT"];

        if (label.Contains("TMP36", StringComparison.Ordinal) || label.Contains("LM35", StringComparison.Ordinal))
            return ["VCC", "VOUT", "GND"];

        if (label.Contains("DS18B20", StringComparison.Ordinal) || label.Contains("18B20", StringComparison.Ordinal))
            return ["GND", "DQ", "VDD"];

        if (kind == ComponentKind.Potentiometer || label.Contains("POT", StringComparison.Ordinal))
            return ["1", "WIPER", "2"];

        if (kind == ComponentKind.VoltageRegulator || label.Contains("7805", StringComparison.Ordinal) || label.Contains("LM317", StringComparison.Ordinal))
            return ["IN", "GND", "OUT"];

        if (kind == ComponentKind.Transistor || label.Contains("2N2222", StringComparison.Ordinal) || label.Contains("BC547", StringComparison.Ordinal))
            return ["E", "B", "C"];

        if (kind == ComponentKind.Battery || label.Contains("MB102", StringComparison.Ordinal))
            return pinCount >= 4 ? ["+", "-", "+", "-"] : ["+", "-"];

        var fallback = new string[pinCount];
        for (int i = 0; i < pinCount; i++)
            fallback[i] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return fallback;
    }

    private void DrawSingleRowBody(
        SKCanvas canvas,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        float minX,
        float maxX,
        float minY,
        ComponentKind kind,
        string? partValue)
    {
        float rowY = minY;
        bool above = rowY < (BreadboardSpec.UpperFirstRowY + 4f * BreadboardSpec.RowPitch + BreadboardSpec.LowerFirstRowY) * 0.5f;
        string label = partValue?.ToUpperInvariant() ?? string.Empty;

        bool isUltrasonic = label.Contains("HC-SR04", StringComparison.Ordinal) || label.Contains("SR04", StringComparison.Ordinal) || label.Contains("ULTRASONIC", StringComparison.Ordinal);
        bool isDht11 = label.Contains("DHT11", StringComparison.Ordinal);
        bool isDht22 = label.Contains("DHT22", StringComparison.Ordinal) || label.Contains("AM2302", StringComparison.Ordinal);
        bool isGas = label.Contains("MQ-2", StringComparison.Ordinal) || label.Contains("MQ2", StringComparison.Ordinal) || label.Contains("GAS", StringComparison.Ordinal);
        bool isSound = label.Contains("KY-038", StringComparison.Ordinal) || label.Contains("KY038", StringComparison.Ordinal) || label.Contains("SOUND", StringComparison.Ordinal);
        bool isPir = label.Contains("HC-SR501", StringComparison.Ordinal) || label.Contains("SR501", StringComparison.Ordinal) || label.Contains("PIR", StringComparison.Ordinal);
        bool isHall = label.Contains("A3144", StringComparison.Ordinal) || label.Contains("HALL", StringComparison.Ordinal);
        bool isTmp = label.Contains("TMP36", StringComparison.Ordinal) || label.Contains("TMP", StringComparison.Ordinal);
        bool isDs18 = label.Contains("DS18B20", StringComparison.Ordinal) || label.Contains("18B20", StringComparison.Ordinal);

        bool isMb102 = kind == ComponentKind.Battery || label.Contains("MB102", StringComparison.Ordinal);
        bool isBreakoutSensor = label.Contains("BME680", StringComparison.Ordinal) ||
                                label.Contains("MPU6050", StringComparison.Ordinal) ||
                                label.Contains("VL53L0X", StringComparison.Ordinal) ||
                                label.Contains("INA219", StringComparison.Ordinal) ||
                                label.Contains("RC522", StringComparison.Ordinal) ||
                                label.Contains("ADS1115", StringComparison.Ordinal);
        bool isLcd = label.Contains("LCD", StringComparison.Ordinal) || label.Contains("1602", StringComparison.Ordinal) || label.Contains("HD44780", StringComparison.Ordinal);

        bool isSensorModule = isUltrasonic || isDht11 || isDht22 || isGas || isSound || isPir || isMb102 || isBreakoutSensor || isLcd || xs.Length > 3;

        float bodyHeight = isLcd ? 44f : (isSensorModule ? 36f : 16f);
        const float legGap = 5f;
        float bodyNear = above ? rowY - legGap : rowY + legGap;
        float bodyFar = above ? bodyNear - bodyHeight : bodyNear + bodyHeight;

        float centerHoleX = (minX + maxX) * 0.5f;
        float halfWidth = 48f;
        float left = isLcd ? centerHoleX - halfWidth : (isSensorModule ? minX - 10f : minX - 5.5f);
        float right = isLcd ? centerHoleX + halfWidth : (isSensorModule ? maxX + 10f : maxX + 5.5f);

        string[] pinNames = ResolveSingleRowPinNames(kind, label, xs.Length);

        for (int pin = 0; pin < xs.Length; pin++)
        {
            float px = xs[pin];
            float py = ys[pin];

            // Metallic lead connecting from hole up into sensor module body
            canvas.DrawLine(px, py, px, bodyNear, _partIcLegPaint);

            // Physical pin contact pad seated in breadboard hole
            canvas.DrawCircle(px, py, 3.2f, _partButtonBasePaint);
            canvas.DrawCircle(px, py, 2.0f, _partBondWirePaint);
            canvas.DrawCircle(px, py, 0.9f, _partIcPinPaint);

            if (Layers.ShowSensorPinLabels && _scale >= 0.52f && pin < pinNames.Length)
            {
                float labelY = MathF.Abs(bodyNear - py) >= 12f
                    ? (py + bodyNear) * 0.5f
                    : (above ? py + 9f : py - 9f);
                DrawPinLabelPill(canvas, px, labelY, pinNames[pin]);
            }
        }

        var body = new SKRect(left, MathF.Min(bodyNear, bodyFar), right, MathF.Max(bodyNear, bodyFar));

        // Soft drop shadow under the module or component body
        canvas.DrawRoundRect(new SKRect(body.Left + 2f, body.Top + 2f, body.Right + 2f, body.Bottom + 2f), 3f, 3f, _partShadowPaint);

        if (kind == ComponentKind.Battery || isMb102)
        {
            // MB102 Breadboard Power Supply Module (Sleek black PCB with DC jack, USB port, ON/OFF switch, LED)
            canvas.DrawRoundRect(body, 3f, 3f, _partBatteryBodyPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partIcPinPaint);

            // DC 5.5x2.1mm barrel jack on left
            float jackLeft = left + 3f;
            var jackRect = new SKRect(jackLeft, body.Top + 5f, jackLeft + 12f, body.Bottom - 5f);
            canvas.DrawRoundRect(jackRect, 1.5f, 1.5f, _partButtonBasePaint);
            canvas.DrawCircle(jackRect.MidX, jackRect.MidY, 2.2f, _partBuzzerHolePaint);

            // Red latching power switch in middle
            float switchX = (left + right) * 0.5f - 8f;
            var switchRect = new SKRect(switchX, body.Top + 6f, switchX + 10f, body.Bottom - 6f);
            canvas.DrawRoundRect(switchRect, 1.2f, 1.2f, _partTiltCollarPaint);
            canvas.DrawCircle(switchRect.MidX, switchRect.MidY, 2.5f, _partLedBodyPaint);

            // Green power LED
            canvas.DrawCircle(switchX + 16f, body.MidY, 2.2f, _partFluxPaint);

            // USB Type-A female receptacle on right
            float usbRight = right - 3f;
            var usbRect = new SKRect(usbRight - 14f, body.Top + 5f, usbRight, body.Bottom - 5f);
            canvas.DrawRoundRect(usbRect, 1.5f, 1.5f, _partBatteryTerminalPaint);
            canvas.DrawRoundRect(usbRect, 1.5f, 1.5f, _partButtonEdgePaint);
            canvas.DrawRect(new SKRect(usbRect.Left + 2f, usbRect.MidY - 1.5f, usbRect.Right - 2f, usbRect.MidY + 1.5f), _partButtonBasePaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("MB102 5V/3.3V");
                canvas.DrawText("MB102 5V/3.3V", (left + right) * 0.5f - tw * 0.5f, body.Bottom - 2.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.VoltageRegulator)
        {
            // TO-220 Voltage Regulator: metal tab extending on far side with mounting hole
            float tabHeight = 8f;
            float tabNear = bodyFar;
            float tabFar = above ? tabNear - tabHeight : tabNear + tabHeight;
            float tabLeft = left + 2f;
            float tabRight = right - 2f;
            var tabRect = new SKRect(tabLeft, MathF.Min(tabNear, tabFar), tabRight, MathF.Max(tabNear, tabFar));
            canvas.DrawRoundRect(tabRect, 1.5f, 1.5f, _partRegTabPaint);
            canvas.DrawRoundRect(tabRect, 1.5f, 1.5f, _partIcPinPaint);

            // Mounting screw hole in tab
            float tabCenterY = (tabNear + tabFar) * 0.5f;
            float tabCenterX = (tabLeft + tabRight) * 0.5f;
            canvas.DrawCircle(tabCenterX, tabCenterY, 2.2f, _partRegTabHolePaint);

            // Black molded epoxy body
            canvas.DrawRoundRect(body, 2f, 2f, _partIcBodyPaint);
            canvas.DrawRoundRect(body, 2f, 2f, _partIcPinPaint);
        }
        else if (kind == ComponentKind.Potentiometer)
        {
            // Bourns 3362 Trimpot: blue body with rotary screw dial
            canvas.DrawRoundRect(body, 2f, 2f, _partPotBodyPaint);
            canvas.DrawRoundRect(body, 2f, 2f, _partIcPinPaint);

            float dialCenterX = (left + right) * 0.5f;
            float dialCenterY = (bodyNear + bodyFar) * 0.5f;
            float dialRadius = MathF.Min(bodyHeight * 0.38f, (right - left) * 0.30f);

            canvas.DrawCircle(dialCenterX, dialCenterY, dialRadius, _partPotDialPaint);
            canvas.DrawCircle(dialCenterX, dialCenterY, dialRadius, _partButtonEdgePaint);
            canvas.DrawLine(dialCenterX - dialRadius * 0.7f, dialCenterY, dialCenterX + dialRadius * 0.7f, dialCenterY, _partPotSlotPaint);
            canvas.DrawLine(dialCenterX, dialCenterY - dialRadius * 0.7f, dialCenterX, dialCenterY + dialRadius * 0.7f, _partPotSlotPaint);
        }
        else if (kind == ComponentKind.Sensor && isUltrasonic)
        {
            // HC-SR04 Ultrasonic Distance Sensor: Royal blue PCB with dual metal transducer cans & quartz crystal
            canvas.DrawRoundRect(body, 3f, 3f, _partModuleBluePcbPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partIcPinPaint);

            // 4 Corner gold mounting eyelets
            canvas.DrawCircle(left + 3f, body.Top + 3f, 1.6f, _partEyeletPaint);
            canvas.DrawCircle(right - 3f, body.Top + 3f, 1.6f, _partEyeletPaint);
            canvas.DrawCircle(left + 3f, body.Bottom - 3f, 1.6f, _partEyeletPaint);
            canvas.DrawCircle(right - 3f, body.Bottom - 3f, 1.6f, _partEyeletPaint);

            // Dual 40kHz Aluminum Ultrasonic Transducer Cans
            float canR = MathF.Min(10.5f, (right - left) * 0.22f);
            float canY = (bodyNear + bodyFar) * 0.5f;
            float can1X = left + (right - left) * 0.28f;
            float can2X = right - (right - left) * 0.28f;

            // Transmitter Can (Left, marked T)
            canvas.DrawCircle(can1X, canY, canR, _partUltrasonicCanPaint);
            canvas.DrawCircle(can1X, canY, canR, _partUltrasonicBezelPaint);
            canvas.DrawCircle(can1X, canY, canR * 0.75f, _partBuzzerHolePaint);
            canvas.DrawCircle(can1X, canY, canR * 0.75f, _partUltrasonicMeshPaint);
            float twT = _partSensorBadgeTextPaint.MeasureText("T");
            canvas.DrawText("T", can1X - twT * 0.5f, canY + 2.4f, _partSensorBadgeTextPaint);

            // Receiver Can (Right, marked R)
            canvas.DrawCircle(can2X, canY, canR, _partUltrasonicCanPaint);
            canvas.DrawCircle(can2X, canY, canR, _partUltrasonicBezelPaint);
            canvas.DrawCircle(can2X, canY, canR * 0.75f, _partBuzzerHolePaint);
            canvas.DrawCircle(can2X, canY, canR * 0.75f, _partUltrasonicMeshPaint);
            float twR = _partSensorBadgeTextPaint.MeasureText("R");
            canvas.DrawText("R", can2X - twR * 0.5f, canY + 2.4f, _partSensorBadgeTextPaint);

            // 4.000 MHz Metal Quartz Resonator in center
            float crystalX = (can1X + can2X) * 0.5f;
            var crystal = new SKRect(crystalX - 3f, canY - 6f, crystalX + 3f, canY + 6f);
            canvas.DrawRoundRect(crystal, 2.2f, 2.2f, _partCrystalCanPaint);
            canvas.DrawRoundRect(crystal, 2.2f, 2.2f, _partButtonEdgePaint);

            if (_scale >= 0.70f)
            {
                float titleY = above ? body.Top + 7f : body.Bottom - 2.5f;
                float twTitle = _partSensorBadgeTextPaint.MeasureText("HC-SR04");
                canvas.DrawText("HC-SR04", (left + right) * 0.5f - twTitle * 0.5f, titleY, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isDht11)
        {
            // DHT11 Digital Temp & Humidity: Cyan blue case with 4 airflow ventilation slots
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partDht11BodyPaint);
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partButtonEdgePaint);

            // Top mounting ear with hole
            float tabW = 9f;
            float tabH = 4.5f;
            float tabY = above ? body.Top - tabH : body.Bottom;
            var tab = new SKRect((left + right) * 0.5f - tabW * 0.5f, tabY, (left + right) * 0.5f + tabW * 0.5f, tabY + tabH);
            canvas.DrawRoundRect(tab, 1.2f, 1.2f, _partDht11BodyPaint);
            canvas.DrawRoundRect(tab, 1.2f, 1.2f, _partButtonEdgePaint);
            canvas.DrawCircle((left + right) * 0.5f, tab.MidY, 1.2f, _partBuzzerHolePaint);

            // 4 Airflow ventilation louver slots
            float slotW = (right - left) * 0.62f;
            float midX = (left + right) * 0.5f;
            float startY = MathF.Min(bodyNear, bodyFar) + 6.5f;
            for (int s = 0; s < 4; s++)
            {
                float sy = startY + s * 4.2f;
                var slot = new SKRect(midX - slotW * 0.5f, sy, midX + slotW * 0.5f, sy + 2.0f);
                canvas.DrawRoundRect(slot, 0.8f, 0.8f, _partDht11SlotPaint);
            }

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("DHT11");
                canvas.DrawText("DHT11", midX - tw * 0.5f, MathF.Min(bodyNear, bodyFar) + 5.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isDht22)
        {
            // DHT22 / AM2302 Precision Temp & Humidity: Off-white polymer with square wire mesh
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partDht22BodyPaint);
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partButtonEdgePaint);

            // Top mounting flange with hole
            float tabW = 9f;
            float tabH = 4.5f;
            float tabY = above ? body.Top - tabH : body.Bottom;
            var tab = new SKRect((left + right) * 0.5f - tabW * 0.5f, tabY, (left + right) * 0.5f + tabW * 0.5f, tabY + tabH);
            canvas.DrawRoundRect(tab, 1.2f, 1.2f, _partDht22BodyPaint);
            canvas.DrawRoundRect(tab, 1.2f, 1.2f, _partButtonEdgePaint);
            canvas.DrawCircle((left + right) * 0.5f, tab.MidY, 1.2f, _partBuzzerHolePaint);

            // Recessed square mesh window
            float midX = (left + right) * 0.5f;
            float midY = (bodyNear + bodyFar) * 0.5f;
            var meshRect = new SKRect(midX - 8f, midY - 6f, midX + 8f, midY + 5f);
            canvas.DrawRect(meshRect, _partCapStripePaint);
            canvas.DrawRect(meshRect, _partDht22MeshPaint);

            for (float mx = meshRect.Left + 2.8f; mx < meshRect.Right; mx += 2.8f)
                canvas.DrawLine(mx, meshRect.Top, mx, meshRect.Bottom, _partDht22MeshPaint);
            for (float my = meshRect.Top + 2.5f; my < meshRect.Bottom; my += 2.5f)
                canvas.DrawLine(meshRect.Left, my, meshRect.Right, my, _partDht22MeshPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorLabelDarkPaint.MeasureText("DHT22");
                canvas.DrawText("DHT22", midX - tw * 0.5f, MathF.Min(bodyNear, bodyFar) + 6f, _partSensorLabelDarkPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isGas)
        {
            // MQ-2 Gas & Smoke Sensor Module: Blue PCB with stainless steel explosion-proof gauze & pot
            canvas.DrawRoundRect(body, 3f, 3f, _partModuleBluePcbPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partIcPinPaint);

            // Large round explosion-proof stainless steel sensor head
            float canX = left + (right - left) * 0.40f;
            float canY = (bodyNear + bodyFar) * 0.5f;
            float canR = MathF.Min(10.5f, bodyHeight * 0.38f);

            canvas.DrawCircle(canX, canY, canR, _partGasBezelPaint);
            canvas.DrawCircle(canX, canY, canR * 0.85f, _partGasGauzeBasePaint);
            canvas.DrawCircle(canX, canY, canR * 0.85f, _partGasMeshPaint);
            canvas.DrawLine(canX - canR * 0.6f, canY, canX + canR * 0.6f, canY, _partGasMeshPaint);
            canvas.DrawLine(canX, canY - canR * 0.6f, canX, canY + canR * 0.6f, _partGasMeshPaint);

            // Warm heated filament glow dot in center
            canvas.DrawCircle(canX, canY, 2.0f, _partHeaterGlowPaint);

            // Blue sensitivity trimmer pot with gold screw
            var pot = new SKRect(right - 13f, canY - 5f, right - 3f, canY + 5f);
            canvas.DrawRoundRect(pot, 1.2f, 1.2f, _partBlueTrimpotPaint);
            canvas.DrawCircle(pot.MidX, pot.MidY, 2.2f, _partGoldScrewPaint);
            canvas.DrawLine(pot.MidX - 1.5f, pot.MidY, pot.MidX + 1.5f, pot.MidY, _partButtonEdgePaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("MQ-2");
                canvas.DrawText("MQ-2", (left + right) * 0.5f - tw * 0.5f, MathF.Min(bodyNear, bodyFar) + 6f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isSound)
        {
            // KY-038 Sound Detector Module: Red PCB with electret mic canister, blue pot, LM393 chip
            canvas.DrawRoundRect(body, 3f, 3f, _partSoundPcbPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partIcPinPaint);

            // Front-mounted electret microphone capsule
            float micX = left + 8.5f;
            float micY = (bodyNear + bodyFar) * 0.5f;
            float micR = 5.8f;
            canvas.DrawCircle(micX, micY, micR, _partMicMetalRimPaint);
            canvas.DrawCircle(micX, micY, micR * 0.72f, _partMicFeltPortPaint);
            canvas.DrawCircle(micX, micY, micR * 0.25f, _partBuzzerHolePaint);

            // Blue multi-turn trimmer pot with top gold screw
            var trim = new SKRect(micX + 9f, micY - 6f, micX + 20f, micY + 6f);
            canvas.DrawRoundRect(trim, 1.2f, 1.2f, _partBlueTrimpotPaint);
            canvas.DrawCircle(trim.Left + 2.8f, trim.MidY, 1.8f, _partGoldScrewPaint);
            canvas.DrawLine(trim.Left + 1.6f, trim.MidY, trim.Left + 4.0f, trim.MidY, _partButtonEdgePaint);

            // LM393 dual comparator IC chip
            var ic = new SKRect(right - 13f, micY - 5f, right - 4f, micY + 5f);
            canvas.DrawRoundRect(ic, 1f, 1f, _partIcBodyPaint);
            canvas.DrawRoundRect(ic, 1f, 1f, _partIcPinPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("KY-038");
                canvas.DrawText("KY-038", (left + right) * 0.5f - tw * 0.5f, MathF.Min(bodyNear, bodyFar) + 6f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isPir)
        {
            // HC-SR501 PIR Motion Sensor: Green PCB carrier with white hemispherical Fresnel lens dome
            canvas.DrawRoundRect(body, 3f, 3f, _partModulePcbPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partIcPinPaint);

            // Prominent white Fresnel lens dome
            float domeX = (left + right) * 0.5f;
            float domeY = (bodyNear + bodyFar) * 0.5f;
            float domeR = MathF.Min(11f, bodyHeight * 0.42f);

            canvas.DrawCircle(domeX, domeY, domeR, _partPirDomePaint);
            canvas.DrawCircle(domeX, domeY, domeR, _partPirFacetPaint);
            canvas.DrawCircle(domeX, domeY, domeR * 0.65f, _partPirFacetPaint);
            canvas.DrawCircle(domeX, domeY, domeR * 0.32f, _partPirFacetPaint);
            canvas.DrawLine(domeX - domeR, domeY, domeX + domeR, domeY, _partPirFacetPaint);
            canvas.DrawLine(domeX, domeY - domeR, domeX, domeY + domeR, _partPirFacetPaint);

            // Dual trimmer pots (Tx & Sx)
            canvas.DrawRect(left + 2f, domeY - 3.5f, 4.5f, 7f, _partPirTrimpotPaint);
            canvas.DrawRect(right - 6.5f, domeY - 3.5f, 4.5f, 7f, _partPirTrimpotPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("PIR");
                canvas.DrawText("PIR", domeX - tw * 0.5f, MathF.Min(bodyNear, bodyFar) + 5.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isHall)
        {
            // A3144 Hall Effect Magnetic Sensor: SIP-3 body with beveled shoulders & magnetic flux arcs
            canvas.DrawRoundRect(body, 2f, 2f, _partHallBodyPaint);
            canvas.DrawRoundRect(body, 2f, 2f, _partIcPinPaint);

            // Magnetic flux field loops
            float midX = (left + right) * 0.5f;
            float midY = (bodyNear + bodyFar) * 0.5f;
            canvas.DrawArc(new SKRect(midX - 5.5f, midY - 4.5f, midX + 5.5f, midY + 4.5f), -50f, 100f, false, _partFluxPaint);
            canvas.DrawArc(new SKRect(midX - 8.5f, midY - 6.5f, midX + 8.5f, midY + 6.5f), -50f, 100f, false, _partFluxPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("A3144");
                canvas.DrawText("A3144", midX - tw * 0.5f, midY + 2.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isTmp)
        {
            // TMP36 Precision Temperature Sensor: TO-92 with turquoise thermometer icon
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcBodyPaint);
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcPinPaint);
            float flatY = above ? body.Top + 4.5f : body.Bottom - 4.5f;
            canvas.DrawLine(body.Left + 4f, flatY, body.Right - 4f, flatY, _partIcMarkPaint);

            // Thermometer bulb icon
            float iconX = body.Left + 6.5f;
            float iconY = (bodyNear + bodyFar) * 0.5f;
            canvas.DrawCircle(iconX, iconY + 3f, 1.8f, _partTempBulbPaint);
            canvas.DrawLine(iconX, iconY + 3f, iconX, iconY - 3f, _partTempBulbPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("TMP36");
                canvas.DrawText("TMP36", (left + right) * 0.5f - tw * 0.5f + 3f, (bodyNear + bodyFar) * 0.5f + 2.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (kind == ComponentKind.Sensor && isDs18)
        {
            // DS18B20 1-Wire Digital Temp: TO-92 with digital pulse step wave icon
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcBodyPaint);
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcPinPaint);
            float flatY = above ? body.Top + 4.5f : body.Bottom - 4.5f;
            canvas.DrawLine(body.Left + 4f, flatY, body.Right - 4f, flatY, _partIcMarkPaint);

            // Digital 1-Wire pulse wave
            float waveX = body.Left + 5f;
            float waveY = (bodyNear + bodyFar) * 0.5f;
            canvas.DrawLine(waveX, waveY + 2.5f, waveX + 2.5f, waveY + 2.5f, _partHighlightPaint);
            canvas.DrawLine(waveX + 2.5f, waveY + 2.5f, waveX + 2.5f, waveY - 2.5f, _partHighlightPaint);
            canvas.DrawLine(waveX + 2.5f, waveY - 2.5f, waveX + 5f, waveY - 2.5f, _partHighlightPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("18B20");
                canvas.DrawText("18B20", (left + right) * 0.5f - tw * 0.5f + 3f, (bodyNear + bodyFar) * 0.5f + 2.5f, _partSensorBadgeTextPaint);
            }
        }
        else if (isLcd)
        {
            // 16x2 Character LCD Module Simulation (Authentic HD44780 + PCF8574 I2C Backpack)
            DrawLcd1602Module(canvas, body, xs, ys, above, partValue);
        }
        else if (isBreakoutSensor || xs.Length > 3 || (kind == ComponentKind.Sensor && !isHall && !isTmp && !isDs18))
        {
            // Multi-pin Breakout Sensor Module (e.g. BME680, MPU6050, VL53L0X, INA219, RC522, ADS1115):
            // Sleek green PCB carrier with corner eyelets and part name
            canvas.DrawRoundRect(body, 3f, 3f, _partModulePcbPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partButtonEdgePaint);

            // 4 Corner gold mounting eyelets
            canvas.DrawCircle(left + 2.5f, body.Top + 2.5f, 1.4f, _partEyeletPaint);
            canvas.DrawCircle(right - 2.5f, body.Top + 2.5f, 1.4f, _partEyeletPaint);
            canvas.DrawCircle(left + 2.5f, body.Bottom - 2.5f, 1.4f, _partEyeletPaint);
            canvas.DrawCircle(right - 2.5f, body.Bottom - 2.5f, 1.4f, _partEyeletPaint);

            if (_scale >= 0.65f && !string.IsNullOrWhiteSpace(partValue))
            {
                string shortLabel = partValue.Trim().ToUpperInvariant();
                if (shortLabel.Length > 12) shortLabel = shortLabel[..12];
                float textWidth = _partSensorBadgeTextPaint.MeasureText(shortLabel);
                float centerX = (left + right) * 0.5f;
                float centerY = (bodyNear + bodyFar) * 0.5f + 2.5f;
                canvas.DrawText(shortLabel, centerX - textWidth * 0.5f, centerY, _partSensorBadgeTextPaint);
            }
        }
        else
        {
            // Default 3-pin / single-row: TO-92 Transistor / standard package
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcBodyPaint);
            canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcPinPaint);

            // Flat face of a TO-92 points away from pin 1: draw it as a chamfer.
            float flatY = above ? body.Top + 4.5f : body.Bottom - 4.5f;
            canvas.DrawLine(body.Left + 4f, flatY, body.Right - 4f, flatY, _partIcMarkPaint);

            if (_scale >= 0.68f && !string.IsNullOrWhiteSpace(partValue))
            {
                string shortLabel = partValue.Trim().ToUpperInvariant();
                if (shortLabel.Length > 8) shortLabel = shortLabel[..8];
                float textWidth = _partIcLabelPaint.MeasureText(shortLabel);
                if (textWidth < (right - left - 4f))
                {
                    float centerX = (left + right) * 0.5f;
                    float centerY = (bodyNear + bodyFar) * 0.5f + 2.5f;
                    canvas.DrawText(shortLabel, centerX - textWidth * 0.5f, centerY, _partIcLabelPaint);
                }
            }
        }

        if (_scale < 0.52f || right - left < 15f || (!Layers.ShowComponentLabels && !Layers.ShowSensorPinLabels))
            return;

        // Draw pin function callouts (e.g. VCC, TRIG, ECHO, GND, or E, B, C) directly at each lead
        for (int pin = 0; pin < xs.Length; pin++)
        {
            string pinLabel = GetPinFunctionLabel(kind, label, pin, xs.Length);
            float width = _partPinNumberPaint.MeasureText(pinLabel);
            float baseline = above ? bodyNear - 3f : bodyNear + _partPinNumberPaint.TextSize + 1.5f;
            float leftEdge = xs[pin] - width * 0.5f;

            var chip = new SKRect(leftEdge - 2f, baseline - _partPinNumberPaint.TextSize - 0.5f, leftEdge + width + 2f, baseline + 1.5f);
            canvas.DrawRoundRect(chip, 1.5f, 1.5f, _labelChipPaint);
            canvas.DrawText(pinLabel, leftEdge, baseline, _partPinNumberPaint);
        }
    }

    internal static string GetPinFunctionLabel(ComponentKind kind, string label, int pinIndex, int pinCount)
    {
        if (kind == ComponentKind.VoltageRegulator)
        {
            if (label.Contains("1117", StringComparison.Ordinal))
                return pinIndex switch { 0 => "GND", 1 => "OUT", 2 => "IN", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
            if (label.Contains("317", StringComparison.Ordinal))
                return pinIndex switch { 0 => "ADJ", 1 => "OUT", 2 => "IN", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
            return pinIndex switch { 0 => "IN", 1 => "GND", 2 => "OUT", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
        }

        if (kind == ComponentKind.Potentiometer)
        {
            return pinIndex switch { 0 => "1", 1 => "W", 2 => "2", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
        }

        if (kind == ComponentKind.Transistor)
        {
            if (label.Contains("BC547", StringComparison.Ordinal) || label.Contains("BC557", StringComparison.Ordinal))
                return pinIndex switch { 0 => "C", 1 => "B", 2 => "E", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
            if (label.Contains("2N7000", StringComparison.Ordinal))
                return pinIndex switch { 0 => "S", 1 => "G", 2 => "D", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
            if (label.Contains("BS170", StringComparison.Ordinal))
                return pinIndex switch { 0 => "D", 1 => "G", 2 => "S", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
            return pinIndex switch { 0 => "E", 1 => "B", 2 => "C", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };
        }

        if (kind == ComponentKind.Sensor)
        {
            if (label.Contains("LCD", StringComparison.OrdinalIgnoreCase) || label.Contains("1602", StringComparison.OrdinalIgnoreCase) || label.Contains("HD44780", StringComparison.OrdinalIgnoreCase))
                return pinIndex switch { 0 => "GND", 1 => "VCC", 2 => "SDA", 3 => "SCL", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("HC-SR04", StringComparison.Ordinal) || label.Contains("SR04", StringComparison.Ordinal) || label.Contains("ULTRASONIC", StringComparison.Ordinal))
                return pinIndex switch { 0 => "VCC", 1 => "TRIG", 2 => "ECHO", 3 => "GND", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("DHT", StringComparison.Ordinal))
                return pinIndex switch { 0 => "VCC", 1 => "DATA", 2 => "NC", 3 => "GND", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("MQ-2", StringComparison.Ordinal) || label.Contains("MQ2", StringComparison.Ordinal) || label.Contains("GAS", StringComparison.Ordinal))
                return pinIndex switch { 0 => "VCC", 1 => "GND", 2 => "DO", 3 => "AO", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("KY-038", StringComparison.Ordinal) || label.Contains("KY038", StringComparison.Ordinal) || label.Contains("SOUND", StringComparison.Ordinal))
                return pinIndex switch { 0 => "AO", 1 => "DO", 2 => "GND", 3 => "VCC", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("HC-SR501", StringComparison.Ordinal) || label.Contains("SR501", StringComparison.Ordinal) || label.Contains("PIR", StringComparison.Ordinal))
                return pinIndex switch { 0 => "VCC", 1 => "OUT", 2 => "GND", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("A3144", StringComparison.Ordinal) || label.Contains("HALL", StringComparison.Ordinal))
                return pinIndex switch { 0 => "VCC", 1 => "GND", 2 => "OUT", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("TMP36", StringComparison.Ordinal) || label.Contains("TMP", StringComparison.Ordinal))
                return pinIndex switch { 0 => "+Vs", 1 => "OUT", 2 => "GND", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (label.Contains("DS18B20", StringComparison.Ordinal) || label.Contains("18B20", StringComparison.Ordinal))
                return pinIndex switch { 0 => "GND", 1 => "DQ", 2 => "VDD", _ => (pinIndex + 1).ToString(CultureInfo.InvariantCulture) };

            if (SensorPinoutCatalogExtensions.TryGetExpandedSensor(label, out ExpandedSensorDefinition? expSensor))
            {
                ExpandedSensorPinSpec? pin = expSensor.GetPin(pinIndex + 1);
                if (pin != null)
                    return pin.Name;
            }

            if (SensorPinoutCatalog.TryGetDefinition(label, pinCount, out SensorDefinition? sensorDef))
            {
                SensorPinSpec? pin = sensorDef.GetPin(pinIndex + 1);
                if (pin != null)
                    return pin.Name;
            }
        }

        if (kind == ComponentKind.IntegratedCircuit || pinCount > 2)
        {
            if (IcPinoutCatalogExtensions.TryGetExpandedIc(label, out ExpandedIcDefinition? expIc))
            {
                ExpandedIcPinSpec? pin = expIc.GetPin(pinIndex + 1);
                if (pin != null)
                    return pin.Name;
            }

            if (IcPinoutCatalog.TryGetDefinition(label, pinCount, out IcDefinition? icDef) && icDef.PinNames != null)
            {
                if (icDef.PinNames.TryGetValue(pinIndex + 1, out string? name))
                    return name;
            }
        }

        return (pinIndex + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void DrawResistorBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        float bodyLength = MathF.Min(ResistorNominalLength, halfSpan * 2f - clearance * 2f);
        if (bodyLength < 14f)
            bodyLength = MathF.Max(10f, halfSpan * 2f - clearance);

        float half = bodyLength * 0.5f;
        const float bellRadius = 7.5f;
        const float waistRadius = 5.5f;

        // 1. Tinned-copper metallic leads with 3D cylindrical shine and bend shadow
        Draw3DLeads(canvas, halfSpan, half, clearance);

        // 2. Soft diffuse drop shadow under dumbbell ceramic body
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 75),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f)
        };
        using SKPath dumbbellPath = BuildDumbbellPath(half, bellRadius, waistRadius);
        canvas.Save();
        canvas.Translate(1.6f, 2.2f);
        canvas.DrawPath(dumbbellPath, shadow);
        canvas.Restore();

        // 3. Ceramic beige/tan dumbbell body with cylindrical lighting gradient
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, -bellRadius),
            new SKPoint(0f, bellRadius),
            new[] { SKColor.Parse("#F2E4C4"), SKColor.Parse("#D9C79E"), SKColor.Parse("#B39E72") },
            new[] { 0f, 0.45f, 1f },
            SKShaderTileMode.Clamp);
        using var ceramicPaint = new SKPaint
        {
            Shader = bodyShader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawPath(dumbbellPath, ceramicPaint);

        // 4. Decode 4 EIA color bands
        Span<int> bands = stackalloc int[4];
        if (!TryResistorBandCodes(value, bands))
        {
            bands[0] = 2;  // Red (2)
            bands[1] = 2;  // Red (2)
            bands[2] = 1;  // Brown (x10) -> 220 Ohm
            bands[3] = 10; // Gold (5%)
        }

        // Draw the 4 EIA color bands across the resistor body with specular highlights
        float bandWidth = MathF.Max(2.2f, bodyLength * 0.095f);
        ReadOnlySpan<float> fractions = [0.20f, 0.38f, 0.56f, 0.78f];
        for (int i = 0; i < 4 && i < _bandPaints.Length; i++)
        {
            int colorIndex = Math.Clamp(bands[i], 0, _bandPaints.Length - 1);
            float centre = -half + bodyLength * fractions[i];

            // Local radius at this position along the dumbbell
            float localH = (fractions[i] < 0.25f || fractions[i] > 0.75f) ? bellRadius - 0.2f : waistRadius + 0.2f;
            var bandRect = new SKRect(centre - bandWidth * 0.5f, -localH + 0.5f, centre + bandWidth * 0.5f, localH - 0.5f);

            canvas.DrawRoundRect(bandRect, 1.2f, 1.2f, _bandPaints[colorIndex]);

            // Top cylindrical specular glint across each band
            using var bandGlint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 110),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.8f,
                IsAntialias = true
            };
            canvas.DrawLine(bandRect.Left + 0.4f, -localH + 1.2f, bandRect.Right - 0.4f, -localH + 1.2f, bandGlint);
        }

        // 5. Specular highlight line along top crest of ceramic body
        using var topCrestShine = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.9f,
            IsAntialias = true
        };
        canvas.DrawLine(-half + 2.5f, -waistRadius * 0.65f, half - 2.5f, -waistRadius * 0.65f, topCrestShine);

        // 6. Crisp outer edge border
        canvas.DrawPath(dumbbellPath, _partResistorEdgePaint);
    }

    private static SKPath BuildDumbbellPath(float half, float bellR, float waistR)
    {
        var path = new SKPath();
        float bellW = half * 0.40f;

        path.MoveTo(-half, -bellR);
        path.CubicTo(-half + bellW, -bellR, -half + bellW, -waistR, 0f, -waistR);
        path.CubicTo(half - bellW, -waistR, half - bellW, -bellR, half, -bellR);
        path.ArcTo(new SKRect(half - bellR * 0.6f, -bellR, half + bellR * 0.6f, bellR), -90f, 180f, false);
        path.CubicTo(half - bellW, bellR, half - bellW, waistR, 0f, waistR);
        path.CubicTo(-half + bellW, waistR, -half + bellW, bellR, -half, bellR);
        path.ArcTo(new SKRect(-half - bellR * 0.6f, -bellR, -half + bellR * 0.6f, bellR), 90f, 180f, false);
        path.Close();
        return path;
    }

    private void Draw3DLeads(SKCanvas canvas, float halfSpan, float bodyHalf, float clearance)
    {
        float legEnd = MathF.Min(halfSpan, bodyHalf + clearance);
        if (legEnd <= bodyHalf + 0.5f)
            legEnd = halfSpan;

        // Shadow under leads
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 60),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _partLeadPaint.StrokeWidth,
            IsAntialias = true
        };
        canvas.DrawLine(-halfSpan + 1f, 1.4f, -legEnd + 1f, 1.4f, shadow);
        canvas.DrawLine(legEnd + 1f, 1.4f, halfSpan + 1f, 1.4f, shadow);

        // Metallic lead core
        canvas.DrawLine(-halfSpan, 0f, -legEnd, 0f, _partLeadPaint);
        canvas.DrawLine(legEnd, 0f, halfSpan, 0f, _partLeadPaint);

        // Top specular shine
        using var shine = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 140),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(0.6f, _partLeadPaint.StrokeWidth * 0.35f),
            IsAntialias = true
        };
        canvas.DrawLine(-halfSpan + 0.5f, -0.4f, -legEnd - 0.5f, -0.4f, shine);
        canvas.DrawLine(legEnd + 0.5f, -0.4f, halfSpan - 0.5f, -0.4f, shine);
    }

    private float? GetLedFirmwareIntensity(in SnappedComponent component)
    {
        BreadboardAOI.Core.BoxCoding.VirtualFirmwareRuntimeState? fw = ActiveFirmwareState;
        if (fw is not { IsRunning: true })
            return null;

        var stripLevels = new Dictionary<int, float>();
        foreach (OffBoardJumperWire wire in ActiveDevBoardWires)
        {
            if (!TryExtractMcuPinAndHole(wire, out string pinId, out string holeId))
                continue;
            if (!PinMatrixLookup.Shared.TryGetHole(holeId, out HoleCoord hole))
                continue;

            float? level = null;
            if (fw.PwmPinValues.TryGetValue(pinId, out int pwm))
                level = Math.Clamp(pwm / 255f, 0f, 1f);
            else if (fw.DigitalPinStates.TryGetValue(pinId, out bool isHigh))
                level = isHigh ? 1.0f : 0.0f;

            if (level.HasValue)
                stripLevels[hole.StripId] = MathF.Max(stripLevels.GetValueOrDefault(hole.StripId), level.Value);
        }

        if (stripLevels.Count == 0)
            return null;

        if (_lastRenderedState?.Components is { Count: > 0 } comps)
        {
            var propagated = new List<(int StripId, float Level)>();
            foreach (SnappedComponent c in comps)
            {
                if (c.RawDetection.Kind != ComponentKind.Resistor && c.RawDetection.Kind != ComponentKind.JumperWire)
                    continue;
                if (stripLevels.TryGetValue(c.Pin1Hole.StripId, out float l1))
                    propagated.Add((c.Pin2Hole.StripId, l1));
                if (stripLevels.TryGetValue(c.Pin2Hole.StripId, out float l2))
                    propagated.Add((c.Pin1Hole.StripId, l2));
            }

            foreach ((int stripId, float lvl) in propagated)
                stripLevels[stripId] = MathF.Max(stripLevels.GetValueOrDefault(stripId), lvl);
        }

        if (stripLevels.TryGetValue(component.Pin1Hole.StripId, out float pin1Level))
            return pin1Level;
        if (stripLevels.TryGetValue(component.Pin2Hole.StripId, out float pin2Level))
            return pin2Level;

        return null;
    }

    private static bool TryExtractMcuPinAndHole(OffBoardJumperWire wire, out string pinId, out string holeId)
    {
        pinId = string.Empty;
        holeId = string.Empty;

        string src = wire.SourceEndpointKey ?? string.Empty;
        string dst = wire.TargetEndpointKey ?? string.Empty;

        string? mcu = src.StartsWith("MCU", StringComparison.OrdinalIgnoreCase)
            ? src
            : dst.StartsWith("MCU", StringComparison.OrdinalIgnoreCase)
                ? dst
                : null;
        string? bb = src.StartsWith("BB:", StringComparison.OrdinalIgnoreCase)
            ? src
            : dst.StartsWith("BB:", StringComparison.OrdinalIgnoreCase)
                ? dst
                : null;

        if (mcu is null || bb is null)
            return false;

        int colon = mcu.LastIndexOf(':');
        if (colon < 0 || colon >= mcu.Length - 1)
            return false;

        pinId = mcu.Substring(colon + 1);
        holeId = bb.Substring(3);
        return !string.IsNullOrWhiteSpace(pinId) && !string.IsNullOrWhiteSpace(holeId);
    }

    private void DrawLedBody(SKCanvas canvas, float halfSpan, float clearance, string? value, float? firmwareIntensity = null)
    {
        float radius = MathF.Min(LedNominalRadius, MathF.Max(5.4f, halfSpan - clearance));
        float bodyCentre = MathF.Min(halfSpan - radius, radius * 0.35f);
        if (bodyCentre < -halfSpan + radius)
            bodyCentre = 0f;

        SKColor coreColor = LedColour(value);
        _partLedBodyPaint.Color = coreColor;

        Draw3DLeads(canvas, halfSpan, radius, clearance);

        // 1. Soft ambient drop shadow underneath LED lens body
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 85),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.2f)
        };
        canvas.DrawCircle(bodyCentre + 1.2f, 1.8f, radius * 1.15f, shadowPaint);

        // 2. Volumetric lens bloom / ambient radiant glow (modulated by live firmware GPIO/PWM state)
        float bloomScale = firmwareIntensity is { } fwLvl
            ? (fwLvl > 0.05f ? 2.8f + (fwLvl * 1.5f) : 1.3f)
            : 2.8f;
        byte bloomAlphaCore = firmwareIntensity is { } fwLvlA
            ? (byte)Math.Clamp((int)(40 + (210 * fwLvlA)), 25, 255)
            : (byte)170;
        byte bloomAlphaMid = firmwareIntensity is { } fwLvlB
            ? (byte)Math.Clamp((int)(10 + (115 * fwLvlB)), 5, 160)
            : (byte)55;

        using var bloomShader = SKShader.CreateRadialGradient(
            new SKPoint(bodyCentre, 0f),
            radius * bloomScale,
            new[] { coreColor.WithAlpha(bloomAlphaCore), coreColor.WithAlpha(bloomAlphaMid), SKColors.Transparent },
            new[] { 0f, 0.45f, 1f },
            SKShaderTileMode.Clamp);
        using var bloomPaint = new SKPaint
        {
            Shader = bloomShader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(bodyCentre, 0f, radius * bloomScale, bloomPaint);

        // 3. Base flange (outer cylindrical rim of 5mm LED) with cathode flat notch
        float flangeRadius = radius * 1.15f;
        using var flangePaint = new SKPaint
        {
            Color = new SKColor((byte)(coreColor.Red * 0.70f), (byte)(coreColor.Green * 0.70f), (byte)(coreColor.Blue * 0.70f), 220),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(bodyCentre, 0f, flangeRadius, flangePaint);

        // 4. 3D Spherical epoxy dome with realistic refractive lighting
        using var domeShader = SKShader.CreateRadialGradient(
            new SKPoint(bodyCentre - radius * 0.35f, -radius * 0.35f),
            radius * 1.4f,
            new[] {
                new SKColor(255, 255, 255, 220),
                coreColor.WithAlpha(245),
                new SKColor((byte)(coreColor.Red * 0.50f), (byte)(coreColor.Green * 0.50f), (byte)(coreColor.Blue * 0.50f), 255)
            },
            new[] { 0f, 0.42f, 1f },
            SKShaderTileMode.Clamp);
        using var domePaint = new SKPaint
        {
            Shader = domeShader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(bodyCentre, 0f, radius, domePaint);

        // 5. Internal silver lead frame cathode cup / anvil & gold bond wire
        float cupX = bodyCentre + radius * 0.16f;
        using var anvilPaint = new SKPaint
        {
            Color = SKColor.Parse("#ECEFF1"),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var anvilPath = new SKPath();
        anvilPath.MoveTo(cupX, -radius * 0.35f);
        anvilPath.LineTo(cupX + (radius * 0.30f), -radius * 0.10f);
        anvilPath.LineTo(cupX + (radius * 0.15f), radius * 0.35f);
        anvilPath.LineTo(cupX - (radius * 0.15f), radius * 0.35f);
        anvilPath.Close();
        canvas.DrawPath(anvilPath, anvilPaint);

        // Active LED semiconductor crystal die inside the cup
        using var diePaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRect(new SKRect(cupX - 1.2f, -1.2f, cupX + 1.2f, 1.2f), diePaint);

        // Gold bond wire connecting die to anode post
        canvas.DrawLine(cupX, -radius * 0.35f, bodyCentre - radius * 0.25f, radius * 0.30f, _partBondWirePaint);

        // 6. Polarity Cathode flat notch on the pin-2 (+X) side
        float flatX = bodyCentre + radius * 0.78f;
        float flatHalf = radius * 0.65f;
        using var flatPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 110),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.3f,
            IsAntialias = true
        };
        canvas.DrawLine(flatX, -flatHalf, flatX, flatHalf, flatPaint);

        // 7. Fresnel edge highlight (color-tinted to match the LED)
        using var edgePaint = new SKPaint
        {
            Color = new SKColor((byte)(coreColor.Red * 0.40f), (byte)(coreColor.Green * 0.40f), (byte)(coreColor.Blue * 0.40f), 220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.0f,
            IsAntialias = true
        };
        canvas.DrawCircle(bodyCentre, 0f, radius, edgePaint);

        // 8. Primary 3D glass specular gloss highlights
        using var glassGlint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 225),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawCircle(bodyCentre - radius * 0.38f, -radius * 0.38f, radius * 0.24f, glassGlint);
        canvas.DrawCircle(bodyCentre - radius * 0.18f, -radius * 0.52f, radius * 0.12f, glassGlint);

        if (Layers.ShowSensorPinLabels && _scale >= 0.52f)
        {
            DrawPinLabelPill(canvas, -halfSpan, 9f, "A (+)");
            DrawPinLabelPill(canvas, halfSpan, 9f, "K (-)");
        }
    }

    private void DrawCapacitorBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        string label = value?.ToUpperInvariant() ?? string.Empty;
        bool isCeramic = label.Contains("PF", StringComparison.Ordinal)
            || label.Contains("NF", StringComparison.Ordinal)
            || label.Contains("CERAMIC", StringComparison.Ordinal)
            || label.Contains("100N", StringComparison.Ordinal)
            || label.Contains("10N", StringComparison.Ordinal)
            || label.Contains("1N", StringComparison.Ordinal)
            || label.Contains("100P", StringComparison.Ordinal);

        if (isCeramic)
        {
            // Ceramic Disc Capacitor (non-polarized, orange/tan disc with 3D drop shadow and glaze gloss)
            float discRadius = MathF.Min(10f, MathF.Max(6.5f, halfSpan * 0.44f));
            Draw3DLeads(canvas, halfSpan, discRadius, clearance);

            // 1. Soft 2.5D drop shadow
            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 70),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f)
            };
            canvas.DrawCircle(1.5f, 2.0f, discRadius, shadow);

            // 2. Ceramic orange/tan body with 3D radial spherical glaze shader
            using var ceramicShader = SKShader.CreateRadialGradient(
                new SKPoint(-discRadius * 0.35f, -discRadius * 0.35f),
                discRadius * 1.35f,
                new[] { SKColor.Parse("#FB923C"), SKColor.Parse("#EA580C"), SKColor.Parse("#9A3412") },
                new[] { 0f, 0.65f, 1f },
                SKShaderTileMode.Clamp);
            using var ceramicFill = new SKPaint { Shader = ceramicShader, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(0f, 0f, discRadius, ceramicFill);
            canvas.DrawCircle(0f, 0f, discRadius, _partCapCeramicEdgePaint);

            // 3. Glaze specular highlight glint
            using var glint = new SKPaint { Color = new SKColor(255, 255, 255, 160), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(-discRadius * 0.32f, -discRadius * 0.32f, discRadius * 0.22f, glint);

            // 4. Stamped EIA 3-digit capacitance code (e.g. 104, 103, 101)
            if (_scale >= 0.65f)
            {
                string code = label.Contains("100N", StringComparison.Ordinal) ? "104"
                    : label.Contains("10N", StringComparison.Ordinal) ? "103"
                    : label.Contains("1N", StringComparison.Ordinal) ? "102"
                    : label.Contains("100P", StringComparison.Ordinal) ? "101"
                    : (label.Length > 0 ? (label.Length > 4 ? label[..4] : label) : "104");

                float textW = _partCapCeramicTextPaint.MeasureText(code);
                canvas.DrawText(code, -textW * 0.5f, discRadius * 0.35f, _partCapCeramicTextPaint);
            }
            return;
        }

        // Polarized Radial Electrolytic Aluminum Canister
        float bodyLength = MathF.Min(CapacitorNominalLength, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(9f, bodyLength * 0.5f);
        const float halfHeight = 8.5f;

        Draw3DLeads(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);

        // 1. Soft 2.5D drop shadow under capacitor cylinder
        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 80),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.0f)
        };
        canvas.DrawRoundRect(new SKRect(body.Left + 1.6f, body.Top + 2.2f, body.Right + 1.6f, body.Bottom + 2.2f), 2.8f, 2.8f, shadowPaint);

        // 2. Deep metallic blue cylindrical PVC shrink sleeve
        using var sleeveShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, -halfHeight),
            new SKPoint(0f, halfHeight),
            new[] { SKColor.Parse("#2563EB"), SKColor.Parse("#1D4ED8"), SKColor.Parse("#0F172A") },
            new[] { 0f, 0.45f, 1f },
            SKShaderTileMode.Clamp);
        using var sleevePaint = new SKPaint { Shader = sleeveShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(body, 2.6f, 2.6f, sleevePaint);

        // 3. Negative cathode silver stripe toward pin 1
        float stripeWidth = MathF.Max(3.2f, bodyLength * 0.22f);
        var stripe = new SKRect(-half, -halfHeight, -half + stripeWidth, halfHeight);
        using var stripeShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, -halfHeight),
            new SKPoint(0f, halfHeight),
            new[] { SKColor.Parse("#FFFFFF"), SKColor.Parse("#E2E8F0"), SKColor.Parse("#94A3B8") },
            null,
            SKShaderTileMode.Clamp);
        using var stripeFill = new SKPaint { Shader = stripeShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(stripe, 2.2f, 2.2f, stripeFill);

        // Negative '-' symbols stamped on the cathode stripe
        using var dashPaint = new SKPaint { Color = SKColor.Parse("#1E293B"), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true };
        canvas.DrawLine(-half + (stripeWidth * 0.25f), 0f, -half + (stripeWidth * 0.75f), 0f, dashPaint);
        canvas.DrawLine(-half + (stripeWidth * 0.25f), -halfHeight * 0.55f, -half + (stripeWidth * 0.75f), -halfHeight * 0.55f, dashPaint);
        canvas.DrawLine(-half + (stripeWidth * 0.25f), halfHeight * 0.55f, -half + (stripeWidth * 0.75f), halfHeight * 0.55f, dashPaint);

        // 4. Aluminum silver top vent plate at opposite end
        float ventWidth = 3.4f;
        var ventRect = new SKRect(half - ventWidth, -halfHeight + 0.8f, half, halfHeight - 0.8f);
        using var ventShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, ventRect.Top),
            new SKPoint(0f, ventRect.Bottom),
            new[] { SKColor.Parse("#F1F5F9"), SKColor.Parse("#CBD5E1"), SKColor.Parse("#64748B") },
            null,
            SKShaderTileMode.Clamp);
        using var ventFill = new SKPaint { Shader = ventShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(ventRect, 1.2f, 1.2f, ventFill);

        // Top vent cross score '+'
        using var ventScore = new SKPaint { Color = SKColor.Parse("#475569"), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(ventRect.MidX, -3f, ventRect.MidX, 3f, ventScore);
        canvas.DrawLine(ventRect.Left + 0.8f, 0f, ventRect.Right - 0.8f, 0f, ventScore);

        // 5. Cylindrical specular top crest shine
        using var topCrest = new SKPaint { Color = new SKColor(255, 255, 255, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(-half + stripeWidth + 1f, -halfHeight + 1.2f, half - ventWidth - 1f, -halfHeight + 1.2f, topCrest);

        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partCapEdgePaint);
    }

    private void DrawIcBody(SKCanvas canvas, float halfSpan, float clearance)
    {
        float bodyLength = MathF.Min(IcNominalLength, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(16f, bodyLength * 0.5f);
        const float halfHeight = 13f;

        Draw3DLeads(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);

        // 1. Soft 2.5D drop shadow
        using var icShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 85),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.4f)
        };
        canvas.DrawRoundRect(new SKRect(body.Left + 1.5f, body.Top + 2.2f, body.Right + 1.5f, body.Bottom + 2.2f), 2.8f, 2.8f, icShadow);

        // 2. Molded dark epoxy resin body with cylindrical lighting gradient
        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, -halfHeight),
            new SKPoint(0f, halfHeight),
            new[] { SKColor.Parse("#2D2D2D"), SKColor.Parse("#1A1A1A"), SKColor.Parse("#252525") },
            new[] { 0f, 0.45f, 1f },
            SKShaderTileMode.Clamp);
        using var bodyFillPaint = new SKPaint { Shader = bodyShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(body, 2.4f, 2.4f, bodyFillPaint);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcPinPaint);

        // 3. Longitudinal center parting mold seam groove
        using var groovePaint = new SKPaint { Color = new SKColor(10, 10, 10, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(-half + 8f, 0f, half - 8f, 0f, groovePaint);

        // 4. Pin-1 orientation notch and index dimple dot
        canvas.DrawCircle(-half + 7.5f, -halfHeight + 6.5f, 2.1f, _partIcMarkPaint);
        canvas.DrawArc(
            new SKRect(-half + 2.5f, -4.4f, -half + 11.5f, 4.4f),
            90f,
            180f,
            false,
            _partIcPinPaint);

        // 5. Metallic lead pins extending from both sides
        const int pinsPerSide = 4;
        float inner = half - 11f;
        using var legShine = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };

        for (int i = 0; i < pinsPerSide; i++)
        {
            float t = pinsPerSide == 1 ? 0.5f : i / (float)(pinsPerSide - 1);
            float x = -inner + t * inner * 2f;
            canvas.DrawLine(x, -halfHeight, x, -halfHeight - 4.6f, _partIcLegPaint);
            canvas.DrawLine(x - 0.4f, -halfHeight, x - 0.4f, -halfHeight - 4.6f, legShine);
            canvas.DrawLine(x, halfHeight, x, halfHeight + 4.6f, _partIcLegPaint);
            canvas.DrawLine(x - 0.4f, halfHeight, x - 0.4f, halfHeight + 4.6f, legShine);
        }
    }

    private void DrawButtonBody(SKCanvas canvas, float halfSpan, float clearance)
    {
        float size = MathF.Min(ButtonNominalSize, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(7f, size * 0.5f);

        Draw3DLeads(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -half, half, half);

        // 1. Soft 2.5D drop shadow
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 80),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.0f)
        };
        canvas.DrawRoundRect(new SKRect(body.Left + 1.4f, body.Top + 1.8f, body.Right + 1.4f, body.Bottom + 1.8f), 2.6f, 2.6f, shadow);

        // 2. Molded black thermoplastic body
        using var basePaint = new SKPaint { Color = SKColor.Parse("#1E1E22"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(body, 2.6f, 2.6f, basePaint);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partButtonEdgePaint);

        // 3. Stainless steel top retainer plate
        float plateMargin = half * 0.15f;
        var plateRect = new SKRect(body.Left + plateMargin, body.Top + plateMargin, body.Right - plateMargin, body.Bottom - plateMargin);
        using var plateShader = SKShader.CreateLinearGradient(
            new SKPoint(plateRect.Left, plateRect.Top),
            new SKPoint(plateRect.Right, plateRect.Bottom),
            new[] { SKColor.Parse("#CFD8DC"), SKColor.Parse("#ECEFF1"), SKColor.Parse("#90A4AE") },
            null,
            SKShaderTileMode.Clamp);
        using var platePaint = new SKPaint { Shader = plateShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(plateRect, 1.8f, 1.8f, platePaint);

        // 4 corner metallic rivets
        using var rivetPaint = new SKPaint { Color = SKColor.Parse("#455A64"), Style = SKPaintStyle.Fill, IsAntialias = true };
        float rOff = half * 0.62f;
        canvas.DrawCircle(-rOff, -rOff, 1.0f, rivetPaint);
        canvas.DrawCircle(rOff, -rOff, 1.0f, rivetPaint);
        canvas.DrawCircle(-rOff, rOff, 1.0f, rivetPaint);
        canvas.DrawCircle(rOff, rOff, 1.0f, rivetPaint);

        // 4. Central circular actuator plunger button with 3D dome shader
        float capRadius = half * 0.50f;
        using var plungerShader = SKShader.CreateRadialGradient(
            new SKPoint(-capRadius * 0.30f, -capRadius * 0.30f),
            capRadius * 1.30f,
            new[] { SKColor.Parse("#42A5F5"), SKColor.Parse("#1976D2"), SKColor.Parse("#0D47A1") },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        using var plungerPaint = new SKPaint { Shader = plungerShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(0f, 0f, capRadius, plungerPaint);
        canvas.DrawCircle(0f, 0f, capRadius, _partButtonEdgePaint);

        // Specular dome glint
        using var glint = new SKPaint { Color = new SKColor(255, 255, 255, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(-capRadius * 0.35f, -capRadius * 0.35f, capRadius * 0.24f, glint);
    }

    private void DrawDiodeBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        string label = value?.ToUpperInvariant() ?? string.Empty;
        bool isGlass = label.Contains("1N4148", StringComparison.Ordinal)
            || label.Contains("ZENER", StringComparison.Ordinal)
            || label.Contains("BZX", StringComparison.Ordinal);

        float bodyLength = MathF.Min(26f, halfSpan * 2f - clearance * 2f);
        if (bodyLength < 12f)
            bodyLength = MathF.Max(10f, halfSpan * 2f - clearance);

        float half = bodyLength * 0.5f;
        const float halfHeight = 6.2f;

        Draw3DLeads(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);

        // 1. Soft 2.5D drop shadow
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 75),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f)
        };
        canvas.DrawRoundRect(new SKRect(body.Left + 1.2f, body.Top + 1.6f, body.Right + 1.2f, body.Bottom + 1.6f), 2.8f, 2.8f, shadow);

        if (isGlass)
        {
            // DO-35 glass envelope with 3D translucent orange glass shader
            using var glassShader = SKShader.CreateLinearGradient(
                new SKPoint(0f, -halfHeight),
                new SKPoint(0f, halfHeight),
                new[] { SKColor.Parse("#FF9800"), SKColor.Parse("#E65100"), SKColor.Parse("#BF360C") },
                new[] { 0f, 0.45f, 1f },
                SKShaderTileMode.Clamp);
            using var glassPaint = new SKPaint { Shader = glassShader, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(body, 2.8f, 2.8f, glassPaint);

            // Internal copper heat slug and silicon die visible inside glass
            using var slugPaint = new SKPaint { Color = SKColor.Parse("#FFB74D"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(-half + 2.5f, -halfHeight * 0.5f, 3.5f, halfHeight, slugPaint);
            using var diePaint = new SKPaint { Color = SKColor.Parse("#212121"), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(-1.5f, -1.5f, 3.0f, 3.0f, diePaint);

            // Black cathode band near lead 2 (positive X in rotated space)
            float bandWidth = MathF.Max(2.6f, bodyLength * 0.18f);
            float bandRight = half - 1.2f;
            var band = new SKRect(bandRight - bandWidth, -halfHeight + 0.5f, bandRight, halfHeight - 0.5f);
            canvas.DrawRect(band, _partDiodeCathodeBlackPaint);
        }
        else
        {
            // DO-41 molded plastic rectifier (1N4007, 1N5819 Schottky) with cylindrical shading
            using var rectShader = SKShader.CreateLinearGradient(
                new SKPoint(0f, -halfHeight),
                new SKPoint(0f, halfHeight),
                new[] { SKColor.Parse("#374151"), SKColor.Parse("#1F2937"), SKColor.Parse("#111827") },
                new[] { 0f, 0.45f, 1f },
                SKShaderTileMode.Clamp);
            using var rectPaint = new SKPaint { Shader = rectShader, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(body, 2.8f, 2.8f, rectPaint);

            // Silver/white cathode band near lead 2
            float bandWidth = MathF.Max(3.0f, bodyLength * 0.20f);
            float bandRight = half - 1.2f;
            var band = new SKRect(bandRight - bandWidth, -halfHeight + 0.5f, bandRight, halfHeight - 0.5f);
            using var silverShader = SKShader.CreateLinearGradient(
                new SKPoint(0f, -halfHeight),
                new SKPoint(0f, halfHeight),
                new[] { SKColor.Parse("#FFFFFF"), SKColor.Parse("#E2E8F0"), SKColor.Parse("#94A3B8") },
                null,
                SKShaderTileMode.Clamp);
            using var silverPaint = new SKPaint { Shader = silverShader, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRect(band, silverPaint);
        }

        // Specular glass/epoxy highlight crest line
        using var topShine = new SKPaint { Color = new SKColor(255, 255, 255, 130), Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true };
        canvas.DrawLine(-half + 3f, -halfHeight + 1.4f, half - 3f, -halfHeight + 1.4f, topShine);

        canvas.DrawRoundRect(body, 2.8f, 2.8f, _partDiodeEdgePaint);

        if (_scale >= 0.72f && !string.IsNullOrWhiteSpace(value))
        {
            string shortLabel = label.Contains("1N4148", StringComparison.Ordinal) ? "1N4148"
                : label.Contains("1N4007", StringComparison.Ordinal) ? "1N4007"
                : label.Contains("1N5819", StringComparison.Ordinal) ? "1N5819"
                : label.Contains("ZENER", StringComparison.Ordinal) ? "ZENER"
                : (label.Length > 6 ? label[..6] : label);

            float tw = _partCapCeramicTextPaint.MeasureText(shortLabel);
            if (tw < bodyLength - 6f)
                canvas.DrawText(shortLabel, -tw * 0.5f, 2.2f, _partCapCeramicTextPaint);
        }

        if (Layers.ShowSensorPinLabels && _scale >= 0.52f)
        {
            DrawPinLabelPill(canvas, -halfSpan, 9f, "A (+)");
            DrawPinLabelPill(canvas, halfSpan, 9f, "K (-)");
        }
    }

    private void DrawBuzzerBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        float radius = MathF.Min(13f, MathF.Max(8f, halfSpan - clearance));
        Draw3DLeads(canvas, halfSpan, radius, clearance);

        // 1. Soft 2.5D drop shadow
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 85),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.2f)
        };
        canvas.DrawCircle(1.6f, 2.2f, radius, shadow);

        // 2. Circular matte black puck with cylindrical radial lighting
        using var puckShader = SKShader.CreateRadialGradient(
            new SKPoint(-radius * 0.35f, -radius * 0.35f),
            radius * 1.35f,
            new[] { SKColor.Parse("#374151"), SKColor.Parse("#1F2937"), SKColor.Parse("#111827") },
            new[] { 0f, 0.60f, 1f },
            SKShaderTileMode.Clamp);
        using var puckPaint = new SKPaint { Shader = puckShader, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(0f, 0f, radius, puckPaint);
        canvas.DrawCircle(0f, 0f, radius, _partBuzzerEdgePaint);

        // 3. Central acoustic sound hole with recessed interior shadow
        float holeR = radius * 0.35f;
        using var holeDepthPaint = new SKPaint { Color = SKColor.Parse("#030712"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(0f, 0f, holeR, holeDepthPaint);
        using var holeRim = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
        canvas.DrawCircle(0f, 0f, holeR, holeRim);

        // 4. Plus polarity (+) marking on lead 1 side (negative X in rotated space)
        float plusX = -radius * 0.65f;
        float plusArm = radius * 0.20f;
        canvas.DrawLine(plusX - plusArm, 0f, plusX + plusArm, 0f, _partBuzzerPlusPaint);
        canvas.DrawLine(plusX, -plusArm, plusX, plusArm, _partBuzzerPlusPaint);

        if (_scale >= 0.75f)
        {
            string text = value?.Contains("PIEZO", StringComparison.OrdinalIgnoreCase) == true ? "PIEZO" : "BUZZ";
            float tw = _partPinNumberPaint.MeasureText(text);
            canvas.DrawText(text, -tw * 0.5f, radius * 0.75f, _partPinNumberPaint);
        }

        if (ActiveFirmwareState is { IsRunning: true, IsBuzzerActive: true } fw)
        {
            int phase = fw.TickCount % 3;
            for (int w = 0; w < 3; w++)
            {
                float waveRadius = radius * (1.28f + (w * 0.36f) + (phase * 0.12f));
                byte alpha = (byte)(185 - (w * 50));
                using var wavePaint = new SKPaint
                {
                    Color = new SKColor(0x00, 0xE5, 0xFF, alpha),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.15f,
                    IsAntialias = true
                };
                canvas.DrawCircle(0f, 0f, waveRadius, wavePaint);
            }

            if (fw.BuzzerFrequencyHz > 0)
                DrawPinLabelPill(canvas, 0f, -radius - 6f, $"{fw.BuzzerFrequencyHz} Hz");
        }
    }

    private void DrawSensorBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        string label = value?.ToUpperInvariant() ?? string.Empty;

        // 1. BPW34 PIN Photodiode (Water-clear transparent DIL package with silicon chip)
        if (label.Contains("BPW34", StringComparison.Ordinal) || label.Contains("PIN PHOTO", StringComparison.Ordinal))
        {
            float bodyLength = MathF.Min(26f, halfSpan * 2f - clearance * 2f);
            if (bodyLength < 14f) bodyLength = MathF.Max(12f, halfSpan * 2f - clearance);
            float half = bodyLength * 0.5f;
            const float halfHeight = 10f;

            DrawLegs(canvas, halfSpan, half, clearance);

            // Soft drop shadow
            canvas.DrawRoundRect(new SKRect(-half + 1.8f, -halfHeight + 1.8f, half + 1.8f, halfHeight + 1.8f), 2.5f, 2.5f, _partShadowPaint);

            var body = new SKRect(-half, -halfHeight, half, halfHeight);
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partGlassFramePaint);

            // Internal silver lead frame tabs
            canvas.DrawRect(-half + 1.5f, -4.5f, 3.2f, 9f, _partDiodeCathodeSilverPaint);
            canvas.DrawRect(half - 4.7f, -4.5f, 3.2f, 9f, _partDiodeCathodeSilverPaint);

            // Active Silicon PIN photodiode die (dark navy-indigo square)
            var die = new SKRect(-5.5f, -5.5f, 5.5f, 5.5f);
            canvas.DrawRect(die, _partPhotodiodeDiePaint);
            canvas.DrawLine(-5.5f, 0f, 5.5f, 0f, _partGlassFramePaint);
            canvas.DrawLine(0f, -5.5f, 0f, 5.5f, _partGlassFramePaint);

            // Fine gold bond wire from die corner to pin 1 terminal
            canvas.DrawLine(-3.5f, -3.5f, -half + 4f, -2f, _partBondWirePaint);

            // Cathode index notch on pin 2 side
            canvas.DrawLine(half - 3.5f, -halfHeight, half, -halfHeight + 3.5f, _partButtonEdgePaint);

            // Diagonal clear resin reflection highlight
            canvas.DrawLine(-half + 4f, halfHeight - 2f, half - 4f, -halfHeight + 2f, _partHighlightPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("BPW34");
                canvas.DrawText("BPW34", -tw * 0.5f, halfHeight - 1.5f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // 2. PT334-6B Phototransistor (5mm black IR daylight-filter bulb with cathode flat)
        if (label.Contains("PT334", StringComparison.Ordinal) || label.Contains("IR-PT", StringComparison.Ordinal) || label.Contains("L14F1", StringComparison.Ordinal))
        {
            float radius = MathF.Min(13.5f, MathF.Max(8f, halfSpan - clearance));
            DrawLegs(canvas, halfSpan, radius, clearance);

            // Drop shadow
            canvas.DrawCircle(1.8f, 1.8f, radius, _partShadowPaint);

            // Deep black/dark-violet visible-light cutoff filter dome
            canvas.DrawCircle(0f, 0f, radius, _partIrFilterDomePaint);
            canvas.DrawCircle(0f, 0f, radius, _partButtonEdgePaint);

            // Cathode flat on pin 2 (emitter) side
            float flatX = radius * 0.75f;
            canvas.DrawLine(flatX, -radius * 0.65f, flatX, radius * 0.65f, _partButtonEdgePaint);

            // Specular glass dome highlight
            canvas.DrawCircle(-radius * 0.35f, -radius * 0.35f, radius * 0.28f, _partHighlightPaint);

            // Incoming IR radiation rays
            canvas.DrawLine(-radius * 0.95f, -radius * 1.15f, -radius * 0.35f, -radius * 0.68f, _partIrRayPaint);
            canvas.DrawLine(-radius * 0.35f, -radius * 1.30f, radius * 0.35f, -radius * 0.78f, _partIrRayPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("PT334");
                canvas.DrawText("PT334", -tw * 0.5f, radius * 0.75f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // 3. NTC 10K Thermistor (Cobalt blue teardrop bead with tapered neck)
        if (label.Contains("NTC", StringComparison.Ordinal) || label.Contains("THERM", StringComparison.Ordinal))
        {
            float radius = MathF.Min(13f, MathF.Max(8f, halfSpan * 0.52f));
            DrawLegs(canvas, halfSpan, radius, clearance);

            // Drop shadow
            canvas.DrawOval(new SKRect(-radius * 1.15f + 1.8f, -radius * 0.9f + 1.8f, radius * 1.15f + 1.8f, radius * 0.9f + 1.8f), _partShadowPaint);

            var bead = new SKRect(-radius * 1.15f, -radius * 0.9f, radius * 1.15f, radius * 0.9f);
            canvas.DrawOval(bead, _partSensorThermistorPaint);
            canvas.DrawOval(bead, _partButtonEdgePaint);

            // Specular gloss enamel highlights
            canvas.DrawCircle(-radius * 0.35f, -radius * 0.30f, radius * 0.26f, _partHighlightPaint);
            canvas.DrawArc(new SKRect(-radius * 0.85f, -radius * 0.6f, radius * 0.45f, radius * 0.5f), 190f, 75f, false, _partHighlightPaint);

            if (_scale >= 0.68f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("10K");
                canvas.DrawText("10K", -tw * 0.5f, 2.5f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // 4. SW-520D Tilt & Vibration Sensor (Gold-plated metal cylinder with black collar)
        if (label.Contains("SW-", StringComparison.Ordinal) || label.Contains("520D", StringComparison.Ordinal) || label.Contains("TILT", StringComparison.Ordinal))
        {
            float bodyLength = MathF.Min(32f, halfSpan * 2f - clearance * 2f);
            if (bodyLength < 16f) bodyLength = MathF.Max(14f, halfSpan * 2f - clearance);
            float half = bodyLength * 0.5f;
            const float halfHeight = 8f;

            DrawLegs(canvas, halfSpan, half, clearance);

            // Drop shadow
            canvas.DrawRoundRect(new SKRect(-half + 1.8f, -halfHeight + 1.8f, half + 1.8f, halfHeight + 1.8f), 3f, 3f, _partShadowPaint);

            var body = new SKRect(-half, -halfHeight, half, halfHeight);
            canvas.DrawRoundRect(body, 3f, 3f, _partTiltGoldPaint);

            // Horizontal metallic luster line
            canvas.DrawLine(-half + 3f, -1.5f, half - 3f, -1.5f, _partHighlightPaint);

            // Black rubber heat-shrink collar at lead emergence end (left side)
            var collar = new SKRect(-half, -halfHeight - 0.5f, -half + bodyLength * 0.28f, halfHeight + 0.5f);
            canvas.DrawRoundRect(collar, 2f, 2f, _partTiltCollarPaint);

            // Stamped crimp ring band
            float bandX = half - bodyLength * 0.25f;
            canvas.DrawLine(bandX, -halfHeight + 0.5f, bandX, halfHeight - 0.5f, _partButtonEdgePaint);

            canvas.DrawRoundRect(body, 3f, 3f, _partButtonEdgePaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorLabelDarkPaint.MeasureText("SW-520D");
                if (tw < bodyLength - 8f)
                    canvas.DrawText("SW-520D", -tw * 0.5f + 3f, 2.5f, _partSensorLabelDarkPaint);
            }
            return;
        }

        // 5. Electret Microphone Capsule (Silver aluminum can with black acoustic felt port)
        if (label.Contains("MIC", StringComparison.Ordinal) || label.Contains("ELECTRET", StringComparison.Ordinal))
        {
            float micRadius = MathF.Min(13.5f, MathF.Max(9f, halfSpan - clearance));
            DrawLegs(canvas, halfSpan, micRadius, clearance);

            // Drop shadow
            canvas.DrawCircle(1.8f, 1.8f, micRadius, _partShadowPaint);

            // Brushed aluminum outer canister
            canvas.DrawCircle(0f, 0f, micRadius, _partMicMetalRimPaint);
            canvas.DrawCircle(0f, 0f, micRadius, _partButtonEdgePaint);

            // Black acoustic felt diaphragm
            canvas.DrawCircle(0f, 0f, micRadius * 0.72f, _partMicFeltPortPaint);

            // Concentric acoustic ring
            canvas.DrawCircle(0f, 0f, micRadius * 0.46f, _partMicRingPaint);

            // Central acoustic sound inlet pinhole
            canvas.DrawCircle(0f, 0f, micRadius * 0.18f, _partBuzzerHolePaint);

            // Positive terminal polarity '+' mark on Pin 1 side (left)
            float plusX = -micRadius * 0.82f;
            canvas.DrawLine(plusX - 2.5f, 0f, plusX + 2.5f, 0f, _partBuzzerPlusPaint);
            canvas.DrawLine(plusX, -2.5f, plusX, 2.5f, _partBuzzerPlusPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorLabelDarkPaint.MeasureText("MIC");
                canvas.DrawText("MIC", micRadius * 0.22f, 2.5f, _partSensorLabelDarkPaint);
            }
            return;
        }

        // 6. LDR GL5528 (Terracotta ceramic disc with serpentine red CdS tracks)
        float ldrRadius = MathF.Min(14f, MathF.Max(9f, halfSpan * 0.55f));
        DrawLegs(canvas, halfSpan, ldrRadius, clearance);

        // Drop shadow
        canvas.DrawCircle(1.8f, 1.8f, ldrRadius, _partShadowPaint);

        canvas.DrawCircle(0f, 0f, ldrRadius, _partSensorLdrBodyPaint);
        canvas.DrawCircle(0f, 0f, ldrRadius, _partButtonEdgePaint);

        // Silver contact crescent pads
        canvas.DrawArc(new SKRect(-ldrRadius + 1f, -ldrRadius * 0.65f, -ldrRadius + 4f, ldrRadius * 0.65f), 90f, 180f, false, _partMicMetalRimPaint);
        canvas.DrawArc(new SKRect(ldrRadius - 4f, -ldrRadius * 0.65f, ldrRadius - 1f, ldrRadius * 0.65f), 270f, 180f, false, _partMicMetalRimPaint);

        // Interlocking serpentine red CdS tracks
        float span = ldrRadius * 0.60f;
        float step = ldrRadius * 0.28f;
        canvas.DrawLine(-span, -step, span, -step, _partSensorLdrTrackPaint);
        canvas.DrawLine(span, -step, span, 0f, _partSensorLdrTrackPaint);
        canvas.DrawLine(span, 0f, -span, 0f, _partSensorLdrTrackPaint);
        canvas.DrawLine(-span, 0f, -span, step, _partSensorLdrTrackPaint);
        canvas.DrawLine(-span, step, span, step, _partSensorLdrTrackPaint);

        // Clear resin dome specular highlight
        canvas.DrawCircle(-ldrRadius * 0.35f, -ldrRadius * 0.35f, ldrRadius * 0.26f, _partHighlightPaint);

        if (_scale >= 0.70f)
        {
            string shortLabel = label.Contains("GL5528", StringComparison.Ordinal) ? "GL5528" : "LDR";
            float tw = _partSensorLabelDarkPaint.MeasureText(shortLabel);
            canvas.DrawText(shortLabel, -tw * 0.5f, ldrRadius * 0.78f, _partSensorLabelDarkPaint);
        }
    }

    private void DrawBatteryBody(SKCanvas canvas, float halfSpan, float clearance, string? partValue)
    {
        string label = partValue?.Trim().ToUpperInvariant() ?? "9V";

        // CR2032 3V Coin Cell (Brushed silver circular button cell)
        if (label.Contains("CR2032", StringComparison.Ordinal) || label.Contains("COIN", StringComparison.Ordinal))
        {
            float coinRadius = MathF.Min(15f, MathF.Max(10f, halfSpan - clearance));
            DrawLegs(canvas, halfSpan, coinRadius, clearance);

            // Soft drop shadow
            canvas.DrawCircle(1.8f, 1.8f, coinRadius, _partShadowPaint);

            // Silver nickel-plated steel body
            canvas.DrawCircle(0f, 0f, coinRadius, _partBatteryTerminalPaint);
            canvas.DrawCircle(0f, 0f, coinRadius, _partButtonEdgePaint);

            // Concentric stamped rim
            canvas.DrawCircle(0f, 0f, coinRadius * 0.82f, _partMicRingPaint);

            // Engraved polarity '+' and text
            float plusY = -coinRadius * 0.45f;
            canvas.DrawLine(-3f, plusY, 3f, plusY, _partBuzzerPlusPaint);
            canvas.DrawLine(0f, plusY - 3f, 0f, plusY + 3f, _partBuzzerPlusPaint);

            if (_scale >= 0.70f)
            {
                float tw1 = _partSensorBadgeTextPaint.MeasureText("CR2032");
                canvas.DrawText("CR2032", -tw1 * 0.5f, 1.5f, _partSensorBadgeTextPaint);
                float tw2 = _partSensorBadgeTextPaint.MeasureText("3V");
                canvas.DrawText("3V", -tw2 * 0.5f, coinRadius * 0.65f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // 4xAA Battery Pack (6.0V black molded holder with 4 cylindrical battery outlines)
        if (label.Contains("4XAA", StringComparison.Ordinal) || label.Contains("6V", StringComparison.Ordinal) || label.Contains("AA", StringComparison.Ordinal))
        {
            float packWidth = MathF.Min(56f, MathF.Max(32f, halfSpan * 2f - clearance * 2f));
            float halfW = packWidth * 0.5f;
            const float halfH = 17f;

            DrawLegs(canvas, halfSpan, halfW, clearance);

            // Drop shadow
            canvas.DrawRoundRect(new SKRect(-halfW + 2f, -halfH + 2f, halfW + 2f, halfH + 2f), 3f, 3f, _partShadowPaint);

            // Black ABS enclosure
            var body = new SKRect(-halfW, -halfH, halfW, halfH);
            canvas.DrawRoundRect(body, 3f, 3f, _partBatteryBodyPaint);
            canvas.DrawRoundRect(body, 3f, 3f, _partButtonEdgePaint);

            // 4 cell ribs
            float cellH = (halfH * 2f - 4f) / 4f;
            for (int c = 0; c < 4; c++)
            {
                float topY = -halfH + 2f + c * cellH;
                var cellRect = new SKRect(-halfW + 3f, topY + 0.8f, halfW - 3f, topY + cellH - 0.8f);
                canvas.DrawRoundRect(cellRect, 1.5f, 1.5f, _partTiltCollarPaint);
                canvas.DrawCircle(-halfW + 5.5f, topY + cellH * 0.5f, 1.8f, _partBatteryTerminalPaint);
            }

            if (_scale >= 0.65f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("4x AA (6V)");
                canvas.DrawText("4x AA (6V)", -tw * 0.5f, 2.5f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // 18650 3.7V Li-ion Cell (Turquoise/green cylindrical cell with button top)
        if (label.Contains("18650", StringComparison.Ordinal) || label.Contains("3.7V", StringComparison.Ordinal))
        {
            float cellLength = MathF.Min(54f, MathF.Max(28f, halfSpan * 2f - clearance * 2f));
            float halfL = cellLength * 0.5f;
            const float halfR = 9.5f;

            DrawLegs(canvas, halfSpan, halfL, clearance);

            // Drop shadow
            canvas.DrawRoundRect(new SKRect(-halfL + 2f, -halfR + 2f, halfL + 2f, halfR + 2f), 3f, 3f, _partShadowPaint);

            // Turquoise/teal PVC wrap
            var body = new SKRect(-halfL, -halfR, halfL - 3f, halfR);
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partModulePcbPaint);
            canvas.DrawRoundRect(body, 2.5f, 2.5f, _partButtonEdgePaint);

            // Silver positive button top at right end
            var buttonTop = new SKRect(halfL - 3f, -halfR * 0.55f, halfL, halfR * 0.55f);
            canvas.DrawRoundRect(buttonTop, 1f, 1f, _partBatteryTerminalPaint);
            canvas.DrawRoundRect(buttonTop, 1f, 1f, _partButtonEdgePaint);

            // Negative flat base at left end
            canvas.DrawLine(-halfL, -halfR + 1f, -halfL, halfR - 1f, _partBatteryTerminalPaint);

            if (_scale >= 0.70f)
            {
                float tw = _partSensorBadgeTextPaint.MeasureText("18650 3.7V");
                canvas.DrawText("18650 3.7V", -tw * 0.5f - 1.5f, 2.5f, _partSensorBadgeTextPaint);
            }
            return;
        }

        // Standard 9V Alkaline Battery (6F22 Block: Navy body, gold front band, octagonal & circular snaps)
        float bodyLength = MathF.Min(56f, MathF.Max(32f, halfSpan * 2f - clearance * 2f));
        float halfW9v = bodyLength * 0.5f;
        const float halfH9v = 17f;

        if (halfSpan > halfW9v + 8f)
        {
            DrawBatteryLeadWires(canvas, halfSpan, halfW9v, clearance);
        }
        else
        {
            DrawLegs(canvas, halfSpan, halfW9v, clearance);
        }

        // Drop shadow
        canvas.DrawRoundRect(new SKRect(-halfW9v + 2f, -halfH9v + 2f, halfW9v + 2f, halfH9v + 2f), 3.5f, 3.5f, _partShadowPaint);

        // Main deep navy battery casing
        var body9v = new SKRect(-halfW9v, -halfH9v, halfW9v, halfH9v);
        canvas.DrawRoundRect(body9v, 3f, 3f, _partBatteryBodyPaint);
        canvas.DrawRoundRect(body9v, 3f, 3f, _partButtonEdgePaint);

        // Top gold accent band (Duracell style)
        float goldWidth = bodyLength * 0.32f;
        var goldBand = new SKRect(-halfW9v, -halfH9v, -halfW9v + goldWidth, halfH9v);
        canvas.DrawRoundRect(goldBand, 3f, 3f, _partBatteryGoldPaint);

        // Snap terminals on left edge
        // Negative snap (hexagonal/octagonal female cup)
        var snapMinus = new SKRect(-halfW9v - 3.5f, -9f, -halfW9v, -2f);
        canvas.DrawRoundRect(snapMinus, 1f, 1f, _partBatteryTerminalPaint);
        canvas.DrawRoundRect(snapMinus, 1f, 1f, _partButtonEdgePaint);

        // Positive snap (circular male stud)
        var snapPlus = new SKRect(-halfW9v - 4.5f, 2f, -halfW9v, 9f);
        canvas.DrawRoundRect(snapPlus, 2f, 2f, _partBatteryTerminalPaint);
        canvas.DrawRoundRect(snapPlus, 2f, 2f, _partButtonEdgePaint);

        // Terminal polarity markers '+' and '-'
        canvas.DrawLine(-halfW9v + 4f, 5.5f, -halfW9v + 8f, 5.5f, _partBuzzerPlusPaint);
        canvas.DrawLine(-halfW9v + 6f, 3.5f, -halfW9v + 6f, 7.5f, _partBuzzerPlusPaint);
        canvas.DrawLine(-halfW9v + 4f, -5.5f, -halfW9v + 8f, -5.5f, _partBuzzerPlusPaint);

        // "9V ALKALINE" branding
        if (_scale >= 0.65f)
        {
            float tw1 = _partIcLabelPaint.MeasureText("9V");
            canvas.DrawText("9V", -halfW9v + goldWidth * 0.5f - tw1 * 0.5f, 3.5f, _partIcLabelPaint);

            float tw2 = _partSensorBadgeTextPaint.MeasureText("ALKALINE");
            if (tw2 < bodyLength - goldWidth - 6f)
            {
                float rightCenterX = (-halfW9v + goldWidth + halfW9v) * 0.5f;
                canvas.DrawText("ALKALINE", rightCenterX - tw2 * 0.5f, 2.5f, _partSensorBadgeTextPaint);
            }
        }
    }

    private void DrawBatteryLeadWires(SKCanvas canvas, float halfSpan, float halfW, float clearance)
    {
        // 9V Battery Snap Clip with Red (+) and Black (-) flexible lead wires
        float wireRadius = MathF.Max(1.4f, 1.6f * _scale);

        // Positive lead wire (Red, departing positive snap stud at (+halfW, +5.5f))
        var pStart = new SKPoint(halfW, 5.5f);
        var pEnd = new SKPoint(halfSpan, 0f);
        float pDx = pEnd.X - pStart.X;
        var pC1 = new SKPoint(pStart.X + (pDx * 0.35f), pStart.Y + 8f);
        var pC2 = new SKPoint(pStart.X + (pDx * 0.75f), pEnd.Y + 4f);

        using var redPath = new SKPath();
        redPath.MoveTo(pStart);
        redPath.CubicTo(pC1, pC2, pEnd);

        // Negative lead wire (Black, departing negative snap cup at (-halfW, -5.5f))
        var nStart = new SKPoint(-halfW, -5.5f);
        var nEnd = new SKPoint(-halfSpan, 0f);
        float nDx = nEnd.X - nStart.X;
        var nC1 = new SKPoint(nStart.X + (nDx * 0.35f), nStart.Y - 8f);
        var nC2 = new SKPoint(nStart.X + (nDx * 0.75f), nEnd.Y - 4f);

        using var blackPath = new SKPath();
        blackPath.MoveTo(nStart);
        blackPath.CubicTo(nC1, nC2, nEnd);

        // 1. Soft 2.5D drop shadow for both flying lead wires
        using var leadShadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 75),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = wireRadius + 1.2f,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f)
        };
        canvas.Save();
        canvas.Translate(1.4f, 1.8f);
        canvas.DrawPath(redPath, leadShadow);
        canvas.DrawPath(blackPath, leadShadow);
        canvas.Restore();

        // 2. Black lead wire (underside shade + core + specular glint)
        using var blackCore = new SKPaint { Color = SKColor.Parse("#1A1A1D"), Style = SKPaintStyle.Stroke, StrokeWidth = wireRadius, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var blackGlint = new SKPaint { Color = new SKColor(255, 255, 255, 90), Style = SKPaintStyle.Stroke, StrokeWidth = wireRadius * 0.30f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawPath(blackPath, blackCore);
        canvas.Save();
        canvas.Translate(0f, -0.4f);
        canvas.DrawPath(blackPath, blackGlint);
        canvas.Restore();

        // 3. Red lead wire (underside shade + crimson core + specular glint)
        using var redUnder = new SKPaint { Color = SKColor.Parse("#8B0000"), Style = SKPaintStyle.Stroke, StrokeWidth = wireRadius + 0.4f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var redCore = new SKPaint { Color = SKColor.Parse("#E53935"), Style = SKPaintStyle.Stroke, StrokeWidth = wireRadius, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var redGlint = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = wireRadius * 0.30f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawPath(redPath, redUnder);
        canvas.DrawPath(redPath, redCore);
        canvas.Save();
        canvas.Translate(0f, -0.4f);
        canvas.DrawPath(redPath, redGlint);
        canvas.Restore();
    }

    private void DrawAvometerProbes(SKCanvas canvas)
    {
        if (!IsDmmProbesVisible)
            return;

        // Draw Black COM Probe if set
        if (DmmBlackProbe.HasValue && !string.IsNullOrEmpty(DmmBlackProbe.Value.HoleId))
        {
            DrawDmmProbeNeedle(canvas, DmmBlackProbe.Value.Px, DmmBlackProbe.Value.Py, isRed: false);
        }

        // Draw Red V/Ω Probe if set
        if (DmmRedProbe.HasValue && !string.IsNullOrEmpty(DmmRedProbe.Value.HoleId))
        {
            DrawDmmProbeNeedle(canvas, DmmRedProbe.Value.Px, DmmRedProbe.Value.Py, isRed: true);
        }
    }

    private void DrawDmmProbeNeedle(SKCanvas canvas, float px, float py, bool isRed)
    {
        SKPaint bodyPaint = isRed ? _dmmRedProbePaint : _dmmBlackProbePaint;
        SKPaint wirePaint = isRed ? _dmmWireRedPaint : _dmmWireBlackPaint;

        canvas.Save();
        canvas.Translate(px, py);

        // Slender stainless steel needle tip entering the hole center
        canvas.DrawLine(0f, 0f, 6f, -14f, _dmmNeedlePaint);
        canvas.DrawCircle(0f, 0f, 1.8f, _partBatteryTerminalPaint);

        // Brass shoulder / stop ring
        canvas.DrawCircle(6f, -14f, 2.8f, _partBatteryGoldPaint);

        // Fluted ergonomic probe handle angled at ~25 degrees
        canvas.Save();
        canvas.Translate(6f, -14f);
        canvas.RotateDegrees(25f);

        // Handle body
        var handle = new SKRect(-3.5f, -32f, 3.5f, 0f);
        canvas.DrawRoundRect(handle, 1.8f, 1.8f, bodyPaint);
        canvas.DrawRoundRect(handle, 1.8f, 1.8f, _partButtonEdgePaint);

        // Finger guard collar flange
        var guard = new SKRect(-6f, -7f, 6f, -3.5f);
        canvas.DrawRoundRect(guard, 1f, 1f, bodyPaint);
        canvas.DrawRoundRect(guard, 1f, 1f, _partButtonEdgePaint);

        // Flexible strain-relief boot at top
        var boot = new SKRect(-2.2f, -38f, 2.2f, -32f);
        canvas.DrawRoundRect(boot, 1f, 1f, _partTiltCollarPaint);

        // Probe polarity text
        if (_scale >= 0.70f)
        {
            string txt = isRed ? "V/Ω" : "COM";
            float tw = _partSensorBadgeTextPaint.MeasureText(txt);
            canvas.DrawText(txt, -tw * 0.5f, -14f, _partSensorBadgeTextPaint);
        }

        canvas.Restore();

        // Trailing coiled flexible test lead wire
        _partWirePath.Reset();
        _partWirePath.MoveTo(18f, -48f);
        _partWirePath.CubicTo(28f, -70f, 40f, -50f, 65f, -80f);
        canvas.DrawPath(_partWirePath, wirePaint);

        canvas.Restore();
    }

    private void DrawUnknownBody(SKCanvas canvas, float halfSpan)
    {
        canvas.DrawLine(-halfSpan, 0f, halfSpan, 0f, _partLeadPaint);
        var body = new SKRect(-7f, -7f, 7f, 7f);
        canvas.DrawRoundRect(body, 2f, 2f, _partIcBodyPaint);
        canvas.DrawRoundRect(body, 2f, 2f, _partIcPinPaint);
    }

    private void DrawLegs(SKCanvas canvas, float halfSpan, float bodyHalf, float clearance)
    {
        float legEnd = MathF.Min(halfSpan, bodyHalf + clearance);
        if (legEnd <= bodyHalf + 0.5f)
            legEnd = halfSpan;

        canvas.DrawLine(-halfSpan, 0f, -legEnd, 0f, _partLeadPaint);
        canvas.DrawLine(legEnd, 0f, halfSpan, 0f, _partLeadPaint);
    }

    /// <summary>Realistic 2.5D/3D curved jumper wire with metallic crimp ferrules, drop shadow, and animated current pulses.</summary>
    private void DrawJumperWire(SKCanvas canvas, in SnappedComponent component, bool hasViolation = false)
    {
        ApplyPartPaintsForScale();

        HoleCoord pin1 = component.Pin1Hole;
        HoleCoord pin2 = component.Pin2Hole;

        float dx = pin2.Px - pin1.Px;
        float dy = pin2.Py - pin1.Py;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 6f)
            return;

        string hexColor = SelectJumperHexColor(component);
        const float boardUnitsPerMm = BoardWidth / BreadboardWidthMm; // ~6.057 board units/mm
        Draw3DJumperWire(
            canvas,
            new SKPoint(pin1.Px, pin1.Py),
            new SKPoint(pin2.Px, pin2.Py),
            hexColor,
            hasViolation: hasViolation,
            pxPerMm: boardUnitsPerMm,
            breadboardRect: new SKRect(0f, 0f, BoardWidth, BoardHeight));
    }

    private static string SelectJumperHexColor(in SnappedComponent component)
    {
        string text = component.RawDetection.DecodedValueLabel?.Trim().ToUpperInvariant() ?? string.Empty;

        if (text.Contains("RED", StringComparison.Ordinal)) return "#D7373F";
        if (text.Contains("BLK", StringComparison.Ordinal) || text.Contains("BLACK", StringComparison.Ordinal)) return "#1E1E22";
        if (text.Contains("BLU", StringComparison.Ordinal) || text.Contains("BLUE", StringComparison.Ordinal)) return "#1976D2";
        if (text.Contains("YEL", StringComparison.Ordinal) || text.Contains("YELLOW", StringComparison.Ordinal)) return "#EBB424";
        if (text.Contains("GRN", StringComparison.Ordinal) || text.Contains("GREEN", StringComparison.Ordinal)) return "#2E8B46";
        if (text.Contains("ORG", StringComparison.Ordinal) || text.Contains("ORANGE", StringComparison.Ordinal)) return "#E0741E";
        if (text.Contains("PUR", StringComparison.Ordinal) || text.Contains("PURPLE", StringComparison.Ordinal) || text.Contains("VIOLET", StringComparison.Ordinal)) return "#7A3E8E";
        if (text.Contains("WHT", StringComparison.Ordinal) || text.Contains("WHITE", StringComparison.Ordinal)) return "#F0F0EB";

        int s1 = component.Pin1Hole.StripId;
        int s2 = component.Pin2Hole.StripId;
        if (s1 == BreadboardSpec.StripTopVcc || s2 == BreadboardSpec.StripTopVcc || s1 == BreadboardSpec.StripBotVcc || s2 == BreadboardSpec.StripBotVcc)
            return "#D7373F";
        if (s1 == BreadboardSpec.StripTopGnd || s2 == BreadboardSpec.StripTopGnd || s1 == BreadboardSpec.StripBotGnd || s2 == BreadboardSpec.StripBotGnd)
            return "#1976D2";

        return "#7A8A74";
    }

    private SKPaint SelectJumperPaint(in SnappedComponent component)
    {
        string text = component.RawDetection.DecodedValueLabel?.Trim().ToUpperInvariant() ?? string.Empty;

        if (text.Contains("RED", StringComparison.Ordinal))
            return _jumperRedPaint;
        if (text.Contains("BLK", StringComparison.Ordinal) || text.Contains("BLACK", StringComparison.Ordinal))
            return _jumperBlackPaint;
        if (text.Contains("BLU", StringComparison.Ordinal) || text.Contains("BLUE", StringComparison.Ordinal))
            return _jumperBluePaint;
        if (text.Contains("YEL", StringComparison.Ordinal) || text.Contains("YELLOW", StringComparison.Ordinal))
            return _jumperYellowPaint;
        if (text.Contains("GRN", StringComparison.Ordinal) || text.Contains("GREEN", StringComparison.Ordinal))
            return _jumperGreenPaint;
        if (text.Contains("ORG", StringComparison.Ordinal) || text.Contains("ORANGE", StringComparison.Ordinal))
            return _jumperOrangePaint;
        if (text.Contains("PUR", StringComparison.Ordinal) || text.Contains("PURPLE", StringComparison.Ordinal) || text.Contains("VIOLET", StringComparison.Ordinal))
            return _jumperPurplePaint;
        if (text.Contains("WHT", StringComparison.Ordinal) || text.Contains("WHITE", StringComparison.Ordinal))
            return _jumperWhitePaint;

        int s1 = component.Pin1Hole.StripId;
        int s2 = component.Pin2Hole.StripId;
        if (s1 == BreadboardSpec.StripTopVcc || s2 == BreadboardSpec.StripTopVcc || s1 == BreadboardSpec.StripBotVcc || s2 == BreadboardSpec.StripBotVcc)
            return _jumperRedPaint;
        if (s1 == BreadboardSpec.StripTopGnd || s2 == BreadboardSpec.StripTopGnd || s1 == BreadboardSpec.StripBotGnd || s2 == BreadboardSpec.StripBotGnd)
            return _jumperBluePaint;

        return _partJumperPaint;
    }

    private void ApplyPartPaintsForScale()
    {
        if (!_partPaintsScaled || _lastPartScale != _scale)
        {
            float inverse = 1f / _scale;
            _partLeadPaint.StrokeWidth = 1.6f * inverse;
            _partHighlightPaint.StrokeWidth = 0.9f * inverse;
            _partResistorEdgePaint.StrokeWidth = 1f * inverse;
            _partLedEdgePaint.StrokeWidth = 1.1f * inverse;
            _partLedEmissionPaint.StrokeWidth = 0.9f * inverse;
            _partCapEdgePaint.StrokeWidth = 1f * inverse;
            _partCapCeramicEdgePaint.StrokeWidth = 1f * inverse;
            _partIcPinPaint.StrokeWidth = 1.1f * inverse;
            _partIcLegPaint.StrokeWidth = 1.2f * inverse;
            _partButtonEdgePaint.StrokeWidth = 1f * inverse;
            _partDiodeEdgePaint.StrokeWidth = 1f * inverse;
            _partBuzzerEdgePaint.StrokeWidth = 1.2f * inverse;
            _partBuzzerPlusPaint.StrokeWidth = 1.3f * inverse;
            _partSensorLdrTrackPaint.StrokeWidth = 1.2f * inverse;
            _partPotSlotPaint.StrokeWidth = 1.3f * inverse;
            _partBondWirePaint.StrokeWidth = 0.8f * inverse;
            _partGlassFramePaint.StrokeWidth = 1f * inverse;
            _partIrRayPaint.StrokeWidth = 0.9f * inverse;
            _partMicRingPaint.StrokeWidth = 0.9f * inverse;
            _partPirFacetPaint.StrokeWidth = 0.8f * inverse;
            _partUltrasonicMeshPaint.StrokeWidth = 0.8f * inverse;
            _partDht22MeshPaint.StrokeWidth = 0.7f * inverse;
            _partGasMeshPaint.StrokeWidth = 0.8f * inverse;
            _partFluxPaint.StrokeWidth = 1f * inverse;
            _partEyeletPaint.StrokeWidth = 1f * inverse;
            _partUltrasonicBezelPaint.StrokeWidth = 1f * inverse;
            _partBatteryPlusLeadPaint.StrokeWidth = 1.8f * inverse;
            _partBatteryMinusLeadPaint.StrokeWidth = 1.8f * inverse;
            _dmmNeedlePaint.StrokeWidth = 1.4f * inverse;
            _dmmWireRedPaint.StrokeWidth = 1.6f * inverse;
            _dmmWireBlackPaint.StrokeWidth = 1.6f * inverse;

            float jumperWidth = 2.4f * inverse;
            _partJumperPaint.StrokeWidth = jumperWidth;
            _jumperRedPaint.StrokeWidth = jumperWidth;
            _jumperBlackPaint.StrokeWidth = jumperWidth;
            _jumperBluePaint.StrokeWidth = jumperWidth;
            _jumperYellowPaint.StrokeWidth = jumperWidth;
            _jumperGreenPaint.StrokeWidth = jumperWidth;
            _jumperOrangePaint.StrokeWidth = jumperWidth;
            _jumperPurplePaint.StrokeWidth = jumperWidth;
            _jumperWhitePaint.StrokeWidth = jumperWidth;

            _partFlagPaint.StrokeWidth = 1.2f * inverse;
            _partSelectionPaint.StrokeWidth = 1.5f * inverse;
            _partPinNumberPaint.TextSize = 7f * inverse;
            _partIcLabelPaint.TextSize = 7.5f * inverse;
            _partCapCeramicTextPaint.TextSize = 6.5f * inverse;
            _partSensorLabelDarkPaint.TextSize = 7.5f * inverse;
            _partSensorBadgeTextPaint.TextSize = 6.5f * inverse;
            _pinVccTextPaint.TextSize = 6.5f * inverse;
            _pinGndTextPaint.TextSize = 6.5f * inverse;
            _pinSignalTextPaint.TextSize = 6.5f * inverse;
            _pinGeneralTextPaint.TextSize = 6.5f * inverse;
            _pinI2cTextPaint.TextSize = 6.5f * inverse;
            _pinPillBorderPaint.StrokeWidth = 0.7f * inverse;

            _lastPartScale = _scale;
            _partPaintsScaled = true;
        }
    }

    private float _lastPartScale = float.NaN;

    // ---- resistor colour code ---------------------------------------------

    /// <summary>Decodes 220R / 4k7 / 10k / 1M into four band palette indexes.</summary>
    internal static bool TryResistorBandCodes(string? value, Span<int> bands)
    {
        return BreadboardAOI.Core.Geometry.ResistorColorCode.TryDecodeBands(value, bands);
    }

    internal static SKColor LedColour(string? value)
    {
        string text = value?.ToUpperInvariant() ?? string.Empty;

        if (text.Contains("GRN", StringComparison.Ordinal) || text.Contains("GREEN", StringComparison.Ordinal))
            return new SKColor(0x00, 0xE6, 0x76); // High-luminance Emerald Green
        if (text.Contains("BLU", StringComparison.Ordinal) || text.Contains("BLUE", StringComparison.Ordinal))
            return new SKColor(0x29, 0x79, 0xFF); // Royal Sapphire Blue
        if (text.Contains("YEL", StringComparison.Ordinal) || text.Contains("YELLOW", StringComparison.Ordinal))
            return new SKColor(0xFF, 0xEA, 0x00); // Radiant Amber Yellow
        if (text.Contains("ORG", StringComparison.Ordinal) || text.Contains("ORANGE", StringComparison.Ordinal) || text.Contains("AMBER", StringComparison.Ordinal))
            return new SKColor(0xFF, 0x91, 0x00); // Deep Amber Orange
        if (text.Contains("WHT", StringComparison.Ordinal) || text.Contains("WHITE", StringComparison.Ordinal))
            return new SKColor(0xFA, 0xFA, 0xFA); // Crisp Crystal White
        if (text.Contains("PUR", StringComparison.Ordinal) || text.Contains("VIOLET", StringComparison.Ordinal) || text.Contains("UV", StringComparison.Ordinal))
            return new SKColor(0xD5, 0x00, 0xF9); // Neon Purple / UV
        if (text.Contains("CYAN", StringComparison.Ordinal) || text.Contains("AQUA", StringComparison.Ordinal))
            return new SKColor(0x00, 0xE5, 0xFF); // Bright Cyan
        if (text.Contains("PINK", StringComparison.Ordinal))
            return new SKColor(0xFF, 0x40, 0x81); // Vibrant Pink

        return new SKColor(0xFF, 0x17, 0x44); // Vivid Ruby Red (default 5mm LED)
    }

    private static SKPaint DashedStroke(byte red, byte green, byte blue, float width)
    {
        SKPaint paint = Stroke(red, green, blue, width);
        paint.PathEffect = SKPathEffect.CreateDash([3.5f, 2.5f], 0f);
        return paint;
    }
}
