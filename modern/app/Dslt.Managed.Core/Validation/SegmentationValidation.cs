using System.Buffers;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Validation;

public sealed record SegmentationValidationThresholds(
    double MinimumDice,
    double MaximumVolumeDifferenceFraction,
    double MaximumHausdorff95Voxels)
{
    public static SegmentationValidationThresholds V1 { get; } = new(0.995, 0.005, 1.0);

    public void Validate()
    {
        if (!double.IsFinite(MinimumDice) || MinimumDice is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumDice));
        if (!double.IsFinite(MaximumVolumeDifferenceFraction) || MaximumVolumeDifferenceFraction < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumVolumeDifferenceFraction));
        if (!double.IsFinite(MaximumHausdorff95Voxels) || MaximumHausdorff95Voxels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumHausdorff95Voxels));
    }
}

public sealed record SegmentationValidationResult(
    int VoxelCount,
    long ReferenceForegroundVoxels,
    long CandidateForegroundVoxels,
    int ReferenceObjectCount,
    int CandidateObjectCount,
    double Dice,
    double VolumeDifferenceFraction,
    double? Hausdorff95Voxels,
    double ReferenceSegmentedVolume,
    double CandidateSegmentedVolume,
    string VolumeUnit,
    bool Passed,
    IReadOnlyList<string> Failures);

public static class SegmentationValidator
{
    public static SegmentationValidationResult Evaluate(
        ReadOnlySpan<int> referenceLabels,
        ReadOnlySpan<int> candidateLabels,
        int width,
        int height,
        int depth,
        Calibration calibration,
        int referenceBackgroundLabel = 0,
        int candidateBackgroundLabel = 0,
        int objectConnectivity = 26,
        SegmentationValidationThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        thresholds ??= SegmentationValidationThresholds.V1;
        thresholds.Validate();
        ValidateGeometry(width, height, depth, calibration, objectConnectivity);
        var voxelCount = checked(width * height * depth);
        if (referenceLabels.Length != voxelCount)
            throw new ArgumentException($"Expected {voxelCount} reference labels but received {referenceLabels.Length}.", nameof(referenceLabels));
        if (candidateLabels.Length != voxelCount)
            throw new ArgumentException($"Expected {voxelCount} candidate labels but received {candidateLabels.Length}.", nameof(candidateLabels));

        var referenceForeground = new bool[voxelCount];
        var candidateForeground = new bool[voxelCount];
        long referenceCount = 0;
        long candidateCount = 0;
        long intersection = 0;
        for (var index = 0; index < voxelCount; index++)
        {
            var referenceIsForeground = referenceLabels[index] != referenceBackgroundLabel;
            var candidateIsForeground = candidateLabels[index] != candidateBackgroundLabel;
            referenceForeground[index] = referenceIsForeground;
            candidateForeground[index] = candidateIsForeground;
            if (referenceIsForeground) referenceCount++;
            if (candidateIsForeground) candidateCount++;
            if (referenceIsForeground && candidateIsForeground) intersection++;
        }

        var denominator = referenceCount + candidateCount;
        var dice = denominator == 0 ? 1.0 : 2.0 * intersection / denominator;
        var volumeDifference = referenceCount == 0
            ? candidateCount == 0 ? 0.0 : 1.0
            : Math.Abs(candidateCount - referenceCount) / (double)referenceCount;
        var referenceObjects = CountLabelComponents(
            referenceLabels, referenceBackgroundLabel, width, height, depth, objectConnectivity);
        var candidateObjects = CountLabelComponents(
            candidateLabels, candidateBackgroundLabel, width, height, depth, objectConnectivity);
        var hausdorff95 = Hausdorff95(
            referenceForeground, candidateForeground, width, height, depth);

        var failures = new List<string>();
        if (dice < thresholds.MinimumDice)
            failures.Add($"Dice {dice:R} is below {thresholds.MinimumDice:R}.");
        if (referenceObjects != candidateObjects)
            failures.Add($"Object count differs: reference {referenceObjects}, candidate {candidateObjects}.");
        if (volumeDifference > thresholds.MaximumVolumeDifferenceFraction)
            failures.Add(
                $"Segmented volume difference {volumeDifference:R} exceeds {thresholds.MaximumVolumeDifferenceFraction:R}.");
        if (hausdorff95 is null)
            failures.Add("Hausdorff95 is undefined because exactly one foreground mask is empty.");
        else if (hausdorff95 > thresholds.MaximumHausdorff95Voxels)
            failures.Add(
                $"Hausdorff95 {hausdorff95.Value:R} voxels exceeds {thresholds.MaximumHausdorff95Voxels:R}.");

        var voxelVolume = calibration.SpacingX * calibration.SpacingY * calibration.SpacingZ;
        var unit = calibration.IsCalibrated ? $"{calibration.UnitName}^3" : "voxel^3";
        return new SegmentationValidationResult(
            voxelCount,
            referenceCount,
            candidateCount,
            referenceObjects,
            candidateObjects,
            dice,
            volumeDifference,
            hausdorff95,
            referenceCount * voxelVolume,
            candidateCount * voxelVolume,
            unit,
            failures.Count == 0,
            failures);
    }

    private static void ValidateGeometry(
        int width,
        int height,
        int depth,
        Calibration calibration,
        int connectivity)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Volume dimensions must be positive.");
        _ = checked(width * height * depth);
        if (!double.IsFinite(calibration.SpacingX) || calibration.SpacingX <= 0 ||
            !double.IsFinite(calibration.SpacingY) || calibration.SpacingY <= 0 ||
            !double.IsFinite(calibration.SpacingZ) || calibration.SpacingZ <= 0 ||
            string.IsNullOrWhiteSpace(calibration.UnitName))
            throw new ArgumentOutOfRangeException(nameof(calibration));
        if (connectivity is not (6 or 18 or 26))
            throw new ArgumentOutOfRangeException(nameof(connectivity), "Connectivity must be 6, 18, or 26.");
    }

    private static int CountLabelComponents(
        ReadOnlySpan<int> labels,
        int backgroundLabel,
        int width,
        int height,
        int depth,
        int connectivity)
    {
        var visited = ArrayPool<bool>.Shared.Rent(labels.Length);
        var queue = ArrayPool<int>.Shared.Rent(labels.Length);
        Array.Clear(visited, 0, labels.Length);
        var slice = checked(width * height);
        var components = 0;
        try
        {
            for (var start = 0; start < labels.Length; start++)
            {
                if (visited[start] || labels[start] == backgroundLabel) continue;
                components = checked(components + 1);
                var label = labels[start];
                var head = 0;
                var tail = 0;
                queue[tail++] = start;
                visited[start] = true;
                while (head < tail)
                {
                    var index = queue[head++];
                    var z = index / slice;
                    var remainder = index - z * slice;
                    var y = remainder / width;
                    var x = remainder - y * width;
                    for (var dz = -1; dz <= 1; dz++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var manhattan = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                                if (manhattan == 0 ||
                                    connectivity == 6 && manhattan != 1 ||
                                    connectivity == 18 && manhattan > 2)
                                    continue;
                                var nx = x + dx;
                                var ny = y + dy;
                                var nz = z + dz;
                                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height || (uint)nz >= (uint)depth)
                                    continue;
                                var neighbor = nz * slice + ny * width + nx;
                                if (visited[neighbor] || labels[neighbor] != label) continue;
                                visited[neighbor] = true;
                                queue[tail++] = neighbor;
                            }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited, clearArray: true);
        }
        return components;
    }

    private static double? Hausdorff95(
        bool[] referenceForeground,
        bool[] candidateForeground,
        int width,
        int height,
        int depth)
    {
        var referenceSurface = BuildSurface(referenceForeground, width, height, depth);
        var candidateSurface = BuildSurface(candidateForeground, width, height, depth);
        var referenceSurfaceCount = referenceSurface.Count(value => value);
        var candidateSurfaceCount = candidateSurface.Count(value => value);
        if (referenceSurfaceCount == 0 && candidateSurfaceCount == 0) return 0;
        if (referenceSurfaceCount == 0 || candidateSurfaceCount == 0) return null;

        var referenceToCandidate = DirectedHausdorff95(
            referenceSurface, candidateSurface, width, height, depth);
        var candidateToReference = DirectedHausdorff95(
            candidateSurface, referenceSurface, width, height, depth);
        return Math.Max(referenceToCandidate, candidateToReference);
    }

    private static bool[] BuildSurface(bool[] foreground, int width, int height, int depth)
    {
        var surface = new bool[foreground.Length];
        var slice = checked(width * height);
        for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = z * slice + y * width + x;
                    if (!foreground[index]) continue;
                    surface[index] = x == 0 || x + 1 == width ||
                        y == 0 || y + 1 == height ||
                        z == 0 || z + 1 == depth ||
                        !foreground[index - 1] || !foreground[index + 1] ||
                        !foreground[index - width] || !foreground[index + width] ||
                        !foreground[index - slice] || !foreground[index + slice];
                }
        return surface;
    }

    private static double DirectedHausdorff95(
        bool[] sourceSurface,
        bool[] targetSurface,
        int width,
        int height,
        int depth)
    {
        var squaredDistances = ArrayPool<double>.Shared.Rent(targetSurface.Length);
        try
        {
            SquaredDistanceTransform(targetSurface, width, height, depth, squaredDistances);
            return Percentile95(SurfaceDistances(sourceSurface, squaredDistances));
        }
        finally
        {
            ArrayPool<double>.Shared.Return(squaredDistances);
        }
    }

    private static void SquaredDistanceTransform(
        bool[] target,
        int width,
        int height,
        int depth,
        double[] distances)
    {
        var far = (double)width * width + (double)height * height + (double)depth * depth + 1;
        for (var index = 0; index < target.Length; index++) distances[index] = target[index] ? 0 : far;
        var maximumLine = Math.Max(width, Math.Max(height, depth));
        var input = new double[maximumLine];
        var output = new double[maximumLine];
        var locations = new int[maximumLine];
        var boundaries = new double[maximumLine + 1];
        var slice = checked(width * height);

        for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y++)
            {
                var offset = z * slice + y * width;
                for (var x = 0; x < width; x++) input[x] = distances[offset + x];
                TransformLine(input, output, width, locations, boundaries);
                for (var x = 0; x < width; x++) distances[offset + x] = output[x];
            }

        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++) input[y] = distances[z * slice + y * width + x];
                TransformLine(input, output, height, locations, boundaries);
                for (var y = 0; y < height; y++) distances[z * slice + y * width + x] = output[y];
            }

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < depth; z++) input[z] = distances[z * slice + y * width + x];
                TransformLine(input, output, depth, locations, boundaries);
                for (var z = 0; z < depth; z++) distances[z * slice + y * width + x] = output[z];
            }
    }

    private static void TransformLine(
        double[] input,
        double[] output,
        int length,
        int[] locations,
        double[] boundaries)
    {
        var envelope = 0;
        locations[0] = 0;
        boundaries[0] = double.NegativeInfinity;
        boundaries[1] = double.PositiveInfinity;
        for (var q = 1; q < length; q++)
        {
            double intersection;
            do
            {
                var previous = locations[envelope];
                intersection = ((input[q] + (double)q * q) -
                    (input[previous] + (double)previous * previous)) /
                    (2.0 * (q - previous));
                if (intersection > boundaries[envelope]) break;
                envelope--;
            } while (envelope >= 0);
            envelope++;
            locations[envelope] = q;
            boundaries[envelope] = intersection;
            boundaries[envelope + 1] = double.PositiveInfinity;
        }

        envelope = 0;
        for (var q = 0; q < length; q++)
        {
            while (boundaries[envelope + 1] < q) envelope++;
            var nearest = locations[envelope];
            var delta = q - nearest;
            output[q] = delta * (double)delta + input[nearest];
        }
    }

    private static double[] SurfaceDistances(bool[] sourceSurface, double[] squaredDistances)
    {
        var values = new double[sourceSurface.Count(value => value)];
        var destination = 0;
        for (var index = 0; index < sourceSurface.Length; index++)
        {
            if (!sourceSurface[index]) continue;
            values[destination++] = Math.Sqrt(squaredDistances[index]);
        }
        return values;
    }

    private static double Percentile95(double[] values)
    {
        Array.Sort(values);
        var rank = Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1);
        return values[rank];
    }
}
