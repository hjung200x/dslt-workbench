using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Dslt.App.Services;

internal sealed record TiffMetadata(
    int BitsPerSample,
    int SampleFormat,
    int SamplesPerPixel,
    int Photometric,
    string? ImageDescription,
    double? XResolution,
    double? YResolution,
    bool HasLsmInfo);

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
        EnsureRange(stream, ifdOffset, 2);
        stream.Position = ifdOffset;
        Span<byte> countBytes = stackalloc byte[2];
        ReadExactly(stream, countBytes);
        var count = ReadUInt16(countBytes, littleEndian);
        if (count > 4096) throw new InvalidDataException("TIFF first directory contains too many fields.");
        var entries = new Dictionary<ushort, TiffEntry>();
        var entryBytes = new byte[12];
        for (var i = 0; i < count; i++)
        {
            ReadExactly(stream, entryBytes);
            var entry = new TiffEntry(
                ReadUInt16(entryBytes.AsSpan(0, 2), littleEndian),
                ReadUInt16(entryBytes.AsSpan(2, 2), littleEndian),
                ReadUInt32(entryBytes.AsSpan(4, 4), littleEndian),
                entryBytes[8..12]);
            entries[entry.Tag] = entry;
        }

        var bits = checked((int)ReadUnsignedTag(stream, entries, 258, littleEndian, 8));
        var sampleFormat = checked((int)ReadUnsignedTag(stream, entries, 339, littleEndian, 1));
        var samplesPerPixel = checked((int)ReadUnsignedTag(stream, entries, 277, littleEndian, 1));
        var photometric = checked((int)ReadUnsignedTag(stream, entries, 262, littleEndian, 1));
        var description = entries.TryGetValue(270, out var descriptionEntry)
            ? ReadAscii(stream, descriptionEntry, littleEndian)
            : null;
        var xResolution = entries.TryGetValue(282, out var xEntry) ? ReadRational(stream, xEntry, littleEndian) : null;
        var yResolution = entries.TryGetValue(283, out var yEntry) ? ReadRational(stream, yEntry, littleEndian) : null;
        return new TiffMetadata(
            bits,
            sampleFormat,
            samplesPerPixel,
            photometric,
            description,
            xResolution,
            yResolution,
            entries.ContainsKey(34412));
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
