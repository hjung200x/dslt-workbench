using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using PureHDF;

namespace Dslt.Validation.Prepare;

public sealed record PlantSegHdf5ImportOptions(
    Calibration Calibration,
    VolumeVoxelType VoxelType,
    int Channels);

public sealed record PlantSegHdf5ImportResult(
    string SourcePath,
    string InputVolumePath,
    string ReferenceLabelsPath,
    int Width,
    int Height,
    int Depth,
    int Channels,
    VolumeVoxelType VoxelType,
    int DistinctLabelCount,
    string SourceFileSha256,
    string InputFileSha256,
    string ReferenceFileSha256);

public static class PlantSegHdf5Importer
{
    public static PlantSegHdf5ImportResult Import(
        string sourcePath,
        string inputVolumePath,
        string referenceLabelsPath,
        PlantSegHdf5ImportOptions options,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputVolumePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceLabelsPath);
        ArgumentNullException.ThrowIfNull(options);

        var source = Path.GetFullPath(sourcePath);
        var input = Path.GetFullPath(inputVolumePath);
        var reference = Path.GetFullPath(referenceLabelsPath);
        if (!File.Exists(source)) throw new FileNotFoundException("PlantSeg HDF5 source was not found.", source);
        if (source.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            source.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            input.Equals(reference, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source, input TIFF, and reference TIFF paths must be distinct.");
        if (!overwrite && (File.Exists(input) || File.Exists(reference)))
            throw new IOException("Output already exists. Pass --force to replace both output files.");
        if (options.Channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(options), "PlantSeg conversion supports one or two output channels.");
        if (options.VoxelType is not (VolumeVoxelType.UnsignedInt8 or VolumeVoxelType.UnsignedInt16 or VolumeVoxelType.Float32))
            throw new NotSupportedException($"PlantSeg conversion does not support {options.VoxelType} input TIFF output.");

        using var hdf5 = H5File.OpenRead(source);
        var rawDataset = hdf5.Dataset("/raw");
        var labelDataset = hdf5.Dataset("/label");
        var rawDimensions = ValidateDimensions(rawDataset.Space.Dimensions, "raw");
        var labelDimensions = ValidateDimensions(labelDataset.Space.Dimensions, "label");
        if (!rawDimensions.SequenceEqual(labelDimensions))
            throw new InvalidDataException("PlantSeg raw and label datasets must have identical ZYX dimensions.");

        var raw = rawDataset.Read<byte[]>();
        var sourceLabels = labelDataset.Read<ushort[]>();
        var voxelCount = checked(rawDimensions[0] * rawDimensions[1] * rawDimensions[2]);
        if (raw.Length != voxelCount || sourceLabels.Length != voxelCount)
            throw new InvalidDataException("PlantSeg dataset element count does not match its declared dimensions.");
        var labels = new int[sourceLabels.Length];
        for (var index = 0; index < sourceLabels.Length; index++) labels[index] = sourceLabels[index];

        var depth = rawDimensions[0];
        var height = rawDimensions[1];
        var width = rawDimensions[2];
        var inputTemp = TemporarySibling(input);
        var referenceTemp = TemporarySibling(reference);
        try
        {
            ValidationInputTiffCodec.WriteFromUInt8(
                inputTemp,
                width,
                height,
                depth,
                raw,
                options.Calibration,
                options.VoxelType,
                options.Channels);
            LabelTiffCodec.Write(referenceTemp, width, height, depth, labels, options.Calibration);
            Commit(inputTemp, input, overwrite);
            Commit(referenceTemp, reference, overwrite);
        }
        finally
        {
            File.Delete(inputTemp);
            File.Delete(referenceTemp);
        }

        return new PlantSegHdf5ImportResult(
            source,
            input,
            reference,
            width,
            height,
            depth,
            options.Channels,
            options.VoxelType,
            labels.Distinct().Count(),
            Sha256File(source),
            Sha256File(input),
            Sha256File(reference));
    }

    private static int[] ValidateDimensions(ulong[] dimensions, string datasetName)
    {
        if (dimensions.Length != 3)
            throw new InvalidDataException($"PlantSeg {datasetName} dataset must be rank 3 in ZYX order.");
        var result = dimensions.Select(value => checked((int)value)).ToArray();
        if (result.Any(value => value <= 0))
            throw new InvalidDataException($"PlantSeg {datasetName} dimensions must be positive.");
        _ = checked(result[0] * result[1] * result[2]);
        return result;
    }

    private static string TemporarySibling(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    }

    private static void Commit(string temporary, string destination, bool overwrite) =>
        File.Move(temporary, destination, overwrite);

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
