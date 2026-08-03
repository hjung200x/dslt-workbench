using System.Buffers.Binary;
using System.IO;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal sealed record RawInt32TiffPage(int Width, int Height, byte[] LittleEndianSamples);

internal static class RawInt32TiffDecoder
{
    private const ushort TiffMagic = 42;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    public static IReadOnlyList<RawInt32TiffPage> Read(
        string path,
        VolumeVoxelType voxelType,
        CancellationToken cancellationToken)
    {
        if (voxelType is not (VolumeVoxelType.UnsignedInt32 or VolumeVoxelType.SignedInt32))
            throw new ArgumentOutOfRangeException(nameof(voxelType));

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header);
        var littleEndian = header[0] == 'I' && header[1] == 'I';
        if (!littleEndian && !(header[0] == 'M' && header[1] == 'M'))
            throw new InvalidDataException("TIFF byte order marker is invalid.");
        if (ReadUInt16(header[2..4], littleEndian) != TiffMagic)
            throw new NotSupportedException("Only classic TIFF is supported; BigTIFF is not supported.");

        var pages = new List<RawInt32TiffPage>();
        var visited = new HashSet<uint>();
        var ifdOffset = ReadUInt32(header[4..8], littleEndian);
        var countBytes = new byte[sizeof(ushort)];
        var nextBytes = new byte[sizeof(uint)];
        long decodedBytes = 0;
        while (ifdOffset != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(ifdOffset)) throw new InvalidDataException("TIFF directory chain contains a cycle.");
            EnsureRange(stream, ifdOffset, 2);
            stream.Position = ifdOffset;
            ReadExactly(stream, countBytes);
            var count = ReadUInt16(countBytes, littleEndian);
            if (count > 4096) throw new InvalidDataException("TIFF directory contains too many fields.");

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
            ReadExactly(stream, nextBytes);
            ifdOffset = ReadUInt32(nextBytes, littleEndian);

            var newSubfileType = ReadScalar(stream, entries, 254, littleEndian, 0);
            if ((newSubfileType & 1) != 0) continue;
            var page = ReadPage(
                stream, entries, littleEndian, voxelType, cancellationToken, decodedBytes);
            pages.Add(page);
            decodedBytes = checked(decodedBytes + page.LittleEndianSamples.LongLength);
        }

        if (pages.Count == 0) throw new InvalidDataException("TIFF stack contains no full-resolution image directories.");
        var first = pages[0];
        if (pages.Any(page => page.Width != first.Width || page.Height != first.Height))
            throw new InvalidDataException("All TIFF frames must have the same dimensions.");
        return pages;
    }

    private static RawInt32TiffPage ReadPage(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        bool littleEndian,
        VolumeVoxelType voxelType,
        CancellationToken cancellationToken,
        long existingDecodedBytes)
    {
        var width = checked((int)ReadRequiredScalar(stream, entries, 256, littleEndian));
        var height = checked((int)ReadRequiredScalar(stream, entries, 257, littleEndian));
        if (width <= 0 || height <= 0) throw new InvalidDataException("TIFF dimensions must be positive.");
        var bits = ReadScalar(stream, entries, 258, littleEndian, 1);
        var compression = ReadScalar(stream, entries, 259, littleEndian, 1);
        var photometric = ReadScalar(stream, entries, 262, littleEndian, 1);
        var samplesPerPixel = ReadScalar(stream, entries, 277, littleEndian, 1);
        var rowsPerStrip = ReadScalar(stream, entries, 278, littleEndian, checked((uint)height));
        var planarConfiguration = ReadScalar(stream, entries, 284, littleEndian, 1);
        var orientation = ReadScalar(stream, entries, 274, littleEndian, 1);
        var sampleFormat = ReadScalar(stream, entries, 339, littleEndian, 1);
        var expectedSampleFormat = voxelType == VolumeVoxelType.UnsignedInt32 ? 1u : 2u;
        if (bits != 32 || sampleFormat != expectedSampleFormat || samplesPerPixel != 1)
            throw new InvalidDataException("32-bit integer TIFF directory sample fields are inconsistent.");
        if (compression != 1)
            throw new NotSupportedException("Compressed 32-bit integer TIFF requires a codec-enabled raw path; only uncompressed strips are currently supported.");
        if (photometric is not (0 or 1))
            throw new NotSupportedException("Only grayscale TIFF photometric interpretation is supported.");
        if (planarConfiguration != 1 || orientation != 1)
            throw new NotSupportedException("Raw 32-bit TIFF requires chunky planar configuration and top-left orientation.");
        if (rowsPerStrip == 0) throw new InvalidDataException("TIFF RowsPerStrip must be positive.");

        var stripOffsets = ReadUnsignedValues(stream, GetRequiredEntry(entries, 273), littleEndian);
        var stripByteCounts = ReadUnsignedValues(stream, GetRequiredEntry(entries, 279), littleEndian);
        if (stripOffsets.Length == 0 || stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException("TIFF strip offsets and byte counts do not match.");

        var outputLength = checked(width * height * sizeof(int));
        var projectedDecodedBytes = checked(existingDecodedBytes + outputLength);
        ValidateAllocationBudget(checked(projectedDecodedBytes * 3 + outputLength));
        var output = new byte[outputLength];
        var row = 0;
        for (var strip = 0; strip < stripOffsets.Length; strip++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stripRows = Math.Min(checked((int)rowsPerStrip), height - row);
            if (stripRows <= 0) throw new InvalidDataException("TIFF contains more strips than its image height permits.");
            var expectedBytes = checked(width * stripRows * sizeof(int));
            if (stripByteCounts[strip] != expectedBytes)
                throw new InvalidDataException("TIFF strip byte count does not match its declared rows and width.");
            EnsureRange(stream, stripOffsets[strip], expectedBytes);
            var stored = new byte[expectedBytes];
            var previous = stream.Position;
            stream.Position = stripOffsets[strip];
            try { ReadExactly(stream, stored); }
            finally { stream.Position = previous; }

            var destinationOffset = checked(row * width * sizeof(int));
            for (var sample = 0; sample < expectedBytes / sizeof(int); sample++)
            {
                var source = stored.AsSpan(sample * sizeof(int), sizeof(int));
                var destination = output.AsSpan(destinationOffset + sample * sizeof(int), sizeof(int));
                if (voxelType == VolumeVoxelType.UnsignedInt32)
                {
                    var value = ReadUInt32(source, littleEndian);
                    if (photometric == 0) value = uint.MaxValue - value;
                    BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
                }
                else
                {
                    var value = ReadInt32(source, littleEndian);
                    if (photometric == 0) value = ~value;
                    BinaryPrimitives.WriteInt32LittleEndian(destination, value);
                }
            }
            row += stripRows;
        }
        if (row != height) throw new InvalidDataException("TIFF strips do not cover the complete image height.");
        return new RawInt32TiffPage(width, height, output);
    }

    private static TiffEntry GetRequiredEntry(Dictionary<ushort, TiffEntry> entries, ushort tag) =>
        entries.TryGetValue(tag, out var entry)
            ? entry
            : throw new InvalidDataException($"TIFF directory is missing required tag {tag}.");

    private static uint ReadRequiredScalar(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        ushort tag,
        bool littleEndian) =>
        ReadUnsignedValues(stream, GetRequiredEntry(entries, tag), littleEndian) is { Length: 1 } values
            ? values[0]
            : throw new InvalidDataException($"TIFF tag {tag} must contain one value.");

    private static uint ReadScalar(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        ushort tag,
        bool littleEndian,
        uint defaultValue)
    {
        if (!entries.TryGetValue(tag, out var entry)) return defaultValue;
        var values = ReadUnsignedValues(stream, entry, littleEndian);
        if (values.Length != 1) throw new InvalidDataException($"TIFF tag {tag} must contain one value.");
        return values[0];
    }

    private static uint[] ReadUnsignedValues(Stream stream, TiffEntry entry, bool littleEndian)
    {
        var itemSize = entry.Type switch
        {
            TypeShort => sizeof(ushort),
            TypeLong => sizeof(uint),
            _ => throw new InvalidDataException($"TIFF tag {entry.Tag} has an unsupported integer type."),
        };
        var byteCount = checked((int)(entry.Count * itemSize));
        var bytes = ReadEntryBytes(stream, entry, byteCount, littleEndian);
        var values = new uint[checked((int)entry.Count)];
        for (var index = 0; index < values.Length; index++)
        {
            var item = bytes.AsSpan(index * itemSize, itemSize);
            values[index] = entry.Type == TypeShort
                ? ReadUInt16(item, littleEndian)
                : ReadUInt32(item, littleEndian);
        }
        return values;
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
        finally { stream.Position = previous; }
    }

    private static void EnsureRange(Stream stream, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
            throw new InvalidDataException("TIFF field points outside the file.");
    }

    private static void ValidateAllocationBudget(long requiredBytes)
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) return;
        var budget = available - available / 4;
        if (requiredBytes > budget)
            throw new InsufficientMemoryException(
                $"32-bit TIFF decode requires up to {requiredBytes:N0} bytes for Workbench buffers; the current 75% memory budget is {budget:N0} bytes.");
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

    private static int ReadInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadInt32BigEndian(bytes);

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] ValueBytes);
}
