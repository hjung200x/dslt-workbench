using Dslt.Validation.Prepare;

namespace Dslt.App.Tests;

internal static class ReferenceSuitabilityAuditorTests
{
    public static void Run()
    {
        TouchingInstancesRequireProtocolReview();
        WallSeparatedInstancesRemainEligibleForReview();
        EnclosedBackgroundIsDistinguishedFromExterior();
        PlanarReferencesRequireReview();
        Console.WriteLine("DSLT reference suitability audit tests passed.");
    }

    private static void TouchingInstancesRequireProtocolReview()
    {
        int[] labels = [1, 1, 2, 2, 1, 1, 2, 2];
        var result = ReferenceSuitabilityAuditor.Analyze(labels, 4, 2, 1);
        Equal(ReferenceBoundaryRepresentation.TouchingInstances, result.BoundaryRepresentation, "touching representation");
        Equal(2L, result.DifferentForegroundLabelFaceCount, "touching faces");
        Equal(4L, result.DifferentForegroundLabelInterfaceVoxelCount, "touching voxels");
        Assert(result.RequiresReferenceProtocolReview, "Touching instance labels must require protocol review.");
    }

    private static void WallSeparatedInstancesRemainEligibleForReview()
    {
        int[] labels =
        [
            1, 1, 0, 2, 2,
            1, 1, 0, 2, 2,
            1, 1, 0, 2, 2,
            1, 1, 0, 2, 2,
        ];
        var result = ReferenceSuitabilityAuditor.Analyze(labels, 5, 2, 2);
        Equal(ReferenceBoundaryRepresentation.WallSeparatedInstances, result.BoundaryRepresentation, "wall-separated representation");
        Equal(0L, result.DifferentForegroundLabelFaceCount, "wall-separated touching faces");
        Assert(!result.RequiresReferenceProtocolReview, "A 3D wall-separated reference should remain eligible for curator review.");
    }

    private static void EnclosedBackgroundIsDistinguishedFromExterior()
    {
        var labels = Enumerable.Repeat(1, 27).ToArray();
        labels[13] = 0;
        var result = ReferenceSuitabilityAuditor.Analyze(labels, 3, 3, 3);
        Equal(1L, result.BackgroundVoxelCount, "background count");
        Equal(0L, result.ExteriorConnectedBackgroundVoxelCount, "exterior background count");
        Equal(1L, result.EnclosedBackgroundVoxelCount, "enclosed background count");
    }

    private static void PlanarReferencesRequireReview()
    {
        var result = ReferenceSuitabilityAuditor.Analyze([0, 1, 1, 0], 2, 2, 1);
        Assert(!result.IsThreeDimensional, "Depth-one reference must be planar.");
        Assert(result.RequiresReferenceProtocolReview, "Planar references must not be silently treated as 3D release evidence.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}.");
    }
}
