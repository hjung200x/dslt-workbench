using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.Managed.Core.Models;
using Microsoft.Win32;

namespace Dslt.App.Services;

public sealed class WpfWorkspaceFileService : IWorkspaceFileService
{
    public async Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open confocal stack",
            Filter = "TIFF / LSM stack|*.tif;*.tiff;*.lsm|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return null;
        return await Task.Run(() => ReadStack(dialog.FileName, cancellationToken), cancellationToken);
    }

    public string? ChooseExportBasePath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export result package",
            Filter = "DSLT result package|*.dslt-result",
            AddExtension = false,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true
            ? Path.ChangeExtension(dialog.FileName, null)
            : null;
    }

    internal static VolumeData ReadStack(string path, CancellationToken cancellationToken)
    {
        var metadata = TiffMetadataReader.Read(path);
        if (metadata.SamplesPerPixel != 1)
            throw new NotSupportedException("Only grayscale TIFF directories with one sample per pixel are supported.");
        if (metadata.Photometric is not (0 or 1))
            throw new NotSupportedException("Only grayscale TIFF photometric interpretation is supported.");

        var voxelType = ResolveVoxelType(metadata);
        var rawInt32Pages = voxelType is VolumeVoxelType.UnsignedInt32 or VolumeVoxelType.SignedInt32
            ? RawInt32TiffDecoder.Read(path, voxelType, cancellationToken)
            : null;
        using var stream = rawInt32Pages is null
            ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
        var decoder = stream is null
            ? null
            : new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var fullDirectoryIndices = metadata.Directories
            .Select((directory, index) => (directory, index))
            .Where(item => !item.directory.IsReducedResolution)
            .Select(item => item.index)
            .ToArray();
        var decoderFrameIndices = decoder is null
            ? []
            : decoder.Frames.Count == metadata.Directories.Count
                ? fullDirectoryIndices
                : decoder.Frames.Count == fullDirectoryIndices.Length
                    ? Enumerable.Range(0, decoder.Frames.Count).ToArray()
                    : throw new InvalidDataException(
                        $"TIFF metadata contains {metadata.Directories.Count} directories ({fullDirectoryIndices.Length} full resolution), but the Windows codec exposed {decoder.Frames.Count} frames.");
        if (rawInt32Pages is not null && rawInt32Pages.Count != fullDirectoryIndices.Length)
            throw new InvalidDataException("Raw TIFF page count does not match its full-resolution directories.");
        var frameCount = rawInt32Pages?.Count ?? decoderFrameIndices.Length;
        if (frameCount == 0) throw new InvalidDataException("TIFF stack contains no frames.");
        var firstFrameIndex = decoderFrameIndices.Length == 0 ? 0 : decoderFrameIndices[0];
        var width = rawInt32Pages?[0].Width ?? decoder!.Frames[firstFrameIndex].PixelWidth;
        var height = rawInt32Pages?[0].Height ?? decoder!.Frames[firstFrameIndex].PixelHeight;
        var imageJ = ParseImageJDescription(metadata.ImageDescription);
        var lsm = metadata.LsmInfo;
        if (lsm is not null && (lsm.DimensionX != width || lsm.DimensionY != height))
            throw new InvalidDataException(
                $"CZ_LSMINFO declares {lsm.DimensionX} x {lsm.DimensionY}, but the full-resolution TIFF frame is {width} x {height}.");
        var channels = lsm?.DimensionChannels ?? ReadPositiveImageJInteger(imageJ, "channels", 1);
        var timeFrames = lsm?.DimensionTime ?? ReadPositiveImageJInteger(imageJ, "frames", 1);
        if (timeFrames != 1)
            throw new NotSupportedException("Time-series TIFF/LSM stacks are not supported; select or export one time point.");
        var declaredImages = lsm is null
            ? ReadPositiveImageJInteger(imageJ, "images", frameCount)
            : checked(lsm.DimensionChannels * lsm.DimensionZ * lsm.DimensionTime);
        if (declaredImages != frameCount)
            throw new InvalidDataException(
                $"ImageJ metadata declares {declaredImages} images, but TIFF contains {frameCount} directories.");
        var depth = lsm?.DimensionZ ?? ReadPositiveImageJInteger(imageJ, "slices", frameCount / channels);
        if (checked(channels * depth) != frameCount)
            throw new InvalidDataException(
                $"ImageJ metadata declares {channels} channels and {depth} slices, but TIFF contains {frameCount} directories.");

        var sliceLength = checked(width * height);
        var voxelCount = checked(sliceLength * depth);
        var sampleCount = checked(voxelCount * channels);
        var bytesPerSample = BytesPerSample(voxelType);
        ValidateAllocationBudget(
            checked((long)sampleCount * sizeof(float) +
                    (long)sampleCount * bytesPerSample +
                    (long)sliceLength * bytesPerSample));
        var samples = new float[sampleCount];
        var rawSamples = new byte[checked(sampleCount * bytesPerSample)];
        var pageBytes = new byte[checked(sliceLength * bytesPerSample)];

        for (var page = 0; page < frameCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rawInt32Pages is not null)
            {
                var rawPage = rawInt32Pages[page];
                if (rawPage.Width != width || rawPage.Height != height)
                    throw new InvalidDataException("All TIFF frames must have the same dimensions.");
                Buffer.BlockCopy(rawPage.LittleEndianSamples, 0, pageBytes, 0, pageBytes.Length);
            }
            else
            {
                var frame = decoder!.Frames[decoderFrameIndices[page]];
                if (frame.PixelWidth != width || frame.PixelHeight != height)
                    throw new InvalidDataException("All TIFF frames must have the same dimensions.");
                var source = PrepareFrame(frame, voxelType);
                source.CopyPixels(pageBytes, checked(width * bytesPerSample), 0);
            }

            var channel = page % channels;
            var z = page / channels;
            var destinationSample = checked(channel * voxelCount + z * sliceLength);
            var destinationByte = checked(destinationSample * bytesPerSample);
            Buffer.BlockCopy(pageBytes, 0, rawSamples, destinationByte, pageBytes.Length);
            ConvertSamples(pageBytes, samples.AsSpan(destinationSample, sliceLength), voxelType);
        }

        NormalizeChannels(samples, voxelCount, channels);
        var calibration = ResolveCalibration(metadata, imageJ);
        var container = metadata.HasLsmInfo || Path.GetExtension(path).Equals(".lsm", StringComparison.OrdinalIgnoreCase)
            ? "LSM"
            : "TIFF";
        return new VolumeData(
            width,
            height,
            depth,
            channels,
            0,
            calibration,
            samples,
            new VolumeSourceInfo(voxelType, container, metadata.ImageDescription, rawSamples));
    }

    private static BitmapSource PrepareFrame(BitmapSource frame, VolumeVoxelType voxelType)
    {
        var expected = voxelType switch
        {
            VolumeVoxelType.UnsignedInt8 => PixelFormats.Gray8,
            VolumeVoxelType.UnsignedInt16 or VolumeVoxelType.SignedInt16 => PixelFormats.Gray16,
            VolumeVoxelType.Float32 => PixelFormats.Gray32Float,
            _ => throw new NotSupportedException($"TIFF sample type {voxelType} is not supported by the WIC pixel path."),
        };
        if (frame.Format == expected) return frame;
        if (voxelType == VolumeVoxelType.SignedInt16)
            throw new NotSupportedException("The installed TIFF codec did not expose signed 16-bit pixels without conversion.");
        return new FormatConvertedBitmap(frame, expected, null, 0);
    }

    private static VolumeVoxelType ResolveVoxelType(TiffMetadata metadata) => (metadata.BitsPerSample, metadata.SampleFormat) switch
    {
        (8, 1) => VolumeVoxelType.UnsignedInt8,
        (16, 1) => VolumeVoxelType.UnsignedInt16,
        (16, 2) => VolumeVoxelType.SignedInt16,
        (32, 3) => VolumeVoxelType.Float32,
        (32, 1) => VolumeVoxelType.UnsignedInt32,
        (32, 2) => VolumeVoxelType.SignedInt32,
        _ => throw new NotSupportedException(
            $"Unsupported TIFF sample type: BitsPerSample={metadata.BitsPerSample}, SampleFormat={metadata.SampleFormat}."),
    };

    private static int BytesPerSample(VolumeVoxelType voxelType) => voxelType switch
    {
        VolumeVoxelType.UnsignedInt8 => 1,
        VolumeVoxelType.UnsignedInt16 or VolumeVoxelType.SignedInt16 => 2,
        VolumeVoxelType.UnsignedInt32 or VolumeVoxelType.SignedInt32 or VolumeVoxelType.Float32 => 4,
        _ => throw new NotSupportedException($"TIFF sample type {voxelType} is not supported."),
    };

    private static void ConvertSamples(ReadOnlySpan<byte> source, Span<float> destination, VolumeVoxelType voxelType)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = voxelType switch
            {
                VolumeVoxelType.UnsignedInt8 => source[i],
                VolumeVoxelType.UnsignedInt16 => BinaryPrimitives.ReadUInt16LittleEndian(source[(i * 2)..]),
                VolumeVoxelType.SignedInt16 => BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]),
                VolumeVoxelType.UnsignedInt32 => BinaryPrimitives.ReadUInt32LittleEndian(source[(i * 4)..]),
                VolumeVoxelType.SignedInt32 => BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]),
                VolumeVoxelType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..])),
                _ => throw new NotSupportedException($"TIFF sample type {voxelType} is not supported."),
            };
            if (!float.IsFinite(destination[i]))
                throw new InvalidDataException("TIFF contains a non-finite floating-point sample.");
        }
    }

    private static void NormalizeChannels(float[] samples, int voxelCount, int channels)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var values = samples.AsSpan(channel * voxelCount, voxelCount);
            var maximumAbsolute = 0f;
            foreach (var value in values) maximumAbsolute = Math.Max(maximumAbsolute, Math.Abs(value));
            if (maximumAbsolute == 0) continue;
            for (var i = 0; i < values.Length; i++) values[i] /= maximumAbsolute;
        }
    }

    private static void ValidateAllocationBudget(long requiredBytes)
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) return;
        var budget = available - available / 4;
        if (requiredBytes > budget)
            throw new InsufficientMemoryException(
                $"TIFF decode requires at least {requiredBytes:N0} bytes for Workbench buffers; the current 75% memory budget is {budget:N0} bytes.");
    }

    private static Calibration ResolveCalibration(TiffMetadata metadata, Dictionary<string, string> imageJ)
    {
        if (metadata.LsmInfo is { } lsm)
        {
            const double metersToMicrometers = 1_000_000;
            return new Calibration(
                lsm.VoxelSizeX * metersToMicrometers,
                lsm.VoxelSizeY * metersToMicrometers,
                lsm.VoxelSizeZ * metersToMicrometers,
                true,
                "um");
        }
        var spacingX = ReadPositiveImageJDouble(imageJ, "pixel_width") ?? InvertResolution(metadata.XResolution);
        var spacingY = ReadPositiveImageJDouble(imageJ, "pixel_height") ?? InvertResolution(metadata.YResolution);
        var spacingZ = ReadPositiveImageJDouble(imageJ, "spacing") ?? 1;
        var unit = imageJ.TryGetValue("unit", out var value) && !string.IsNullOrWhiteSpace(value) ? value : "pixel";
        var calibrated = spacingX != 1 || spacingY != 1 || spacingZ != 1 || !unit.Equals("pixel", StringComparison.OrdinalIgnoreCase);
        return new Calibration(spacingX, spacingY, spacingZ, calibrated, unit);
    }

    private static double InvertResolution(double? resolution) =>
        resolution is > 0 && double.IsFinite(resolution.Value) ? 1 / resolution.Value : 1;

    private static Dictionary<string, string> ParseImageJDescription(string? description)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(description)) return values;
        foreach (var line in description.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static int ReadPositiveImageJInteger(Dictionary<string, string> values, string key, int defaultValue) =>
        values.TryGetValue(key, out var text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : defaultValue;

    private static double? ReadPositiveImageJDouble(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        value > 0 && double.IsFinite(value)
            ? value
            : null;

}
