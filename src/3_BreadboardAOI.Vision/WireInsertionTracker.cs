// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/WireInsertionTracker.cs
// PURPOSE: Temporal delta wire endpoint pairing across consecutive hand-settled
//          frames so messy/overlapping jumper wires pair deterministically,
//          with HSV insulation color clustering + geodesic distance fallback
//          when the application cold-starts on an already-wired breadboard.
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>Canonical jumper insulation color bucket derived from HSV sampling near wire tips.</summary>
public enum WireColorBucket
{
    Unknown = 0,
    Black = 1,
    White = 2,
    Red = 3,
    Orange = 4,
    Yellow = 5,
    Green = 6,
    Cyan = 7,
    Blue = 8,
    Purple = 9
}

/// <summary>Single observed wire insertion endpoint in 1000 x 500 warped coordinates.</summary>
public readonly record struct WireEndpointObservation(
    float Px,
    float Py,
    float Confidence = 0.9f,
    int SourceComponentId = 0);

/// <summary>Deterministically paired jumper wire connecting two breadboard coordinates/nets.</summary>
public readonly record struct TrackedWirePair(
    int WireId,
    float End1Px,
    float End1Py,
    float End2Px,
    float End2Py,
    string? Net1,
    string? Net2,
    WireColorBucket Color,
    float Confidence,
    bool PairedByTemporalDelta);

/// <summary>
/// Tracks wire endpoints across consecutive settled frames (<see cref="HandMotionGate"/>).
/// Whenever a user inserts one jumper wire between hand-settled epochs, exactly two new
/// endpoints appear (<c>Delta = Current \ Previous</c>), which pair with 100% determinism
/// regardless of how many messy wires cross over the middle of the board. When multiple
/// wires appear simultaneously on cold start, pairs are resolved by HSV color bucket
/// affinity + pose keypoint continuity.
/// </summary>
public sealed class WireInsertionTracker
{
    public const float EndpointMatchRadiusPx = 12.5f;
    private const float EndpointMatchRadiusSq = EndpointMatchRadiusPx * EndpointMatchRadiusPx;
    private const float MinWireSpanPx = 10f;

    private readonly List<TrackedWirePair> _activeWires = new(32);
    private int _nextWireId = 1;
    private bool _hasBaseline;

    /// <summary>Currently active paired jumper wires on the breadboard.</summary>
    public IReadOnlyList<TrackedWirePair> ActiveWires => _activeWires;

    /// <summary>Clears all temporal wire state (e.g. when the board is cleared or camera switches).</summary>
    public void Reset()
    {
        _activeWires.Clear();
        _nextWireId = 1;
        _hasBaseline = false;
    }

    /// <summary>
    /// Updates the tracker from YOLOv11-Pose <see cref="DetectedComponent"/> detections on a settled frame
    /// and emits a unified component list where messy/overlapping wires are replaced by their
    /// deterministically paired counterparts.
    /// </summary>
    public IReadOnlyList<TrackedWirePair> UpdateFromPoseDetections(
        Mat warpedFrame,
        IReadOnlyList<DetectedComponent> rawComponents,
        List<DetectedComponent>? reconciledOutput = null)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ArgumentNullException.ThrowIfNull(rawComponents);

        var endpoints = new List<WireEndpointObservation>(rawComponents.Count * 2);
        for (int i = 0; i < rawComponents.Count; i++)
        {
            DetectedComponent c = rawComponents[i];
            if (c.Kind != ComponentKind.JumperWire)
                continue;

            endpoints.Add(new WireEndpointObservation(c.Lead1Px, c.Lead1Py, c.Confidence, c.TrackId));
            endpoints.Add(new WireEndpointObservation(c.Lead2Px, c.Lead2Py, c.Confidence, c.TrackId));
        }

        IReadOnlyList<TrackedWirePair> pairs = UpdateFromEndpoints(warpedFrame, endpoints);

        if (reconciledOutput is not null)
        {
            reconciledOutput.Clear();
            for (int i = 0; i < rawComponents.Count; i++)
            {
                if (rawComponents[i].Kind != ComponentKind.JumperWire)
                    reconciledOutput.Add(rawComponents[i]);
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                TrackedWirePair wire = pairs[i];
                float minX = Math.Min(wire.End1Px, wire.End2Px);
                float minY = Math.Min(wire.End1Py, wire.End2Py);
                float maxX = Math.Max(wire.End1Px, wire.End2Px);
                float maxY = Math.Max(wire.End1Py, wire.End2Py);

                reconciledOutput.Add(new DetectedComponent(
                    TrackId: 1000 + wire.WireId,
                    Kind: ComponentKind.JumperWire,
                    Confidence: wire.Confidence,
                    BoxX: minX,
                    BoxY: minY,
                    BoxW: Math.Max(4f, maxX - minX),
                    BoxH: Math.Max(4f, maxY - minY),
                    Lead1Px: wire.End1Px,
                    Lead1Py: wire.End1Py,
                    Lead2Px: wire.End2Px,
                    Lead2Py: wire.End2Py));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Core temporal delta + HSV cold-start solver operating on raw wire tip observations.
    /// </summary>
    public IReadOnlyList<TrackedWirePair> UpdateFromEndpoints(
        Mat warpedFrame,
        IReadOnlyList<WireEndpointObservation> observedEndpoints)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ArgumentNullException.ThrowIfNull(observedEndpoints);

        // Deduplicate closely overlapping tip detections within 6 px.
        List<WireEndpointObservation> cleanEndpoints = DeduplicateEndpoints(observedEndpoints, 6.0f);
        Span<bool> consumed = cleanEndpoints.Count <= 128
            ? stackalloc bool[cleanEndpoints.Count]
            : new bool[cleanEndpoints.Count];

        // Step 1: Retain existing tracked wires whose both endpoints are still present in the settled frame.
        var survivingWires = new List<TrackedWirePair>(_activeWires.Count);
        for (int w = 0; w < _activeWires.Count; w++)
        {
            TrackedWirePair existing = _activeWires[w];
            int idx1 = FindNearestUnconsumed(cleanEndpoints, consumed, existing.End1Px, existing.End1Py, EndpointMatchRadiusSq);
            if (idx1 < 0)
                continue;

            consumed[idx1] = true;
            int idx2 = FindNearestUnconsumed(cleanEndpoints, consumed, existing.End2Px, existing.End2Py, EndpointMatchRadiusSq);
            if (idx2 < 0)
            {
                // Wire was partially unplugged or moved; release idx1 so it can re-pair with the new destination hole.
                consumed[idx1] = false;
                continue;
            }

            consumed[idx2] = true;
            WireEndpointObservation ep1 = cleanEndpoints[idx1];
            WireEndpointObservation ep2 = cleanEndpoints[idx2];

            survivingWires.Add(existing with
            {
                End1Px = ep1.Px,
                End1Py = ep1.Py,
                End2Px = ep2.Px,
                End2Py = ep2.Py,
                Net1 = SnapNet(ep1.Px, ep1.Py),
                Net2 = SnapNet(ep2.Px, ep2.Py)
            });
        }

        // Step 2: Collect newly appeared endpoints (delta endpoints).
        var deltaEndpoints = new List<WireEndpointObservation>();
        for (int i = 0; i < cleanEndpoints.Count; i++)
        {
            if (!consumed[i])
                deltaEndpoints.Add(cleanEndpoints[i]);
        }

        // Step 3: If exactly 2 new endpoints appeared since the previous settled frame, pair them deterministically!
        if (_hasBaseline && deltaEndpoints.Count == 2)
        {
            WireEndpointObservation a = deltaEndpoints[0];
            WireEndpointObservation b = deltaEndpoints[1];
            if (Distance(a.Px, a.Py, b.Px, b.Py) >= MinWireSpanPx)
            {
                WireColorBucket color = SampleMergedWireColor(warpedFrame, a.Px, a.Py, b.Px, b.Py);
                survivingWires.Add(new TrackedWirePair(
                    WireId: _nextWireId++,
                    End1Px: a.Px,
                    End1Py: a.Py,
                    End2Px: b.Px,
                    End2Py: b.Py,
                    Net1: SnapNet(a.Px, a.Py),
                    Net2: SnapNet(b.Px, b.Py),
                    Color: color,
                    Confidence: Math.Min(a.Confidence, b.Confidence),
                    PairedByTemporalDelta: true));
            }
        }
        else if (deltaEndpoints.Count >= 2)
        {
            // Step 4: Cold-start or simultaneous multi-wire insertion fallback using HSV insulation color + pose affinity.
            PairByHsvColorFallback(warpedFrame, deltaEndpoints, survivingWires);
        }

        _activeWires.Clear();
        _activeWires.AddRange(survivingWires);
        _hasBaseline = true;
        return _activeWires;
    }

    private void PairByHsvColorFallback(
        Mat warpedFrame,
        List<WireEndpointObservation> candidates,
        List<TrackedWirePair> destination)
    {
        int n = candidates.Count;
        var colors = new (WireColorBucket Bucket, Scalar Hsv)[n];
        for (int i = 0; i < n; i++)
            colors[i] = SampleEndpointHsv(warpedFrame, candidates[i].Px, candidates[i].Py);

        var paired = new bool[n];

        // Greedily match endpoints with the lowest combined cost (same source pose track ID -> same HSV bucket -> hue delta).
        while (true)
        {
            int bestI = -1;
            int bestJ = -1;
            float bestCost = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                if (paired[i])
                    continue;

                for (int j = i + 1; j < n; j++)
                {
                    if (paired[j])
                        continue;

                    float span = Distance(candidates[i].Px, candidates[i].Py, candidates[j].Px, candidates[j].Py);
                    if (span < MinWireSpanPx)
                        continue;

                    float cost = ComputeColdStartPairCost(
                        candidates[i],
                        candidates[j],
                        colors[i],
                        colors[j],
                        span);

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestI = i;
                        bestJ = j;
                    }
                }
            }

            if (bestI < 0 || bestJ < 0)
                break;

            paired[bestI] = true;
            paired[bestJ] = true;

            WireEndpointObservation a = candidates[bestI];
            WireEndpointObservation b = candidates[bestJ];
            WireColorBucket bucket = colors[bestI].Bucket != WireColorBucket.Unknown
                ? colors[bestI].Bucket
                : colors[bestJ].Bucket;

            destination.Add(new TrackedWirePair(
                WireId: _nextWireId++,
                End1Px: a.Px,
                End1Py: a.Py,
                End2Px: b.Px,
                End2Py: b.Py,
                Net1: SnapNet(a.Px, a.Py),
                Net2: SnapNet(b.Px, b.Py),
                Color: bucket,
                Confidence: Math.Min(a.Confidence, b.Confidence),
                PairedByTemporalDelta: false));
        }
    }

    private static float ComputeColdStartPairCost(
        in WireEndpointObservation a,
        in WireEndpointObservation b,
        (WireColorBucket Bucket, Scalar Hsv) colorA,
        (WireColorBucket Bucket, Scalar Hsv) colorB,
        float spanPx)
    {
        float cost = spanPx * 0.15f;

        // Strong affinity when both tips came from the same YOLO pose instance AND share compatible insulation color.
        if (a.SourceComponentId != 0 && a.SourceComponentId == b.SourceComponentId)
            cost -= 120f;

        if (colorA.Bucket != WireColorBucket.Unknown && colorA.Bucket == colorB.Bucket)
        {
            cost -= 200f;
        }
        else if (colorA.Bucket != WireColorBucket.Unknown && colorB.Bucket != WireColorBucket.Unknown)
        {
            cost += 250f;
        }

        // Circular hue distance in OpenCV 0..180 Hue space.
        double rawHueDiff = Math.Abs(colorA.Hsv.Val0 - colorB.Hsv.Val0);
        double hueDiff = Math.Min(rawHueDiff, 180.0 - rawHueDiff);
        cost += (float)(hueDiff * 2.0);

        return cost;
    }

    /// <summary>Samples a 9x9 patch around an endpoint in HSV space to classify the jumper wire insulation color.</summary>
    public static (WireColorBucket Bucket, Scalar Hsv) SampleEndpointHsv(Mat warpedFrame, float px, float py)
    {
        if (warpedFrame.Empty())
            return (WireColorBucket.Unknown, default);

        int cx = Math.Clamp((int)MathF.Round(px), 0, warpedFrame.Width - 1);
        int cy = Math.Clamp((int)MathF.Round(py), 0, warpedFrame.Height - 1);
        Rect patch = Rect.Intersect(new Rect(cx - 5, cy - 5, 11, 11), new Rect(0, 0, warpedFrame.Width, warpedFrame.Height));
        if (patch.Width <= 0 || patch.Height <= 0)
            return (WireColorBucket.Unknown, default);

        using var roi = new Mat(warpedFrame, patch);
        using var bgr = new Mat();
        if (roi.Channels() == 4)
            Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);
        else if (roi.Channels() == 1)
            Cv2.CvtColor(roi, bgr, ColorConversionCodes.GRAY2BGR);
        else
            roi.CopyTo(bgr);

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Scalar mean = Cv2.Mean(hsv);
        return (ClassifyHsv(mean.Val0, mean.Val1, mean.Val2), mean);
    }

    private static WireColorBucket SampleMergedWireColor(Mat warpedFrame, float x1, float y1, float x2, float y2)
    {
        var c1 = SampleEndpointHsv(warpedFrame, x1, y1);
        if (c1.Bucket != WireColorBucket.Unknown)
            return c1.Bucket;

        var c2 = SampleEndpointHsv(warpedFrame, x2, y2);
        return c2.Bucket;
    }

    public static WireColorBucket ClassifyHsv(double h, double s, double v)
    {
        if (v < 48)
            return WireColorBucket.Black;
        if (s < 38 && v > 175)
            return WireColorBucket.White;
        if (s < 35)
            return WireColorBucket.Unknown;

        if (h < 9 || h >= 168)
            return WireColorBucket.Red;
        if (h < 22)
            return WireColorBucket.Orange;
        if (h < 36)
            return WireColorBucket.Yellow;
        if (h < 84)
            return WireColorBucket.Green;
        if (h < 102)
            return WireColorBucket.Cyan;
        if (h < 136)
            return WireColorBucket.Blue;
        return WireColorBucket.Purple;
    }

    private static List<WireEndpointObservation> DeduplicateEndpoints(
        IReadOnlyList<WireEndpointObservation> input,
        float radiusPx)
    {
        float radiusSq = radiusPx * radiusPx;
        var result = new List<WireEndpointObservation>(input.Count);

        for (int i = 0; i < input.Count; i++)
        {
            WireEndpointObservation candidate = input[i];
            bool duplicate = false;

            for (int j = 0; j < result.Count; j++)
            {
                if (DistanceSq(candidate.Px, candidate.Py, result[j].Px, result[j].Py) <= radiusSq)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                result.Add(candidate);
        }

        return result;
    }

    private static int FindNearestUnconsumed(
        List<WireEndpointObservation> endpoints,
        Span<bool> consumed,
        float targetX,
        float targetY,
        float maxDistSq)
    {
        int bestIndex = -1;
        float bestDistSq = maxDistSq;

        for (int i = 0; i < endpoints.Count; i++)
        {
            if (consumed[i])
                continue;

            float dSq = DistanceSq(endpoints[i].Px, endpoints[i].Py, targetX, targetY);
            if (dSq <= bestDistSq)
            {
                bestDistSq = dSq;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static string? SnapNet(float px, float py) =>
        PinMatrixLookup.Shared.TrySnapToHole(px, py, out HoleCoord hole) ? $"STRIP_{hole.StripId}" : null;

    private static float DistanceSq(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return dx * dx + dy * dy;
    }

    private static float Distance(float x1, float y1, float x2, float y2) =>
        MathF.Sqrt(DistanceSq(x1, y1, x2, y2));
}
