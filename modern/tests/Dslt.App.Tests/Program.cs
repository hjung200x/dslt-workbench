using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Tests;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-stack-{Guid.NewGuid():N}.tif");
        try
        {
            var encoder = new TiffBitmapEncoder();
            for (var z = 0; z < 3; z++)
            {
                var pixels = Enumerable.Repeat((byte)(64 + z * 64), 12).ToArray();
                var frame = BitmapFrame.Create(BitmapSource.Create(
                    4, 3, 96, 96, PixelFormats.Gray8, null, pixels, 4));
                encoder.Frames.Add(frame);
            }
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Width != 4 || volume.Height != 3 || volume.Depth != 3)
                throw new InvalidOperationException("TIFF stack dimensions were not preserved.");
            var sliceLength = volume.Width * volume.Height;
            if (!(volume.Samples[0] < volume.Samples[sliceLength] &&
                  volume.Samples[sliceLength] < volume.Samples[sliceLength * 2]))
                throw new InvalidOperationException("TIFF frame intensity order was not preserved.");
            if (volume.Source?.VoxelType != VolumeVoxelType.UnsignedInt8)
                throw new InvalidOperationException("Gray8 source voxel type was not preserved.");
            if (volume.Source.ChannelPlanarRawSamples[0] != 64 || volume.Source.ChannelPlanarRawSamples[^1] != 192)
                throw new InvalidOperationException("Gray8 decoded samples were not preserved.");

            RunGray16RoundTripTest();
            RunGray32FloatRoundTripTest();
            RunImageJHyperStackTest();
            Console.WriteLine("DSLT WPF TIFF type, metadata, and hyperstack tests passed.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunGray16RoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray16-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new ushort[] { 0, 1, 1024, ushort.MaxValue };
            var bytes = new byte[values.Length * sizeof(ushort)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var encoder = new TiffBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                2, 2, 96, 96, PixelFormats.Gray16, null, bytes, 2 * sizeof(ushort))));
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Source?.VoxelType != VolumeVoxelType.UnsignedInt16)
                throw new InvalidOperationException("Gray16 source voxel type was not preserved.");
            if (!bytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Gray16 decoded bytes were not bit-exact.");
            if (volume.Samples[^1] != 1f)
                throw new InvalidOperationException("Gray16 processing samples were not channel-normalized.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RunGray32FloatRoundTripTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32f-{Guid.NewGuid():N}.tif");
        try
        {
            var values = new[] { -2f, -0.5f, 0.25f, 1f };
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            WriteGray32FloatTiff(path, 2, 2, values);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Source?.VoxelType != VolumeVoxelType.Float32)
                throw new InvalidOperationException($"Gray32Float source voxel type was not preserved: {volume.Source?.VoxelType}.");
            if (!bytes.SequenceEqual(volume.Source.ChannelPlanarRawSamples))
                throw new InvalidOperationException("Gray32Float decoded bytes were not bit-exact.");
            if (volume.Samples[0] != -1f || volume.Samples[^1] != 0.5f)
                throw new InvalidOperationException("Gray32Float processing samples were not maximum-absolute normalized.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteGray32FloatTiff(string path, int width, int height, float[] values)
    {
        const ushort entryCount = 14;
        const uint ifdOffset = 8;
        const uint xResolutionOffset = ifdOffset + 2 + entryCount * 12 + 4;
        const uint yResolutionOffset = xResolutionOffset + 8;
        const uint pixelOffset = yResolutionOffset + 8;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifdOffset);
        writer.Write(entryCount);
        WriteLongEntry(writer, 256, checked((uint)width));
        WriteLongEntry(writer, 257, checked((uint)height));
        WriteShortEntry(writer, 258, 32);
        WriteShortEntry(writer, 259, 1);
        WriteShortEntry(writer, 262, 1);
        WriteLongEntry(writer, 273, pixelOffset);
        WriteShortEntry(writer, 277, 1);
        WriteLongEntry(writer, 278, checked((uint)height));
        WriteLongEntry(writer, 279, checked((uint)(values.Length * sizeof(float))));
        WriteOffsetEntry(writer, 282, 5, xResolutionOffset);
        WriteOffsetEntry(writer, 283, 5, yResolutionOffset);
        WriteShortEntry(writer, 284, 1);
        WriteShortEntry(writer, 296, 1);
        WriteShortEntry(writer, 339, 3);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        foreach (var value in values) writer.Write(value);
    }

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write((ushort)4);
        writer.Write(1u);
        writer.Write(value);
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write((ushort)3);
        writer.Write(1u);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WriteOffsetEntry(BinaryWriter writer, ushort tag, ushort type, uint offset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(1u);
        writer.Write(offset);
    }

    private static void RunImageJHyperStackTest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-imagej-{Guid.NewGuid():N}.tif");
        try
        {
            const string description = "ImageJ=1.54\nimages=4\nchannels=2\nslices=2\nframes=1\nhyperstack=true\nunit=um\npixel_width=0.25\npixel_height=0.5\nspacing=1.5\n";
            var pageValues = new byte[] { 10, 100, 20, 200 };
            var encoder = new TiffBitmapEncoder();
            for (var page = 0; page < pageValues.Length; page++)
            {
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new[] { pageValues[page] }, 1);
                if (page == 0)
                {
                    var metadata = new BitmapMetadata("tiff");
                    metadata.SetQuery("/ifd/{ushort=270}", description);
                    encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
                }
                else
                {
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                }
            }
            using (var stream = File.Create(path)) encoder.Save(stream);

            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Channels != 2 || volume.Depth != 2)
                throw new InvalidOperationException("ImageJ C/Z dimensions were not reconstructed.");
            var expectedPlanar = new byte[] { 10, 20, 100, 200 };
            if (!expectedPlanar.SequenceEqual(volume.Source!.ChannelPlanarRawSamples))
                throw new InvalidOperationException("ImageJ XYCZ page order was not converted to channel-planar storage.");
            if (volume.Samples[0] != 0.5f || volume.Samples[1] != 1f ||
                volume.Samples[2] != 0.5f || volume.Samples[3] != 1f)
                throw new InvalidOperationException("ImageJ channels were not normalized independently.");
            if (volume.Calibration.SpacingX != 0.25 || volume.Calibration.SpacingY != 0.5 ||
                volume.Calibration.SpacingZ != 1.5 || volume.Calibration.UnitName != "um")
                throw new InvalidOperationException("ImageJ voxel calibration was not preserved.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
