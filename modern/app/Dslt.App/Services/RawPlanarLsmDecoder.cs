using System.Buffers.Binary;
using System.IO;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal sealed record RawPlanarLsmPage(
    int Width,
    int Height,
    int Channels,
    byte[] ChannelPlanarLittleEndianSamples);

internal static class RawPlanarLsmDecoder
{
    private const ushort TiffMagic = 42;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const uint CompressionNone = 1;
    private const uint CompressionLzw = 5;
    private const uint CompressionDeflate = 8;
    private const uint CompressionAdobeDeflate = 32946;
    private const uint CompressionPackBits = 32773;

    public static IReadOnlyList<RawPlanarLsmPage> Read(
        string path,
        VolumeVoxelType voxelType,
        int expectedChannels,
        CancellationToken cancellationToken)
    {
        if (voxelType is not (VolumeVoxelType.UnsignedInt8 or VolumeVoxelType.UnsignedInt16))
            throw new NotSupportedException(
                $"Packed planar LSM samples do not support {voxelType}.");
        if (expectedChannels <= 1)
            throw new ArgumentOutOfRangeException(nameof(expectedChannels));

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        ReadExactly(stream, header);
        if (!(header[0] == 'I' && header[1] == 'I'))
            throw new InvalidDataException("Packed planar LSM requires little-endian TIFF byte order.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[2..4]) != TiffMagic)
            throw new NotSupportedException("Only classic TIFF-based LSM is supported.");

        var pages = new List<RawPlanarLsmPage>();
        var visited = new HashSet<uint>();
        var ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var countBytes = new byte[sizeof(ushort)];
        var nextBytes = new byte[sizeof(uint)];
        long decodedBytes = 0;
        while (ifdOffset != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(ifdOffset)) throw new InvalidDataException("TIFF directory chain contains a cycle.");
            EnsureRange(stream, ifdOffset, sizeof(ushort));
            stream.Position = ifdOffset;
            ReadExactly(stream, countBytes);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
            if (count > 4096) throw new InvalidDataException("TIFF directory contains too many fields.");

            var entries = new Dictionary<ushort, TiffEntry>();
            var entryBytes = new byte[12];
            for (var index = 0; index < count; index++)
            {
                ReadExactly(stream, entryBytes);
                var entry = new TiffEntry(
                    BinaryPrimitives.ReadUInt16LittleEndian(entryBytes.AsSpan(0, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(entryBytes.AsSpan(2, 2)),
                    BinaryPrimitives.ReadUInt32LittleEndian(entryBytes.AsSpan(4, 4)),
                    entryBytes[8..12]);
                entries[entry.Tag] = entry;
            }
            ReadExactly(stream, nextBytes);
            ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(nextBytes);

            if ((ReadScalar(stream, entries, 254, 0) & 1) != 0) continue;
            var page = ReadPage(
                stream,
                entries,
                voxelType,
                expectedChannels,
                cancellationToken,
                decodedBytes);
            pages.Add(page);
            decodedBytes = checked(decodedBytes + page.ChannelPlanarLittleEndianSamples.LongLength);
        }

        if (pages.Count == 0)
            throw new InvalidDataException("LSM contains no full-resolution image directories.");
        var first = pages[0];
        if (pages.Any(page =>
                page.Width != first.Width || page.Height != first.Height || page.Channels != first.Channels))
            throw new InvalidDataException("All packed planar LSM directories must have the same dimensions and channels.");
        return pages;
    }

    private static RawPlanarLsmPage ReadPage(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        VolumeVoxelType voxelType,
        int expectedChannels,
        CancellationToken cancellationToken,
        long existingDecodedBytes)
    {
        var width = checked((int)ReadRequiredScalar(stream, entries, 256));
        var height = checked((int)ReadRequiredScalar(stream, entries, 257));
        if (width <= 0 || height <= 0) throw new InvalidDataException("TIFF dimensions must be positive.");
        var expectedBits = voxelType == VolumeVoxelType.UnsignedInt8 ? 8u : 16u;
        AssertUniformTag(stream, entries, 258, expectedBits, "BitsPerSample");
        AssertUniformTag(stream, entries, 339, 1, "SampleFormat", optional: true);
        var compression = ReadScalar(stream, entries, 259, CompressionNone);
        var photometric = ReadScalar(stream, entries, 262, 1);
        var samplesPerPixel = ReadScalar(stream, entries, 277, 1);
        var rowsPerStrip = ReadScalar(stream, entries, 278, checked((uint)height));
        var planarConfiguration = ReadScalar(stream, entries, 284, 1);
        var orientation = ReadScalar(stream, entries, 274, 1);
        var fillOrder = ReadScalar(stream, entries, 266, 1);
        var predictor = ReadScalar(stream, entries, 317, 1);
        if (samplesPerPixel != expectedChannels)
            throw new InvalidDataException(
                $"Packed planar LSM directory contains {samplesPerPixel} samples per pixel; expected {expectedChannels}.");
        if (compression is not (CompressionNone or CompressionLzw or CompressionDeflate or
                                CompressionAdobeDeflate or CompressionPackBits))
            throw new NotSupportedException($"Packed planar LSM compression {compression} is not supported.");
        if (photometric is not (0 or 1 or 2))
            throw new NotSupportedException($"Packed planar LSM photometric interpretation {photometric} is not supported.");
        if (planarConfiguration != 2 || orientation != 1)
            throw new NotSupportedException(
                "Packed planar LSM requires separate sample planes and top-left orientation.");
        if (fillOrder != 1)
            throw new NotSupportedException("Packed planar LSM requires MSB-to-LSB fill order.");
        if (predictor is not (1 or 2))
            throw new NotSupportedException($"Packed planar LSM predictor {predictor} is not supported.");
        if (rowsPerStrip == 0) throw new InvalidDataException("TIFF RowsPerStrip must be positive.");

        var stripOffsets = ReadUnsignedValues(stream, GetRequiredEntry(entries, 273));
        var stripByteCounts = ReadUnsignedValues(stream, GetRequiredEntry(entries, 279));
        if (stripOffsets.Length == 0 || stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException("TIFF strip offsets and byte counts do not match.");
        var stripsPerPlane = checked((height + (int)rowsPerStrip - 1) / (int)rowsPerStrip);
        if (stripOffsets.Length != checked(stripsPerPlane * expectedChannels))
            throw new InvalidDataException(
                "Packed planar LSM strip count does not match its channel and row layout.");

        var bytesPerSample = voxelType == VolumeVoxelType.UnsignedInt8 ? 1 : 2;
        var planeLength = checked(width * height * bytesPerSample);
        var outputLength = checked(planeLength * expectedChannels);
        var projectedDecodedBytes = checked(existingDecodedBytes + outputLength);
        ValidateAllocationBudget(checked(projectedDecodedBytes * 3 + outputLength));
        var output = new byte[outputLength];
        for (var channel = 0; channel < expectedChannels; channel++)
        {
            var row = 0;
            for (var channelStrip = 0; channelStrip < stripsPerPlane; channelStrip++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var strip = checked(channel * stripsPerPlane + channelStrip);
                var stripRows = Math.Min(checked((int)rowsPerStrip), height - row);
                if (stripRows <= 0)
                    throw new InvalidDataException("TIFF contains more strips than its image height permits.");
                var expectedBytes = checked(width * stripRows * bytesPerSample);
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
                var decoded = RawInt32TiffDecoder.DecodeStrip(
                    stored, compression, expectedBytes, cancellationToken);
                if (predictor == 2)
                    RawInt32TiffDecoder.UndoHorizontalPredictor(
                        decoded, width, stripRows, bytesPerSample, littleEndian: true);
                if (photometric == 0)
                    InvertMinIsWhite(decoded, bytesPerSample);

                var destination = checked(channel * planeLength + row * width * bytesPerSample);
                decoded.CopyTo(output, destination);
                row += stripRows;
            }
            if (row != height)
                throw new InvalidDataException("TIFF strips do not cover the complete sample plane.");
        }
        return new RawPlanarLsmPage(width, height, expectedChannels, output);
    }

    private static void InvertMinIsWhite(Span<byte> samples, int bytesPerSample)
    {
        if (bytesPerSample == 1)
        {
            for (var index = 0; index < samples.Length; index++) samples[index] = (byte)~samples[index];
            return;
        }
        for (var offset = 0; offset < samples.Length; offset += sizeof(ushort))
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(samples[offset..]);
            BinaryPrimitives.WriteUInt16LittleEndian(samples[offset..], (ushort)~value);
        }
    }

    private static void AssertUniformTag(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        ushort tag,
        uint expected,
        string name,
        bool optional = false)
    {
        if (!entries.TryGetValue(tag, out var entry))
        {
            if (optional) return;
            throw new InvalidDataException($"TIFF directory is missing required tag {tag}.");
        }
        var values = ReadUnsignedValues(stream, entry);
        if (values.Length == 0 || values.Any(value => value != expected))
            throw new InvalidDataException($"Packed planar LSM {name} values are inconsistent.");
    }

    private static TiffEntry GetRequiredEntry(Dictionary<ushort, TiffEntry> entries, ushort tag) =>
        entries.TryGetValue(tag, out var entry)
            ? entry
            : throw new InvalidDataException($"TIFF directory is missing required tag {tag}.");

    private static uint ReadRequiredScalar(Stream stream, Dictionary<ushort, TiffEntry> entries, ushort tag) =>
        ReadUnsignedValues(stream, GetRequiredEntry(entries, tag)) is { Length: 1 } values
            ? values[0]
            : throw new InvalidDataException($"TIFF tag {tag} must contain one value.");

    private static uint ReadScalar(
        Stream stream,
        Dictionary<ushort, TiffEntry> entries,
        ushort tag,
        uint defaultValue)
    {
        if (!entries.TryGetValue(tag, out var entry)) return defaultValue;
        var values = ReadUnsignedValues(stream, entry);
        if (values.Length != 1) throw new InvalidDataException($"TIFF tag {tag} must contain one value.");
        return values[0];
    }

    private static uint[] ReadUnsignedValues(Stream stream, TiffEntry entry)
    {
        var itemSize = entry.Type switch
        {
            TypeShort => sizeof(ushort),
            TypeLong => sizeof(uint),
            _ => throw new InvalidDataException($"TIFF tag {entry.Tag} has an unsupported integer type."),
        };
        var byteCount = checked((int)(entry.Count * itemSize));
        var bytes = ReadEntryBytes(stream, entry, byteCount);
        var values = new uint[checked((int)entry.Count)];
        for (var index = 0; index < values.Length; index++)
        {
            var item = bytes.AsSpan(index * itemSize, itemSize);
            values[index] = entry.Type == TypeShort
                ? BinaryPrimitives.ReadUInt16LittleEndian(item)
                : BinaryPrimitives.ReadUInt32LittleEndian(item);
        }
        return values;
    }

    private static byte[] ReadEntryBytes(Stream stream, TiffEntry entry, int byteCount)
    {
        if (byteCount <= 4) return entry.ValueBytes[..byteCount];
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.ValueBytes);
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

    private static void ValidateAllocationBudget(long requiredBytes)
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) return;
        var budget = available - available / 4;
        if (requiredBytes > budget)
            throw new InsufficientMemoryException(
                $"Packed planar LSM decode requires up to {requiredBytes:N0} bytes; the current 75% memory budget is {budget:N0} bytes.");
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

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] ValueBytes);
}
