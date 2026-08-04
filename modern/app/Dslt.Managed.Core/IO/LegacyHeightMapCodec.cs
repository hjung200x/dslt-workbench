using System.Buffers.Binary;

namespace Dslt.Managed.Core.IO;

public sealed record LegacyHeightMap(int Width, int Height, float[] Values)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Height-map dimensions must be positive.");
        var expected = checked(Width * Height);
        if (Values is null || Values.Length != expected)
            throw new ArgumentException($"Expected {expected} height-map values.", nameof(Values));
        if (Values.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Height-map values must be finite.", nameof(Values));
    }
}

public static class LegacyHeightMapCodec
{
    public const int HeaderFirst = 120;
    public const int HeaderSecond = 240;
    private const int HeaderBytes = 4 * sizeof(int);

    public static LegacyHeightMap Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderBytes];
        ReadExactly(stream, header, cancellationToken);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != HeaderFirst ||
            BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(int)..]) != HeaderSecond)
            throw new InvalidDataException("Legacy height-map header must contain 120 and 240.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[(2 * sizeof(int))..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[(3 * sizeof(int))..]);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Legacy height-map dimensions must be positive.");
        int count;
        long expectedLength;
        try
        {
            count = checked(width * height);
            expectedLength = checked(HeaderBytes + (long)count * sizeof(float));
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Legacy height-map dimensions exceed the supported allocation range.", error);
        }
        if (stream.Length != expectedLength)
            throw new InvalidDataException(
                $"Legacy height-map length is {stream.Length} bytes; expected exactly {expectedLength} bytes.");

        var values = new float[count];
        var buffer = new byte[16 * 1024];
        var valueOffset = 0;
        while (valueOffset < values.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valueCount = Math.Min(buffer.Length / sizeof(float), values.Length - valueOffset);
            var bytes = buffer.AsSpan(0, valueCount * sizeof(float));
            ReadExactly(stream, bytes, cancellationToken);
            for (var index = 0; index < valueCount; index++)
            {
                var bits = BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * sizeof(float))..]);
                var value = BitConverter.Int32BitsToSingle(bits);
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Legacy height-map contains a non-finite value.");
                values[valueOffset + index] = value;
            }
            valueOffset += valueCount;
        }
        return new LegacyHeightMap(width, height, values);
    }

    public static void Write(
        string path,
        LegacyHeightMap heightMap,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(heightMap);
        heightMap.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, HeaderFirst);
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(int)..], HeaderSecond);
        BinaryPrimitives.WriteInt32LittleEndian(header[(2 * sizeof(int))..], heightMap.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[(3 * sizeof(int))..], heightMap.Height);
        stream.Write(header);

        var buffer = new byte[16 * 1024];
        var valueOffset = 0;
        while (valueOffset < heightMap.Values.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valueCount = Math.Min(buffer.Length / sizeof(float), heightMap.Values.Length - valueOffset);
            var bytes = buffer.AsSpan(0, valueCount * sizeof(float));
            for (var index = 0; index < valueCount; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes[(index * sizeof(float))..],
                    BitConverter.SingleToInt32Bits(heightMap.Values[valueOffset + index]));
            }
            stream.Write(bytes);
            valueOffset += valueCount;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ReadExactly(
        Stream stream,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(destination[offset..]);
            if (read == 0) throw new EndOfStreamException("Legacy height-map file is truncated.");
            offset += read;
        }
    }
}
