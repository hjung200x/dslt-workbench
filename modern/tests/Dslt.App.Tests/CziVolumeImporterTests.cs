using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;

namespace Dslt.App.Tests;

internal static class CziVolumeImporterTests
{
    internal static void RunConfigured()
    {
        var configured = Environment.GetEnvironmentVariable("DSLT_CZI_SMOKE");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var repeatCount = int.TryParse(Environment.GetEnvironmentVariable("DSLT_CZI_REPEAT_COUNT"), out var parsed)
            ? Math.Clamp(parsed, 1, 10)
            : 1;
        foreach (var path in configured.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RunOne(path, repeatCount);
        }
    }

    private static void RunOne(string path, int repeatCount)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Configured CZI smoke input was not found.", fullPath);
        string? expectedDecodedHash = null;
        long firstSettledPrivateBytes = 0;
        long lastSettledPrivateBytes = 0;
        for (var repetition = 0; repetition < repeatCount; repetition++)
        {
            var summary = ImportOnce(fullPath);
            if (expectedDecodedHash is not null && summary.DecodedHash != expectedDecodedHash)
                throw new InvalidOperationException("Repeated CZI imports did not produce identical decoded samples.");
            expectedDecodedHash = summary.DecodedHash;

            Console.WriteLine(
                $"CZI smoke {Path.GetFileName(fullPath)} repeat {repetition + 1}/{repeatCount}: " +
                $"{summary.Width} x {summary.Height} x {summary.Depth} x 1, " +
                $"C={summary.ChannelIndex} {summary.ChannelName}, {summary.VoxelType}, " +
                $"spacing {summary.SpacingX:R}/{summary.SpacingY:R}/{summary.SpacingZ:R} " +
                $"{summary.UnitName}, SHA-256 {summary.DecodedHash}.");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            lastSettledPrivateBytes = process.PrivateMemorySize64;
            if (repetition == 0) firstSettledPrivateBytes = lastSettledPrivateBytes;
        }

        if (repeatCount >= 3)
        {
            var growth = lastSettledPrivateBytes - firstSettledPrivateBytes;
            const long maximumPersistentGrowth = 128L * 1024 * 1024;
            if (growth > maximumPersistentGrowth)
                throw new InvalidOperationException(
                    $"Repeated CZI imports retained {growth:N0} private bytes; limit is {maximumPersistentGrowth:N0}.");
            Console.WriteLine(
                $"CZI lifecycle {Path.GetFileName(fullPath)}: settled private-byte change {growth:N0} after {repeatCount} imports.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ImportSummary ImportOnce(string fullPath)
    {
        using var importer = CziVolumeImporter.Open(fullPath);
        var descriptor = importer.Descriptor;
        if (descriptor.Scenes.Count == 0 || descriptor.ZCount <= 0)
            throw new InvalidOperationException("CZI did not expose a positive scene and Z range.");
        if (descriptor.HasMultipleTiles || descriptor.UnsupportedDimensionsMask != 0)
            throw new NotSupportedException("Configured CZI smoke input is outside the phase-one geometry contract.");
        var channel = descriptor.Channels.FirstOrDefault(item =>
                          item.IsSupported && item.Name.Equals("CW2MR", StringComparison.OrdinalIgnoreCase))
                      ?? descriptor.Channels.FirstOrDefault(item => item.IsSupported)
                      ?? throw new NotSupportedException("Configured CZI has no supported grayscale channel.");
        var volume = importer.Read(
            new CziImportSelection(descriptor.Scenes[0], descriptor.TimeStart, [channel]),
            null,
            CancellationToken.None);
        volume.Validate();
        if (volume.Source is not { Container: "CZI", ImportIdentity: not null } source)
            throw new InvalidOperationException("CZI source identity was not retained.");
        if (source.ImportIdentity.SceneIndex != descriptor.Scenes[0].Index ||
            source.ImportIdentity.TimeIndex != descriptor.TimeStart ||
            source.ImportIdentity.OriginalChannelIndices.Count != 1 ||
            source.ImportIdentity.OriginalChannelIndices[0] != channel.Index)
            throw new InvalidOperationException("CZI selection identity does not match the decoded volume.");
        var actualHash = Convert.ToHexString(SHA256.HashData(source.ChannelPlanarRawSamples)).ToLowerInvariant();
        if (actualHash != source.ImportIdentity.DecodedSamplesSha256)
            throw new InvalidOperationException("CZI decoded sample hash does not match its source identity.");
        var provenance = ProcessingProvenance.Create(
            volume,
            new OperationParameters(ProcessingOperation.Copy, ProcessingBackend.Cpu),
            new ProcessingResult(
                ProcessingBackend.Cpu,
                OutputKind.VolumeFloat32,
                volume.Width,
                volume.Height,
                volume.Depth,
                0,
                volume.Samples,
                null));
        if (provenance.InputImportIdentity != source.ImportIdentity)
            throw new InvalidOperationException("CZI import identity was not copied into processing provenance.");
        return new ImportSummary(
            volume.Width,
            volume.Height,
            volume.Depth,
            channel.Index,
            channel.Name,
            source.VoxelType,
            volume.Calibration.SpacingX,
            volume.Calibration.SpacingY,
            volume.Calibration.SpacingZ,
            volume.Calibration.UnitName,
            actualHash);
    }

    private readonly record struct ImportSummary(
        int Width,
        int Height,
        int Depth,
        int ChannelIndex,
        string ChannelName,
        VolumeVoxelType VoxelType,
        double SpacingX,
        double SpacingY,
        double SpacingZ,
        string UnitName,
        string DecodedHash);
}
