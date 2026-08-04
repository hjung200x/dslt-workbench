using System.Text;

namespace Dslt.Managed.Core.IO;

/// <summary>Writes one uncompressed classic-TIFF page with IEEE Float32 grayscale samples.</summary>
public static class Float32TiffCodec
{
    private const ushort TiffMagic = 42;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort CompressionNone = 1;
    private const ushort PhotometricMinIsBlack = 1;
    private const ushort SampleFormatIeeeFloatingPoint = 3;
    private const ushort EntryCount = 12;
    private const uint IfdOffset = 8;
    private const uint PixelOffset = IfdOffset + sizeof(ushort) + EntryCount * 12 + sizeof(uint);

    public static void WriteSingle(
        string path,
        int width,
        int height,
        ReadOnlySpan<float> pixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "TIFF dimensions must be positive.");
        var pixelCount = checked(width * height);
        if (pixels.Length != pixelCount)
            throw new ArgumentException($"Expected {pixelCount} Float32 pixels.", nameof(pixels));
        foreach (var value in pixels)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("Float32 TIFF pixels must be finite.", nameof(pixels));
        }
        var byteCount = checked((uint)(pixelCount * sizeof(float)));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write(TiffMagic);
        writer.Write(IfdOffset);
        writer.Write(EntryCount);
        WriteLongEntry(writer, 256, checked((uint)width));
        WriteLongEntry(writer, 257, checked((uint)height));
        WriteShortEntry(writer, 258, 32);
        WriteShortEntry(writer, 259, CompressionNone);
        WriteShortEntry(writer, 262, PhotometricMinIsBlack);
        WriteLongEntry(writer, 273, PixelOffset);
        WriteShortEntry(writer, 274, 1);
        WriteShortEntry(writer, 277, 1);
        WriteLongEntry(writer, 278, checked((uint)height));
        WriteLongEntry(writer, 279, byteCount);
        WriteShortEntry(writer, 284, 1);
        WriteShortEntry(writer, 339, SampleFormatIeeeFloatingPoint);
        writer.Write(0u);

        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            writer.Write(pixels[index]);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void WriteShortEntry(BinaryWriter writer, ushort tag, ushort value)
    {
        writer.Write(tag);
        writer.Write(TypeShort);
        writer.Write(1u);
        writer.Write(value);
        writer.Write((ushort)0);
    }

    private static void WriteLongEntry(BinaryWriter writer, ushort tag, uint value)
    {
        writer.Write(tag);
        writer.Write(TypeLong);
        writer.Write(1u);
        writer.Write(value);
    }
}
