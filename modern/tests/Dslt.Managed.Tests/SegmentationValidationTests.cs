using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Validation;

internal static class SegmentationValidationTests
{
    public static void Run()
    {
        IdenticalLabelsPass();
        ShiftedSurfaceHasOneVoxelHausdorff();
        DisconnectedLabelsRespectConnectivity();
        EmptyMasksAreExplicit();
        CalibratedVolumeIsReported();
        DistanceTransformMatchesBruteForce();
        Console.WriteLine("DSLT segmentation metric oracle tests passed.");
    }

    private static void IdenticalLabelsPass()
    {
        var labels = new int[5 * 5 * 3];
        labels[1 * 25 + 1 * 5 + 1] = 7;
        labels[1 * 25 + 3 * 5 + 3] = 9;
        var candidate = labels.Select(value => value == 7 ? 100 : value == 9 ? 200 : 0).ToArray();
        var result = SegmentationValidator.Evaluate(labels, candidate, 5, 5, 3, Calibration.Unit);
        Assert(result.Passed, "Identical foreground masks should pass.");
        Equal(1.0, result.Dice, "Identical Dice");
        Equal(2, result.ReferenceObjectCount, "Reference object count");
        Equal(2, result.CandidateObjectCount, "Candidate object count");
        Equal(0.0, result.Hausdorff95Voxels!.Value, "Identical HD95");
    }

    private static void ShiftedSurfaceHasOneVoxelHausdorff()
    {
        var reference = new int[7 * 3 * 3];
        var candidate = new int[reference.Length];
        reference[1 * 21 + 1 * 7 + 2] = 1;
        candidate[1 * 21 + 1 * 7 + 3] = 1;
        var permissive = new SegmentationValidationThresholds(0, 1, 1);
        var result = SegmentationValidator.Evaluate(
            reference, candidate, 7, 3, 3, Calibration.Unit, thresholds: permissive);
        Equal(1.0, result.Hausdorff95Voxels!.Value, "One-voxel shift HD95");
        Assert(result.Passed, "One-voxel surface shift should pass a one-voxel HD95 gate with permissive Dice.");

        candidate[1 * 21 + 1 * 7 + 4] = 1;
        candidate[1 * 21 + 1 * 7 + 3] = 0;
        var failed = SegmentationValidator.Evaluate(
            reference, candidate, 7, 3, 3, Calibration.Unit, thresholds: permissive);
        Equal(2.0, failed.Hausdorff95Voxels!.Value, "Two-voxel shift HD95");
        Assert(!failed.Passed, "Two-voxel surface shift must fail a one-voxel HD95 gate.");
    }

    private static void DisconnectedLabelsRespectConnectivity()
    {
        var labels = new int[8];
        labels[0] = labels[7] = 4;
        var six = SegmentationValidator.Evaluate(
            labels, labels, 2, 2, 2, Calibration.Unit, objectConnectivity: 6);
        var twentySix = SegmentationValidator.Evaluate(
            labels, labels, 2, 2, 2, Calibration.Unit, objectConnectivity: 26);
        var eighteen = SegmentationValidator.Evaluate(
            labels, labels, 2, 2, 2, Calibration.Unit, objectConnectivity: 18);
        Equal(2, six.ReferenceObjectCount, "6-connected diagonal objects");
        Equal(2, eighteen.ReferenceObjectCount, "18-connected corner objects");
        Equal(1, twentySix.ReferenceObjectCount, "26-connected diagonal objects");
    }

    private static void EmptyMasksAreExplicit()
    {
        var empty = new int[27];
        var bothEmpty = SegmentationValidator.Evaluate(empty, empty, 3, 3, 3, Calibration.Unit);
        Assert(bothEmpty.Passed, "Two empty masks should agree.");
        Equal(1.0, bothEmpty.Dice, "Empty Dice");
        Equal(0.0, bothEmpty.Hausdorff95Voxels!.Value, "Empty HD95");

        var foreground = new int[27];
        foreground[13] = 1;
        var oneEmpty = SegmentationValidator.Evaluate(empty, foreground, 3, 3, 3, Calibration.Unit);
        Assert(!oneEmpty.Passed, "Exactly one empty mask must fail.");
        Assert(oneEmpty.Hausdorff95Voxels is null, "One-empty HD95 must be undefined.");
    }

    private static void CalibratedVolumeIsReported()
    {
        var labels = new[] { -1, 2, 2, -1 };
        var calibration = new Calibration(2, 3, 4, true, "um");
        var result = SegmentationValidator.Evaluate(
            labels, labels, 2, 2, 1, calibration, referenceBackgroundLabel: -1, candidateBackgroundLabel: -1);
        Equal(48.0, result.ReferenceSegmentedVolume, "Calibrated reference volume");
        Equal("um^3", result.VolumeUnit, "Calibrated volume unit");
    }

    private static void DistanceTransformMatchesBruteForce()
    {
        const int width = 5;
        const int height = 4;
        const int depth = 3;
        var random = new Random(7319);
        var permissive = new SegmentationValidationThresholds(0, 100, 100);
        for (var fixture = 0; fixture < 20; fixture++)
        {
            var reference = new int[width * height * depth];
            var candidate = new int[reference.Length];
            for (var index = 0; index < reference.Length; index++)
            {
                if (random.NextDouble() < 0.18) reference[index] = 1;
                if (random.NextDouble() < 0.18) candidate[index] = 1;
            }
            reference[random.Next(reference.Length)] = 1;
            candidate[random.Next(candidate.Length)] = 1;
            var result = SegmentationValidator.Evaluate(
                reference, candidate, width, height, depth, Calibration.Unit, thresholds: permissive);
            var expected = BruteForceHausdorff95(reference, candidate, width, height, depth);
            Equal(expected, result.Hausdorff95Voxels!.Value, $"Brute-force HD95 fixture {fixture}");
        }
    }

    private static double BruteForceHausdorff95(int[] reference, int[] candidate, int width, int height, int depth)
    {
        var referenceSurface = SurfaceCoordinates(reference, width, height, depth);
        var candidateSurface = SurfaceCoordinates(candidate, width, height, depth);
        return Math.Max(
            DirectedPercentile(referenceSurface, candidateSurface),
            DirectedPercentile(candidateSurface, referenceSurface));
    }

    private static List<(int X, int Y, int Z)> SurfaceCoordinates(
        int[] labels,
        int width,
        int height,
        int depth)
    {
        var surface = new List<(int X, int Y, int Z)>();
        var slice = width * height;
        for (var z = 0; z < depth; z++)
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = z * slice + y * width + x;
                    if (labels[index] == 0) continue;
                    var boundary = x == 0 || x + 1 == width || y == 0 || y + 1 == height ||
                        z == 0 || z + 1 == depth || labels[index - 1] == 0 || labels[index + 1] == 0 ||
                        labels[index - width] == 0 || labels[index + width] == 0 ||
                        labels[index - slice] == 0 || labels[index + slice] == 0;
                    if (boundary) surface.Add((x, y, z));
                }
        return surface;
    }

    private static double DirectedPercentile(
        List<(int X, int Y, int Z)> source,
        List<(int X, int Y, int Z)> target)
    {
        var distances = source.Select(point =>
        {
            var minimumSquared = target.Min(other =>
                (point.X - other.X) * (point.X - other.X) +
                (point.Y - other.Y) * (point.Y - other.Y) +
                (point.Z - other.Z) * (point.Z - other.Z));
            return Math.Sqrt(minimumSquared);
        }).Order().ToArray();
        return distances[Math.Max(0, (int)Math.Ceiling(distances.Length * 0.95) - 1)];
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}.");
    }
}
