// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/TargetSchematicLoader.cs
// PURPOSE: Loads the reference KiCad-style netlist (target_555_blinker_netlist.json)
//          and computes the air-wire (ratsnest) vectors the CAD viewport draws
//          between holes that the target netlist says must be electrically common.
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>One target net: a set of breadboard holes that must be electrically common.</summary>
public sealed record TargetNet(string Name, IReadOnlyList<string> Holes);

/// <summary>Reference component placement from the schematic (used by Phase 4/5 coaching).</summary>
public sealed record TargetComponent(string Reference, ComponentKind Kind, string Pin1HoleId, string Pin2HoleId);

/// <summary>Parsed reference netlist.</summary>
public sealed record TargetSchematic(
    string Name,
    IReadOnlyList<TargetNet> Nets,
    IReadOnlyList<TargetComponent> Components);

/// <summary>A single dashed green air-wire from an anchor hole to an unrouted target hole.</summary>
public readonly record struct RatsnestVector(HoleCoord From, HoleCoord To, string NetName);

/// <summary>Loads, validates, and evaluates the reference netlist.</summary>
public static class TargetSchematicLoader
{
    public const string DefaultRelativePath = "assets/schematics/target_555_blinker_netlist.json";

    private const int MaxParentSearchDepth = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Parses a netlist JSON document. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static TargetSchematic Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Target netlist JSON is empty.", nameof(json));

        SchematicDto? dto = JsonSerializer.Deserialize<SchematicDto>(json, JsonOptions);
        if (dto is null)
            throw new JsonException("Target netlist JSON deserialized to null.");

        var nets = new List<TargetNet>(dto.Nets?.Count ?? 0);
        if (dto.Nets is not null)
        {
            foreach (NetDto net in dto.Nets)
            {
                if (string.IsNullOrWhiteSpace(net.Name) || net.Holes is null || net.Holes.Count == 0)
                    continue;

                var holes = new List<string>(net.Holes.Count);
                foreach (string? hole in net.Holes)
                {
                    if (!string.IsNullOrWhiteSpace(hole))
                        holes.Add(hole.Trim());
                }

                if (holes.Count > 0)
                    nets.Add(new TargetNet(net.Name.Trim(), holes));
            }
        }

        var components = new List<TargetComponent>(dto.Components?.Count ?? 0);
        if (dto.Components is not null)
        {
            foreach (ComponentDto component in dto.Components)
            {
                if (string.IsNullOrWhiteSpace(component.Ref)
                    || string.IsNullOrWhiteSpace(component.Pin1)
                    || string.IsNullOrWhiteSpace(component.Pin2))
                {
                    continue;
                }

                var kind = ComponentKind.Unknown;
                if (!string.IsNullOrWhiteSpace(component.Kind))
                    Enum.TryParse(component.Kind, ignoreCase: true, out kind);

                components.Add(new TargetComponent(
                    component.Ref.Trim(),
                    kind,
                    component.Pin1.Trim(),
                    component.Pin2.Trim()));
            }
        }

        string name = string.IsNullOrWhiteSpace(dto.Name) ? "unnamed_target" : dto.Name.Trim();
        return new TargetSchematic(name, nets, components);
    }

    public static TargetSchematic LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path);
        return Parse(json);
    }

    public static bool TryLoadFromFile(string path, out TargetSchematic? schematic, out string? error)
    {
        schematic = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Target netlist path is empty.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Target netlist not found: {path}";
            return false;
        }

        try
        {
            schematic = LoadFromFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Searches upward from the executable directory for the shipped 555 netlist.</summary>
    public static string? ResolveDefaultPath(string? startDirectory = null)
    {
        DirectoryInfo? directory = new(string.IsNullOrWhiteSpace(startDirectory) ? AppContext.BaseDirectory : startDirectory);

        for (int depth = 0; depth <= MaxParentSearchDepth && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, DefaultRelativePath);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Best-effort load of the shipped netlist; returns null (with a reason) when absent.</summary>
    public static TargetSchematic? TryLoadDefault(out string? error)
    {
        string? path = ResolveDefaultPath();
        if (path is null)
        {
            error = $"Could not locate {DefaultRelativePath} above {AppContext.BaseDirectory}.";
            return null;
        }

        return TryLoadFromFile(path, out TargetSchematic? schematic, out error) ? schematic : null;
    }

    /// <summary>
    /// For every target net, groups its holes by their currently resolved root net and
    /// emits one air-wire per unresolved group. The DRC engine must have been evaluated
    /// first so <paramref name="checker"/> holds the live Union-Find state.
    /// </summary>
    public static IReadOnlyList<RatsnestVector> ComputeRatsnest(
        TargetSchematic target,
        PinMatrixLookup lookup,
        ElectricalRulesChecker checker)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(checker);

        var vectors = new List<RatsnestVector>();

        foreach (TargetNet net in target.Nets)
        {
            HoleCoord anchor = default;
            int anchorRoot = -1;
            var otherRoots = new List<(int Root, HoleCoord Hole)>(2);

            foreach (string holeId in net.Holes)
            {
                if (!lookup.TryGetHole(holeId, out HoleCoord hole))
                    continue;

                int root = checker.GetRootNet(hole.StripId);
                if (root < 0)
                    continue;

                if (anchorRoot < 0)
                {
                    anchor = hole;
                    anchorRoot = root;
                    continue;
                }

                if (root == anchorRoot)
                    continue;

                bool alreadyTracked = false;
                for (int i = 0; i < otherRoots.Count; i++)
                {
                    if (otherRoots[i].Root == root)
                    {
                        alreadyTracked = true;
                        break;
                    }
                }

                if (!alreadyTracked)
                    otherRoots.Add((root, hole));
            }

            foreach ((int _, HoleCoord hole) in otherRoots)
                vectors.Add(new RatsnestVector(anchor, hole, net.Name));
        }

        return vectors;
    }

    private sealed class SchematicDto
    {
        public string? Name { get; set; }

        public List<NetDto>? Nets { get; set; }

        public List<ComponentDto>? Components { get; set; }
    }

    private sealed class NetDto
    {
        public string? Name { get; set; }

        public List<string?>? Holes { get; set; }
    }

    private sealed class ComponentDto
    {
        public string? Ref { get; set; }

        public string? Kind { get; set; }

        public string? Pin1 { get; set; }

        public string? Pin2 { get; set; }
    }
}
