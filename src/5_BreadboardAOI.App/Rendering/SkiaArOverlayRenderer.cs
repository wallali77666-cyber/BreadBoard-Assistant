// ============================================================================
// FILE: src/5_BreadboardAOI.App/Rendering/SkiaArOverlayRenderer.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Renders the Adobe/CAD-style technical viewport overlay at 60 FPS.
//          Draws X-Ray conductive strip capsules, pin crosshairs, & KiCad air-wires.
// ============================================================================

using SkiaSharp;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.App.Rendering;

public sealed class SkiaArOverlayRenderer
{
    // Pre-allocated Skia paints (Zero GC allocation during 60 FPS OnPaintSurface)
    private readonly SKPaint _pasteboardPaint = new() { Color = new SKColor(0x14, 0x14, 0x14), Style = SKPaintStyle.Fill };
    private readonly SKPaint _xrayCriticalFill = new() { Color = new SKColor(0xD7, 0x37, 0x3F, 95), Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _xrayCriticalStroke = new() { Color = new SKColor(0xD7, 0x37, 0x3F, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
    private readonly SKPaint _ratsnestArrowPaint = new()
    {
        Color = new SKColor(0x2D, 0x9D, 0x78), // Adobe Spectrum Technical Emerald
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2.0f,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0f)
    };
    private readonly SKPaint _hudTextPaint = new()
    {
        Color = new SKColor(0xCC, 0xCC, 0xCC),
        Typeface = SKTypeface.FromFamilyName("Consolas"),
        TextSize = 11f,
        IsAntialias = true
    };

    public void RenderCadViewport(SKCanvas canvas, int viewportWidth, int viewportHeight, SKBitmap? warpedBoardFrame, DrcStatePacket? drcState)
    {
        // 1. Clear with Adobe InDesign #141414 Matte Pasteboard surround
        canvas.DrawRect(0, 0, viewportWidth, viewportHeight, _pasteboardPaint);

        // 2. Draw warped 1000x500 breadboard frame centered in viewport
        if (warpedBoardFrame is not null)
        {
            canvas.DrawBitmap(warpedBoardFrame, 0, 0);
        }

        if (drcState is null) return;

        // 3. Render DRC Violations (X-Ray Strip Capsule + Crosshair + KiCad Ratsnest Vector)
        foreach (var v in drcState.Violations)
        {
            // A. Draw glowing X-Ray capsule over the 5-hole conductive strip (height ~ 64px in 1000x500 space)
            var stripRect = new SKRect(v.BadHole.Px - 7f, v.BadHole.Py - 32f, v.BadHole.Px + 7f, v.BadHole.Py + 32f);
            canvas.DrawRoundRect(stripRect, 4f, 4f, _xrayCriticalFill);
            canvas.DrawRoundRect(stripRect, 4f, 4f, _xrayCriticalStroke);

            // B. Draw precision CAD crosshair on the offending hole
            DrawCadCrosshair(canvas, v.BadHole.Px, v.BadHole.Py, _xrayCriticalStroke);

            // C. Draw KiCad Ratsnest Air-Wire to the suggested target hole
            if (!string.IsNullOrEmpty(v.SuggestedTargetHole.HoleId))
            {
                canvas.DrawLine(
                    v.BadHole.Px, v.BadHole.Py,
                    v.SuggestedTargetHole.Px, v.SuggestedTargetHole.Py,
                    _ratsnestArrowPaint);

                canvas.DrawCircle(v.SuggestedTargetHole.Px, v.SuggestedTargetHole.Py, 5.5f, _ratsnestArrowPaint);
            }

            // D. Draw crisp technical callout badge
            canvas.DrawText($"[{v.Code}] {v.BadHole.HoleId}", v.BadHole.Px + 10f, v.BadHole.Py - 6f, _hudTextPaint);
        }

        // [JUNIOR TASK - ISSUE #3A]: Add ruler tick marks along top (0..1000px / Cols 1..63) 
        // and left edge (Rows A..J), plus hover crosshair snapping to the nearest HoleCoord.
    }

    private static void DrawCadCrosshair(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        canvas.DrawCircle(x, y, 6f, paint);
        canvas.DrawLine(x - 10f, y, x + 10f, y, paint);
        canvas.DrawLine(x, y - 10f, x, y + 10f, paint);
    }
}