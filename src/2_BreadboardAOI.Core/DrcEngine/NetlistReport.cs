// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/NetlistReport.cs
// PURPOSE: Collapses one DrcStatePacket into the resolved netlist the builder
//          dock and the tutor inspect: one row per net, with every tie point
//          that currently belongs to it. Built on demand (state change), never
//          inside the 60 FPS path.
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>One resolved net and the holes whose leads sit on it.</summary>
public sealed record NetGroup(string Name, IReadOnlyList<string> Holes)
{
    public int Count => Holes.Count;
}

/// <summary>Read-only view of the connectivity a <see cref="DrcStatePacket"/> resolved.</summary>
public static class NetlistReport
{
    /// <summary>
    /// Groups component terminals by net name. Power nets come first, then the
    /// remaining nets in ordinal order so the panel does not jump between frames.
    /// </summary>
    public static IReadOnlyList<NetGroup> Build(DrcStatePacket state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        for (int i = 0; i < state.Components.Count; i++)
        {
            SnappedComponent component = state.Components[i];
            Add(map, component.Net1Name, component.Pin1Hole.HoleId);
            Add(map, component.Net2Name, component.Pin2Hole.HoleId);
        }

        var names = new List<string>(map.Count);
        foreach (string name in map.Keys)
        {
            if (name.Length > 0)
                names.Add(name);
        }

        names.Sort(CompareNets);

        var groups = new List<NetGroup>(names.Count);
        foreach (string name in names)
        {
            SortedSet<string> holes = map[name];
            var list = new List<string>(holes.Count);
            foreach (string hole in holes)
                list.Add(hole);

            groups.Add(new NetGroup(name, list));
        }

        return groups;
    }

    /// <summary>Compact multi-line panel text: <c>NET_VCC   4  A12, B12, ...</c></summary>
    public static string Format(DrcStatePacket state, int maxHolesPerNet = 8)
    {
        IReadOnlyList<NetGroup> groups = Build(state);
        if (groups.Count == 0)
            return "NO NETS RESOLVED";

        if (maxHolesPerNet < 1)
            maxHolesPerNet = 1;

        var builder = new System.Text.StringBuilder(256);
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');

            NetGroup group = groups[i];
            builder.Append(group.Name.PadRight(12));
            builder.Append(' ');
            builder.Append(group.Count);

            int shown = Math.Min(group.Count, maxHolesPerNet);
            for (int h = 0; h < shown; h++)
            {
                builder.Append(h == 0 ? "  " : ", ");
                builder.Append(group.Holes[h]);
            }

            if (group.Count > shown)
                builder.Append(" \u2026");
        }

        return builder.ToString();
    }

    private static void Add(Dictionary<string, SortedSet<string>> map, string netName, string holeId)
    {
        if (string.IsNullOrEmpty(netName) || string.IsNullOrEmpty(holeId))
            return;

        if (!map.TryGetValue(netName, out SortedSet<string>? holes))
        {
            holes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            map[netName] = holes;
        }

        holes.Add(holeId);
    }

    private static int CompareNets(string left, string right)
    {
        int rankLeft = Rank(left);
        int rankRight = Rank(right);
        return rankLeft != rankRight
            ? rankLeft.CompareTo(rankRight)
            : string.CompareOrdinal(left, right);
    }

    private static int Rank(string name) => name switch
    {
        "NET_VCC" => 0,
        "NET_GND" => 1,
        _ => 2
    };
}
