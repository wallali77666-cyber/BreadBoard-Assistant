// ============================================================================
// FILE: src/2_BreadboardAOI.Core/KiCad/KicadSymbol.cs
// PURPOSE: Immutable, renderer-friendly model of one symbol from the official
//          KiCad symbol libraries. Units are KiCad millimetres with +Y up; the
//          schematic renderer flips Y when it paints.
//
// SCOPE: only what a schematic sheet needs — body graphics, pins, reference and
//        value. Simulation, footprint filters and property tables are ignored.
// ============================================================================

using System;
using System.Collections.Generic;

namespace BreadboardAOI.Core.KiCad;

/// <summary>A point in KiCad symbol space (millimetres, +Y is up).</summary>
public readonly record struct KicadPoint(float X, float Y);

public enum KicadGraphicKind
{
    Polyline,
    Rectangle,
    Circle
}

public enum KicadFillStyle
{
    None,
    Outline,
    Background
}

/// <summary>One body drawing primitive. Arcs are already flattened to polylines.</summary>
public sealed class KicadGraphic
{
    public KicadGraphic(KicadGraphicKind kind, KicadPoint[] points, float strokeWidth, KicadFillStyle fill)
    {
        Kind = kind;
        Points = points;
        StrokeWidth = strokeWidth;
        Fill = fill;
    }

    public KicadGraphicKind Kind { get; }

    /// <summary>Polyline vertices, rectangle corners, or circle centre + rim point.</summary>
    public KicadPoint[] Points { get; }

    public float StrokeWidth { get; }

    public KicadFillStyle Fill { get; }
}

/// <summary>
/// One electrical pin. <see cref="X"/>/<see cref="Y"/> is the connection point;
/// the drawn pin line runs to <see cref="EndX"/>/<see cref="EndY"/> at the body.
/// </summary>
public readonly record struct KicadPin(
    string Number,
    string Name,
    float X,
    float Y,
    float Length,
    int Orientation,
    bool Hidden)
{
    public float EndX => X + Length * DirectionX;

    public float EndY => Y + Length * DirectionY;

    /// <summary>KiCad angle: 0 = right, 90 = up, 180 = left, 270 = down.</summary>
    public float DirectionX => Orientation switch
    {
        0 => 1f,
        180 => -1f,
        _ => 0f
    };

    public float DirectionY => Orientation switch
    {
        90 => 1f,
        270 => -1f,
        _ => 0f
    };
}

/// <summary>A resolved KiCad symbol with inherited graphics already merged.</summary>
public sealed class KicadSymbol
{
    public KicadSymbol(
        string library,
        string name,
        string? baseName,
        string reference,
        string value,
        string description,
        string footprint,
        IReadOnlyList<KicadPin> pins,
        IReadOnlyList<KicadGraphic> graphics)
    {
        Library = library;
        Name = name;
        BaseName = baseName;
        Reference = reference;
        Value = value;
        Description = description;
        Footprint = footprint;
        Pins = pins;
        Graphics = graphics;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (KicadGraphic graphic in graphics)
        {
            foreach (KicadPoint point in graphic.Points)
            {
                minX = MathF.Min(minX, point.X);
                minY = MathF.Min(minY, point.Y);
                maxX = MathF.Max(maxX, point.X);
                maxY = MathF.Max(maxY, point.Y);

                if (graphic.Kind == KicadGraphicKind.Circle && graphic.Points.Length == 2)
                {
                    float radius = MathF.Sqrt(
                        MathF.Pow(graphic.Points[1].X - graphic.Points[0].X, 2f) +
                        MathF.Pow(graphic.Points[1].Y - graphic.Points[0].Y, 2f));

                    minX = MathF.Min(minX, graphic.Points[0].X - radius);
                    minY = MathF.Min(minY, graphic.Points[0].Y - radius);
                    maxX = MathF.Max(maxX, graphic.Points[0].X + radius);
                    maxY = MathF.Max(maxY, graphic.Points[0].Y + radius);
                }
            }
        }

        foreach (KicadPin pin in pins)
        {
            minX = MathF.Min(minX, MathF.Min(pin.X, pin.EndX));
            minY = MathF.Min(minY, MathF.Min(pin.Y, pin.EndY));
            maxX = MathF.Max(maxX, MathF.Max(pin.X, pin.EndX));
            maxY = MathF.Max(maxY, MathF.Max(pin.Y, pin.EndY));
        }

        MinX = minX == float.MaxValue ? -5f : minX;
        MinY = minY == float.MaxValue ? -5f : minY;
        MaxX = maxX == float.MinValue ? 5f : maxX;
        MaxY = maxY == float.MinValue ? 5f : maxY;
    }

    public string Library { get; }

    public string Name { get; }

    /// <summary>Base symbol this one extends, or <c>null</c> when it stands alone.</summary>
    public string? BaseName { get; }

    /// <summary>KiCad "Library/Symbol", the id stored on a draft part.</summary>
    public string FullId => Library + "/" + Name;

    public string Reference { get; }

    public string Value { get; }

    public string Description { get; }

    public string Footprint { get; }

    public IReadOnlyList<KicadPin> Pins { get; }

    public IReadOnlyList<KicadGraphic> Graphics { get; }

    public float MinX { get; }

    public float MinY { get; }

    public float MaxX { get; }

    public float MaxY { get; }

    public float Width => MaxX - MinX;

    public float Height => MaxY - MinY;

    /// <summary>Reference prefix without the number, e.g. "R" for R1.</summary>
    public string ReferencePrefix => string.IsNullOrWhiteSpace(Reference) ? "U" : Reference.Trim();
}

/// <summary>A search hit: a library entry name without parsing the symbol.</summary>
public readonly record struct KicadSymbolRef(string Library, string Name)
{
    public string FullId => Library + "/" + Name;
}
