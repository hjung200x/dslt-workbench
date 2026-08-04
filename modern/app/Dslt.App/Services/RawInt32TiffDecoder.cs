using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal sealed record RawInt32TiffPage(int Width, int Height, byte[] LittleEndianSamples);

internal static class RawInt32TiffDecoder
{
    private const ushort TiffMagic = 42;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const uint CompressionNone = 1;
    private const uint CompressionLzw = 5;
    private const uint CompressionDeflate = 8;
    private const uint CompressionAdobeDeflate = 32946;
    private const uint CompressionPackBits = 32773;

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
        var fillOrder = ReadScalar(stream, entries, 266, littleEndian, 1);
        var predictor = ReadScalar(stream, entries, 317, littleEndian, 1);
        var sampleFormat = ReadScalar(stream, entries, 339, littleEndian, 1);
        var expectedSampleFormat = voxelType == VolumeVoxelType.UnsignedInt32 ? 1u : 2u;
        if (bits != 32 || sampleFormat != expectedSampleFormat || samplesPerPixel != 1)
            throw new InvalidDataException("32-bit integer TIFF directory sample fields are inconsistent.");
        if (compression is not (CompressionNone or CompressionLzw or CompressionDeflate or
                                CompressionAdobeDeflate or CompressionPackBits))
            throw new NotSupportedException($"TIFF compression {compression} is not supported for 32-bit integer samples.");
        if (photometric is not (0 or 1))
            throw new NotSupportedException("Only grayscale TIFF photometric interpretation is supported.");
        if (planarConfiguration != 1 || orientation != 1)
            throw new NotSupportedException("Raw 32-bit TIFF requires chunky planar configuration and top-left orientation.");
        if (fillOrder != 1)
            throw new NotSupportedException("Raw 32-bit TIFF requires MSB-to-LSB fill order.");
        if (predictor is not (1 or 2))
            throw new NotSupportedException($"TIFF predictor {predictor} is not supported for 32-bit integer samples.");
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
            if (compression == CompressionNone && stripByteCounts[strip] != expectedBytes)
                throw new InvalidDataException("TIFF strip byte count does not match its declared rows and width.");
            if (stripByteCounts[strip] == 0 || stripByteCounts[strip] > int.MaxValue)
                throw new InvalidDataException("TIFF strip byte count is invalid.");
            var storedLength = checked((int)stripByteCounts[strip]);
            ValidateAllocationBudget(checked(projectedDecodedBytes * 3 + outputLength + storedLength + expectedBytes));
            EnsureRange(stream, stripOffsets[strip], storedLength);
            var stored = new byte[storedLength];
            var previous = stream.Position;
            stream.Position = stripOffsets[strip];
            try { ReadExactly(stream, stored); }
            finally { stream.Position = previous; }
            var decoded = DecodeStrip(stored, compression, expectedBytes, cancellationToken);
            if (predictor == 2)
                UndoHorizontalPredictor(decoded, width, stripRows, littleEndian);

            var destinationOffset = checked(row * width * sizeof(int));
            for (var sample = 0; sample < expectedBytes / sizeof(int); sample++)
            {
                var source = decoded.AsSpan(sample * sizeof(int), sizeof(int));
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

    private static byte[] DecodeStrip(
        byte[] stored,
        uint compression,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return compression switch
        {
            CompressionNone => stored,
            CompressionLzw => DecodeLzw(stored, expectedBytes, cancellationToken),
            CompressionDeflate or CompressionAdobeDeflate =>
                DecodeDeflate(stored, expectedBytes, cancellationToken),
            CompressionPackBits => DecodePackBits(stored, expectedBytes, cancellationToken),
            _ => throw new NotSupportedException($"TIFF compression {compression} is not supported."),
        };
    }

    private static byte[] DecodeDeflate(
        byte[] stored,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var input = new MemoryStream(stored, writable: false);
            using var decoder = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
            return ReadDecodedExactly(decoder, expectedBytes, cancellationToken);
        }
        catch (Exception zlibError) when (zlibError is InvalidDataException or EndOfStreamException)
        {
            try
            {
                using var input = new MemoryStream(stored, writable: false);
                using var decoder = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
                return ReadDecodedExactly(decoder, expectedBytes, cancellationToken);
            }
            catch (Exception rawError) when (rawError is InvalidDataException or EndOfStreamException)
            {
                throw new InvalidDataException(
                    "TIFF Deflate strip is malformed or does not expand to its declared size.",
                    new AggregateException(zlibError, rawError));
            }
        }
    }

    private static byte[] ReadDecodedExactly(
        Stream decoder,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        var result = new byte[expectedBytes];
        var offset = 0;
        while (offset < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = decoder.Read(result, offset, result.Length - offset);
            if (count == 0)
                throw new EndOfStreamException("TIFF compressed strip ended before its declared rows were decoded.");
            offset += count;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.ReadByte() != -1)
            throw new InvalidDataException("TIFF compressed strip expands beyond its declared rows and width.");
        return result;
    }

    private static byte[] DecodePackBits(
        ReadOnlySpan<byte> stored,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        var result = new byte[expectedBytes];
        var source = 0;
        var destination = 0;
        while (destination < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source >= stored.Length)
                throw new InvalidDataException("TIFF PackBits strip ended before its declared rows were decoded.");
            var control = unchecked((sbyte)stored[source++]);
            if (control >= 0)
            {
                var count = control + 1;
                if (count > stored.Length - source || count > result.Length - destination)
                    throw new InvalidDataException("TIFF PackBits literal run exceeds the strip bounds.");
                stored.Slice(source, count).CopyTo(result.AsSpan(destination));
                source += count;
                destination += count;
            }
            else if (control != -128)
            {
                var count = 1 - control;
                if (source >= stored.Length || count > result.Length - destination)
                    throw new InvalidDataException("TIFF PackBits repeated run exceeds the strip bounds.");
                result.AsSpan(destination, count).Fill(stored[source++]);
                destination += count;
            }
        }

        while (source < stored.Length && stored[source] == 0x80) source++;
        if (source != stored.Length)
            throw new InvalidDataException("TIFF PackBits strip contains trailing encoded data.");
        return result;
    }

    private static byte[] DecodeLzw(
        ReadOnlySpan<byte> stored,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        const int clearCode = 256;
        const int endOfInformationCode = 257;
        const int firstDictionaryCode = 258;
        const int maximumCodeCount = 4096;

        var prefix = new short[maximumCodeCount];
        var suffix = new byte[maximumCodeCount];
        var expansion = new byte[maximumCodeCount];
        var output = new byte[expectedBytes];
        var bitReader = new MsbBitReader(stored);
        var outputOffset = 0;
        var codeSize = 9;
        var nextCode = firstDictionaryCode;
        var previousCode = -1;
        var sawEnd = false;

        while (bitReader.TryRead(codeSize, out var code))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (code == clearCode)
            {
                codeSize = 9;
                nextCode = firstDictionaryCode;
                previousCode = -1;
                continue;
            }
            if (code == endOfInformationCode)
            {
                sawEnd = true;
                break;
            }
            if (code > nextCode || (code == nextCode && previousCode < 0))
                throw new InvalidDataException("TIFF LZW strip contains an invalid dictionary code.");

            int expansionLength;
            byte firstByte;
            if (code == nextCode)
            {
                expansionLength = ExpandLzwCode(previousCode, nextCode, prefix, suffix, expansion);
                firstByte = expansion[expansionLength - 1];
                Array.Reverse(expansion, 0, expansionLength);
                if (expansionLength >= expansion.Length)
                    throw new InvalidDataException("TIFF LZW dictionary expansion is too large.");
                expansion[expansionLength++] = firstByte;
            }
            else
            {
                expansionLength = ExpandLzwCode(code, nextCode, prefix, suffix, expansion);
                firstByte = expansion[expansionLength - 1];
                Array.Reverse(expansion, 0, expansionLength);
            }

            if (expansionLength > output.Length - outputOffset)
                throw new InvalidDataException("TIFF LZW strip expands beyond its declared rows and width.");
            expansion.AsSpan(0, expansionLength).CopyTo(output.AsSpan(outputOffset));
            outputOffset += expansionLength;

            if (previousCode >= 0 && nextCode < maximumCodeCount)
            {
                prefix[nextCode] = checked((short)previousCode);
                suffix[nextCode] = firstByte;
                nextCode++;
                // TIFF LZW EarlyChange=1: the decoder table trails the encoder by one entry.
                if (nextCode == (1 << codeSize) - 2 && codeSize < 12) codeSize++;
            }
            previousCode = code;
        }

        if (!sawEnd)
            throw new InvalidDataException("TIFF LZW strip is missing the end-of-information code.");
        if (outputOffset != output.Length)
            throw new InvalidDataException("TIFF LZW strip ended before its declared rows were decoded.");
        return output;
    }

    private static int ExpandLzwCode(
        int code,
        int nextCode,
        short[] prefix,
        byte[] suffix,
        byte[] expansion)
    {
        var length = 0;
        var guard = 0;
        while (code >= 256)
        {
            if (code < 258 || code >= nextCode || guard++ >= expansion.Length)
                throw new InvalidDataException("TIFF LZW strip contains a cyclic or invalid dictionary entry.");
            expansion[length++] = suffix[code];
            code = prefix[code];
        }
        if (code < 0 || code > byte.MaxValue || length >= expansion.Length)
            throw new InvalidDataException("TIFF LZW literal code is invalid.");
        expansion[length++] = checked((byte)code);
        return length;
    }

    private static void UndoHorizontalPredictor(
        Span<byte> decoded,
        int width,
        int rows,
        bool littleEndian)
    {
        var rowBytes = checked(width * sizeof(uint));
        for (var row = 0; row < rows; row++)
        {
            var currentRow = decoded.Slice(row * rowBytes, rowBytes);
            var previous = ReadUInt32(currentRow[..sizeof(uint)], littleEndian);
            for (var x = 1; x < width; x++)
            {
                var sample = currentRow.Slice(x * sizeof(uint), sizeof(uint));
                previous = unchecked(previous + ReadUInt32(sample, littleEndian));
                if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(sample, previous);
                else BinaryPrimitives.WriteUInt32BigEndian(sample, previous);
            }
        }
    }

    private ref struct MsbBitReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private long _bitOffset;

        public bool TryRead(int bitCount, out int value)
        {
            if (bitCount <= 0 || bitCount > 12) throw new ArgumentOutOfRangeException(nameof(bitCount));
            var totalBits = checked((long)_bytes.Length * 8);
            if (_bitOffset > totalBits - bitCount)
            {
                value = 0;
                return false;
            }
            value = 0;
            for (var index = 0; index < bitCount; index++)
            {
                var absoluteBit = _bitOffset + index;
                var current = _bytes[checked((int)(absoluteBit / 8))];
                value = (value << 1) | ((current >> (7 - checked((int)(absoluteBit % 8)))) & 1);
            }
            _bitOffset += bitCount;
            return true;
        }
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
