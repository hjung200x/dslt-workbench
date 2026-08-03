using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.IO;

public enum LabelTiffEncoding
{
    SignedInt16,
    SignedInt32,
}

public sealed record LabelTiffVolume(
    int Width,
    int Height,
    int Depth,
    LabelTiffEncoding Encoding,
    Calibration Calibration,
    int[] Labels);

public static class LabelTiffCodec
{
    private const ushort TiffMagic = 42;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort CompressionNone = 1;
    private const ushort PhotometricMinIsBlack = 1;
    private const ushort SampleFormatSignedInteger = 2;
    private const int MaximumDirectories = 1_000_000;

    public static LabelTiffEncoding SelectEncoding(ReadOnlySpan<int> labels)
    {
        foreach (var label in labels)
        {
            if (label is < short.MinValue or > short.MaxValue) return LabelTiffEncoding.SignedInt32;
        }
        return LabelTiffEncoding.SignedInt16;
    }

    public static void Write(
        string path,
        int width,
        int height,
        int depth,
        ReadOnlySpan<int> labels,
        Calibration calibration,
        LabelTiffEncoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateDimensions(width, height, depth);
        var voxelCount = checked(width * height * depth);
        if (labels.Length != voxelCount)
            throw new ArgumentException($"Expected {voxelCount} labels but received {labels.Length}.", nameof(labels));

        var selectedEncoding = encoding ?? SelectEncoding(labels);
        if (selectedEncoding == LabelTiffEncoding.SignedInt16)
        {
            foreach (var label in labels)
            {
                if (label is < short.MinValue or > short.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(labels), "A label is outside the signed 16-bit TIFF range.");
            }
        }

        ValidateCalibration(calibration);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var bitsPerSample = selectedEncoding == LabelTiffEncoding.SignedInt16 ? 16 : 32;
        var bytesPerSample = bitsPerSample / 8;
        var pageByteCount = checked(width * height * bytesPerSample);
        var description = BuildImageJDescription(depth, calibration, bitsPerSample);
        var descriptionBytes = Encoding.ASCII.GetBytes(description + "\0");
        var layouts = BuildLayouts(depth, pageByteCount, descriptionBytes.Length);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write(TiffMagic);
        writer.Write((uint)layouts[0].IfdOffset);

        var sliceLength = checked(width * height);
        for (var z = 0; z < depth; z++)
        {
            var layout = layouts[z];
            stream.Position = layout.IfdOffset;
            var entryCount = checked((ushort)(z == 0 ? 18 : 17));
            writer.Write(entryCount);
            WriteLongEntry(writer, 254, z == 0 ? 0u : 2u);
            WriteLongEntry(writer, 256, checked((uint)width));
            WriteLongEntry(writer, 257, checked((uint)height));
            WriteShortEntry(writer, 258, checked((ushort)bitsPerSample));
            WriteShortEntry(writer, 259, CompressionNone);
            WriteShortEntry(writer, 262, PhotometricMinIsBlack);
            if (z == 0) WriteOffsetEntry(writer, 270, TypeAscii, checked((uint)descriptionBytes.Length), layout.DescriptionOffset);
            WriteLongEntry(writer, 273, checked((uint)layout.PixelOffset));
            WriteShortEntry(writer, 274, 1);
            WriteShortEntry(writer, 277, 1);
            WriteLongEntry(writer, 278, checked((uint)height));
            WriteLongEntry(writer, 279, checked((uint)pageByteCount));
            WriteOffsetEntry(writer, 282, TypeRational, 1, layout.XResolutionOffset);
            WriteOffsetEntry(writer, 283, TypeRational, 1, layout.YResolutionOffset);
            WriteShortEntry(writer, 284, 1);
            WriteShortEntry(writer, 296, 1);
            WritePageNumberEntry(writer, checked((ushort)z), checked((ushort)depth));
            WriteShortEntry(writer, 339, SampleFormatSignedInteger);
            writer.Write(z + 1 < depth ? checked((uint)layouts[z + 1].IfdOffset) : 0u);

            stream.Position = layout.XResolutionOffset;
            WriteInverseSpacing(writer, calibration.SpacingX);
            stream.Position = layout.YResolutionOffset;
            WriteInverseSpacing(writer, calibration.SpacingY);
            if (z == 0)
            {
                stream.Position = layout.DescriptionOffset;
                writer.Write(descriptionBytes);
                if ((descriptionBytes.Length & 1) != 0) writer.Write((byte)0);
            }

            stream.Position = layout.PixelOffset;
            var start = checked(z * sliceLength);
            if (selectedEncoding == LabelTiffEncoding.SignedInt16)
            {
                for (var i = 0; i < sliceLength; i++) writer.Write(checked((short)labels[start + i]));
            }
            else
            {
                for (var i = 0; i < sliceLength; i++) writer.Write(labels[start + i]);
            }
        }
    }

    public static LabelTiffVolume Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 8) throw new InvalidDataException("TIFF header is truncated.");
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header);
        var littleEndian = header[0] == 'I' && header[1] == 'I';
        if (!littleEndian && !(header[0] == 'M' && header[1] == 'M'))
            throw new InvalidDataException("TIFF byte order marker is invalid.");
        if (ReadUInt16(header[2..4], littleEndian) != TiffMagic)
            throw new NotSupportedException("Only classic TIFF files are supported; BigTIFF is not supported.");

        var nextIfd = ReadUInt32(header[4..8], littleEndian);
        var visited = new HashSet<uint>();
        var pages = new List<int[]>();
        var width = 0;
        var height = 0;
        LabelTiffEncoding? encoding = null;
        string? description = null;
        double spacingX = 1;
        double spacingY = 1;
        var countBytes = new byte[2];
        var entryBytes = new byte[12];
        var nextBytes = new byte[4];

        while (nextIfd != 0)
        {
            if (pages.Count >= MaximumDirectories) throw new InvalidDataException("TIFF contains too many directories.");
            if (!visited.Add(nextIfd)) throw new InvalidDataException("TIFF directory chain contains a cycle.");
            EnsureRange(stream, nextIfd, 2);
            stream.Position = nextIfd;
            ReadExactly(stream, countBytes);
            var entryCount = ReadUInt16(countBytes, littleEndian);
            var entries = new Dictionary<ushort, IfdEntry>();
            for (var i = 0; i < entryCount; i++)
            {
                ReadExactly(stream, entryBytes);
                var entry = new IfdEntry(
                    ReadUInt16(entryBytes[0..2], littleEndian),
                    ReadUInt16(entryBytes[2..4], littleEndian),
                    ReadUInt32(entryBytes[4..8], littleEndian),
                    entryBytes[8..12].ToArray());
                entries[entry.Tag] = entry;
            }
            ReadExactly(stream, nextBytes);
            nextIfd = ReadUInt32(nextBytes, littleEndian);

            var pageWidth = checked((int)ReadSingleUnsigned(stream, entries, 256, littleEndian));
            var pageHeight = checked((int)ReadSingleUnsigned(stream, entries, 257, littleEndian));
            if (pageWidth <= 0 || pageHeight <= 0) throw new InvalidDataException("TIFF dimensions must be positive.");
            if (pages.Count == 0)
            {
                width = pageWidth;
                height = pageHeight;
            }
            else if (pageWidth != width || pageHeight != height)
            {
                throw new InvalidDataException("All label TIFF directories must have identical dimensions.");
            }

            var bits = checked((int)ReadSingleUnsigned(stream, entries, 258, littleEndian));
            var sampleFormat = entries.ContainsKey(339)
                ? ReadSingleUnsigned(stream, entries, 339, littleEndian)
                : 1u;
            if (sampleFormat != SampleFormatSignedInteger || bits is not (16 or 32))
                throw new NotSupportedException("Label TIFF must use signed 16-bit or signed 32-bit samples.");
            var pageEncoding = bits == 16 ? LabelTiffEncoding.SignedInt16 : LabelTiffEncoding.SignedInt32;
            if (encoding is not null && encoding != pageEncoding)
                throw new InvalidDataException("All label TIFF directories must use the same sample type.");
            encoding = pageEncoding;

            if (ReadSingleUnsigned(stream, entries, 259, littleEndian, CompressionNone) != CompressionNone)
                throw new NotSupportedException("Compressed label TIFF is not supported by the compatibility codec.");
            if (ReadSingleUnsigned(stream, entries, 277, littleEndian, 1) != 1)
                throw new NotSupportedException("Label TIFF must contain one sample per pixel.");
            if (ReadSingleUnsigned(stream, entries, 262, littleEndian, PhotometricMinIsBlack) != PhotometricMinIsBlack)
                throw new NotSupportedException("Label TIFF must use Photometric MinIsBlack.");
            if (ReadSingleUnsigned(stream, entries, 274, littleEndian, 1) != 1)
                throw new NotSupportedException("Label TIFF must use top-left orientation.");
            if (ReadSingleUnsigned(stream, entries, 284, littleEndian, 1) != 1)
                throw new NotSupportedException("Planar label TIFF is not supported.");

            var stripOffsets = ReadUnsignedValues(stream, Require(entries, 273), littleEndian);
            var stripByteCounts = ReadUnsignedValues(stream, Require(entries, 279), littleEndian);
            if (stripOffsets.Length != stripByteCounts.Length || stripOffsets.Length == 0)
                throw new InvalidDataException("TIFF strip offsets and byte counts are inconsistent.");
            var expectedBytes = checked(pageWidth * pageHeight * (bits / 8));
            var pageBytes = new byte[expectedBytes];
            var destinationOffset = 0;
            for (var strip = 0; strip < stripOffsets.Length; strip++)
            {
                var byteCount = checked((int)stripByteCounts[strip]);
                if (destinationOffset + byteCount > pageBytes.Length)
                    throw new InvalidDataException("TIFF strips exceed the expected page size.");
                EnsureRange(stream, stripOffsets[strip], byteCount);
                stream.Position = stripOffsets[strip];
                ReadExactly(stream, pageBytes.AsSpan(destinationOffset, byteCount));
                destinationOffset += byteCount;
            }
            if (destinationOffset != expectedBytes) throw new InvalidDataException("TIFF strips do not fill the expected page size.");

            var pageLabels = new int[checked(pageWidth * pageHeight)];
            for (var i = 0; i < pageLabels.Length; i++)
            {
                var sample = pageBytes.AsSpan(i * (bits / 8), bits / 8);
                pageLabels[i] = bits == 16
                    ? ReadInt16(sample, littleEndian)
                    : ReadInt32(sample, littleEndian);
            }
            pages.Add(pageLabels);

            if (pages.Count == 1)
            {
                if (entries.TryGetValue(270, out var descriptionEntry))
                    description = ReadAscii(stream, descriptionEntry, littleEndian);
                if (entries.TryGetValue(282, out var xResolution))
                    spacingX = InvertPositive(ReadRational(stream, xResolution, littleEndian));
                if (entries.TryGetValue(283, out var yResolution))
                    spacingY = InvertPositive(ReadRational(stream, yResolution, littleEndian));
            }
        }

        if (pages.Count == 0 || encoding is null) throw new InvalidDataException("TIFF contains no image directories.");
        var spacingZ = ParseImageJDouble(description, "spacing") ?? 1;
        var unit = ParseImageJValue(description, "unit") ?? "pixel";
        var calibrated = spacingX != 1 || spacingY != 1 || spacingZ != 1 || !unit.Equals("pixel", StringComparison.OrdinalIgnoreCase);
        var labels = new int[checked(width * height * pages.Count)];
        var pageLength = checked(width * height);
        for (var z = 0; z < pages.Count; z++) pages[z].CopyTo(labels, z * pageLength);
        return new LabelTiffVolume(
            width,
            height,
            pages.Count,
            encoding.Value,
            new Calibration(spacingX, spacingY, spacingZ, calibrated, unit),
            labels);
    }

    private static PageLayout[] BuildLayouts(int depth, int pageByteCount, int descriptionLength)
    {
        var layouts = new PageLayout[depth];
        long offset = 8;
        for (var z = 0; z < depth; z++)
        {
            var entryCount = z == 0 ? 18 : 17;
            var ifdSize = checked(2 + entryCount * 12 + 4);
            var xResolution = checked(offset + ifdSize);
            var yResolution = checked(xResolution + 8);
            var description = z == 0 ? checked(yResolution + 8) : 0;
            var descriptionStorage = z == 0 ? checked(descriptionLength + (descriptionLength & 1)) : 0;
            var pixels = checked(yResolution + 8 + descriptionStorage);
            layouts[z] = new PageLayout(offset, xResolution, yResolution, description, pixels);
            offset = checked(pixels + pageByteCount);
            if (offset > uint.MaxValue) throw new NotSupportedException("Classic TIFF output exceeds the 4 GiB offset limit; BigTIFF is required.");
        }
        return layouts;
    }

    private static string BuildImageJDescription(int depth, Calibration calibration, int bits) =>
        string.Join('\n',
            "ImageJ=1.54",
            $"images={depth.ToString(CultureInfo.InvariantCulture)}",
            "channels=1",
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

    private static void ValidateDimensions(int width, int height, int depth)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Label dimensions must be positive.");
        _ = checked(width * height * depth);
        if (depth > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(depth), "Classic TIFF PageNumber supports at most 65535 slices.");
    }

    private static void ValidateCalibration(Calibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (!double.IsFinite(calibration.SpacingX) || calibration.SpacingX <= 0 ||
            !double.IsFinite(calibration.SpacingY) || calibration.SpacingY <= 0 ||
            !double.IsFinite(calibration.SpacingZ) || calibration.SpacingZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(calibration), "Voxel spacing must be finite and positive.");
        if (string.IsNullOrWhiteSpace(calibration.UnitName) ||
            calibration.UnitName.Any(character => character is '\n' or '\r' or '\0' || character > 127))
            throw new ArgumentException("Calibration unit contains an invalid character.", nameof(calibration));
    }

    private static IfdEntry Require(Dictionary<ushort, IfdEntry> entries, ushort tag) =>
        entries.TryGetValue(tag, out var entry)
            ? entry
            : throw new InvalidDataException($"Required TIFF tag {tag} is missing.");

    private static uint ReadSingleUnsigned(
        Stream stream,
        Dictionary<ushort, IfdEntry> entries,
        ushort tag,
        bool littleEndian,
        uint? defaultValue = null)
    {
        if (!entries.TryGetValue(tag, out var entry))
        {
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new InvalidDataException($"Required TIFF tag {tag} is missing.");
        }
        var values = ReadUnsignedValues(stream, entry, littleEndian);
        if (values.Length == 0) throw new InvalidDataException($"TIFF tag {tag} has no values.");
        return values[0];
    }

    private static uint[] ReadUnsignedValues(Stream stream, IfdEntry entry, bool littleEndian)
    {
        var elementSize = entry.Type switch
        {
            TypeShort => 2,
            TypeLong => 4,
            _ => throw new NotSupportedException($"TIFF tag {entry.Tag} uses unsupported field type {entry.Type}."),
        };
        var byteCount = checked((int)(entry.Count * elementSize));
        var bytes = ReadEntryBytes(stream, entry, byteCount, littleEndian);
        var values = new uint[checked((int)entry.Count)];
        for (var i = 0; i < values.Length; i++)
        {
            var valueBytes = bytes.AsSpan(i * elementSize, elementSize);
            values[i] = entry.Type == TypeShort
                ? ReadUInt16(valueBytes, littleEndian)
                : ReadUInt32(valueBytes, littleEndian);
        }
        return values;
    }

    private static string ReadAscii(Stream stream, IfdEntry entry, bool littleEndian)
    {
        if (entry.Type != TypeAscii) throw new InvalidDataException($"TIFF tag {entry.Tag} is not ASCII.");
        var byteCount = checked((int)entry.Count);
        var bytes = ReadEntryBytes(stream, entry, byteCount, littleEndian);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, length);
    }

    private static double ReadRational(Stream stream, IfdEntry entry, bool littleEndian)
    {
        if (entry.Type != TypeRational || entry.Count == 0) throw new InvalidDataException($"TIFF tag {entry.Tag} is not rational.");
        var bytes = ReadEntryBytes(stream, entry, 8, littleEndian);
        var numerator = ReadUInt32(bytes.AsSpan(0, 4), littleEndian);
        var denominator = ReadUInt32(bytes.AsSpan(4, 4), littleEndian);
        return denominator == 0 ? 0 : numerator / (double)denominator;
    }

    private static byte[] ReadEntryBytes(Stream stream, IfdEntry entry, int byteCount, bool littleEndian)
    {
        if (byteCount <= 4) return entry.ValueBytes[..byteCount];
        var offset = ReadUInt32(entry.ValueBytes, littleEndian);
        EnsureRange(stream, offset, byteCount);
        var previous = stream.Position;
        try
        {
            stream.Position = offset;
            var bytes = new byte[byteCount];
            ReadExactly(stream, bytes);
            return bytes;
        }
        finally
        {
            stream.Position = previous;
        }
    }

    private static string? ParseImageJValue(string? description, string key)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        foreach (var line in description.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static double? ParseImageJDouble(string? description, string key) =>
        double.TryParse(ParseImageJValue(description, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value > 0
            ? value
            : null;

    private static double InvertPositive(double value) => double.IsFinite(value) && value > 0 ? 1 / value : 1;

    private static void EnsureRange(Stream stream, long offset, long count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length || count > stream.Length - offset)
            throw new InvalidDataException("TIFF field points outside the file.");
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0) throw new EndOfStreamException("TIFF data is truncated.");
            read += count;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static short ReadInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadInt16BigEndian(bytes);

    private static int ReadInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadInt32BigEndian(bytes);

    private sealed record PageLayout(
        long IfdOffset,
        long XResolutionOffset,
        long YResolutionOffset,
        long DescriptionOffset,
        long PixelOffset);

    private sealed record IfdEntry(ushort Tag, ushort Type, uint Count, byte[] ValueBytes);
}
