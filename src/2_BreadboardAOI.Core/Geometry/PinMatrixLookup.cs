// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Geometry/PinMatrixLookup.cs
// PURPOSE: Precomputed 830-hole spatial hash with O(1) neighbour snapping.
//          The 60 FPS pipeline must never allocate or scan 830 entries per lead.
// ============================================================================

using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.Geometry;

/// <summary>Minimal snapping abstraction so the DRC engine can be unit-tested with fakes.</summary>
public interface IHoleSnapper
{
    bool TrySnap(float px, float py, out HoleCoord hole);
}

/// <summary>
/// Spatial hash over all 830 tie points. Snap queries inspect the 3x3 grid cells
/// around the pixel and return the nearest hole within the 11.5 px capture radius.
/// </summary>
public sealed class PinMatrixLookup : IHoleSnapper
{
    public const float DefaultSnapRadius = 11.5f;

    private const float GridCellSize = 20f; // >= hole pitch so 3x3 covers the snap radius

    private static readonly int GridColumns = (int)MathF.Ceiling(BreadboardSpec.CanvasWidth / GridCellSize);
    private static readonly int GridRows = (int)MathF.Ceiling(BreadboardSpec.CanvasHeight / GridCellSize);

    private readonly HoleCoord[] _holes;
    private readonly int[] _bucketStart;
    private readonly int[] _bucketItems;
    private readonly int[] _stripOffsets;
    private readonly HoleCoord[] _stripOrderedHoles;
    private readonly Dictionary<string, int> _indexByHoleId;

    private static readonly Lazy<PinMatrixLookup> SharedInstance = new(static () => new PinMatrixLookup(), isThreadSafe: true);

    /// <summary>Immutable process-wide matrix used by the DRC engine and the CAD renderer.</summary>
    public static PinMatrixLookup Shared => SharedInstance.Value;

    public PinMatrixLookup()
    {
        _holes = BuildHoles();
        _indexByHoleId = BuildHoleIndex(_holes);
        _bucketStart = new int[GridColumns * GridRows + 1];
        _bucketItems = BuildBuckets(_holes, _bucketStart);
        (_stripOffsets, _stripOrderedHoles) = BuildStripIndex(_holes);
    }

    /// <summary>All 830 holes in deterministic order (upper A..E, lower F..J, then rails).</summary>
    public ReadOnlySpan<HoleCoord> Holes => _holes;

    public int Count => _holes.Length;

    /// <inheritdoc />
    bool IHoleSnapper.TrySnap(float px, float py, out HoleCoord hole) =>
        TrySnapToHole(px, py, out hole, DefaultSnapRadius);

    /// <summary>
    /// Snaps a warped-space pixel to the nearest tie point within <paramref name="maxRadius"/>.
    /// The default 11.5 px radius is half the 14.84 px column pitch minus a safety margin.
    /// </summary>
    public bool TrySnapToHole(float px, float py, out HoleCoord hole, float maxRadius = DefaultSnapRadius)
    {        hole = default;

        if (!float.IsFinite(px) || !float.IsFinite(py) || maxRadius <= 0f)
            return false;

        int cellX = Clamp((int)MathF.Floor(px / GridCellSize), 0, GridColumns - 1);
        int cellY = Clamp((int)MathF.Floor(py / GridCellSize), 0, GridRows - 1);
        int cellRadius = Math.Max(1, (int)MathF.Ceiling(maxRadius / GridCellSize));

        float bestDistanceSq = maxRadius * maxRadius;
        int bestIndex = -1;

        int minY = Math.Max(0, cellY - cellRadius);
        int maxY = Math.Min(GridRows - 1, cellY + cellRadius);
        int minX = Math.Max(0, cellX - cellRadius);
        int maxX = Math.Min(GridColumns - 1, cellX + cellRadius);

        for (int gy = minY; gy <= maxY; gy++)
        {
            int rowBase = gy * GridColumns;
            for (int gx = minX; gx <= maxX; gx++)
            {
                int cell = rowBase + gx;
                int start = _bucketStart[cell];
                int end = _bucketStart[cell + 1];

                for (int i = start; i < end; i++)
                {
                    int holeIndex = _bucketItems[i];
                    float dx = px - _holes[holeIndex].Px;
                    float dy = py - _holes[holeIndex].Py;
                    float distanceSq = dx * dx + dy * dy;

                    if (distanceSq <= bestDistanceSq && (bestIndex < 0 || distanceSq < bestDistanceSq))
                    {
                        bestDistanceSq = distanceSq;
                        bestIndex = holeIndex;
                    }
                }
            }
        }

        if (bestIndex < 0)
            return false;

        hole = _holes[bestIndex];
        return true;
    }

    public bool TryGetHole(string? holeId, out HoleCoord hole)
    {
        hole = default;
        if (string.IsNullOrWhiteSpace(holeId))
            return false;

        if (!_indexByHoleId.TryGetValue(holeId.Trim(), out int index))
            return false;

        hole = _holes[index];
        return true;
    }

    public bool TryGetHole(char row, int column, out HoleCoord hole)
    {
        hole = default;
        if (!BreadboardSpec.IsValidColumn(column))
            return false;

        char upper = char.ToUpperInvariant(row);
        int rowIndex = Array.IndexOf(BreadboardSpec.AllRowLabels, upper.ToString());
        if (rowIndex < 0)
            return false;

        int index = rowIndex * BreadboardSpec.ColumnCount + (column - 1);
        hole = _holes[index];
        return true;
    }

    public bool TryGetRailHole(int stripId, int railIndex, out HoleCoord hole)
    {
        hole = default;
        if (!BreadboardSpec.IsRailStrip(stripId) || railIndex is < 1 or > BreadboardSpec.RailHoleCount)
            return false;

        int railOffset = (stripId - BreadboardSpec.StripTopVcc) * BreadboardSpec.RailHoleCount;
        int index = BreadboardSpec.TerminalHoleCount + railOffset + (railIndex - 1);
        hole = _holes[index];
        return true;
    }

    /// <summary>All physical holes sharing one internal metal clip (5 for strips, 50 for rails).</summary>
    public ReadOnlySpan<HoleCoord> GetStripHoles(int stripId)
    {
        if (stripId < 0 || stripId >= BreadboardSpec.StripCount)
            return ReadOnlySpan<HoleCoord>.Empty;

        int start = _stripOffsets[stripId];
        int count = _stripOffsets[stripId + 1] - start;
        return count == 0 ? ReadOnlySpan<HoleCoord>.Empty : new ReadOnlySpan<HoleCoord>(_stripOrderedHoles, start, count);
    }

    /// <summary>
    /// Finds the closest unpopulated strip in the same lettered row, alternating
    /// outward from <paramref name="preferredDirection"/> (Col + 1 first).
    /// Used to draw the corrective green KiCad ratsnest arrow for same-strip shorts.
    /// </summary>
    public bool TryFindNearestFreeHoleInRow(
        HoleCoord reference,
        bool[]? occupiedStripIds,
        out HoleCoord target,
        int preferredDirection = 1)
    {
        target = default;

        if (!BreadboardSpec.TryParseHoleId(reference.HoleId, out char row, out int column))
            return false;

        int direction = preferredDirection >= 0 ? 1 : -1;

        for (int distance = 1; distance < BreadboardSpec.ColumnCount; distance++)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int candidateColumn = column + (attempt == 0 ? direction : -direction) * distance;
                if (!BreadboardSpec.IsValidColumn(candidateColumn))
                    continue;

                int candidateStrip = BreadboardSpec.StripIdFor(row, candidateColumn);
                if (candidateStrip == reference.StripId)
                    continue;

                if (occupiedStripIds is not null
                    && candidateStrip < occupiedStripIds.Length
                    && occupiedStripIds[candidateStrip])
                {
                    continue;
                }

                target = BreadboardSpec.CreateHole(row, candidateColumn);
                return true;
            }
        }

        return false;
    }

    private static HoleCoord[] BuildHoles()
    {
        var holes = new HoleCoord[BreadboardSpec.TotalHoleCount];
        int index = 0;

        foreach (string row in BreadboardSpec.AllRowLabels)
        {
            char rowChar = row[0];
            for (int column = 1; column <= BreadboardSpec.ColumnCount; column++)
                holes[index++] = BreadboardSpec.CreateHole(rowChar, column);
        }

        for (int strip = BreadboardSpec.StripTopVcc; strip <= BreadboardSpec.StripBotGnd; strip++)
        {
            for (int railIndex = 1; railIndex <= BreadboardSpec.RailHoleCount; railIndex++)
                holes[index++] = BreadboardSpec.CreateRailHole(strip, railIndex);
        }

        return holes;
    }

    private static Dictionary<string, int> BuildHoleIndex(HoleCoord[] holes)
    {
        var map = new Dictionary<string, int>(holes.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < holes.Length; i++)
            map[holes[i].HoleId] = i;

        return map;
    }

    private static int[] BuildBuckets(HoleCoord[] holes, int[] bucketStart)
    {
        int[] counts = bucketStart; // reuse as per-cell counts, converted to offsets below
        foreach (HoleCoord hole in holes)
            counts[CellIndex(hole.Px, hole.Py)]++;

        int running = 0;
        for (int cell = 0; cell < counts.Length - 1; cell++)
        {
            int count = counts[cell];
            bucketStart[cell] = running;
            running += count;
        }

        bucketStart[^1] = running;

        var items = new int[holes.Length];
        int[] cursor = new int[bucketStart.Length];
        Array.Copy(bucketStart, cursor, bucketStart.Length);

        for (int i = 0; i < holes.Length; i++)
        {
            int cell = CellIndex(holes[i].Px, holes[i].Py);
            items[cursor[cell]++] = i;
        }

        return items;
    }

    private static (int[] Offsets, HoleCoord[] OrderedHoles) BuildStripIndex(HoleCoord[] holes)
    {
        var offsets = new int[BreadboardSpec.StripCount + 1];
        foreach (HoleCoord hole in holes)
        {
            if (hole.StripId is >= 0 and < BreadboardSpec.StripCount)
                offsets[hole.StripId + 1]++;
        }

        for (int strip = 0; strip < BreadboardSpec.StripCount; strip++)
            offsets[strip + 1] += offsets[strip];

        var ordered = new HoleCoord[holes.Length];
        int[] cursor = new int[BreadboardSpec.StripCount + 1];
        Array.Copy(offsets, cursor, offsets.Length);

        foreach (HoleCoord hole in holes)
        {
            int strip = hole.StripId;
            if (strip is >= 0 and < BreadboardSpec.StripCount)
                ordered[cursor[strip]++] = hole;
        }

        return (offsets, ordered);
    }

    private static int CellIndex(float px, float py)
    {
        int cellX = Clamp((int)(px / GridCellSize), 0, GridColumns - 1);
        int cellY = Clamp((int)(py / GridCellSize), 0, GridRows - 1);
        return cellY * GridColumns + cellX;
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
