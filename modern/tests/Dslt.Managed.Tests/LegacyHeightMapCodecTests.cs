using System.Buffers.Binary;
using Dslt.Managed.Core.IO;

internal static class LegacyHeightMapCodecTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-hmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "surface.hmp");
            var expected = new LegacyHeightMap(3, 2, [-0.25F, 0F, 1F, 2.5F, 3F, 4.25F]);
            LegacyHeightMapCodec.Write(path, expected);
            var bytes = File.ReadAllBytes(path);
            var actual = LegacyHeightMapCodec.Read(path);
            Assert(BinaryPrimitives.ReadInt32LittleEndian(bytes) == 120 &&
                   BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == 240 &&
                   BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) == 3 &&
                   BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) == 2 &&
                   bytes.Length == 16 + expected.Values.Length * sizeof(float) &&
                   actual.Width == expected.Width && actual.Height == expected.Height &&
                   actual.Values.SequenceEqual(expected.Values),
                "Legacy .hmp round trip did not preserve its source-derived binary layout.");

            var badHeader = Path.Combine(directory, "bad-header.hmp");
            File.WriteAllBytes(badHeader, bytes);
            using (var stream = File.Open(badHeader, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            Expect<InvalidDataException>(() => LegacyHeightMapCodec.Read(badHeader));

            var truncated = Path.Combine(directory, "truncated.hmp");
            File.WriteAllBytes(truncated, bytes[..^1]);
            Expect<InvalidDataException>(() => LegacyHeightMapCodec.Read(truncated));
            Expect<ArgumentException>(() => LegacyHeightMapCodec.Write(
                Path.Combine(directory, "nan.hmp"),
                new LegacyHeightMap(1, 1, [float.NaN])));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
