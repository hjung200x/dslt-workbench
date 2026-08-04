using System.Buffers.Binary;
using System.IO;
using Dslt.App.Services;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Validation.Prepare;
using PureHDF;

namespace Dslt.App.Tests;

internal static class PlantSegHdf5ImporterTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-plantseg-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "plantseg.h5");
            WritePlantSegFixture(source);
            var calibration = new Calibration(0.25, 0.5, 1.5, true, "um");

            VerifyEncoding(directory, source, calibration, VolumeVoxelType.UnsignedInt8, 1);
            VerifyEncoding(directory, source, calibration, VolumeVoxelType.UnsignedInt16, 2);
            VerifyEncoding(directory, source, calibration, VolumeVoxelType.Float32, 1);

            var existingInput = Path.Combine(directory, "protected-input.tif");
            var existingReference = Path.Combine(directory, "protected-reference.tif");
            File.WriteAllText(existingInput, "keep-input");
            File.WriteAllText(existingReference, "keep-reference");
            try
            {
                PlantSegHdf5Importer.Import(
                    source,
                    existingInput,
                    existingReference,
                    new PlantSegHdf5ImportOptions(calibration, VolumeVoxelType.UnsignedInt8, 1));
                throw new InvalidOperationException("Existing PlantSeg outputs were overwritten without --force.");
            }
            catch (IOException)
            {
                if (File.ReadAllText(existingInput) != "keep-input" ||
                    File.ReadAllText(existingReference) != "keep-reference")
                    throw new InvalidOperationException("Protected PlantSeg outputs changed after a rejected import.");
            }

            var invalidSource = Path.Combine(directory, "invalid-rank.h5");
            new H5File
            {
                ["raw"] = new byte[2, 3],
                ["label"] = new ushort[2, 3],
            }.Write(invalidSource);
            ExpectInvalidData(() => PlantSegHdf5Importer.Import(
                invalidSource,
                Path.Combine(directory, "invalid-input.tif"),
                Path.Combine(directory, "invalid-reference.tif"),
                new PlantSegHdf5ImportOptions(calibration, VolumeVoxelType.UnsignedInt8, 1)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyEncoding(
        string directory,
        string source,
        Calibration calibration,
        VolumeVoxelType voxelType,
        int channels)
    {
        var stem = $"{voxelType}-{channels}c";
        var input = Path.Combine(directory, $"{stem}-input.tif");
        var reference = Path.Combine(directory, $"{stem}-reference.tif");
        var result = PlantSegHdf5Importer.Import(
            source,
            input,
            reference,
            new PlantSegHdf5ImportOptions(calibration, voxelType, channels));

        if (result.Width != 3 || result.Height != 2 || result.Depth != 2 || result.Channels != channels ||
            result.VoxelType != voxelType || result.DistinctLabelCount != 8 ||
            result.SourceFileSha256.Length != 64 || result.InputFileSha256.Length != 64 ||
            result.ReferenceFileSha256.Length != 64)
            throw new InvalidOperationException("PlantSeg import evidence is incomplete or inconsistent.");

        var labels = LabelTiffCodec.Read(reference);
        var expectedLabels = new[] { 0, 1, 1, 2, 3, 3, 4, 5, 5, 6, 7, 0 };
        if (labels.Width != 3 || labels.Height != 2 || labels.Depth != 2 ||
            !labels.Labels.SequenceEqual(expectedLabels))
            throw new InvalidOperationException("PlantSeg labels were not preserved in ZYX order.");
        if (Math.Abs(labels.Calibration.SpacingX - 0.25) > 1e-9 ||
            Math.Abs(labels.Calibration.SpacingY - 0.5) > 1e-9 ||
            Math.Abs(labels.Calibration.SpacingZ - 1.5) > 1e-9)
            throw new InvalidOperationException("PlantSeg reference calibration was not preserved.");

        var volume = WpfWorkspaceFileService.ReadStack(input, CancellationToken.None);
        volume.Validate();
        if (volume.Width != 3 || volume.Height != 2 || volume.Depth != 2 || volume.Channels != channels ||
            volume.Source?.VoxelType != voxelType)
            throw new InvalidOperationException("Workbench did not preserve PlantSeg input dimensions or sample type.");
        if (Math.Abs(volume.Calibration.SpacingX - 0.25) > 1e-9 ||
            Math.Abs(volume.Calibration.SpacingY - 0.5) > 1e-9 ||
            Math.Abs(volume.Calibration.SpacingZ - 1.5) > 1e-9)
            throw new InvalidOperationException("Workbench did not preserve PlantSeg input calibration.");

        var sourceRaw = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        var bytesPerSample = voxelType switch
        {
            VolumeVoxelType.UnsignedInt8 => 1,
            VolumeVoxelType.UnsignedInt16 => 2,
            VolumeVoxelType.Float32 => 4,
            _ => throw new InvalidOperationException(),
        };
        var channelBytes = checked(sourceRaw.Length * bytesPerSample);
        if (volume.Source.ChannelPlanarRawSamples.Length != checked(channelBytes * channels))
            throw new InvalidOperationException("Workbench PlantSeg raw sample buffer length is incorrect.");
        for (var channel = 0; channel < channels; channel++)
        {
            var samples = volume.Source.ChannelPlanarRawSamples.AsSpan(channel * channelBytes, channelBytes);
            for (var index = 0; index < sourceRaw.Length; index++)
            {
                var actual = voxelType switch
                {
                    VolumeVoxelType.UnsignedInt8 => samples[index],
                    VolumeVoxelType.UnsignedInt16 => BinaryPrimitives.ReadUInt16LittleEndian(samples[(index * 2)..]),
                    VolumeVoxelType.Float32 => BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(samples[(index * 4)..])),
                    _ => throw new InvalidOperationException(),
                };
                var expected = voxelType switch
                {
                    VolumeVoxelType.UnsignedInt8 => sourceRaw[index],
                    VolumeVoxelType.UnsignedInt16 => sourceRaw[index] * 257d,
                    VolumeVoxelType.Float32 => sourceRaw[index] / 255d,
                    _ => throw new InvalidOperationException(),
                };
                if (Math.Abs(actual - expected) > 1e-6)
                    throw new InvalidOperationException($"PlantSeg {voxelType} sample conversion changed value {index}.");
            }
        }
    }

    private static void WritePlantSegFixture(string path)
    {
        var raw = new byte[2, 2, 3];
        var labels = new ushort[2, 2, 3];
        var expectedLabels = new ushort[] { 0, 1, 1, 2, 3, 3, 4, 5, 5, 6, 7, 0 };
        var index = 0;
        for (var z = 0; z < 2; z++)
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 3; x++)
        {
            raw[z, y, x] = checked((byte)index);
            labels[z, y, x] = expectedLabels[index];
            index++;
        }
        new H5File
        {
            ["raw"] = raw,
            ["label"] = labels,
        }.Write(path);
    }

    private static void ExpectInvalidData(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Invalid PlantSeg rank was accepted.");
        }
        catch (InvalidDataException)
        {
            // Expected.
        }
    }
}
