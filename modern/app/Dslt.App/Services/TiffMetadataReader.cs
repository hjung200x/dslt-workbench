using System.Buffers.Binary;
using System.IO;
using System.Text;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal sealed record TiffMetadata(
    int BitsPerSample,
    int SampleFormat,
    int SamplesPerPixel,
    int Photometric,
    string? ImageDescription,
    double? XResolution,
    double? YResolution,
    bool HasLsmInfo,
    IReadOnlyList<TiffDirectoryMetadata> Directories,
    LsmMetadata? LsmInfo);

internal sealed record TiffDirectoryMetadata(int Width, int Height, bool IsReducedResolution);

internal sealed record LsmMetadata(
    int DimensionX,
    int DimensionY,
    int DimensionZ,
    int DimensionChannels,
    int DimensionTime,
    double VoxelSizeX,
    double VoxelSizeY,
    double VoxelSizeZ,
    IReadOnlyList<VolumeChannelInfo> ChannelMetadata,
    IReadOnlyList<double> TimeStampsSeconds);

internal static class TiffMetadataReader
{
    public static TiffMetadata Read(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header);
        var littleEndian = header[0] == 'I' && header[1] == 'I';
        if (!littleEndian && !(header[0] == 'M' && header[1] == 'M'))
            throw new InvalidDataException("TIFF byte order marker is invalid.");
        if (ReadUInt16(header[2..4], littleEndian) != 42)
            throw new NotSupportedException("Only classic TIFF is supported; BigTIFF is not supported.");
        var ifdOffset = ReadUInt32(header[4..8], littleEndian);
        var visited = new HashSet<uint>();
        var directories = new List<TiffDirectoryMetadata>();
        Dictionary<ushort, TiffEntry>? entries = null;
        while (ifdOffset != 0)
        {
            if (!visited.Add(ifdOffset)) throw new InvalidDataException("TIFF directory chain contains a cycle.");
            var current = ReadDirectory(stream, ifdOffset, littleEndian, out ifdOffset);
            entries ??= current;
            var width = checked((int)ReadUnsignedTag(stream, current, 256, littleEndian, 0));
            var height = checked((int)ReadUnsignedTag(stream, current, 257, littleEndian, 0));
            if (width <= 0 || height <= 0) throw new InvalidDataException("TIFF dimensions must be positive.");
            var newSubfileType = ReadUnsignedTag(stream, current, 254, littleEndian, 0);
            directories.Add(new TiffDirectoryMetadata(width, height, (newSubfileType & 1) != 0));
        }
        if (entries is null) throw new InvalidDataException("TIFF stack contains no directories.");

        var bits = checked((int)ReadUnsignedTag(stream, entries, 258, littleEndian, 8));
        var sampleFormat = checked((int)ReadUnsignedTag(stream, entries, 339, littleEndian, 1));
        var samplesPerPixel = checked((int)ReadUnsignedTag(stream, entries, 277, littleEndian, 1));
        var photometric = checked((int)ReadUnsignedTag(stream, entries, 262, littleEndian, 1));
        var description = entries.TryGetValue(270, out var descriptionEntry)
            ? ReadAscii(stream, descriptionEntry, littleEndian)
            : null;
        var xResolution = entries.TryGetValue(282, out var xEntry) ? ReadRational(stream, xEntry, littleEndian) : null;
        var yResolution = entries.TryGetValue(283, out var yEntry) ? ReadRational(stream, yEntry, littleEndian) : null;
        var lsmInfo = entries.TryGetValue(34412, out var lsmEntry)
            ? ReadLsmInfo(stream, lsmEntry, littleEndian)
            : null;
        return new TiffMetadata(
            bits,
            sampleFormat,
            samplesPerPixel,
            photometric,
            description,
            xResolution,
            yResolution,
            lsmInfo is not null,
            directories,
            lsmInfo);
    }

    private static Dictionary<ushort, TiffEntry> ReadDirectory(
        Stream stream,
        uint ifdOffset,
        bool littleEndian,
        out uint nextIfdOffset)
    {
        EnsureRange(stream, ifdOffset, 2);
        stream.Position = ifdOffset;
        Span<byte> countBytes = stackalloc byte[2];
        ReadExactly(stream, countBytes);
        var count = ReadUInt16(countBytes, littleEndian);
        if (count > 4096) throw new InvalidDataException("TIFF directory contains too many fields.");
        EnsureRange(stream, ifdOffset, checked(2L + count * 12L + 4L));
        var entries = new Dictionary<ushort, TiffEntry>();
        var entryBytes = new byte[12];
        for (var index = 0; index < count; index++)
        {
            ReadExactly(stream, entryBytes);
            var entry = new TiffEntry(
                ReadUInt16(entryBytes.AsSpan(0, 2), littleEndian),
                ReadUInt16(entryBytes.AsSpan(2, 2), littleEndian),
                ReadUInt32(entryBytes.AsSpan(4, 4), littleEndian),
                entryBytes[8..12]);
            entries[entry.Tag] = entry;
        }
        Span<byte> nextBytes = stackalloc byte[4];
        ReadExactly(stream, nextBytes);
        nextIfdOffset = ReadUInt32(nextBytes, littleEndian);
        return entries;
    }

    private static LsmMetadata ReadLsmInfo(Stream stream, TiffEntry entry, bool littleEndian)
    {
        if (!littleEndian) throw new InvalidDataException("CZ_LSMINFO requires little-endian TIFF byte order.");
        if (entry.Type != 1 || entry.Count < 64 || entry.Count > 1024 * 1024)
            throw new InvalidDataException("CZ_LSMINFO tag has an invalid byte layout.");
        var bytes = ReadEntryBytes(stream, entry, checked((int)entry.Count), littleEndian);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (magic is not (50350412u or 67127628u))
            throw new InvalidDataException("CZ_LSMINFO magic number is invalid.");
        var structureSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        if (structureSize < 64 || structureSize > bytes.Length)
            throw new InvalidDataException("CZ_LSMINFO structure size is invalid.");

        var dimensionChannels = ReadPositiveInt32(bytes, 20, "DimensionChannels");
        var channelColorsOffset = ReadOptionalLsmOffset(bytes, structureSize, 108, "OffsetChannelColors");
        var timeStampsOffset = ReadOptionalLsmOffset(bytes, structureSize, 132, "OffsetTimeStamps");
        return new LsmMetadata(
            ReadPositiveInt32(bytes, 8, "DimensionX"),
            ReadPositiveInt32(bytes, 12, "DimensionY"),
            ReadPositiveInt32(bytes, 16, "DimensionZ"),
            dimensionChannels,
            ReadPositiveInt32(bytes, 24, "DimensionTime"),
            ReadPositiveDouble(bytes, 40, "VoxelSizeX"),
            ReadPositiveDouble(bytes, 48, "VoxelSizeY"),
            ReadPositiveDouble(bytes, 56, "VoxelSizeZ"),
            channelColorsOffset == 0
                ? []
                : ReadLsmChannelMetadata(stream, channelColorsOffset, dimensionChannels),
            timeStampsOffset == 0
                ? []
                : ReadLsmTimeStamps(stream, timeStampsOffset));
    }

    private static uint ReadOptionalLsmOffset(byte[] bytes, int structureSize, int fieldOffset, string name)
    {
        if (structureSize < checked(fieldOffset + sizeof(uint))) return 0;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(fieldOffset, sizeof(uint)));
        if (value is > 0 and < 8)
            throw new InvalidDataException($"CZ_LSMINFO {name} is invalid.");
        return value;
    }

    private static IReadOnlyList<VolumeChannelInfo> ReadLsmChannelMetadata(
        Stream stream,
        uint offset,
        int expectedChannels)
    {
        const int headerSize = 24;
        const uint maximumBlockSize = 16 * 1024 * 1024;
        var header = ReadAbsoluteBytes(stream, offset, headerSize);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        var colorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var nameCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var colorsOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var namesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        if (size < headerSize || size > maximumBlockSize || size > int.MaxValue)
            throw new InvalidDataException("LSM ChannelColors block size is invalid.");
        if (colorCount != nameCount || colorCount != expectedChannels)
            throw new InvalidDataException("LSM ChannelColors count does not match DimensionChannels.");

        var block = ReadAbsoluteBytes(stream, offset, checked((int)size));
        var colorsLength = checked((int)colorCount * 4);
        if (colorsOffset < headerSize || colorsOffset > size || colorsLength > size - colorsOffset)
            throw new InvalidDataException("LSM ChannelColors color array points outside its block.");
        if (namesOffset < headerSize || namesOffset > size)
            throw new InvalidDataException("LSM ChannelColors name array points outside its block.");

        var result = new VolumeChannelInfo[checked((int)nameCount)];
        var namePosition = checked((int)namesOffset);
        var colorPosition = checked((int)colorsOffset);
        for (var channel = 0; channel < result.Length; channel++)
        {
            if (namePosition > block.Length - sizeof(uint))
                throw new InvalidDataException("LSM channel name length is truncated.");
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(
                block.AsSpan(namePosition, sizeof(uint)));
            namePosition += sizeof(uint);
            if (nameSize == 0 || nameSize > int.MaxValue || nameSize > block.Length - namePosition)
                throw new InvalidDataException("LSM channel name exceeds the ChannelColors block.");
            var nameBytes = block.AsSpan(namePosition, checked((int)nameSize));
            if (nameBytes[^1] != 0)
                throw new InvalidDataException("LSM channel name is not null terminated.");
            var name = DecodeLsmText(nameBytes[..^1]);
            if (string.IsNullOrWhiteSpace(name)) name = $"Channel {channel + 1}";
            namePosition += checked((int)nameSize);

            result[channel] = new VolumeChannelInfo(
                name,
                block[colorPosition],
                block[colorPosition + 1],
                block[colorPosition + 2],
                block[colorPosition + 3]);
            colorPosition += 4;
        }
        return result;
    }

    private static IReadOnlyList<double> ReadLsmTimeStamps(Stream stream, uint offset)
    {
        const int headerSize = 8;
        const int maximumCount = 1_000_000;
        var header = ReadAbsoluteBytes(stream, offset, headerSize);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (count < 0 || count > maximumCount || size != checked(headerSize + count * sizeof(double)))
            throw new InvalidDataException("LSM TimeStamps block size or count is invalid.");
        var block = ReadAbsoluteBytes(stream, offset, size);
        var result = new double[count];
        var previous = double.NegativeInfinity;
        for (var index = 0; index < count; index++)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(
                block.AsSpan(headerSize + index * sizeof(double), sizeof(double)));
            var value = BitConverter.Int64BitsToDouble(bits);
            if (!double.IsFinite(value) || value < 0 || value < previous)
                throw new InvalidDataException("LSM timestamps must be finite, non-negative, and nondecreasing.");
            result[index] = value;
            previous = value;
        }
        return result;
    }

    private static byte[] ReadAbsoluteBytes(Stream stream, uint offset, int byteCount)
    {
        if (byteCount < 0) throw new InvalidDataException("LSM metadata byte count is invalid.");
        EnsureRange(stream, offset, byteCount);
        var previous = stream.Position;
        stream.Position = offset;
        try
        {
            var bytes = new byte[byteCount];
            ReadExactly(stream, bytes);
            return bytes;
        }
        finally
        {
            stream.Position = previous;
        }
    }

    private static string DecodeLsmText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes).Trim();
        }
    }

    private static int ReadPositiveInt32(byte[] bytes, int offset, string name)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
        if (value <= 0) throw new InvalidDataException($"CZ_LSMINFO {name} must be positive.");
        return value;
    }

    private static double ReadPositiveDouble(byte[] bytes, int offset, string name)
    {
        var bits = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, sizeof(long)));
        var value = BitConverter.Int64BitsToDouble(bits);
        if (!(value > 0) || !double.IsFinite(value))
            throw new InvalidDataException($"CZ_LSMINFO {name} must be finite and positive.");
        return value;
    }

    private static uint ReadUnsignedTag(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        ushort tag,
        bool littleEndian,
        uint defaultValue)
    {
        if (!entries.TryGetValue(tag, out var entry)) return defaultValue;
        var size = entry.Type switch
        {
            3 => 2,
            4 => 4,
            _ => throw new InvalidDataException($"TIFF tag {tag} has an invalid type."),
        };
        var bytes = ReadEntryBytes(stream, entry, checked((int)(entry.Count * size)), littleEndian);
        return entry.Type == 3 ? ReadUInt16(bytes.AsSpan(0, 2), littleEndian) : ReadUInt32(bytes.AsSpan(0, 4), littleEndian);
    }

    private static string ReadAscii(Stream stream, TiffEntry entry, bool littleEndian)
    {
        if (entry.Type != 2) throw new InvalidDataException($"TIFF tag {entry.Tag} has an invalid ASCII type.");
        var bytes = ReadEntryBytes(stream, entry, checked((int)entry.Count), littleEndian);
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    private static double? ReadRational(Stream stream, TiffEntry entry, bool littleEndian)
    {
        if (entry.Type != 5 || entry.Count == 0) return null;
        var bytes = ReadEntryBytes(stream, entry, 8, littleEndian);
        var numerator = ReadUInt32(bytes.AsSpan(0, 4), littleEndian);
        var denominator = ReadUInt32(bytes.AsSpan(4, 4), littleEndian);
        return denominator == 0 ? null : numerator / (double)denominator;
    }

    private static byte[] ReadEntryBytes(Stream stream, TiffEntry entry, int byteCount, bool littleEndian)
    {
        if (byteCount <= 4) return entry.ValueBytes[..byteCount];
        var offset = ReadUInt32(entry.ValueBytes, littleEndian);
        EnsureRange(stream, offset, byteCount);
        var previous = stream.Position;
        stream.Position = offset;
        try
        {
            var bytes = new byte[byteCount];
            ReadExactly(stream, bytes);
            return bytes;
        }
        finally
        {
            stream.Position = previous;
        }
    }

    private static void EnsureRange(Stream stream, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
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

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] ValueBytes);
}
