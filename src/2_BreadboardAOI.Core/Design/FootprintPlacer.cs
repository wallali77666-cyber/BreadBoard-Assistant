// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Design/FootprintPlacer.cs
// PURPOSE: Maps a multi-lead part onto real tie points. A DIP straddles the
//          centre trench: pin 1 lands in the inner row of the half the operator
//          clicked (E for A..E, F for F..J), pins 2..N/2 walk to the right and
//          the remaining pins walk back along the opposite inner row. A TO-92
//          or regulator is a single row of adjacent columns.
//
// PHYSICS: two pins must never share a strip, so the placer validates every
//          generated hole and refuses to build a footprint that would short
//          itself. It never invents a hole outside columns 1..63.
// ============================================================================

using System;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.Design;

public static class FootprintPlacer
{
    /// <summary>Largest package the 63-column board will hold across the trench.</summary>
    public const int MaxDualRowPins = 40;

    public const int MaxSingleRowPins = 20;

    /// <summary>
    /// Builds a DIP/adapter footprint in pin-number order. The returned array is
    /// the hole for pin 1, pin 2, ... pin N.
    /// </summary>
    public static bool TryPlaceDualRow(
        PinMatrixLookup matrix,
        string? startHoleId,
        int pinCount,
        out string[] pins,
        out string? error)
    {
        pins = Array.Empty<string>();

        if (pinCount < 4 || pinCount > MaxDualRowPins)
        {
            error = $"A DIP footprint needs 4..{MaxDualRowPins} pins; {pinCount} is not supported.";
            return false;
        }

        if (!TryParseTerminalHole(startHoleId, out char row, out int column, out error))
            return false;

        int upperCount = (pinCount + 1) / 2;
        int lowerCount = pinCount - upperCount;

        if (column + upperCount - 1 > BreadboardSpec.ColumnCount)
        {
            error = $"A {pinCount}-pin package needs {upperCount} columns from {column}, but the board ends at " +
                    $"{BreadboardSpec.ColumnCount}. Click further left.";
            return false;
        }

        bool lowerHalf = row is >= 'F' and <= 'J';
        char innerRow = lowerHalf ? 'F' : 'E';
        char outerRow = lowerHalf ? 'E' : 'F';

        var result = new string[pinCount];

        for (int i = 0; i < upperCount; i++)
            result[i] = BreadboardSpec.CreateHole(innerRow, column + i).HoleId;

        for (int i = 0; i < lowerCount; i++)
        {
            int pinIndex = upperCount + i;
            result[pinIndex] = BreadboardSpec.CreateHole(outerRow, column + upperCount - 1 - i).HoleId;
        }

        for (int i = 0; i < result.Length; i++)
        {
            if (!matrix.TryGetHole(result[i], out _))
            {
                error = $"The package would need {result[i]}, which is not a tie point.";
                return false;
            }
        }

        if (HasDuplicate(result))
        {
            error = "That footprint would put two pins in one hole.";
            return false;
        }

        pins = result;
        error = null;
        return true;
    }

    /// <summary>Builds a straight row of adjacent columns (TO-92, 7805, 3-pin module).</summary>
    public static bool TryPlaceSingleRow(
        PinMatrixLookup matrix,
        string? startHoleId,
        int pinCount,
        out string[] pins,
        out string? error)
    {
        pins = Array.Empty<string>();

        if (pinCount < 2 || pinCount > MaxSingleRowPins)
        {
            error = $"A single-row part needs 2..{MaxSingleRowPins} pins; {pinCount} is not supported.";
            return false;
        }

        if (!TryParseTerminalHole(startHoleId, out char row, out int column, out error))
            return false;

        if (column + pinCount - 1 > BreadboardSpec.ColumnCount)
        {
            error = $"A {pinCount}-pin row from column {column} would run past column {BreadboardSpec.ColumnCount}.";
            return false;
        }

        var result = new string[pinCount];
        for (int i = 0; i < pinCount; i++)
            result[i] = BreadboardSpec.CreateHole(row, column + i).HoleId;

        for (int i = 0; i < result.Length; i++)
        {
            if (!matrix.TryGetHole(result[i], out _))
            {
                error = $"The part would need {result[i]}, which is not a tie point.";
                return false;
            }
        }

        pins = result;
        error = null;
        return true;
    }

    /// <summary>True when the hole belongs to a terminal strip rather than a power rail.</summary>
    public static bool IsTerminalHole(string? holeId) =>
        BreadboardSpec.TryParseHoleId(holeId, out char row, out int column)
        && row is >= 'A' and <= 'J'
        && BreadboardSpec.IsValidColumn(column);

    private static bool TryParseTerminalHole(string? holeId, out char row, out int column, out string? error)
    {
        row = default;
        column = 0;

        if (string.IsNullOrWhiteSpace(holeId)
            || !BreadboardSpec.TryParseHoleId(holeId, out row, out column)
            || row is < 'A' or > 'J'
            || !BreadboardSpec.IsValidColumn(column))
        {
            error = "Pin 1 must be a terminal tie point such as E10; power rails cannot host a multi-pin part.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasDuplicate(string[] holes)
    {
        for (int i = 0; i < holes.Length; i++)
        {
            for (int j = i + 1; j < holes.Length; j++)
            {
                if (string.Equals(holes[i], holes[j], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
