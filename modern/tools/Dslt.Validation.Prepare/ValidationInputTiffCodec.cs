using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Dslt.Managed.Core.Models;

namespace Dslt.Validation.Prepare;

internal static class ValidationInputTiffCodec
{
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;

    public static void WriteFromUInt8(
        string path,
        int width,
        int height,
        int depth,
        ReadOnlySpan<byte> source,
        Calibration calibration,
        VolumeVoxelType voxelType,
        int channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "TIFF dimensions must be positive.");
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Validation TIFF output supports one or two channels.");
        if (voxelType is not (VolumeVoxelType.UnsignedInt8 or VolumeVoxelType.UnsignedInt16 or VolumeVoxelType.Float32))
            throw new NotSupportedException($"Validation TIFF output does not support {voxelType}.");
        if (!double.IsFinite(calibration.SpacingX) || calibration.SpacingX <= 0 ||
            !double.IsFinite(calibration.SpacingY) || calibration.SpacingY <= 0 ||
            !double.IsFinite(calibration.SpacingZ) || calibration.SpacingZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(calibration), "Voxel spacing must be finite and positive.");
        if (string.IsNullOrWhiteSpace(calibration.UnitName) ||
            calibration.UnitName.Any(character => character is '\n' or '\r' or '\0' || character > 127))
            throw new ArgumentException("Calibration unit contains an invalid character.", nameof(calibration));

        var sliceLength = checked(width * height);
        var voxelCount = checked(sliceLength * depth);
        if (source.Length != voxelCount)
            throw new ArgumentException($"Expected {voxelCount} source samples but received {source.Length}.", nameof(source));
        var pageCount = checked(depth * channels);
        if (pageCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(depth), "Classic TIFF PageNumber supports at most 65535 pages.");

        var bitsPerSample = voxelType switch
        {
            VolumeVoxelType.UnsignedInt8 => 8,
            VolumeVoxelType.UnsignedInt16 => 16,
            VolumeVoxelType.Float32 => 32,
            _ => throw new UnreachableException(),
        };
        var sampleFormat = voxelType == VolumeVoxelType.Float32 ? (ushort)3 : (ushort)1;
        var pageByteCount = checked(sliceLength * (bitsPerSample / 8));
        var description = BuildImageJDescription(depth, channels, calibration, bitsPerSample);
        var descriptionBytes = Encoding.ASCII.GetBytes(description + "\0");
        var layouts = BuildLayouts(pageCount, pageByteCount, descriptionBytes.Length);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(checked((uint)layouts[0].IfdOffset));

        for (var page = 0; page < pageCount; page++)
        {
            var layout = layouts[page];
            stream.Position = layout.IfdOffset;
            writer.Write(checked((ushort)(page == 0 ? 18 : 17)));
            WriteLongEntry(writer, 254, page == 0 ? 0u : 2u);
            WriteLongEntry(writer, 256, checked((uint)width));
            WriteLongEntry(writer, 257, checked((uint)height));
            WriteShortEntry(writer, 258, checked((ushort)bitsPerSample));
            WriteShortEntry(writer, 259, 1);
            WriteShortEntry(writer, 262, 1);
            if (page == 0)
                WriteOffsetEntry(writer, 270, TypeAscii, checked((uint)descriptionBytes.Length), layout.DescriptionOffset);
            WriteLongEntry(writer, 273, checked((uint)layout.PixelOffset));
            WriteShortEntry(writer, 274, 1);
            WriteShortEntry(writer, 277, 1);
            WriteLongEntry(writer, 278, checked((uint)height));
            WriteLongEntry(writer, 279, checked((uint)pageByteCount));
            WriteOffsetEntry(writer, 282, TypeRational, 1, layout.XResolutionOffset);
            WriteOffsetEntry(writer, 283, TypeRational, 1, layout.YResolutionOffset);
            WriteShortEntry(writer, 284, 1);
            WriteShortEntry(writer, 296, 1);
            WritePageNumberEntry(writer, checked((ushort)page), checked((ushort)pageCount));
            WriteShortEntry(writer, 339, sampleFormat);
            writer.Write(page + 1 < pageCount ? checked((uint)layouts[page + 1].IfdOffset) : 0u);

            stream.Position = layout.XResolutionOffset;
            WriteInverseSpacing(writer, calibration.SpacingX);
            stream.Position = layout.YResolutionOffset;
            WriteInverseSpacing(writer, calibration.SpacingY);
            if (page == 0)
            {
                stream.Position = layout.DescriptionOffset;
                writer.Write(descriptionBytes);
                if ((descriptionBytes.Length & 1) != 0) writer.Write((byte)0);
            }

            stream.Position = layout.PixelOffset;
            var z = page / channels;
            var sourceSlice = source.Slice(checked(z * sliceLength), sliceLength);
            foreach (var value in sourceSlice)
            {
                switch (voxelType)
                {
                    case VolumeVoxelType.UnsignedInt8:
                        writer.Write(value);
                        break;
                    case VolumeVoxelType.UnsignedInt16:
                        writer.Write(checked((ushort)(value * 257)));
                        break;
                    case VolumeVoxelType.Float32:
                        writer.Write(value / 255f);
                        break;
                }
            }
        }
    }

    private static PageLayout[] BuildLayouts(int pageCount, int pageByteCount, int descriptionLength)
    {
        var layouts = new PageLayout[pageCount];
        long offset = 8;
        for (var page = 0; page < pageCount; page++)
        {
            var entryCount = page == 0 ? 18 : 17;
            var ifdSize = checked(2 + entryCount * 12 + 4);
            var xResolution = checked(offset + ifdSize);
            var yResolution = checked(xResolution + 8);
            var description = page == 0 ? checked(yResolution + 8) : 0;
            var descriptionStorage = page == 0 ? checked(descriptionLength + (descriptionLength & 1)) : 0;
            var pixels = checked(yResolution + 8 + descriptionStorage);
            layouts[page] = new PageLayout(offset, xResolution, yResolution, description, pixels);
            offset = checked(pixels + pageByteCount);
            if (offset > uint.MaxValue)
                throw new NotSupportedException("Classic validation TIFF output exceeds the 4 GiB offset limit.");
        }
        return layouts;
    }

    private static string BuildImageJDescription(int depth, int channels, Calibration calibration, int bits) =>
        string.Join('\n',
            "ImageJ=1.54",
            $"images={checked(depth * channels).ToString(CultureInfo.InvariantCulture)}",
            $"channels={channels.ToString(CultureInfo.InvariantCulture)}",
            $"slices={depth.ToString(CultureInfo.InvariantCulture)}",
            "frames=1",
            "hyperstack=true",
            "mode=grayscale",
            $"unit={calibration.UnitName}",
            $"spacing={calibration.SpacingZ.ToString("R", CultureInfo.InvariantCulture)}",
            $"bits={bits.ToString(CultureInfo.InvariantCulture)}");

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write(TypeLong);
        writer.Write(1u);
        writer.Write(value);
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write(TypeShort);
        writer.Write(1u);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WritePageNumberEntry(BinaryWriter writer, ushort page, ushort total)
    {
        writer.Write((ushort)297);
        writer.Write(TypeShort);
        writer.Write(2u);
        writer.Write(page);
        writer.Write(total);
    }

    private static void WriteOffsetEntry(BinaryWriter writer, ushort tag, ushort type, uint count, long offset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(checked((uint)offset));
    }

    private static void WriteInverseSpacing(BinaryWriter writer, double spacing)
    {
        const uint numerator = 1_000_000;
        var denominator = checked((uint)Math.Clamp(Math.Round(spacing * numerator), 1, uint.MaxValue));
        writer.Write(numerator);
        writer.Write(denominator);
    }

    private sealed record PageLayout(
        long IfdOffset,
        long XResolutionOffset,
        long YResolutionOffset,
        long DescriptionOffset,
        long PixelOffset);
}
