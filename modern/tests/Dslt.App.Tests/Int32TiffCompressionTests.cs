using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Tests;

internal static class Int32TiffCompressionTests
{
    public static void Run()
    {
        RunCompressedRoundTrips();
        RunLzwDictionaryGrowth();
        RunMalformedCompressedStrip();
    }

    private static void RunLzwDictionaryGrowth()
    {
        const int width = 256;
        const int height = 4;
        var values = Enumerable.Range(0, width * height)
            .Select(index => unchecked((uint)index * 2654435761u) ^ unchecked((uint)(index >> 3)))
            .ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32-lzw-growth-{Guid.NewGuid():N}.tif");
        try
        {
            WriteSingleStripTiff(path, values, width, height, compression: 5, predictor: 2);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            var expectedBytes = ToLittleEndianBytes(values);
            if (!expectedBytes.SequenceEqual(volume.Source!.ChannelPlanarRawSamples))
                throw new InvalidOperationException("LZW dictionary-growth TIFF was not decoded bit-exactly.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunCompressedRoundTrips()
    {
        var cases = new[]
        {
            new Fixture(
                "lzw-predictor", 5, 2, false, true,
                new uint[] { 100, 105, 200, 210 }),
            new Fixture(
                "deflate-signed-big-endian", 8, 2, true, false,
                new[] { -100, -90, 100, 120 }.Select(value => unchecked((uint)value)).ToArray()),
            new Fixture(
                "adobe-deflate", 32946, 1, false, true,
                new uint[] { 0, uint.MaxValue, 1, uint.MaxValue - 1 }),
            new Fixture(
                "packbits-predictor", 32773, 2, false, true,
                new uint[] { 0, 0, 0, 0 }),
        };

        foreach (var fixture in cases)
        {
            var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32-{fixture.Name}-{Guid.NewGuid():N}.tif");
            try
            {
                WriteMultiStripTiff(
                    path,
                    fixture.Values,
                    fixture.Signed,
                    fixture.LittleEndian,
                    fixture.Compression,
                    fixture.Predictor);
                var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
                volume.Validate();
                if (!ToLittleEndianBytes(fixture.Values).SequenceEqual(volume.Source!.ChannelPlanarRawSamples))
                    throw new InvalidOperationException(
                        $"{fixture.Name} Int32 TIFF decoded bytes were not bit-exact.");
                var expectedType = fixture.Signed ? VolumeVoxelType.SignedInt32 : VolumeVoxelType.UnsignedInt32;
                if (volume.Source.VoxelType != expectedType)
                    throw new InvalidOperationException($"{fixture.Name} Int32 TIFF sample format changed.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    private static void RunMalformedCompressedStrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-gray32-lzw-invalid-{Guid.NewGuid():N}.tif");
        try
        {
            WriteMultiStripTiff(
                path,
                new uint[] { 1, 2, 3, 4 },
                signed: false,
                littleEndian: true,
                compression: 5,
                predictor: 2,
                truncateSecondStrip: true);
            try
            {
                _ = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
                throw new InvalidOperationException("Truncated compressed Int32 TIFF strip was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected.
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void WriteSingleStripTiff(
        string path,
        uint[] values,
        int width,
        int height,
        ushort compression,
        ushort predictor)
    {
        if (width <= 0 || height <= 0 || values.Length != checked(width * height))
            throw new ArgumentException("The fixture dimensions and sample count must agree.", nameof(values));
        using var raw = new MemoryStream();
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = checked(row * width + x);
                var value = predictor == 2 && x > 0
                    ? unchecked(values[index] - values[index - 1])
                    : values[index];
                WriteUInt32(raw, value, littleEndian: true);
            }
        }
        var compressed = Compress(raw.ToArray(), compression);

        const ushort entryCount = 13;
        const uint ifdOffset = 8;
        const uint ifdByteCount = 2 + entryCount * 12 + 4;
        var pixelOffset = checked(ifdOffset + ifdByteCount);
        using var stream = File.Create(path);
        WriteHeader(stream, littleEndian: true, ifdOffset);
        WriteUInt16(stream, entryCount, littleEndian: true);
        WriteLongEntry(stream, 256, checked((uint)width), littleEndian: true);
        WriteLongEntry(stream, 257, checked((uint)height), littleEndian: true);
        WriteShortEntry(stream, 258, 32, littleEndian: true);
        WriteShortEntry(stream, 259, compression, littleEndian: true);
        WriteShortEntry(stream, 262, 1, littleEndian: true);
        WriteLongEntry(stream, 273, pixelOffset, littleEndian: true);
        WriteShortEntry(stream, 274, 1, littleEndian: true);
        WriteShortEntry(stream, 277, 1, littleEndian: true);
        WriteLongEntry(stream, 278, checked((uint)height), littleEndian: true);
        WriteLongEntry(stream, 279, checked((uint)compressed.Length), littleEndian: true);
        WriteShortEntry(stream, 284, 1, littleEndian: true);
        WriteShortEntry(stream, 317, predictor, littleEndian: true);
        WriteShortEntry(stream, 339, 1, littleEndian: true);
        WriteUInt32(stream, 0, littleEndian: true);
        stream.Write(compressed);
    }

    private static void WriteMultiStripTiff(
        string path,
        uint[] values,
        bool signed,
        bool littleEndian,
        ushort compression,
        ushort predictor,
        bool truncateSecondStrip = false)
    {
        if (values.Length != 4)
            throw new ArgumentException("The compressed fixture requires one 2x2 page.", nameof(values));
        if (predictor is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(predictor));

        var strips = new byte[2][];
        for (var row = 0; row < 2; row++)
        {
            var first = values[row * 2];
            var second = predictor == 2
                ? unchecked(values[row * 2 + 1] - first)
                : values[row * 2 + 1];
            using var raw = new MemoryStream();
            WriteUInt32(raw, first, littleEndian);
            WriteUInt32(raw, second, littleEndian);
            strips[row] = Compress(raw.ToArray(), compression);
        }
        if (truncateSecondStrip)
        {
            if (strips[1].Length < 2) throw new InvalidOperationException("Compressed fixture is too short to truncate.");
            strips[1] = strips[1][..^1];
        }

        const ushort entryCount = 13;
        const uint ifdOffset = 8;
        const uint ifdByteCount = 2 + entryCount * 12 + 4;
        var stripOffsetsOffset = checked(ifdOffset + ifdByteCount);
        var stripByteCountsOffset = checked(stripOffsetsOffset + 2 * sizeof(uint));
        var firstPixelOffset = checked(stripByteCountsOffset + 2 * sizeof(uint));
        var secondPixelOffset = checked(firstPixelOffset + (uint)strips[0].Length);

        using var stream = File.Create(path);
        WriteHeader(stream, littleEndian, ifdOffset);
        WriteUInt16(stream, entryCount, littleEndian);
        WriteLongEntry(stream, 256, 2, littleEndian);
        WriteLongEntry(stream, 257, 2, littleEndian);
        WriteShortEntry(stream, 258, 32, littleEndian);
        WriteShortEntry(stream, 259, compression, littleEndian);
        WriteShortEntry(stream, 262, 1, littleEndian);
        WriteArrayOffsetEntry(stream, 273, 2, stripOffsetsOffset, littleEndian);
        WriteShortEntry(stream, 274, 1, littleEndian);
        WriteShortEntry(stream, 277, 1, littleEndian);
        WriteLongEntry(stream, 278, 1, littleEndian);
        WriteArrayOffsetEntry(stream, 279, 2, stripByteCountsOffset, littleEndian);
        WriteShortEntry(stream, 284, 1, littleEndian);
        WriteShortEntry(stream, 317, predictor, littleEndian);
        WriteShortEntry(stream, 339, signed ? (ushort)2 : (ushort)1, littleEndian);
        WriteUInt32(stream, 0, littleEndian);
        WriteUInt32(stream, firstPixelOffset, littleEndian);
        WriteUInt32(stream, secondPixelOffset, littleEndian);
        WriteUInt32(stream, checked((uint)strips[0].Length), littleEndian);
        WriteUInt32(stream, checked((uint)strips[1].Length), littleEndian);
        stream.Write(strips[0]);
        stream.Write(strips[1]);
    }

    private static byte[] ToLittleEndianBytes(uint[] values)
    {
        var bytes = new byte[values.Length * sizeof(uint)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                values[index]);
        return bytes;
    }

    private static byte[] Compress(byte[] bytes, ushort compression) => compression switch
    {
        5 => EncodeTiffLzw(bytes),
        8 => CompressWithZLib(bytes),
        32946 => CompressWithRawDeflate(bytes),
        32773 => EncodePackBits(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    private static byte[] CompressWithZLib(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(bytes);
        return output.ToArray();
    }

    private static byte[] CompressWithRawDeflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(bytes);
        return output.ToArray();
    }

    private static byte[] EncodePackBits(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var repeat = 1;
            while (offset + repeat < bytes.Length && bytes[offset + repeat] == bytes[offset] && repeat < 128)
                repeat++;
            if (repeat >= 3)
            {
                output.WriteByte(unchecked((byte)(1 - repeat)));
                output.WriteByte(bytes[offset]);
                offset += repeat;
                continue;
            }

            var literalStart = offset;
            offset += repeat;
            while (offset < bytes.Length && offset - literalStart < 128)
            {
                repeat = 1;
                while (offset + repeat < bytes.Length && bytes[offset + repeat] == bytes[offset] && repeat < 128)
                    repeat++;
                if (repeat >= 3) break;
                offset += repeat;
            }
            var literalLength = offset - literalStart;
            output.WriteByte(checked((byte)(literalLength - 1)));
            output.Write(bytes.Slice(literalStart, literalLength));
        }
        return output.ToArray();
    }

    private static byte[] EncodeTiffLzw(ReadOnlySpan<byte> bytes)
    {
        const int clearCode = 256;
        const int endCode = 257;
        var dictionary = new Dictionary<(int Prefix, byte Suffix), int>();
        var codes = new List<(int Code, int Width)> { (clearCode, 9) };
        var width = 9;
        var nextCode = 258;
        if (bytes.Length > 0)
        {
            var prefix = (int)bytes[0];
            for (var index = 1; index < bytes.Length; index++)
            {
                var suffix = bytes[index];
                if (dictionary.TryGetValue((prefix, suffix), out var combined))
                {
                    prefix = combined;
                    continue;
                }
                codes.Add((prefix, width));
                if (nextCode < 4096)
                {
                    dictionary[(prefix, suffix)] = nextCode++;
                    if (nextCode == (1 << width) - 1 && width < 12) width++;
                }
                prefix = suffix;
            }
            codes.Add((prefix, width));
        }
        codes.Add((endCode, width));

        using var output = new MemoryStream();
        var currentByte = 0;
        var occupiedBits = 0;
        foreach (var (code, codeWidth) in codes)
        {
            for (var bit = codeWidth - 1; bit >= 0; bit--)
            {
                currentByte = (currentByte << 1) | ((code >> bit) & 1);
                occupiedBits++;
                if (occupiedBits != 8) continue;
                output.WriteByte(checked((byte)currentByte));
                currentByte = 0;
                occupiedBits = 0;
            }
        }
        if (occupiedBits > 0)
            output.WriteByte(checked((byte)(currentByte << (8 - occupiedBits))));
        return output.ToArray();
    }

    private static void WriteHeader(Stream stream, bool littleEndian, uint ifdOffset)
    {
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        stream.WriteByte(littleEndian ? (byte)'I' : (byte)'M');
        WriteUInt16(stream, 42, littleEndian);
        WriteUInt32(stream, ifdOffset, littleEndian);
    }

    private static void WriteLongEntry(Stream stream, ushort tag, uint value, bool littleEndian)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, 4, littleEndian);
        WriteUInt32(stream, 1, littleEndian);
        WriteUInt32(stream, value, littleEndian);
    }

    private static void WriteShortEntry(Stream stream, ushort tag, ushort value, bool littleEndian)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, 3, littleEndian);
        WriteUInt32(stream, 1, littleEndian);
        WriteUInt16(stream, value, littleEndian);
        WriteUInt16(stream, 0, littleEndian);
    }

    private static void WriteArrayOffsetEntry(
        Stream stream,
        ushort tag,
        uint count,
        uint offset,
        bool littleEndian)
    {
        WriteUInt16(stream, tag, littleEndian);
        WriteUInt16(stream, 4, littleEndian);
        WriteUInt32(stream, count, littleEndian);
        WriteUInt32(stream, offset, littleEndian);
    }

    private static void WriteUInt16(Stream stream, ushort value, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record Fixture(
        string Name,
        ushort Compression,
        ushort Predictor,
        bool Signed,
        bool LittleEndian,
        uint[] Values);
}
