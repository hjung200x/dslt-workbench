using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using PureHDF;
using PureHDF.Selections;

namespace Dslt.Validation.Prepare;

public sealed record PlantSegCrop(int X, int Y, int Z, int Width, int Height, int Depth);

public sealed record PlantSegHdf5ImportOptions(
    Calibration Calibration,
    VolumeVoxelType VoxelType,
    int Channels,
    PlantSegCrop? Crop = null);

public sealed record PlantSegHdf5ImportResult(
    string SourcePath,
    string InputVolumePath,
    string ReferenceLabelsPath,
    int Width,
    int Height,
    int Depth,
    int OriginX,
    int OriginY,
    int OriginZ,
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

        var sourceDepth = rawDimensions[0];
        var sourceHeight = rawDimensions[1];
        var sourceWidth = rawDimensions[2];
        var crop = options.Crop ?? new PlantSegCrop(0, 0, 0, sourceWidth, sourceHeight, sourceDepth);
        ValidateCrop(crop, sourceWidth, sourceHeight, sourceDepth);
        var fileSelection = options.Crop is null
            ? null
            : new HyperslabSelection(
                rank: 3,
                starts: [(ulong)crop.Z, (ulong)crop.Y, (ulong)crop.X],
                blocks: [(ulong)crop.Depth, (ulong)crop.Height, (ulong)crop.Width]);
        var raw = rawDataset.Read<byte[]>(fileSelection: fileSelection);
        var sourceLabels = labelDataset.Read<ushort[]>(fileSelection: fileSelection);
        var voxelCount = checked(crop.Width * crop.Height * crop.Depth);
        if (raw.Length != voxelCount || sourceLabels.Length != voxelCount)
            throw new InvalidDataException("PlantSeg dataset selection count does not match the requested crop dimensions.");
        var labels = new int[sourceLabels.Length];
        for (var index = 0; index < sourceLabels.Length; index++) labels[index] = sourceLabels[index];

        var depth = crop.Depth;
        var height = crop.Height;
        var width = crop.Width;
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
            crop.X,
            crop.Y,
            crop.Z,
            options.Channels,
            options.VoxelType,
            labels.Distinct().Count(),
            Sha256File(source),
            Sha256File(input),
            Sha256File(reference));
    }

    private static void ValidateCrop(PlantSegCrop crop, int width, int height, int depth)
    {
        if (crop.X < 0 || crop.Y < 0 || crop.Z < 0 ||
            crop.Width <= 0 || crop.Height <= 0 || crop.Depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(crop), "PlantSeg crop origin must be non-negative and size positive.");
        if (checked((long)crop.X + crop.Width) > width ||
            checked((long)crop.Y + crop.Height) > height ||
            checked((long)crop.Z + crop.Depth) > depth)
            throw new ArgumentOutOfRangeException(nameof(crop), "PlantSeg crop exceeds the source volume.");
        _ = checked(crop.Width * crop.Height * crop.Depth);
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
