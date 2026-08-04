using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;

namespace Dslt.Validation.Prepare;

public sealed record ReferenceLabelImportOptions(
    Calibration Calibration,
    bool Binary,
    int SourceBackgroundLabel,
    int OutputBackgroundLabel,
    int ForegroundLabel);

public sealed record ReferenceLabelImportResult(
    string SourcePath,
    string OutputPath,
    int Width,
    int Height,
    int Depth,
    LabelTiffEncoding Encoding,
    int DistinctLabelCount,
    string SourceFileSha256,
    string OutputFileSha256);

public static class ReferenceLabelImporter
{
    public static ReferenceLabelImportResult Import(
        string sourcePath,
        string outputPath,
        ReferenceLabelImportOptions options,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var source = Path.GetFullPath(sourcePath);
        var output = Path.GetFullPath(outputPath);
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and output paths must differ.", nameof(outputPath));
        if (!File.Exists(source)) throw new FileNotFoundException("Reference label image was not found.", source);
        if (File.Exists(output) && !overwrite)
            throw new IOException($"Output already exists: {output}. Pass --force to replace it.");
        if (options.Binary && options.OutputBackgroundLabel == options.ForegroundLabel)
            throw new ArgumentException("Binary background and foreground labels must differ.", nameof(options));

        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("Reference label image contains no frames.");

        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0) throw new InvalidDataException("Reference label dimensions must be positive.");
        var sliceLength = checked(width * height);
        var labels = new int[checked(sliceLength * decoder.Frames.Count)];

        for (var z = 0; z < decoder.Frames.Count; z++)
        {
            var frame = decoder.Frames[z];
            if (frame.PixelWidth != width || frame.PixelHeight != height)
                throw new InvalidDataException("All reference label frames must have identical dimensions.");
            DecodeFrame(frame, labels.AsSpan(z * sliceLength, sliceLength), options.Binary);
        }

        if (options.Binary)
        {
            for (var index = 0; index < labels.Length; index++)
            {
                labels[index] = labels[index] == options.SourceBackgroundLabel
                    ? options.OutputBackgroundLabel
                    : options.ForegroundLabel;
            }
        }

        var encoding = LabelTiffCodec.SelectEncoding(labels);
        LabelTiffCodec.Write(output, width, height, decoder.Frames.Count, labels, options.Calibration, encoding);
        return new ReferenceLabelImportResult(
            source,
            output,
            width,
            height,
            decoder.Frames.Count,
            encoding,
            labels.Distinct().Count(),
            Sha256File(source),
            Sha256File(output));
    }

    private static void DecodeFrame(BitmapSource frame, Span<int> destination, bool binary)
    {
        if (frame.Format.BitsPerPixel <= 8)
        {
            var converted = ConvertFormat(frame, PixelFormats.Gray8);
            var pixels = new byte[destination.Length];
            converted.CopyPixels(pixels, converted.PixelWidth, 0);
            for (var index = 0; index < pixels.Length; index++) destination[index] = pixels[index];
            return;
        }

        if (frame.Format.BitsPerPixel <= 16 && IsGray(frame.Format))
        {
            var converted = ConvertFormat(frame, PixelFormats.Gray16);
            var pixels = new ushort[destination.Length];
            converted.CopyPixels(pixels, checked(converted.PixelWidth * sizeof(ushort)), 0);
            for (var index = 0; index < pixels.Length; index++) destination[index] = pixels[index];
            return;
        }

        if (binary)
        {
            var converted = ConvertFormat(frame, PixelFormats.Gray8);
            var pixels = new byte[destination.Length];
            converted.CopyPixels(pixels, converted.PixelWidth, 0);
            for (var index = 0; index < pixels.Length; index++) destination[index] = pixels[index];
            return;
        }

        throw new NotSupportedException(
            $"Preserved reference labels must be 1- to 16-bit grayscale or indexed images; received {frame.Format} ({frame.Format.BitsPerPixel} bpp). Use --binary only when every non-background value is foreground.");
    }

    private static bool IsGray(PixelFormat format) =>
        format == PixelFormats.Gray16 || format == PixelFormats.Gray8 || format == PixelFormats.BlackWhite ||
        format == PixelFormats.Gray2 || format == PixelFormats.Gray4;

    private static BitmapSource ConvertFormat(BitmapSource source, PixelFormat target)
    {
        if (source.Format == target) return source;
        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = target;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
