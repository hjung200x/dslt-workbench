using System.IO;
using System.Security.Cryptography;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;

namespace Dslt.Validation.Prepare;

public sealed record VolumeInspection(
    string InputPath,
    long FileSize,
    string FileMd5,
    string FileSha256,
    string CanonicalAxes,
    int Width,
    int Height,
    int Depth,
    int Channels,
    int SelectedChannel,
    VolumeVoxelType VoxelType,
    string Container,
    Calibration Calibration,
    string DecodedSha256,
    IReadOnlyList<VolumeChannelInfo> ChannelMetadata,
    IReadOnlyList<double> TimeStampsSeconds);

public static class VolumeInspector
{
    public static VolumeInspection Inspect(string inputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Volume input was not found.", fullPath);

        var volume = WpfWorkspaceFileService.ReadStack(fullPath, cancellationToken);
        volume.Validate();
        var source = volume.Source ?? throw new InvalidDataException("The Workbench loader did not retain source evidence.");
        cancellationToken.ThrowIfCancellationRequested();

        return new VolumeInspection(
            fullPath,
            new FileInfo(fullPath).Length,
            ComputeFileHash(fullPath, MD5.Create(), cancellationToken),
            ComputeFileHash(fullPath, SHA256.Create(), cancellationToken),
            "CZYX",
            volume.Width,
            volume.Height,
            volume.Depth,
            volume.Channels,
            volume.SelectedChannel,
            source.VoxelType,
            source.Container,
            volume.Calibration,
            Convert.ToHexString(SHA256.HashData(source.ChannelPlanarRawSamples)).ToLowerInvariant(),
            source.ChannelMetadata ?? [],
            source.TimeStampsSeconds ?? []);
    }

    private static string ComputeFileHash(
        string path,
        HashAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        using (algorithm)
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                algorithm.TransformBlock(buffer, 0, count, null, 0);
            }
            algorithm.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(algorithm.Hash!).ToLowerInvariant();
        }
    }
}
