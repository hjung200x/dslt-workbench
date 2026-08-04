using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using Dslt.Managed.Core.IO;

namespace Dslt.Validation.Prepare;

public enum ReferenceBoundaryRepresentation
{
    Empty,
    BinaryForeground,
    WallSeparatedInstances,
    TouchingInstances,
}

public sealed record ReferenceSuitabilityAudit(
    string? SourcePath,
    string? SourceFileSha256,
    int Width,
    int Height,
    int Depth,
    int BackgroundLabel,
    long VoxelCount,
    long ForegroundVoxelCount,
    double ForegroundFraction,
    int DistinctForegroundLabelCount,
    long BackgroundVoxelCount,
    double BackgroundFraction,
    long ExteriorConnectedBackgroundVoxelCount,
    long EnclosedBackgroundVoxelCount,
    long DifferentForegroundLabelFaceCount,
    long DifferentForegroundLabelInterfaceVoxelCount,
    double DifferentForegroundLabelInterfaceFraction,
    bool IsThreeDimensional,
    ReferenceBoundaryRepresentation BoundaryRepresentation,
    bool RequiresReferenceProtocolReview,
    string HausdorffSemantics,
    string RecommendedUse,
    IReadOnlyList<string> Findings);

public static class ReferenceSuitabilityAuditor
{
    public static ReferenceSuitabilityAudit AuditFile(
        string path,
        int backgroundLabel = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Reference label TIFF was not found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        var volume = LabelTiffCodec.Read(fullPath);
        using var stream = File.OpenRead(fullPath);
        var fileSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return Analyze(
            volume.Labels,
            volume.Width,
            volume.Height,
            volume.Depth,
            backgroundLabel,
            fullPath,
            fileSha256,
            cancellationToken);
    }

    public static ReferenceSuitabilityAudit Analyze(
        ReadOnlySpan<int> labels,
        int width,
        int height,
        int depth,
        int backgroundLabel = 0,
        string? sourcePath = null,
        string? sourceFileSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Reference dimensions must be positive.");
        var voxelCount = checked(width * height * depth);
        if (labels.Length != voxelCount)
            throw new ArgumentException($"Expected {voxelCount} labels but received {labels.Length}.", nameof(labels));

        var distinctLabels = new HashSet<int>();
        long foregroundCount = 0;
        for (var index = 0; index < labels.Length; index++)
        {
            if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var label = labels[index];
            if (label == backgroundLabel) continue;
            foregroundCount++;
            distinctLabels.Add(label);
        }

        var interfaceVoxels = ArrayPool<bool>.Shared.Rent(voxelCount);
        Array.Clear(interfaceVoxels, 0, voxelCount);
        long differentLabelFaces = 0;
        try
        {
            var slice = checked(width * height);
            for (var z = 0; z < depth; z++)
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var index = z * slice + y * width + x;
                        if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var label = labels[index];
                        if (label == backgroundLabel) continue;
                        if (x + 1 < width)
                            InspectNeighbor(labels, index, index + 1, label, backgroundLabel, interfaceVoxels, ref differentLabelFaces);
                        if (y + 1 < height)
                            InspectNeighbor(labels, index, index + width, label, backgroundLabel, interfaceVoxels, ref differentLabelFaces);
                        if (z + 1 < depth)
                            InspectNeighbor(labels, index, index + slice, label, backgroundLabel, interfaceVoxels, ref differentLabelFaces);
                    }

            long interfaceVoxelCount = 0;
            for (var index = 0; index < voxelCount; index++)
                if (interfaceVoxels[index]) interfaceVoxelCount++;

            var backgroundCount = voxelCount - foregroundCount;
            var exteriorBackgroundCount = CountExteriorConnectedBackground(
                labels, width, height, depth, backgroundLabel, cancellationToken);
            var enclosedBackgroundCount = backgroundCount - exteriorBackgroundCount;
            var representation = Classify(distinctLabels.Count, foregroundCount, differentLabelFaces);
            var requiresReview = depth <= 1 || representation == ReferenceBoundaryRepresentation.TouchingInstances;
            var findings = BuildFindings(
                depth,
                representation,
                foregroundCount / (double)voxelCount,
                backgroundCount,
                exteriorBackgroundCount);

            return new ReferenceSuitabilityAudit(
                sourcePath,
                sourceFileSha256,
                width,
                height,
                depth,
                backgroundLabel,
                voxelCount,
                foregroundCount,
                foregroundCount / (double)voxelCount,
                distinctLabels.Count,
                backgroundCount,
                backgroundCount / (double)voxelCount,
                exteriorBackgroundCount,
                enclosedBackgroundCount,
                differentLabelFaces,
                interfaceVoxelCount,
                foregroundCount == 0 ? 0 : interfaceVoxelCount / (double)foregroundCount,
                depth > 1,
                representation,
                requiresReview,
                "HD95 compares surfaces of the binary label != background occupancy; transitions between two foreground labels are not surfaces.",
                requiresReview
                    ? "preflight-only-until-reference-protocol-review"
                    : "eligible-for-curator-review; this structural audit alone does not approve a v1.0 case",
                findings);

        }
        finally
        {
            ArrayPool<bool>.Shared.Return(interfaceVoxels, clearArray: true);
        }
    }

    private static void InspectNeighbor(
        ReadOnlySpan<int> labels,
        int index,
        int neighbor,
        int label,
        int backgroundLabel,
        bool[] interfaceVoxels,
        ref long differentLabelFaces)
    {
        var neighborLabel = labels[neighbor];
        if (neighborLabel == backgroundLabel || neighborLabel == label) return;
        differentLabelFaces++;
        interfaceVoxels[index] = true;
        interfaceVoxels[neighbor] = true;
    }

    private static ReferenceBoundaryRepresentation Classify(
        int foregroundLabelCount,
        long foregroundVoxelCount,
        long differentLabelFaces) =>
        foregroundVoxelCount == 0
            ? ReferenceBoundaryRepresentation.Empty
            : foregroundLabelCount == 1
                ? ReferenceBoundaryRepresentation.BinaryForeground
                : differentLabelFaces > 0
                    ? ReferenceBoundaryRepresentation.TouchingInstances
                    : ReferenceBoundaryRepresentation.WallSeparatedInstances;

    private static IReadOnlyList<string> BuildFindings(
        int depth,
        ReferenceBoundaryRepresentation representation,
        double foregroundFraction,
        long backgroundCount,
        long exteriorBackgroundCount)
    {
        var findings = new List<string>();
        if (depth <= 1)
            findings.Add("The reference is planar, not a 3D whole-volume label stack.");
        if (representation == ReferenceBoundaryRepresentation.TouchingInstances)
            findings.Add(
                "Different positive instance labels touch across voxel faces. The release HD95 metric merges them into one foreground mask, while legacy DSLT/Watershed can retain explicit background walls.");
        if (foregroundFraction >= 0.99)
            findings.Add(
                "At least 99% of the volume is foreground; HD95 is dominated by the volume exterior unless internal walls are encoded as background.");
        if (backgroundCount > 0 && exteriorBackgroundCount == backgroundCount)
            findings.Add("All background voxels are 6-connected to the volume exterior.");
        if (findings.Count == 0)
            findings.Add("No structural incompatibility was detected; curator acceptance and acquisition evidence are still required.");
        return findings;
    }

    private static long CountExteriorConnectedBackground(
        ReadOnlySpan<int> labels,
        int width,
        int height,
        int depth,
        int backgroundLabel,
        CancellationToken cancellationToken)
    {
        var voxelCount = labels.Length;
        var slice = checked(width * height);
        var visited = ArrayPool<bool>.Shared.Rent(voxelCount);
        var queue = ArrayPool<int>.Shared.Rent(voxelCount);
        Array.Clear(visited, 0, voxelCount);
        var head = 0;
        var tail = 0;
        try
        {
            for (var z = 0; z < depth; z++)
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        if (x != 0 && x + 1 != width &&
                            y != 0 && y + 1 != height &&
                            z != 0 && z + 1 != depth)
                            continue;
                        var index = z * slice + y * width + x;
                        if (labels[index] != backgroundLabel || visited[index]) continue;
                        visited[index] = true;
                        queue[tail++] = index;
                    }

            while (head < tail)
            {
                if ((head & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var index = queue[head++];
                var z = index / slice;
                var remainder = index - z * slice;
                var y = remainder / width;
                var x = remainder - y * width;
                if (x > 0) TryEnqueueBackground(labels, visited, queue, ref tail, index - 1, backgroundLabel);
                if (x + 1 < width) TryEnqueueBackground(labels, visited, queue, ref tail, index + 1, backgroundLabel);
                if (y > 0) TryEnqueueBackground(labels, visited, queue, ref tail, index - width, backgroundLabel);
                if (y + 1 < height) TryEnqueueBackground(labels, visited, queue, ref tail, index + width, backgroundLabel);
                if (z > 0) TryEnqueueBackground(labels, visited, queue, ref tail, index - slice, backgroundLabel);
                if (z + 1 < depth) TryEnqueueBackground(labels, visited, queue, ref tail, index + slice, backgroundLabel);
            }
            return tail;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited, clearArray: true);
        }
    }

    private static void TryEnqueueBackground(
        ReadOnlySpan<int> labels,
        bool[] visited,
        int[] queue,
        ref int tail,
        int index,
        int backgroundLabel)
    {
        if (visited[index] || labels[index] != backgroundLabel) return;
        visited[index] = true;
        queue[tail++] = index;
    }
}
