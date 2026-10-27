// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Geometry/BreadboardSpec.cs
// PURPOSE: Physical constants of the 830-tie-point solderless breadboard mapped
//          into the fixed 1000 x 500 px homography-warped coordinate space.
// ============================================================================

using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.Geometry;

/// <summary>
/// Deterministic layout of the canonical 830-point breadboard:
/// 63 terminal columns x 5 rows on each side of the IC trench (630 holes) plus
/// 4 power rails x 50 holes (200 holes). Coordinates are the authority used by
/// both <see cref="PinMatrixLookup"/> and the SkiaSharp overlay.
/// </summary>
public static class BreadboardSpec
{
    public const int CanvasWidth = 1000;
    public const int CanvasHeight = 500;

    public const int ColumnCount = 63;
    public const int UpperRowCount = 5; // A..E
    public const int LowerRowCount = 5; // F..J
    public const int RailHoleCount = 50;

    public const int TerminalHoleCount = ColumnCount * (UpperRowCount + LowerRowCount); // 630
    public const int RailHoleTotal = RailHoleCount * 4;                                 // 200

    /// <summary>630 terminal holes + 200 power-rail holes.</summary>
    public const int TotalHoleCount = TerminalHoleCount + RailHoleTotal;                // 830

    public const float FirstColumnX = 40f;
    public const float LastColumnX = 960f;

    /// <summary>Horizontal pitch between terminal columns: (960 - 40) / 62 = 14.8387 px.</summary>
    public const float ColumnPitch = (LastColumnX - FirstColumnX) / (ColumnCount - 1);

    public const float RowPitch = 20f;
    public const float UpperFirstRowY = 95f;  // Row A
    public const float LowerFirstRowY = 225f; // Row F

    public const float RailFirstX = 40f;
    public const float RailLastX = 960f;
    public const float RailPitch = (RailLastX - RailFirstX) / (RailHoleCount - 1);

    public const float TopVccRailY = 25f;
    public const float TopGndRailY = 45f;
    public const float BottomVccRailY = 455f;
    public const float BottomGndRailY = 475f;

    // Reserved power-rail strip IDs. Rail 1/3 are positive, 2/4 are ground.
    public const int StripTopVcc = 1;
    public const int StripTopGnd = 2;
    public const int StripBotVcc = 3;
    public const int StripBotGnd = 4;

    // Upper half (A..E) strips 101..163, lower half (F..J) strips 201..263.
    public const int UpperStripBase = 101;
    public const int LowerStripBase = 201;
    public const int MaxStripId = LowerStripBase + ColumnCount - 1; // 263
    public const int StripCount = MaxStripId + 1;                   // 264

    public static readonly string[] UpperRowLabels = ["A", "B", "C", "D", "E"];
    public static readonly string[] LowerRowLabels = ["F", "G", "H", "I", "J"];
    public static readonly string[] AllRowLabels = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J"];

    public static bool IsValidColumn(int column) => column is >= 1 and <= ColumnCount;

    public static bool IsRailStrip(int stripId) => stripId is >= StripTopVcc and <= StripBotGnd;

    public static float ColumnX(int column)
    {
        if (!IsValidColumn(column))
            throw new ArgumentOutOfRangeException(nameof(column), column, $"Columns are 1..{ColumnCount}.");

        return FirstColumnX + (column - 1) * ColumnPitch;
    }

    /// <summary>0-based row index (A=0 .. J=9).</summary>
    public static int RowIndex(char row)
    {
        char upper = char.ToUpperInvariant(row);
        int index = Array.IndexOf(AllRowLabels, upper.ToString());
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(row), row, "Rows are A..J.");

        return index;
    }

    public static bool IsUpperRow(char row) => char.ToUpperInvariant(row) is >= 'A' and <= 'E';

    public static float RowY(char row)
    {
        int index = RowIndex(row);
        return index < UpperRowCount
            ? UpperFirstRowY + index * RowPitch
            : LowerFirstRowY + (index - UpperRowCount) * RowPitch;
    }

    /// <summary>Strip ID shared by the five holes of one vertical column (<c>A15..E15 = 115</c>).</summary>
    public static int StripIdFor(char row, int column)
    {
        if (!IsValidColumn(column))
            throw new ArgumentOutOfRangeException(nameof(column), column, $"Columns are 1..{ColumnCount}.");

        return IsUpperRow(row) ? UpperStripBase + column - 1 : LowerStripBase + column - 1;
    }

    /// <summary>Column 1..63 for a terminal strip ID (101..163 or 201..263), else 0.</summary>
    public static int ColumnFromStrip(int stripId) => stripId switch
    {
        >= UpperStripBase and <= UpperStripBase + ColumnCount - 1 => stripId - UpperStripBase + 1,
        >= LowerStripBase and <= LowerStripBase + ColumnCount - 1 => stripId - LowerStripBase + 1,
        _ => 0
    };

    public static bool IsUpperStrip(int stripId) => stripId is >= UpperStripBase and <= UpperStripBase + ColumnCount - 1;

    public static string HoleIdFor(char row, int column)
    {
        if (!IsValidColumn(column))
            throw new ArgumentOutOfRangeException(nameof(column), column, $"Columns are 1..{ColumnCount}.");

        return string.Concat(char.ToUpperInvariant(row).ToString(), column.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool TryParseHoleId(string? holeId, out char row, out int column)
    {
        row = '\0';
        column = 0;
        if (string.IsNullOrWhiteSpace(holeId))
            return false;

        string trimmed = holeId.Trim();
        if (trimmed.Length is < 2 or > 3)
            return false;

        char candidate = char.ToUpperInvariant(trimmed[0]);
        if (candidate is < 'A' or > 'J')
            return false;

        if (!int.TryParse(trimmed.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            return false;

        if (!IsValidColumn(parsed))
            return false;

        row = candidate;
        column = parsed;
        return true;
    }

    public static HoleCoord CreateHole(char row, int column) =>
        new(HoleIdFor(row, column), StripIdFor(row, column), ColumnX(column), RowY(row));

    public static string RailPrefix(int stripId) => stripId switch
    {
        StripTopVcc => "TOP_VCC",
        StripTopGnd => "TOP_GND",
        StripBotVcc => "BOT_VCC",
        StripBotGnd => "BOT_GND",
        _ => throw new ArgumentOutOfRangeException(nameof(stripId), stripId, "Power rails are strips 1..4.")
    };

    public static float RailY(int stripId) => stripId switch
    {
        StripTopVcc => TopVccRailY,
        StripTopGnd => TopGndRailY,
        StripBotVcc => BottomVccRailY,
        StripBotGnd => BottomGndRailY,
        _ => throw new ArgumentOutOfRangeException(nameof(stripId), stripId, "Power rails are strips 1..4.")
    };

    public static string RailHoleId(int stripId, int index)
    {
        if (index is < 1 or > RailHoleCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Rail holes are 1..{RailHoleCount}.");

        return string.Concat(RailPrefix(stripId), "_", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static HoleCoord CreateRailHole(int stripId, int index)
    {
        string id = RailHoleId(stripId, index);
        float x = RailFirstX + (index - 1) * RailPitch;
        return new HoleCoord(id, stripId, x, RailY(stripId));
    }
}
