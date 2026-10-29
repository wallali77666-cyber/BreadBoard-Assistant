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
    private readonly SKPaint _partResistorBodyPaint = Fill(0xCB, 0xB6, 0x8A);
    private readonly SKPaint _partResistorEdgePaint = Stroke(0x4C, 0x45, 0x38, 1f);
    private readonly SKPaint _partLedBodyPaint = Fill(0xD9, 0x3A, 0x3A);
    private readonly SKPaint _partLedEdgePaint = Stroke(0x3E, 0x1D, 0x1D, 1.1f);
    private readonly SKPaint _partLedEmissionPaint = Stroke(0xB0, 0x8A, 0x8A, 0.9f);
    private readonly SKPaint _partCapBodyPaint = Fill(0x2E, 0x4D, 0x6C);
    private readonly SKPaint _partCapStripePaint = Fill(0xDC, 0xDA, 0xD2);
    private readonly SKPaint _partCapEdgePaint = Stroke(0x18, 0x2A, 0x3C, 1f);
    private readonly SKPaint _partIcBodyPaint = Fill(0x2B, 0x2B, 0x30);
    private readonly SKPaint _partIcMarkPaint = Fill(0xA8, 0xA6, 0x9E);
    private readonly SKPaint _partIcPinPaint = Stroke(0xC4, 0xC2, 0xB8, 1.1f);
    private readonly SKPaint _partIcLegPaint = Stroke(0x6A, 0x6A, 0x64, 1.2f);
    private readonly SKPaint _partButtonBasePaint = Fill(0x3A, 0x3A, 0x3F);
    private readonly SKPaint _partButtonCapPaint = Fill(0xBC, 0xB8, 0xAE);
    private readonly SKPaint _partButtonEdgePaint = Stroke(0x22, 0x22, 0x25, 1f);
    private readonly SKPaint _partJumperPaint = Stroke(0x7A, 0x8A, 0x74, 2.4f);
    private readonly SKPaint _partJumperPlugPaint = Fill(0x3A, 0x3A, 0x3D);
    private readonly SKPaint _partFlagPaint = DashedStroke(0xD7, 0x37, 0x3F, 1.2f);
    private readonly SKPaint _partPinNumberPaint;

    /// <summary>Resistor colour code: black brown red orange yellow green blue violet grey white gold silver.</summary>
    private readonly SKPaint[] _bandPaints =
    [
        Fill(0x1A, 0x1A, 0x18),
        Fill(0x6B, 0x42, 0x24),
        Fill(0xC0, 0x2E, 0x2E),
        Fill(0xE0, 0x74, 0x1E),
        Fill(0xE8, 0xC5, 0x2B),
        Fill(0x2E, 0x8B, 0x46),
        Fill(0x2A, 0x5C, 0xA8),
        Fill(0x7A, 0x3E, 0x8E),
        Fill(0x80, 0x80, 0x7C),
        Fill(0xF2, 0xF0, 0xE6),
        Fill(0xC9, 0xA2, 0x3A),
        Fill(0xB8, 0xB8, 0xB4)
    ];

    private readonly SKPath _partWirePath = new();

    private bool _partPaintsScaled;

    /// <summary>Draws the physical part between its two snapped holes.</summary>
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
                DrawLedBody(canvas, halfSpan, clearance, component.RawDetection.DecodedValueLabel);
                break;

            case ComponentKind.Capacitor:
                DrawCapacitorBody(canvas, halfSpan, clearance);
                break;

            case ComponentKind.IntegratedCircuit:
                DrawIcBody(canvas, halfSpan, clearance);
                break;

            case ComponentKind.PushButton:
                DrawButtonBody(canvas, halfSpan, clearance);
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
            DrawDualRowBody(canvas, xs, ys, minX, maxX, minY, maxY);
        else
            DrawSingleRowBody(canvas, xs, ys, minX, maxX, minY);
    }

    private void DrawDualRowBody(
        SKCanvas canvas,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        float minX,
        float maxX,
        float minY,
        float maxY)
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

        // One leg per hole: the package outline follows the real footprint width.
        for (int pin = 0; pin < xs.Length; pin++)
        {
            float x = xs[pin];
            float y = ys[pin];
            if (y <= minY + 2f)
                canvas.DrawLine(x, y, x, bodyTop, _partIcLegPaint);
            else
                canvas.DrawLine(x, y, x, bodyBottom, _partIcLegPaint);
        }

        var body = new SKRect(left, bodyTop, right, bodyBottom);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcBodyPaint);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcPinPaint);

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

    private void DrawSingleRowBody(
        SKCanvas canvas,
        ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys,
        float minX,
        float maxX,
        float minY)
    {
        float rowY = minY;
        bool above = rowY < (BreadboardSpec.UpperFirstRowY + 4f * BreadboardSpec.RowPitch + BreadboardSpec.LowerFirstRowY) * 0.5f;

        const float bodyHeight = 15f;
        const float legGap = 5f;
        float bodyNear = above ? rowY - legGap : rowY + legGap;
        float bodyFar = above ? bodyNear - bodyHeight : bodyNear + bodyHeight;

        float left = minX - 5f;
        float right = maxX + 5f;

        for (int pin = 0; pin < xs.Length; pin++)
            canvas.DrawLine(xs[pin], ys[pin], xs[pin], bodyNear, _partIcLegPaint);

        var body = new SKRect(left, MathF.Min(bodyNear, bodyFar), right, MathF.Max(bodyNear, bodyFar));
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcBodyPaint);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partIcPinPaint);

        // Flat face of a TO-92 points away from pin 1: draw it as a chamfer.
        float flatY = above ? body.Top + 4.5f : body.Bottom - 4.5f;
        canvas.DrawLine(body.Left + 4f, flatY, body.Right - 4f, flatY, _partIcMarkPaint);

        if (_scale < 0.72f || right - left < 30f)
            return;

        for (int pin = 0; pin < xs.Length; pin++)
        {
            string number = (pin + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            float width = _partPinNumberPaint.MeasureText(number);
            float baseline = above ? bodyNear - 3f : bodyNear + _partPinNumberPaint.TextSize + 1f;
            canvas.DrawText(number, xs[pin] - width * 0.5f, baseline, _partPinNumberPaint);
        }
    }

    private void DrawResistorBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        float bodyLength = MathF.Min(ResistorNominalLength, halfSpan * 2f - clearance * 2f);
        if (bodyLength < 14f)
            bodyLength = MathF.Max(10f, halfSpan * 2f - clearance);

        float half = bodyLength * 0.5f;
        const float halfHeight = 7f;

        DrawLegs(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);
        canvas.DrawRoundRect(body, 3.4f, 3.4f, _partResistorBodyPaint);
        canvas.DrawRoundRect(body, 3.4f, 3.4f, _partResistorEdgePaint);

        Span<int> bands = stackalloc int[4];
        if (!TryResistorBandCodes(value, bands))
        {
            bands[0] = 2;
            bands[1] = 2;
            bands[2] = 1;
            bands[3] = 10;
        }

        float bandWidth = MathF.Max(1.6f, bodyLength * 0.085f);
        ReadOnlySpan<float> fractions = [0.2f, 0.38f, 0.56f, 0.76f];
        for (int i = 0; i < 4 && i < _bandPaints.Length; i++)
        {
            float centre = -half + bodyLength * fractions[i];
            var band = new SKRect(centre - bandWidth * 0.5f, -halfHeight + 0.6f, centre + bandWidth * 0.5f, halfHeight - 0.6f);
            canvas.DrawRect(band, _bandPaints[bands[i]]);
        }
    }

    private void DrawLedBody(SKCanvas canvas, float halfSpan, float clearance, string? value)
    {
        float radius = MathF.Min(LedNominalRadius, MathF.Max(5.4f, halfSpan - clearance));
        float bodyCentre = MathF.Min(halfSpan - radius, radius * 0.35f);
        if (bodyCentre < -halfSpan + radius)
            bodyCentre = 0f;

        _partLedBodyPaint.Color = LedColour(value);

        DrawLegs(canvas, halfSpan, radius, clearance);

        canvas.DrawCircle(bodyCentre, 0f, radius, _partLedBodyPaint);
        canvas.DrawCircle(bodyCentre, 0f, radius, _partLedEdgePaint);

        // Cathode flat on the pin-2 side plus the emission arrows of a lit LED.
        float flatX = bodyCentre + radius * 0.72f;
        float flatHalf = radius * 0.62f;
        canvas.DrawLine(flatX, -flatHalf, flatX, flatHalf, _partLedEdgePaint);

        float arrowX = bodyCentre + radius * 1.15f;
        float arrowY = -radius * 0.95f;
        canvas.DrawLine(arrowX, arrowY, arrowX + radius * 0.55f, arrowY - radius * 0.55f, _partLedEmissionPaint);
        canvas.DrawLine(arrowX + radius * 0.1f, arrowY + radius * 0.3f, arrowX + radius * 0.6f, arrowY - radius * 0.2f, _partLedEmissionPaint);

        // Specular highlight so the dome reads as glass, not a flat disc.
        canvas.DrawCircle(bodyCentre - radius * 0.3f, -radius * 0.34f, radius * 0.26f, _partHighlightPaint);
    }

    private void DrawCapacitorBody(SKCanvas canvas, float halfSpan, float clearance)
    {
        float bodyLength = MathF.Min(CapacitorNominalLength, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(8f, bodyLength * 0.5f);
        const float halfHeight = 8f;

        DrawLegs(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partCapBodyPaint);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partCapEdgePaint);

        // Negative stripe toward pin 1, as printed on a radial electrolytic.
        float stripeWidth = MathF.Max(2.4f, bodyLength * 0.18f);
        var stripe = new SKRect(-half - 0.4f, -halfHeight, -half + stripeWidth, halfHeight);
        canvas.DrawRoundRect(stripe, 2.2f, 2.2f, _partCapStripePaint);

        canvas.DrawLine(-half + stripeWidth + 3f, -halfHeight + 3f, -half + stripeWidth + 3f, halfHeight - 3f, _partCapStripePaint);
    }

    private void DrawIcBody(SKCanvas canvas, float halfSpan, float clearance)
    {
        float bodyLength = MathF.Min(IcNominalLength, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(16f, bodyLength * 0.5f);
        const float halfHeight = 13f;

        DrawLegs(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -halfHeight, half, halfHeight);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcBodyPaint);
        canvas.DrawRoundRect(body, 2.4f, 2.4f, _partIcPinPaint);

        // Pin-1 notch and index dot at the pin-1 end.
        canvas.DrawCircle(-half + 7.5f, -halfHeight + 6.5f, 2.1f, _partIcMarkPaint);
        canvas.DrawArc(
            new SKRect(-half + 2.5f, -4.4f, -half + 11.5f, 4.4f),
            90f,
            180f,
            false,
            _partIcPinPaint);

        // Four pin stubs per side: the DIP package look without pretending to know the die.
        const int pinsPerSide = 4;
        float inner = half - 11f;
        for (int i = 0; i < pinsPerSide; i++)
        {
            float t = pinsPerSide == 1 ? 0.5f : i / (float)(pinsPerSide - 1);
            float x = -inner + t * inner * 2f;
            canvas.DrawLine(x, -halfHeight, x, -halfHeight - 4.6f, _partIcLegPaint);
            canvas.DrawLine(x, halfHeight, x, halfHeight + 4.6f, _partIcLegPaint);
        }
    }

    private void DrawButtonBody(SKCanvas canvas, float halfSpan, float clearance)
    {
        float size = MathF.Min(ButtonNominalSize, halfSpan * 2f - clearance * 2f);
        float half = MathF.Max(6f, size * 0.5f);

        DrawLegs(canvas, halfSpan, half, clearance);

        var body = new SKRect(-half, -half, half, half);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partButtonBasePaint);
        canvas.DrawRoundRect(body, 2.6f, 2.6f, _partButtonEdgePaint);

        // Corrugated actuator cap.
        float capRadius = half * 0.58f;
        canvas.DrawCircle(0f, 0f, capRadius, _partButtonCapPaint);
        canvas.DrawCircle(0f, 0f, capRadius, _partButtonEdgePaint);
        canvas.DrawCircle(0f, 0f, capRadius * 0.42f, _partButtonBasePaint);
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

    /// <summary>Bowed jumper wire with a socket plug at each tie point.</summary>
    private void DrawJumperWire(SKCanvas canvas, in SnappedComponent component)
    {
        ApplyPartPaintsForScale();

        HoleCoord pin1 = component.Pin1Hole;
        HoleCoord pin2 = component.Pin2Hole;

        float dx = pin2.Px - pin1.Px;
        float dy = pin2.Py - pin1.Py;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 6f)
            return;

        // Perpendicular bow, always lifting toward the top of the board.
        float normalX = -dy / length;
        float normalY = dx / length;
        if (normalY > 0f)
        {
            normalX = -normalX;
            normalY = -normalY;
        }

        float bow = MathF.Min(30f, length * 0.2f);
        float controlX = (pin1.Px + pin2.Px) * 0.5f + normalX * bow;
        float controlY = (pin1.Py + pin2.Py) * 0.5f + normalY * bow;

        _partWirePath.Reset();
        _partWirePath.MoveTo(pin1.Px, pin1.Py);
        _partWirePath.QuadTo(controlX, controlY, pin2.Px, pin2.Py);
        canvas.DrawPath(_partWirePath, _partJumperPaint);

        canvas.DrawCircle(pin1.Px, pin1.Py, 2.9f, _partJumperPlugPaint);
        canvas.DrawCircle(pin2.Px, pin2.Py, 2.9f, _partJumperPlugPaint);
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
            _partIcPinPaint.StrokeWidth = 1.1f * inverse;
            _partIcLegPaint.StrokeWidth = 1.2f * inverse;
            _partButtonEdgePaint.StrokeWidth = 1f * inverse;
            _partJumperPaint.StrokeWidth = 2.4f * inverse;
            _partFlagPaint.StrokeWidth = 1.2f * inverse;
            _partPinNumberPaint.TextSize = 7f * inverse;

            _lastPartScale = _scale;
            _partPaintsScaled = true;
        }
    }

    private float _lastPartScale = float.NaN;

    // ---- resistor colour code ---------------------------------------------

    /// <summary>Decodes 220R / 4k7 / 10k / 1M into four band palette indexes.</summary>
    internal static bool TryResistorBandCodes(string? value, Span<int> bands)
    {
        if (bands.Length < 4 || string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim().ToUpperInvariant()
            .Replace("OHMS", string.Empty, StringComparison.Ordinal)
            .Replace("OHM", string.Empty, StringComparison.Ordinal)
            .Replace("Ω", string.Empty, StringComparison.Ordinal);

        if (text.Length == 0)
            return false;

        // Split a decimal marker that is written as R, K, M or a dot.
        int cursor = 0;
        int integerPart = 0;
        while (cursor < text.Length && char.IsDigit(text[cursor]))
        {
            integerPart = integerPart * 10 + (text[cursor] - '0');
            cursor++;
        }

        if (cursor == 0)
            return false;

        double magnitude = integerPart;
        if (cursor < text.Length)
        {
            char marker = text[cursor];
            if (marker is 'R' or 'K' or 'M')
            {
                cursor++;

                int fractionDigits = 0;
                double fraction = 0;
                while (cursor < text.Length && char.IsDigit(text[cursor]) && fractionDigits < 3)
                {
                    fraction = fraction * 10 + (text[cursor] - '0');
                    fractionDigits++;
                    cursor++;
                }

                for (int i = 0; i < fractionDigits; i++)
                    fraction /= 10;

                magnitude += fraction;

                magnitude *= marker switch
                {
                    'K' => 1_000d,
                    'M' => 1_000_000d,
                    _ => 1d
                };
            }
            else if (marker is '.' or ',')
            {
                cursor++;
                int fractionDigits = 0;
                double fraction = 0;
                while (cursor < text.Length && char.IsDigit(text[cursor]) && fractionDigits < 3)
                {
                    fraction = fraction * 10 + (text[cursor] - '0');
                    fractionDigits++;
                    cursor++;
                }

                for (int i = 0; i < fractionDigits; i++)
                    fraction /= 10;

                magnitude += fraction;

                if (cursor < text.Length && text[cursor] is 'K' or 'M')
                {
                    magnitude *= text[cursor] == 'K' ? 1_000d : 1_000_000d;
                    cursor++;
                }
            }
        }

        if (!double.IsFinite(magnitude) || magnitude <= 0d)
            return false;

        int exponent = 0;
        while (magnitude >= 100d && exponent < 9)
        {
            magnitude /= 10d;
            exponent++;
        }

        while (magnitude < 10d && exponent > -1)
        {
            magnitude *= 10d;
            exponent--;
        }

        int digits = (int)Math.Round(magnitude);
        if (digits >= 100)
        {
            digits = (int)Math.Round(digits / 10d);
            exponent++;
        }

        if (digits < 10)
            return false;

        int multiplier = Math.Clamp(exponent, 0, 9);

        bands[0] = digits / 10;
        bands[1] = digits % 10;
        bands[2] = multiplier;
        bands[3] = 10; // gold: 5 %
        return true;
    }

    private static SKColor LedColour(string? value)
    {
        string text = value?.ToUpperInvariant() ?? string.Empty;

        if (text.Contains("GRN", StringComparison.Ordinal) || text.Contains("GREEN", StringComparison.Ordinal))
            return new SKColor(0x35, 0xB2, 0x5A);
        if (text.Contains("BLU", StringComparison.Ordinal))
            return new SKColor(0x3A, 0x6F, 0xD8);
        if (text.Contains("YEL", StringComparison.Ordinal))
            return new SKColor(0xE8, 0xC4, 0x2B);
        if (text.Contains("ORG", StringComparison.Ordinal))
            return new SKColor(0xE0, 0x7A, 0x1E);
        if (text.Contains("WHT", StringComparison.Ordinal) || text.Contains("WHITE", StringComparison.Ordinal))
            return new SKColor(0xEC, 0xEA, 0xE2);

        return new SKColor(0xD9, 0x3A, 0x3A); // red is the default 5 mm LED
    }

    private static SKPaint DashedStroke(byte red, byte green, byte blue, float width)
    {
        SKPaint paint = Stroke(red, green, blue, width);
        paint.PathEffect = SKPathEffect.CreateDash([3.5f, 2.5f], 0f);
        return paint;
    }
}
