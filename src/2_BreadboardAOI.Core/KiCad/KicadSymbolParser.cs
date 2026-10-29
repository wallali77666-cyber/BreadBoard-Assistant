// ============================================================================
// FILE: src/2_BreadboardAOI.Core/KiCad/KicadSymbolParser.cs
// PURPOSE: Minimal, tolerant S-expression reader for the KiCad 6/7/8/9/10
//          `.kicad_sym` format. It builds only the geometry and pin data the
//          schematic sheet needs; unknown nodes are ignored, never fatal.
//
// ALLOCATION: parsing happens when a symbol is selected, never inside the
//             60 FPS loop, so ordinary lists are fine here.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BreadboardAOI.Core.KiCad;

internal sealed class SExprNode
{
    public string? Atom;

    public List<SExprNode>? Children;

    public bool IsList => Children is not null;

    public string Head => Children is { Count: > 0 } && Children[0].Atom is { } head ? head : string.Empty;

    public string AtomAt(int index) =>
        Children is { } list && index < list.Count && list[index].Atom is { } atom ? atom : string.Empty;

    public SExprNode? ListAt(int index) =>
        Children is { } list && index < list.Count && list[index].Children is not null ? list[index] : null;

    public SExprNode? Find(string head)
    {
        if (Children is not { } list)
            return null;

        for (int i = 1; i < list.Count; i++)
        {
            List<SExprNode>? children = list[i].Children;
            if (children is { Count: > 0 } && string.Equals(children[0].Atom, head, StringComparison.Ordinal))
                return list[i];
        }

        return null;
    }

    public IEnumerable<SExprNode> FindAll(string head)
    {
        if (Children is not { } list)
            yield break;

        for (int i = 1; i < list.Count; i++)
        {
            List<SExprNode>? children = list[i].Children;
            if (children is { Count: > 0 } && string.Equals(children[0].Atom, head, StringComparison.Ordinal))
                yield return list[i];
        }
    }

    public static float NumberAt(SExprNode node, int index, float fallback = 0f) =>
        float.TryParse(node.AtomAt(index), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
}

/// <summary>Parses one `.kicad_sym` file into a <see cref="KicadSymbol"/>.</summary>
public static class KicadSymbolParser
{
    private const int ArcSegments = 16;

    /// <summary>
    /// Parses the first top-level symbol in <paramref name="text"/>. Returns
    /// <c>null</c> when the text is not a KiCad symbol library or the symbol has
    /// no geometry at all, so a broken file never crashes the sheet.
    /// </summary>
    public static KicadSymbol? Parse(string library, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        SExprNode root;
        try
        {
            root = ParseTree(text);
        }
        catch (FormatException)
        {
            return null;
        }

        SExprNode? symbolNode = null;
        foreach (SExprNode child in root.FindAll("symbol"))
        {
            symbolNode = child;
            break;
        }

        if (symbolNode is null)
            return null;

        string name = symbolNode.AtomAt(1);
        if (name.Length == 0)
            return null;

        string? extends = symbolNode.Find("extends")?.AtomAt(1);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SExprNode property in symbolNode.FindAll("property"))
        {
            string key = property.AtomAt(1);
            if (key.Length > 0)
                properties[key] = property.AtomAt(2);
        }

        var pins = new List<KicadPin>(16);
        var graphics = new List<KicadGraphic>(32);

        CollectUnits(symbolNode, name, pins, graphics);

        string reference = properties.TryGetValue("Reference", out string? refText) ? refText : "U";
        string value = properties.TryGetValue("Value", out string? valueText) ? valueText : name;
        string description = properties.TryGetValue("Description", out string? descriptionText) ? descriptionText : string.Empty;
        string footprint = properties.TryGetValue("Footprint", out string? footprintText) ? footprintText : string.Empty;

        return new KicadSymbol(
            library,
            name,
            string.IsNullOrWhiteSpace(extends) ? null : extends,
            reference,
            value,
            description,
            footprint,
            pins,
            graphics);
    }

    /// <summary>
    /// Merges the graphics and pins of the body unit symbols. Unit 0 is shared by
    /// every unit; unit 1 is drawn for multi-unit parts so a 74LS00 shows gate A
    /// instead of four coincident gates.
    /// </summary>
    private static void CollectUnits(
        SExprNode symbolNode,
        string symbolName,
        List<KicadPin> pins,
        List<KicadGraphic> graphics)
    {
        foreach (SExprNode unit in symbolNode.FindAll("symbol"))
        {
            string unitName = unit.AtomAt(1);
            if (unitName.Length == 0)
                continue;

            // Body-unit nodes are named "<Symbol>_<unit>_<style>". Unit 0 is shared
            // by every unit, unit 1 is the first gate, and style 0 is shared by all
            // De Morgan representations. A 555 keeps its power pins in "_0_0".
            if (!TryParseUnitSuffix(unitName, out int unitIndex, out int style))
                continue;

            if (unitIndex > 1 || style > 1)
                continue;

            foreach (SExprNode child in unit.Children!)
            {
                switch (child.Head)
                {
                    case "polyline":
                        AddPolyline(child, graphics);
                        break;
                    case "rectangle":
                        AddRectangle(child, graphics);
                        break;
                    case "circle":
                        AddCircle(child, graphics);
                        break;
                    case "arc":
                        AddArc(child, graphics);
                        break;
                    case "pin":
                        AddPin(child, pins);
                        break;
                }
            }
        }
    }

    /// <summary>Splits "NE555D_1_1" into unit 1 and style 1.</summary>
    private static bool TryParseUnitSuffix(string unitName, out int unit, out int style)
    {
        unit = -1;
        style = -1;

        int last = unitName.LastIndexOf('_');
        if (last <= 0 || last == unitName.Length - 1)
            return false;

        int previous = unitName.LastIndexOf('_', last - 1);
        if (previous <= 0)
            return false;

        return int.TryParse(unitName.AsSpan(previous + 1, last - previous - 1), out unit)
            && int.TryParse(unitName.AsSpan(last + 1), out style);
    }

    private static void AddPolyline(SExprNode node, List<KicadGraphic> graphics)
    {
        KicadPoint[] points = ReadPoints(node.Find("pts"));
        if (points.Length >= 2)
            graphics.Add(new KicadGraphic(KicadGraphicKind.Polyline, points, ReadStroke(node), ReadFill(node)));
    }

    private static void AddRectangle(SExprNode node, List<KicadGraphic> graphics)
    {
        SExprNode? start = node.Find("start");
        SExprNode? end = node.Find("end");
        if (start is null || end is null)
            return;

        var points = new[]
        {
            new KicadPoint(SExprNode.NumberAt(start, 1), SExprNode.NumberAt(start, 2)),
            new KicadPoint(SExprNode.NumberAt(end, 1), SExprNode.NumberAt(end, 2))
        };

        graphics.Add(new KicadGraphic(KicadGraphicKind.Rectangle, points, ReadStroke(node), ReadFill(node)));
    }

    private static void AddCircle(SExprNode node, List<KicadGraphic> graphics)
    {
        SExprNode? centre = node.Find("center");
        SExprNode? radiusNode = node.Find("radius");
        if (centre is null || radiusNode is null)
            return;

        float cx = SExprNode.NumberAt(centre, 1);
        float cy = SExprNode.NumberAt(centre, 2);
        float radius = SExprNode.NumberAt(radiusNode, 1);

        var points = new[]
        {
            new KicadPoint(cx, cy),
            new KicadPoint(cx + radius, cy)
        };

        graphics.Add(new KicadGraphic(KicadGraphicKind.Circle, points, ReadStroke(node), ReadFill(node)));
    }

    private static void AddArc(SExprNode node, List<KicadGraphic> graphics)
    {
        SExprNode? start = node.Find("start");
        SExprNode? mid = node.Find("mid");
        SExprNode? end = node.Find("end");

        if (start is null || mid is null || end is null)
        {
            // Older KiCad releases stored arcs as (start) (end) (radius); the
            // straight chord is a better fallback than a missing line.
            if (start is not null && end is not null)
            {
                var chord = new[]
                {
                    new KicadPoint(SExprNode.NumberAt(start, 1), SExprNode.NumberAt(start, 2)),
                    new KicadPoint(SExprNode.NumberAt(end, 1), SExprNode.NumberAt(end, 2))
                };

                graphics.Add(new KicadGraphic(KicadGraphicKind.Polyline, chord, ReadStroke(node), KicadFillStyle.None));
            }

            return;
        }

        KicadPoint a = new(SExprNode.NumberAt(start, 1), SExprNode.NumberAt(start, 2));
        KicadPoint b = new(SExprNode.NumberAt(mid, 1), SExprNode.NumberAt(mid, 2));
        KicadPoint c = new(SExprNode.NumberAt(end, 1), SExprNode.NumberAt(end, 2));

        graphics.Add(new KicadGraphic(
            KicadGraphicKind.Polyline,
            FlattenArc(a, b, c),
            ReadStroke(node),
            ReadFill(node)));
    }

    private static void AddPin(SExprNode node, List<KicadPin> pins)
    {
        SExprNode? at = node.Find("at");
        if (at is null)
            return;

        float x = SExprNode.NumberAt(at, 1);
        float y = SExprNode.NumberAt(at, 2);
        int orientation = (int)SExprNode.NumberAt(at, 3);

        float length = node.Find("length") is { } lengthNode ? SExprNode.NumberAt(lengthNode, 1) : 2.54f;
        string name = node.Find("name")?.AtomAt(1) ?? string.Empty;
        string number = node.Find("number")?.AtomAt(1) ?? string.Empty;

        bool hidden = node.Find("hide") is not null
            || node.Children!.Exists(child => string.Equals(child.Atom, "hide", StringComparison.Ordinal));

        pins.Add(new KicadPin(number, name, x, y, length, orientation, hidden));
    }

    private static KicadPoint[] ReadPoints(SExprNode? pts)
    {
        if (pts?.Children is not { } list)
            return Array.Empty<KicadPoint>();

        var points = new List<KicadPoint>(list.Count);
        foreach (SExprNode child in list)
        {
            if (child.Head is not "xy")
                continue;

            points.Add(new KicadPoint(SExprNode.NumberAt(child, 1), SExprNode.NumberAt(child, 2)));
        }

        return points.ToArray();
    }

    private static float ReadStroke(SExprNode node)
    {
        float width = node.Find("stroke") is { } stroke && stroke.Find("width") is { } widthNode
            ? SExprNode.NumberAt(widthNode, 1)
            : 0f;

        // KiCad treats 0 as "use the default line width" (6 mil).
        return width <= 0f ? 0.1524f : width;
    }

    private static KicadFillStyle ReadFill(SExprNode node)
    {
        string type = node.Find("fill")?.Find("type")?.AtomAt(1) ?? "none";

        return type switch
        {
            "outline" => KicadFillStyle.Outline,
            "background" => KicadFillStyle.Background,
            _ => KicadFillStyle.None
        };
    }

    /// <summary>Circular arc through start/mid/end, flattened for SkiaSharp.</summary>
    private static KicadPoint[] FlattenArc(KicadPoint a, KicadPoint b, KicadPoint c)
    {
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y;

        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-9)
            return new[] { a, b, c };

        double aSq = ax * ax + ay * ay;
        double bSq = bx * bx + by * by;
        double cSq = cx * cx + cy * cy;

        double ux = (aSq * (by - cy) + bSq * (cy - ay) + cSq * (ay - by)) / d;
        double uy = (aSq * (cx - bx) + bSq * (ax - cx) + cSq * (bx - ax)) / d;
        double radius = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));

        double startAngle = Math.Atan2(ay - uy, ax - ux);
        double midAngle = Math.Atan2(by - uy, bx - ux);
        double endAngle = Math.Atan2(cy - uy, cx - ux);

        static double Normalise(double angle)
        {
            while (angle < 0)
                angle += 2 * Math.PI;
            while (angle >= 2 * Math.PI)
                angle -= 2 * Math.PI;
            return angle;
        }

        double ccwEnd = Normalise(endAngle - startAngle);
        double ccwMid = Normalise(midAngle - startAngle);
        double sweep = ccwMid <= ccwEnd ? ccwEnd : ccwEnd - 2 * Math.PI;

        var points = new KicadPoint[ArcSegments + 1];
        for (int i = 0; i <= ArcSegments; i++)
        {
            double angle = startAngle + sweep * i / ArcSegments;
            points[i] = new KicadPoint(
                (float)(ux + radius * Math.Cos(angle)),
                (float)(uy + radius * Math.Sin(angle)));
        }

        return points;
    }

    // ---- S-expression reader ------------------------------------------------

    private static SExprNode ParseTree(string text)
    {
        int cursor = 0;

        // Skip a UTF-8 BOM if the caller read the file as bytes.
        if (text.Length > 0 && text[0] == '\uFEFF')
            cursor++;

        SkipWhitespace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '(')
            throw new FormatException("A .kicad_sym file must start with '('.");

        return ReadList(text, ref cursor);
    }

    private static SExprNode ReadList(string text, ref int cursor)
    {
        var node = new SExprNode { Children = new List<SExprNode>(8) };
        cursor++; // '('

        while (true)
        {
            SkipWhitespace(text, ref cursor);

            if (cursor >= text.Length)
                throw new FormatException("Unterminated S-expression.");

            char ch = text[cursor];

            if (ch == ')')
            {
                cursor++;
                return node;
            }

            if (ch == '(')
            {
                node.Children.Add(ReadList(text, ref cursor));
                continue;
            }

            node.Children.Add(new SExprNode { Atom = ReadAtom(text, ref cursor) });
        }
    }

    private static string ReadAtom(string text, ref int cursor)
    {
        if (text[cursor] == '"')
            return ReadQuoted(text, ref cursor);

        int start = cursor;
        while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] != '(' && text[cursor] != ')')
            cursor++;

        return text.Substring(start, cursor - start);
    }

    private static string ReadQuoted(string text, ref int cursor)
    {
        var builder = new StringBuilder(16);
        cursor++; // opening quote

        while (cursor < text.Length)
        {
            char ch = text[cursor++];

            if (ch == '\\' && cursor < text.Length)
            {
                char escaped = text[cursor++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => escaped
                });

                continue;
            }

            if (ch == '"')
                return builder.ToString();

            builder.Append(ch);
        }

        throw new FormatException("Unterminated string in S-expression.");
    }

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;
    }
}
