using System.Buffers.Binary;
using System.IO;
using System.Text;
using Dslt.App.Services;

namespace Dslt.App.Tests;

internal static class LsmMetadataTests
{
    private const uint LsmInfoSize = 136;
    private const uint ChannelBlockSize = 49;
    private const uint TimeStampBlockSize = 40;

    public static void Run()
    {
        RunRoundTrip();
        RunPackedPlanarRoundTrip();
        RunPackedPlanarMinIsWhiteRoundTrip();
        RunCoreOnly();
        AssertRejected("magic", validMagic: false, corruptChannelNames: false, corruptTimeStamps: false);
        AssertRejected("channel-names", validMagic: true, corruptChannelNames: true, corruptTimeStamps: false);
        AssertRejected("timestamps", validMagic: true, corruptChannelNames: false, corruptTimeStamps: true);
    }

    private static void RunPackedPlanarMinIsWhiteRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-lsm-planar-white-{Guid.NewGuid():N}.lsm");
        try
        {
            WriteSyntheticPackedPlanarLsm(path, minIsWhite: true);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            if (!(volume.Source?.ChannelPlanarRawSamples ?? []).SequenceEqual(
                    new byte[] { 245, 244, 235, 234, 155, 154, 55, 54 }))
                throw new InvalidOperationException("Packed planar MinIsWhite LSM samples were not inverted.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunPackedPlanarRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-lsm-planar-{Guid.NewGuid():N}.lsm");
        try
        {
            WriteSyntheticPackedPlanarLsm(path);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Width != 2 || volume.Height != 1 || volume.Depth != 2 || volume.Channels != 2)
                throw new InvalidOperationException("Packed planar LSM dimensions were not reconstructed.");
            if (volume.Source?.VoxelType != Dslt.Managed.Core.Models.VolumeVoxelType.UnsignedInt8 ||
                volume.Source.Container != "LSM")
                throw new InvalidOperationException("Packed planar LSM source identity was not preserved.");
            if (!volume.Source.ChannelPlanarRawSamples.SequenceEqual(
                    new byte[] { 10, 11, 20, 21, 100, 101, 200, 201 }))
                throw new InvalidOperationException("Packed planar LSM samples were not reordered from ZCYX to CZYX.");
            if (Math.Abs(volume.Calibration.SpacingX - 0.25) > 1e-12 ||
                Math.Abs(volume.Calibration.SpacingY - 0.5) > 1e-12 ||
                Math.Abs(volume.Calibration.SpacingZ - 1.5) > 1e-12)
                throw new InvalidOperationException("Packed planar LSM calibration was not preserved.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-lsm-{Guid.NewGuid():N}.lsm");
        try
        {
            WriteSyntheticLsm(path, validMagic: true, corruptChannelNames: false, corruptTimeStamps: false);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if (volume.Width != 1 || volume.Height != 1 || volume.Depth != 2 || volume.Channels != 2)
                throw new InvalidOperationException("CZ_LSMINFO dimensions were not reconstructed.");
            if (volume.Source?.Container != "LSM")
                throw new InvalidOperationException("LSM container identity was not preserved.");
            if (!volume.Source.ChannelPlanarRawSamples.SequenceEqual(new byte[] { 10, 20, 100, 200 }))
                throw new InvalidOperationException("LSM thumbnail IFDs were not excluded from channel-planar pixels.");
            if (Math.Abs(volume.Calibration.SpacingX - 0.25) > 1e-12 ||
                Math.Abs(volume.Calibration.SpacingY - 0.5) > 1e-12 ||
                Math.Abs(volume.Calibration.SpacingZ - 1.5) > 1e-12 ||
                volume.Calibration.UnitName != "um")
                throw new InvalidOperationException("CZ_LSMINFO meter voxel sizes were not converted to micrometers.");

            var channels = volume.Source.ChannelMetadata ?? [];
            if (channels.Count != 2 ||
                channels[0] != new Dslt.Managed.Core.Models.VolumeChannelInfo("DAPI", 0, 0, 255, 255) ||
                channels[1] != new Dslt.Managed.Core.Models.VolumeChannelInfo("GFP", 0, 255, 0, 255))
                throw new InvalidOperationException("LSM channel names or RGBA values were not preserved.");
            if (!(volume.Source.TimeStampsSeconds ?? []).SequenceEqual(new[] { 0.0, 0.5, 1.0, 1.5 }))
                throw new InvalidOperationException("LSM timestamps were not preserved.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunCoreOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-lsm-core-{Guid.NewGuid():N}.lsm");
        try
        {
            WriteSyntheticLsm(
                path,
                validMagic: true,
                corruptChannelNames: false,
                corruptTimeStamps: false,
                includeOptionalMetadata: false);
            var volume = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
            volume.Validate();
            if ((volume.Source?.ChannelMetadata ?? []).Count != 0 ||
                (volume.Source?.TimeStampsSeconds ?? []).Count != 0)
                throw new InvalidOperationException("Core-only CZ_LSMINFO unexpectedly produced optional metadata.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void AssertRejected(
        string suffix,
        bool validMagic,
        bool corruptChannelNames,
        bool corruptTimeStamps)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dslt-lsm-invalid-{suffix}-{Guid.NewGuid():N}.lsm");
        try
        {
            WriteSyntheticLsm(path, validMagic, corruptChannelNames, corruptTimeStamps);
            try
            {
                _ = WpfWorkspaceFileService.ReadStack(path, CancellationToken.None);
                throw new InvalidOperationException($"Malformed LSM {suffix} metadata was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected.
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void WriteSyntheticLsm(
        string path,
        bool validMagic,
        bool corruptChannelNames,
        bool corruptTimeStamps,
        bool includeOptionalMetadata = true)
    {
        const int pageCount = 8;
        var lsmInfoSize = includeOptionalMetadata ? LsmInfoSize : 64u;
        var firstPageMetadataSize = checked(
            lsmInfoSize + (includeOptionalMetadata ? ChannelBlockSize + TimeStampBlockSize : 0u));
        var fullValues = new byte[] { 10, 100, 20, 200 };
        var ifdOffsets = new uint[pageCount];
        var pixelOffsets = new uint[pageCount];
        uint cursor = 8;
        for (var page = 0; page < pageCount; page++)
        {
            var entryCount = page == 0 ? 14u : 13u;
            var ifdBytes = checked(2u + entryCount * 12u + 4u);
            ifdOffsets[page] = cursor;
            pixelOffsets[page] = checked(cursor + ifdBytes + (page == 0 ? firstPageMetadataSize : 0u));
            cursor = checked(pixelOffsets[page] + 1u);
        }

        var lsmInfoOffset = checked(pixelOffsets[0] - firstPageMetadataSize);
        var channelBlockOffset = checked(lsmInfoOffset + lsmInfoSize);
        var timeStampBlockOffset = checked(channelBlockOffset + ChannelBlockSize);
        using var stream = File.Create(path);
        stream.WriteByte((byte)'I');
        stream.WriteByte((byte)'I');
        WriteUInt16(stream, 42);
        WriteUInt32(stream, ifdOffsets[0]);
        for (var page = 0; page < pageCount; page++)
        {
            var reduced = page % 2 == 1;
            var entryCount = page == 0 ? (ushort)14 : (ushort)13;
            WriteUInt16(stream, entryCount);
            WriteLongEntry(stream, 254, reduced ? 1u : 0u);
            WriteLongEntry(stream, 256, 1);
            WriteLongEntry(stream, 257, 1);
            WriteShortEntry(stream, 258, 8);
            WriteShortEntry(stream, 259, 1);
            WriteShortEntry(stream, 262, 1);
            WriteLongEntry(stream, 273, pixelOffsets[page]);
            WriteShortEntry(stream, 274, 1);
            WriteShortEntry(stream, 277, 1);
            WriteLongEntry(stream, 278, 1);
            WriteLongEntry(stream, 279, 1);
            WriteShortEntry(stream, 284, 1);
            WriteShortEntry(stream, 339, 1);
            if (page == 0)
                WriteArrayOffsetEntry(stream, 34412, lsmInfoSize, lsmInfoOffset, type: 1);
            WriteUInt32(stream, page + 1 < pageCount ? ifdOffsets[page + 1] : 0);
            if (page == 0)
            {
                WriteLsmInfo(
                    stream,
                    validMagic,
                    lsmInfoSize,
                    includeOptionalMetadata ? channelBlockOffset : 0,
                    includeOptionalMetadata ? timeStampBlockOffset : 0);
                if (includeOptionalMetadata)
                {
                    WriteChannelBlock(stream, corruptChannelNames);
                    WriteTimeStampBlock(stream, corruptTimeStamps);
                }
            }
            stream.WriteByte(reduced ? (byte)255 : fullValues[page / 2]);
        }
    }

    private static void WriteSyntheticPackedPlanarLsm(string path, bool minIsWhite = false)
    {
        const int pageCount = 2;
        const uint packedLsmInfoSize = 64;
        var ifdOffsets = new uint[pageCount];
        var stripOffsetArrayOffsets = new uint[pageCount];
        var stripCountArrayOffsets = new uint[pageCount];
        var pixelOffsets = new uint[pageCount];
        uint cursor = 8;
        for (var page = 0; page < pageCount; page++)
        {
            var entryCount = page == 0 ? 14u : 13u;
            var ifdBytes = checked(2u + entryCount * 12u + 4u);
            ifdOffsets[page] = cursor;
            stripOffsetArrayOffsets[page] = checked(cursor + ifdBytes);
            stripCountArrayOffsets[page] = checked(stripOffsetArrayOffsets[page] + 8u);
            pixelOffsets[page] = checked(
                stripCountArrayOffsets[page] + 8u + (page == 0 ? packedLsmInfoSize : 0u));
            cursor = checked(pixelOffsets[page] + 4u);
        }

        var lsmInfoOffset = checked(stripCountArrayOffsets[0] + 8u);
        using var stream = File.Create(path);
        stream.WriteByte((byte)'I');
        stream.WriteByte((byte)'I');
        WriteUInt16(stream, 42);
        WriteUInt32(stream, ifdOffsets[0]);
        for (var page = 0; page < pageCount; page++)
        {
            WriteUInt16(stream, page == 0 ? (ushort)14 : (ushort)13);
            WriteLongEntry(stream, 254, 0);
            WriteLongEntry(stream, 256, 2);
            WriteLongEntry(stream, 257, 1);
            WriteShortEntry(stream, 258, 8);
            WriteShortEntry(stream, 259, 1);
            WriteShortEntry(stream, 262, (ushort)(minIsWhite ? 0 : 2));
            WriteArrayOffsetEntry(stream, 273, 2, stripOffsetArrayOffsets[page], type: 4);
            WriteShortEntry(stream, 274, 1);
            WriteShortEntry(stream, 277, 2);
            WriteLongEntry(stream, 278, 1);
            WriteArrayOffsetEntry(stream, 279, 2, stripCountArrayOffsets[page], type: 4);
            WriteShortEntry(stream, 284, 2);
            WriteShortEntry(stream, 339, 1);
            if (page == 0)
                WriteArrayOffsetEntry(stream, 34412, packedLsmInfoSize, lsmInfoOffset, type: 1);
            WriteUInt32(stream, page + 1 < pageCount ? ifdOffsets[page + 1] : 0);

            WriteUInt32(stream, pixelOffsets[page]);
            WriteUInt32(stream, checked(pixelOffsets[page] + 2u));
            WriteUInt32(stream, 2);
            WriteUInt32(stream, 2);
            if (page == 0) WritePackedLsmInfo(stream);
            stream.Write(page == 0
                ? new byte[] { 10, 11, 100, 101 }
                : new byte[] { 20, 21, 200, 201 });
        }
    }

    private static void WritePackedLsmInfo(Stream stream)
    {
        WriteUInt32(stream, 50350412u);
        WriteInt32(stream, 64);
        WriteInt32(stream, 2);
        WriteInt32(stream, 1);
        WriteInt32(stream, 2);
        WriteInt32(stream, 2);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteDouble(stream, 0.25e-6);
        WriteDouble(stream, 0.5e-6);
        WriteDouble(stream, 1.5e-6);
    }

    private static void WriteLsmInfo(
        Stream stream,
        bool validMagic,
        uint lsmInfoSize,
        uint channelBlockOffset,
        uint timeStampBlockOffset)
    {
        WriteUInt32(stream, validMagic ? 50350412u : 0u);
        WriteInt32(stream, checked((int)lsmInfoSize));
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteInt32(stream, 2);
        WriteInt32(stream, 2);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteInt32(stream, 1);
        WriteDouble(stream, 0.25e-6);
        WriteDouble(stream, 0.5e-6);
        WriteDouble(stream, 1.5e-6);
        if (lsmInfoSize == 64) return;
        WriteDouble(stream, 0);
        WriteDouble(stream, 0);
        WriteDouble(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, channelBlockOffset);
        WriteDouble(stream, 0.5);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, timeStampBlockOffset);
    }

    private static void WriteChannelBlock(Stream stream, bool corruptNames)
    {
        WriteUInt32(stream, ChannelBlockSize);
        WriteUInt32(stream, 2);
        WriteUInt32(stream, 2);
        WriteUInt32(stream, 24);
        WriteUInt32(stream, 32);
        WriteUInt32(stream, 0);
        stream.Write(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 });
        WriteUInt32(stream, corruptNames ? 1000u : 5u);
        stream.Write(Encoding.ASCII.GetBytes("DAPI"));
        stream.WriteByte(0);
        WriteUInt32(stream, 4);
        stream.Write(Encoding.ASCII.GetBytes("GFP"));
        stream.WriteByte(0);
    }

    private static void WriteTimeStampBlock(Stream stream, bool corrupt)
    {
        WriteInt32(stream, corrupt ? checked((int)TimeStampBlockSize - 1) : checked((int)TimeStampBlockSize));
        WriteInt32(stream, 4);
        WriteDouble(stream, 0);
        WriteDouble(stream, 0.5);
        WriteDouble(stream, 1.0);
        WriteDouble(stream, 1.5);
    }

    private static void WriteLongEntry(Stream stream, ushort tag, uint value)
    {
        WriteUInt16(stream, tag);
        WriteUInt16(stream, 4);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, value);
    }

    private static void WriteShortEntry(Stream stream, ushort tag, ushort value)
    {
        WriteUInt16(stream, tag);
        WriteUInt16(stream, 3);
        WriteUInt32(stream, 1);
        WriteUInt16(stream, value);
        WriteUInt16(stream, 0);
    }

    private static void WriteArrayOffsetEntry(Stream stream, ushort tag, uint count, uint offset, ushort type)
    {
        WriteUInt16(stream, tag);
        WriteUInt16(stream, type);
        WriteUInt32(stream, count);
        WriteUInt32(stream, offset);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        stream.Write(bytes);
    }
}
