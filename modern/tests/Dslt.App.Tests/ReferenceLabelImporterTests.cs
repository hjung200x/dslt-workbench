using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Validation.Prepare;

namespace Dslt.App.Tests;

internal static class ReferenceLabelImporterTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-reference-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "compressed-reference.tif");
            WriteReference(source);
            var calibration = new Calibration(0.25, 0.5, 1.5, true, "um");
            var preserve = Path.Combine(directory, "preserved.tif");
            var preserved = ReferenceLabelImporter.Import(
                source,
                preserve,
                new ReferenceLabelImportOptions(calibration, false, 0, 0, 1));
            var decoded = LabelTiffCodec.Read(preserve);
            if (decoded.Width != 3 || decoded.Height != 2 || decoded.Depth != 2)
                throw new InvalidOperationException("Normalized reference dimensions were not preserved.");
            if (!decoded.Labels.SequenceEqual(new[] { 0, 1, 2, 3, 4, 255, 255, 4, 3, 2, 1, 0 }))
                throw new InvalidOperationException("Normalized reference labels were not preserved.");
            if (preserved.DistinctLabelCount != 6 || preserved.SourceFileSha256.Length != 64 ||
                preserved.OutputFileSha256.Length != 64)
                throw new InvalidOperationException("Reference import evidence is incomplete.");
            if (Math.Abs(decoded.Calibration.SpacingZ - 1.5) > 1e-9 || decoded.Calibration.UnitName != "um")
                throw new InvalidOperationException("Normalized reference calibration was not preserved.");

            var binary = Path.Combine(directory, "binary.tif");
            ReferenceLabelImporter.Import(
                source,
                binary,
                new ReferenceLabelImportOptions(calibration, true, 0, -1, 7));
            var binaryDecoded = LabelTiffCodec.Read(binary);
            if (!binaryDecoded.Labels.SequenceEqual(new[] { -1, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, -1 }))
                throw new InvalidOperationException("Binary reference mapping was not applied.");

            var colorSource = Path.Combine(directory, "color-binary.png");
            WriteColorBinaryReference(colorSource);
            var colorBinary = Path.Combine(directory, "color-binary.tif");
            ReferenceLabelImporter.Import(
                colorSource,
                colorBinary,
                new ReferenceLabelImportOptions(calibration, true, 0, 0, 1));
            var colorDecoded = LabelTiffCodec.Read(colorBinary);
            if (!colorDecoded.Labels.SequenceEqual(new[] { 0, 1, 1, 0 }))
                throw new InvalidOperationException("Color binary reference was not normalized.");

            try
            {
                ReferenceLabelImporter.Import(
                    source,
                    binary,
                    new ReferenceLabelImportOptions(calibration, true, 0, 0, 1));
                throw new InvalidOperationException("Existing normalized output was overwritten without explicit permission.");
            }
            catch (IOException)
            {
                // Expected: preparation must not destroy existing evidence by default.
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteReference(string path)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        AddFrame(encoder, new byte[] { 0, 1, 2, 3, 4, 255 });
        AddFrame(encoder, new byte[] { 255, 4, 3, 2, 1, 0 });
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void AddFrame(TiffBitmapEncoder encoder, byte[] pixels)
    {
        var source = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Gray8, null, pixels, 3);
        encoder.Frames.Add(BitmapFrame.Create(source));
    }

    private static void WriteColorBinaryReference(string path)
    {
        var pixels = new byte[]
        {
            0, 0, 0, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
            0, 0, 0, 255,
        };
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
