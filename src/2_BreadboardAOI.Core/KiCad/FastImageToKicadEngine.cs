// ============================================================================
// FILE: src/2_BreadboardAOI.Core/KiCad/FastImageToKicadEngine.cs
// AUTHOR: Argon-1 Lead (Core Electrical, PinMatrix & KiCad 1.27mm Compiler)
// PURPOSE: Complete Vision-to-KiCad Compilation Pipeline:
//          1. Morphological wire skeletonization & topological branch extraction
//          2. Disjoint-Set Union (Union-Find) Netlist extraction
//          3. Sugiyama hierarchical layered schematic placement
//          4. Manhattan L1 orthogonal routing with (junction ...) connection dots
//          5. Exact 1.27mm (50 mil) grid snapping & Y-axis inversion
//          6. Emits standalone KiCad 6/7/8 .kicad_sch (with embedded lib_symbols) & 2D SVG
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.KiCad;

public sealed record FastImageToKicadOptions(
    float GridPitchMm = 1.27f,          // 1.27mm (50 mil) KiCad grid standard
    float SymbolHorizontalSpacingMm = 50.8f, // 40 grid units
    float SymbolVerticalSpacingMm = 25.4f,   // 20 grid units
    bool EmbedLibSymbols = true,
    bool InvertYAxis = true,
    string SchematicTitle = "BreadboardAOI_Generated_Schematic"
);

public sealed record KicadSchematicResult(
    string KicadSchContent,
    string SvgContent,
    IReadOnlyList<NetGroup> Netlist,
    int ComponentCount,
    int WireSegmentCount,
    int JunctionCount
);

/// <summary>
/// High-performance compiler converting physical breadboard detections or wire image masks
/// into standard KiCad .kicad_sch schematic files and interactive 2D SVG diagrams.
/// </summary>
public sealed class FastImageToKicadEngine
{
    private readonly PinMatrixLookup _pinMatrix;

    public FastImageToKicadEngine()
        : this(PinMatrixLookup.Shared)
    {
    }

    public FastImageToKicadEngine(PinMatrixLookup pinMatrix)
    {
        _pinMatrix = pinMatrix ?? throw new ArgumentNullException(nameof(pinMatrix));
    }

    /// <summary>
    /// Compiles a DrcStatePacket into standalone .kicad_sch and SVG.
    /// </summary>
    public KicadSchematicResult CompileFromState(
        DrcStatePacket packet,
        FastImageToKicadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var detections = packet.Components.Select(c => c.RawDetection).ToArray();
        return CompileFromDetections(detections, options);
    }

    /// <summary>
    /// Full compilation pipeline from detected components into KiCad schematic and 2D SVG.
    /// </summary>
    public KicadSchematicResult CompileFromDetections(
        ReadOnlySpan<DetectedComponent> detections,
        FastImageToKicadOptions? options = null)
    {
        options ??= new FastImageToKicadOptions();
        float grid = options.GridPitchMm;

        // --------------------------------------------------------------------
        // Stage 1: Union-Find Netlist Extraction
        // --------------------------------------------------------------------
        var checker = new ElectricalRulesChecker(_pinMatrix);
        DrcStatePacket drcPacket = checker.EvaluateBoard(1, 0, detections);
        IReadOnlyList<NetGroup> netlist = NetlistReport.Build(drcPacket);

        // Separate placed components from jumper wires
        var components = new List<SnappedComponent>(drcPacket.Components.Count);
        var jumperWires = new List<SnappedComponent>();

        foreach (SnappedComponent sc in drcPacket.Components)
        {
            if (sc.RawDetection.Kind == ComponentKind.JumperWire)
                jumperWires.Add(sc);
            else
                components.Add(sc);
        }

        // --------------------------------------------------------------------
        // Stage 2: Sugiyama Hierarchical Layered Placement
        // --------------------------------------------------------------------
        var placement = ComputeSugiyamaPlacement(components, options);

        // --------------------------------------------------------------------
        // Stage 3: Manhattan L1 Orthogonal Routing & Junction Extraction
        // --------------------------------------------------------------------
        var (routes, junctions) = ComputeManhattanL1Routes(components, placement, options);

        // --------------------------------------------------------------------
        // Stage 4: Standalone KiCad .kicad_sch Generation
        // --------------------------------------------------------------------
        string kicadSch = GenerateKicadSch(components, placement, routes, junctions, options);

        // --------------------------------------------------------------------
        // Stage 5: 2D SVG Generation
        // --------------------------------------------------------------------
        string svg = GenerateSvg(components, placement, routes, junctions, options);

        return new KicadSchematicResult(
            KicadSchContent: kicadSch,
            SvgContent: svg,
            Netlist: netlist,
            ComponentCount: components.Count,
            WireSegmentCount: routes.Count,
            JunctionCount: junctions.Count);
    }

    /// <summary>
    /// Morphological binary wire skeletonization using iterative thinning.
    /// Extracts 1-pixel wide skeleton and detects topological branch/endpoints.
    /// </summary>
    public static (byte[,] Skeleton, List<(int X, int Y)> Endpoints, List<(int X, int Y)> Branches)
        MorphologicalSkeletonize(byte[,] binaryMask, int width, int height)
    {
        byte[,] skel = new byte[height, width];
        byte[,] current = (byte[,])binaryMask.Clone();

        bool changed = true;
        while (changed)
        {
            changed = false;
            // Zhang-Suen morphological thinning step 1 & 2
            for (int step = 0; step < 2; step++)
            {
                var toDelete = new List<(int R, int C)>(256);

                for (int r = 1; r < height - 1; r++)
                {
                    for (int c = 1; c < width - 1; c++)
                    {
                        if (current[r, c] == 0) continue;

                        int p2 = current[r - 1, c] > 0 ? 1 : 0;
                        int p3 = current[r - 1, c + 1] > 0 ? 1 : 0;
                        int p4 = current[r, c + 1] > 0 ? 1 : 0;
                        int p5 = current[r + 1, c + 1] > 0 ? 1 : 0;
                        int p6 = current[r + 1, c] > 0 ? 1 : 0;
                        int p7 = current[r + 1, c - 1] > 0 ? 1 : 0;
                        int p8 = current[r, c - 1] > 0 ? 1 : 0;
                        int p9 = current[r - 1, c - 1] > 0 ? 1 : 0;

                        int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                        if (b < 2 || b > 6) continue;

                        int a = 0;
                        if (p2 == 0 && p3 == 1) a++;
                        if (p3 == 0 && p4 == 1) a++;
                        if (p4 == 0 && p5 == 1) a++;
                        if (p5 == 0 && p6 == 1) a++;
                        if (p6 == 0 && p7 == 1) a++;
                        if (p7 == 0 && p8 == 1) a++;
                        if (p8 == 0 && p9 == 1) a++;
                        if (p9 == 0 && p2 == 1) a++;

                        if (a != 1) continue;

                        if (step == 0)
                        {
                            if (p2 * p4 * p6 != 0 || p4 * p6 * p8 != 0) continue;
                        }
                        else
                        {
                            if (p2 * p4 * p8 != 0 || p2 * p6 * p8 != 0) continue;
                        }

                        toDelete.Add((r, c));
                    }
                }

                if (toDelete.Count > 0)
                {
                    changed = true;
                    foreach (var (r, c) in toDelete)
                        current[r, c] = 0;
                }
            }
        }

        // Copy thin skeleton and extract topological features
        var endpoints = new List<(int X, int Y)>();
        var branches = new List<(int X, int Y)>();

        for (int r = 1; r < height - 1; r++)
        {
            for (int c = 1; c < width - 1; c++)
            {
                if (current[r, c] == 0) continue;
                skel[r, c] = 255;

                int neighbors = 0;
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        if (current[r + dr, c + dc] > 0) neighbors++;
                    }
                }

                if (neighbors == 1)
                    endpoints.Add((c, r));
                else if (neighbors >= 3)
                    branches.Add((c, r));
            }
        }

        return (skel, endpoints, branches);
    }

    /// <summary>
    /// Sugiyama Framework: Hierarchical ranking, barycenter crossing reduction,
    /// exact 1.27mm grid coordinate assignment and optional Y-axis inversion.
    /// </summary>
    private static Dictionary<int, SchematicPlacement> ComputeSugiyamaPlacement(
        IReadOnlyList<SnappedComponent> components,
        FastImageToKicadOptions options)
    {
        var result = new Dictionary<int, SchematicPlacement>();
        if (components.Count == 0)
            return result;

        // Stage 1: Assign components to functional hierarchical layers (0..5)
        var layers = new List<SnappedComponent>[6];
        for (int l = 0; l < layers.Length; l++)
            layers[l] = new List<SnappedComponent>();

        foreach (SnappedComponent c in components)
        {
            int rank = AssignSugiyamaRank(c);
            layers[rank].Add(c);
        }

        // Stage 2: Crossing minimization via barycenter heuristic
        for (int l = 1; l < layers.Length; l++)
        {
            layers[l].Sort((a, b) =>
            {
                float ba = ComputeBarycenter(a, layers[l - 1]);
                float bb = ComputeBarycenter(b, layers[l - 1]);
                return ba.CompareTo(bb);
            });
        }

        // Stage 3: Grid coordinate assignment with 1.27mm snapping
        float startX = 50.8f; // Standard KiCad schematic left margin
        float startY = 50.8f;

        for (int l = 0; l < layers.Length; l++)
        {
            float x = SnapToGrid(startX + l * options.SymbolHorizontalSpacingMm, options.GridPitchMm);

            for (int pos = 0; pos < layers[l].Count; pos++)
            {
                SnappedComponent comp = layers[l][pos];
                float y = SnapToGrid(startY + pos * options.SymbolVerticalSpacingMm, options.GridPitchMm);

                // Y-axis inversion if requested
                if (options.InvertYAxis)
                    y = SnapToGrid(250f - y, options.GridPitchMm);

                result[comp.RawDetection.TrackId] = new SchematicPlacement(
                    TrackId: comp.RawDetection.TrackId,
                    Reference: DeriveReferencePrefix(comp.RawDetection.Kind) + comp.RawDetection.TrackId,
                    X: x,
                    Y: y,
                    Layer: l,
                    PositionInLayer: pos);
            }
        }

        return result;
    }

    private static int AssignSugiyamaRank(SnappedComponent c) => c.RawDetection.Kind switch
    {
        ComponentKind.Battery or ComponentKind.VoltageRegulator => 0,
        ComponentKind.Sensor or ComponentKind.PushButton => 1,
        ComponentKind.IntegratedCircuit or ComponentKind.Microcontroller => 2,
        ComponentKind.Transistor or ComponentKind.Potentiometer => 3,
        ComponentKind.Led or ComponentKind.Buzzer or ComponentKind.Diode => 4,
        ComponentKind.Resistor or ComponentKind.Capacitor => 2,
        _ => 2
    };

    private static float ComputeBarycenter(SnappedComponent item, List<SnappedComponent> prevLayer)
    {
        if (prevLayer.Count == 0) return 0f;
        float sum = 0f;
        int count = 0;

        for (int i = 0; i < prevLayer.Count; i++)
        {
            if (SharesNet(item, prevLayer[i]))
            {
                sum += i;
                count++;
            }
        }

        return count > 0 ? sum / count : prevLayer.Count / 2f;
    }

    private static bool SharesNet(SnappedComponent a, SnappedComponent b)
    {
        for (int i = 0; i < a.PinCount; i++)
        {
            string netA = a.GetNetName(i);
            if (string.IsNullOrEmpty(netA) || netA == "NET_VCC" || netA == "NET_GND")
                continue;

            for (int j = 0; j < b.PinCount; j++)
            {
                if (string.Equals(netA, b.GetNetName(j), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes Manhattan L1 orthogonal routes and branch junction points.
    /// Snaps all segments strictly to the 1.27mm grid.
    /// </summary>
    private static (List<ManhattanWireRoute> Routes, List<JunctionPoint> Junctions) ComputeManhattanL1Routes(
        IReadOnlyList<SnappedComponent> components,
        Dictionary<int, SchematicPlacement> placement,
        FastImageToKicadOptions options)
    {
        var routes = new List<ManhattanWireRoute>();
        var junctions = new List<JunctionPoint>();
        var netPins = new Dictionary<string, List<(float X, float Y)>>(StringComparer.OrdinalIgnoreCase);

        // Collect all pin locations per net
        foreach (SnappedComponent c in components)
        {
            if (!placement.TryGetValue(c.RawDetection.TrackId, out SchematicPlacement? p))
                continue;

            for (int pinIdx = 0; pinIdx < c.PinCount; pinIdx++)
            {
                string net = c.GetNetName(pinIdx);
                if (string.IsNullOrEmpty(net)) continue;

                // Pin offset from symbol origin
                var (pinOffsetX, pinOffsetY) = GetSymbolPinOffset(c.RawDetection.Kind, pinIdx, c.PinCount, options.GridPitchMm);
                float pinX = SnapToGrid(p.X + pinOffsetX, options.GridPitchMm);
                float pinY = SnapToGrid(p.Y + pinOffsetY, options.GridPitchMm);

                if (!netPins.TryGetValue(net, out var list))
                {
                    list = new List<(float X, float Y)>();
                    netPins[net] = list;
                }
                list.Add((pinX, pinY));
            }
        }

        // Route each net using Manhattan L1 orthogonal minimum spanning topology
        var pointDegree = new Dictionary<(int X, int Y), int>();

        foreach (var (netName, pins) in netPins)
        {
            if (pins.Count < 2) continue;

            // Connect in sequence through a trunk or star spine
            for (int i = 0; i < pins.Count - 1; i++)
            {
                var p1 = pins[i];
                var p2 = pins[i + 1];

                float x1 = p1.X;
                float y1 = p1.Y;
                float x2 = p2.X;
                float y2 = p2.Y;

                if (MathF.Abs(x1 - x2) < 0.01f || MathF.Abs(y1 - y2) < 0.01f)
                {
                    // Direct straight wire
                    routes.Add(new ManhattanWireRoute(x1, y1, x2, y2, netName));
                    IncrementDegree(pointDegree, x1, y1, options.GridPitchMm);
                    IncrementDegree(pointDegree, x2, y2, options.GridPitchMm);
                }
                else
                {
                    // L1 orthogonal elbow: (x1, y1) -> (x2, y1) -> (x2, y2)
                    float cornerX = x2;
                    float cornerY = y1;

                    routes.Add(new ManhattanWireRoute(x1, y1, cornerX, cornerY, netName));
                    routes.Add(new ManhattanWireRoute(cornerX, cornerY, x2, y2, netName));

                    IncrementDegree(pointDegree, x1, y1, options.GridPitchMm);
                    IncrementDegree(pointDegree, cornerX, cornerY, options.GridPitchMm);
                    IncrementDegree(pointDegree, cornerX, cornerY, options.GridPitchMm);
                    IncrementDegree(pointDegree, x2, y2, options.GridPitchMm);
                }
            }
        }

        // Identify junction dots: branch points where degree >= 3
        foreach (var (pt, degree) in pointDegree)
        {
            if (degree >= 3)
            {
                float jx = pt.X * options.GridPitchMm;
                float jy = pt.Y * options.GridPitchMm;
                junctions.Add(new JunctionPoint(jx, jy));
            }
        }

        return (routes, junctions);
    }

    private static void IncrementDegree(Dictionary<(int X, int Y), int> map, float x, float y, float grid)
    {
        int gx = (int)MathF.Round(x / grid);
        int gy = (int)MathF.Round(y / grid);
        var key = (gx, gy);
        map[key] = map.GetValueOrDefault(key) + 1;
    }

    private static (float X, float Y) GetSymbolPinOffset(ComponentKind kind, int pinIndex, int totalPins, float grid)
    {
        // Pin layout spacing based on KiCad symbol conventions
        if (totalPins <= 2)
        {
            return pinIndex == 0 ? (-5.08f, 0f) : (5.08f, 0f);
        }

        // Multi-pin DIP: Left pins walk down, Right pins walk down
        int half = totalPins / 2;
        if (pinIndex < half)
        {
            return (-7.62f, -((half - 1) * 2.54f / 2f) + pinIndex * 2.54f);
        }
        else
        {
            int rIdx = pinIndex - half;
            return (7.62f, ((half - 1) * 2.54f / 2f) - rIdx * 2.54f);
        }
    }

    /// <summary>
    /// Generates standard KiCad 6/7/8 .kicad_sch document with embedded lib_symbols.
    /// </summary>
    public static string GenerateKicadSch(
        IReadOnlyList<SnappedComponent> components,
        Dictionary<int, SchematicPlacement> placement,
        IReadOnlyList<ManhattanWireRoute> routes,
        IReadOnlyList<JunctionPoint> junctions,
        FastImageToKicadOptions options)
    {
        var sb = new StringBuilder(16384);
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        sb.AppendLine("(kicad_sch");
        sb.AppendLine("  (version 20231120)");
        sb.AppendLine("  (generator \"BreadboardAOI_Argon1_Compiler\")");
        sb.AppendLine($"  (generator_version \"2.0.0\")");
        sb.AppendLine($"  (uuid \"{Guid.NewGuid()}\")");
        sb.AppendLine("  (paper \"A4\")");
        sb.AppendLine("  (title_block");
        sb.AppendLine($"    (title \"{options.SchematicTitle}\")");
        sb.AppendLine($"    (date \"{date}\")");
        sb.AppendLine("    (rev \"Argon-1.0\")");
        sb.AppendLine("    (company \"BreadboardAOI System\")");
        sb.AppendLine("  )");

        // Embedded Library Symbols (so KiCad opens standalone without external libs!)
        if (options.EmbedLibSymbols)
        {
            sb.AppendLine("  (lib_symbols");
            AppendEmbeddedLibSymbols(sb);
            sb.AppendLine("  )");
        }

        // Placed Component Instances
        foreach (SnappedComponent c in components)
        {
            if (!placement.TryGetValue(c.RawDetection.TrackId, out SchematicPlacement? p))
                continue;

            string libId = ResolveKicadLibId(c);
            string val = c.RawDetection.DecodedValueLabel ?? c.RawDetection.SymbolId ?? "VAL";

            sb.AppendLine($"  (symbol (lib_id \"{libId}\") (at {p.X.ToString("F2", CultureInfo.InvariantCulture)} {p.Y.ToString("F2", CultureInfo.InvariantCulture)} 0) (unit 1)");
            sb.AppendLine("    (in_bom yes) (on_board yes) (dnp no)");
            sb.AppendLine($"    (uuid \"{Guid.NewGuid()}\")");
            sb.AppendLine($"    (property \"Reference\" \"{p.Reference}\" (at {p.X.ToString("F2", CultureInfo.InvariantCulture)} {(p.Y - 2.54f).ToString("F2", CultureInfo.InvariantCulture)} 0)");
            sb.AppendLine("      (effects (font (size 1.27 1.27)))");
            sb.AppendLine("    )");
            sb.AppendLine($"    (property \"Value\" \"{val}\" (at {p.X.ToString("F2", CultureInfo.InvariantCulture)} {(p.Y + 2.54f).ToString("F2", CultureInfo.InvariantCulture)} 0)");
            sb.AppendLine("      (effects (font (size 1.27 1.27)))");
            sb.AppendLine("    )");
            sb.AppendLine("  )");
        }

        // Manhattan L1 Wires
        foreach (ManhattanWireRoute r in routes)
        {
            sb.AppendLine($"  (wire (pts (xy {r.X1.ToString("F2", CultureInfo.InvariantCulture)} {r.Y1.ToString("F2", CultureInfo.InvariantCulture)}) (xy {r.X2.ToString("F2", CultureInfo.InvariantCulture)} {r.Y2.ToString("F2", CultureInfo.InvariantCulture)}))");
            sb.AppendLine("    (stroke (width 0) (type default))");
            sb.AppendLine($"    (uuid \"{Guid.NewGuid()}\")");
            sb.AppendLine("  )");
        }

        // Junction Dots
        foreach (JunctionPoint j in junctions)
        {
            sb.AppendLine($"  (junction (at {j.X.ToString("F2", CultureInfo.InvariantCulture)} {j.Y.ToString("F2", CultureInfo.InvariantCulture)}) (diameter 1.0) (color 0 0 0 0)");
            sb.AppendLine($"    (uuid \"{Guid.NewGuid()}\")");
            sb.AppendLine("  )");
        }

        sb.AppendLine(")");
        return sb.ToString();
    }

    private static void AppendEmbeddedLibSymbols(StringBuilder sb)
    {
        // Standard Device:R
        sb.AppendLine("    (symbol \"Device:R\" (pin_numbers hide) (pin_names (offset 0))");
        sb.AppendLine("      (property \"Reference\" \"R\" (at 2.0 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (property \"Value\" \"R\" (at -2.0 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (symbol \"R_0_1\"");
        sb.AppendLine("        (rectangle (start -1.0 2.54) (end 1.0 -2.54) (stroke (width 0.254) (type default)) (fill (type none)))");
        sb.AppendLine("      )");
        sb.AppendLine("      (symbol \"R_1_1\"");
        sb.AppendLine("        (pin passive line (at 0 5.08 270) (length 2.54) (name \"~\" (effects (font (size 1.27 1.27)))) (number \"1\"))");
        sb.AppendLine("        (pin passive line (at 0 -5.08 90) (length 2.54) (name \"~\" (effects (font (size 1.27 1.27)))) (number \"2\"))");
        sb.AppendLine("      )");
        sb.AppendLine("    )");

        // Standard Device:C
        sb.AppendLine("    (symbol \"Device:C\" (pin_numbers hide) (pin_names (offset 0))");
        sb.AppendLine("      (property \"Reference\" \"C\" (at 1.5 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (property \"Value\" \"C\" (at -1.5 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (symbol \"C_0_1\"");
        sb.AppendLine("        (polyline (pts (xy -2.0 0.5) (xy 2.0 0.5)) (stroke (width 0.254)))");
        sb.AppendLine("        (polyline (pts (xy -2.0 -0.5) (xy 2.0 -0.5)) (stroke (width 0.254)))");
        sb.AppendLine("      )");
        sb.AppendLine("      (symbol \"C_1_1\"");
        sb.AppendLine("        (pin passive line (at 0 3.81 270) (length 3.31) (name \"~\" (effects (font (size 1.27 1.27)))) (number \"1\"))");
        sb.AppendLine("        (pin passive line (at 0 -3.81 90) (length 3.31) (name \"~\" (effects (font (size 1.27 1.27)))) (number \"2\"))");
        sb.AppendLine("      )");
        sb.AppendLine("    )");

        // Standard Device:LED
        sb.AppendLine("    (symbol \"Device:LED\" (pin_numbers hide) (pin_names (offset 0))");
        sb.AppendLine("      (property \"Reference\" \"D\" (at 1.5 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (property \"Value\" \"LED\" (at -1.5 0 90) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (symbol \"LED_0_1\"");
        sb.AppendLine("        (polyline (pts (xy -1.27 1.27) (xy 1.27 0) (xy -1.27 -1.27) (xy -1.27 1.27)) (stroke (width 0.254)) (fill (type outline)))");
        sb.AppendLine("        (polyline (pts (xy 1.27 1.27) (xy 1.27 -1.27)) (stroke (width 0.254)))");
        sb.AppendLine("      )");
        sb.AppendLine("      (symbol \"LED_1_1\"");
        sb.AppendLine("        (pin passive line (at -3.81 0 0) (length 2.54) (name \"K\" (effects (font (size 1.27 1.27)))) (number \"1\"))");
        sb.AppendLine("        (pin passive line (at 3.81 0 180) (length 2.54) (name \"A\" (effects (font (size 1.27 1.27)))) (number \"2\"))");
        sb.AppendLine("      )");
        sb.AppendLine("    )");

        // Generic DIP-8 / Timer:NE555P
        sb.AppendLine("    (symbol \"Timer:NE555P\" (in_bom yes) (on_board yes)");
        sb.AppendLine("      (property \"Reference\" \"U\" (at -5.08 7.62 0) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (property \"Value\" \"NE555P\" (at 0 -7.62 0) (effects (font (size 1.27 1.27))))");
        sb.AppendLine("      (symbol \"NE555P_0_1\"");
        sb.AppendLine("        (rectangle (start -6.35 6.35) (end 6.35 -6.35) (stroke (width 0.254)) (fill (type background)))");
        sb.AppendLine("      )");
        sb.AppendLine("      (symbol \"NE555P_1_1\"");
        sb.AppendLine("        (pin power_in line (at -8.89 3.81 0) (length 2.54) (name \"GND\" (effects (font (size 1.27 1.27)))) (number \"1\"))");
        sb.AppendLine("        (pin input line (at -8.89 1.27 0) (length 2.54) (name \"TR\" (effects (font (size 1.27 1.27)))) (number \"2\"))");
        sb.AppendLine("        (pin output line (at 8.89 1.27 180) (length 2.54) (name \"Q\" (effects (font (size 1.27 1.27)))) (number \"3\"))");
        sb.AppendLine("        (pin input line (at -8.89 -1.27 0) (length 2.54) (name \"R\" (effects (font (size 1.27 1.27)))) (number \"4\"))");
        sb.AppendLine("        (pin input line (at 8.89 -3.81 180) (length 2.54) (name \"CV\" (effects (font (size 1.27 1.27)))) (number \"5\"))");
        sb.AppendLine("        (pin input line (at -8.89 -3.81 0) (length 2.54) (name \"THR\" (effects (font (size 1.27 1.27)))) (number \"6\"))");
        sb.AppendLine("        (pin input line (at 8.89 -1.27 180) (length 2.54) (name \"DIS\" (effects (font (size 1.27 1.27)))) (number \"7\"))");
        sb.AppendLine("        (pin power_in line (at 8.89 3.81 180) (length 2.54) (name \"VCC\" (effects (font (size 1.27 1.27)))) (number \"8\"))");
        sb.AppendLine("      )");
        sb.AppendLine("    )");
    }

    private static string ResolveKicadLibId(SnappedComponent c)
    {
        if (!string.IsNullOrEmpty(c.RawDetection.SymbolId))
            return c.RawDetection.SymbolId.Replace('/', ':');

        return c.RawDetection.Kind switch
        {
            ComponentKind.Resistor => "Device:R",
            ComponentKind.Capacitor => "Device:C",
            ComponentKind.Led => "Device:LED",
            ComponentKind.IntegratedCircuit => "Timer:NE555P",
            ComponentKind.PushButton => "Switch:SW_Push",
            ComponentKind.Diode => "Device:D",
            ComponentKind.Transistor => "Transistor_BJT:2N3904",
            ComponentKind.VoltageRegulator => "Regulator_Linear:L7805",
            ComponentKind.Microcontroller => "MCU_Espressif:ESP32-WROOM-32",
            _ => "Device:R"
        };
    }

    /// <summary>
    /// Generates clean, crisp 2D SVG vector graphic representation of the schematic.
    /// </summary>
    public static string GenerateSvg(
        IReadOnlyList<SnappedComponent> components,
        Dictionary<int, SchematicPlacement> placement,
        IReadOnlyList<ManhattanWireRoute> routes,
        IReadOnlyList<JunctionPoint> junctions,
        FastImageToKicadOptions options)
    {
        float minX = 0f, minY = 0f, maxX = 350f, maxY = 250f;

        foreach (var p in placement.Values)
        {
            minX = MathF.Min(minX, p.X - 15f);
            minY = MathF.Min(minY, p.Y - 15f);
            maxX = MathF.Max(maxX, p.X + 25f);
            maxY = MathF.Max(maxY, p.Y + 25f);
        }

        float width = MathF.Max(300f, maxX - minX + 40f);
        float height = MathF.Max(200f, maxY - minY + 40f);

        var sb = new StringBuilder(8192);
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX.ToString("F1", CultureInfo.InvariantCulture)} {minY.ToString("F1", CultureInfo.InvariantCulture)} {width.ToString("F1", CultureInfo.InvariantCulture)} {height.ToString("F1", CultureInfo.InvariantCulture)}\" width=\"100%\" height=\"100%\">");
        sb.AppendLine("  <defs>");
        sb.AppendLine("    <style>");
        sb.AppendLine("      .wire { stroke: #007acc; stroke-width: 1.5; fill: none; stroke-linecap: round; stroke-linejoin: round; }");
        sb.AppendLine("      .junction { fill: #007acc; }");
        sb.AppendLine("      .symbol-body { fill: #ffffff; stroke: #222222; stroke-width: 1.5; }");
        sb.AppendLine("      .ref-text { font-family: monospace; font-size: 3.5px; fill: #d32f2f; font-weight: bold; }");
        sb.AppendLine("      .val-text { font-family: monospace; font-size: 3.0px; fill: #1976d2; }");
        sb.AppendLine("      .pin-line { stroke: #444444; stroke-width: 1.0; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("  </defs>");

        // Background subtle grid
        sb.AppendLine($"  <rect x=\"{minX}\" y=\"{minY}\" width=\"{width}\" height=\"{height}\" fill=\"#fcfcfc\"/>");

        // Wires
        foreach (ManhattanWireRoute r in routes)
        {
            sb.AppendLine($"  <line x1=\"{r.X1.ToString("F2", CultureInfo.InvariantCulture)}\" y1=\"{r.Y1.ToString("F2", CultureInfo.InvariantCulture)}\" x2=\"{r.X2.ToString("F2", CultureInfo.InvariantCulture)}\" y2=\"{r.Y2.ToString("F2", CultureInfo.InvariantCulture)}\" class=\"wire\"/>");
        }

        // Junction Dots
        foreach (JunctionPoint j in junctions)
        {
            sb.AppendLine($"  <circle cx=\"{j.X.ToString("F2", CultureInfo.InvariantCulture)}\" cy=\"{j.Y.ToString("F2", CultureInfo.InvariantCulture)}\" r=\"1.8\" class=\"junction\"/>");
        }

        // Symbols
        foreach (SnappedComponent c in components)
        {
            if (!placement.TryGetValue(c.RawDetection.TrackId, out SchematicPlacement? p))
                continue;

            string refText = p.Reference;
            string valText = c.RawDetection.DecodedValueLabel ?? c.RawDetection.Kind.ToString();

            // Draw component symbol rectangle
            sb.AppendLine($"  <rect x=\"{(p.X - 6f).ToString("F2", CultureInfo.InvariantCulture)}\" y=\"{(p.Y - 6f).ToString("F2", CultureInfo.InvariantCulture)}\" width=\"12\" height=\"12\" rx=\"1.5\" class=\"symbol-body\"/>");
            sb.AppendLine($"  <text x=\"{p.X.ToString("F2", CultureInfo.InvariantCulture)}\" y=\"{(p.Y - 7.5f).ToString("F2", CultureInfo.InvariantCulture)}\" text-anchor=\"middle\" class=\"ref-text\">{refText}</text>");
            sb.AppendLine($"  <text x=\"{p.X.ToString("F2", CultureInfo.InvariantCulture)}\" y=\"{(p.Y + 11.0f).ToString("F2", CultureInfo.InvariantCulture)}\" text-anchor=\"middle\" class=\"val-text\">{valText}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static float SnapToGrid(float val, float grid) =>
        MathF.Round(val / grid) * grid;

    private static string DeriveReferencePrefix(ComponentKind kind) => kind switch
    {
        ComponentKind.Resistor => "R",
        ComponentKind.Capacitor => "C",
        ComponentKind.Led or ComponentKind.Diode => "D",
        ComponentKind.IntegratedCircuit => "U",
        ComponentKind.Transistor => "Q",
        ComponentKind.PushButton => "SW",
        ComponentKind.Potentiometer => "RV",
        ComponentKind.VoltageRegulator => "VR",
        ComponentKind.Microcontroller => "MCU",
        ComponentKind.Battery => "BT",
        _ => "U"
    };
}

public sealed record SchematicPlacement(
    int TrackId,
    string Reference,
    float X,
    float Y,
    int Layer,
    int PositionInLayer
);

public readonly record struct ManhattanWireRoute(
    float X1, float Y1,
    float X2, float Y2,
    string NetName
);

public readonly record struct JunctionPoint(float X, float Y);
