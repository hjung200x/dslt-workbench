using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Dslt.Managed.Core.Models;

namespace Dslt.App.Services;

internal sealed record CziSceneDescriptor(int Ordinal, int Index, int X, int Y, int Width, int Height)
{
    public string DisplayName => $"Scene {Index} ({Width} x {Height})";
}

internal sealed record CziChannelDescriptor(
    int Index,
    VolumeVoxelType VoxelType,
    bool IsSupported,
    string Name,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha)
{
    public string DisplayName => $"C={Index}: {Name} ({VoxelType})";
}

internal sealed record CziDocumentDescriptor(
    int TimeStart,
    int TimeCount,
    int ZStart,
    int ZCount,
    bool HasPyramid,
    bool HasMultipleTiles,
    uint UnsupportedDimensionsMask,
    uint SpacingFlags,
    double SpacingXUm,
    double SpacingYUm,
    double SpacingZUm,
    IReadOnlyList<CziSceneDescriptor> Scenes,
    IReadOnlyList<CziChannelDescriptor> Channels);

internal sealed record CziImportSelection(
    CziSceneDescriptor Scene,
    int TimeIndex,
    IReadOnlyList<CziChannelDescriptor> Channels);

internal sealed class CziVolumeImporter : IDisposable
{
    internal const string DecoderName = "ZEISS libCZI 0.69.1";
    internal const string DecoderRevision = "61f74ff097d6d0fbe6e36f204ff59d92e299d7cd";
    private const uint AbiVersion = 0x0001_0000;
    private const uint SpacingXValid = 1u << 0;
    private const uint SpacingYValid = 1u << 1;
    private const uint SpacingZValid = 1u << 2;
    private readonly string _path;
    private readonly SafeCziDocumentHandle _document;
    private bool _disposed;

    private CziVolumeImporter(string path, SafeCziDocumentHandle document, CziDocumentDescriptor descriptor)
    {
        _path = path;
        _document = document;
        Descriptor = descriptor;
    }

    public CziDocumentDescriptor Descriptor { get; }

    public static CziVolumeImporter Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (CziNative.GetAbiVersion() != AbiVersion)
            throw new NotSupportedException("The installed dslt_czi native ABI is incompatible with this application.");

        CziNative.ThrowIfFailed(CziNative.OpenUtf8(fullPath, out var document));
        try
        {
            var native = NativeCziDocumentInfo.Create();
            CziNative.ThrowIfFailed(CziNative.GetDocumentInfo(document, ref native));
            if (native.AbiVersion != AbiVersion)
                throw new InvalidDataException("The CZI document descriptor has an unexpected ABI version.");

            var scenes = new CziSceneDescriptor[native.SceneCount];
            for (var ordinal = 0; ordinal < scenes.Length; ordinal++)
            {
                var scene = NativeCziSceneInfo.Create();
                CziNative.ThrowIfFailed(CziNative.GetSceneInfo(document, ordinal, ref scene));
                scenes[ordinal] = new CziSceneDescriptor(
                    scene.Ordinal, scene.SceneIndex, scene.X, scene.Y, scene.Width, scene.Height);
            }

            var channels = new CziChannelDescriptor[native.ChannelCount];
            for (var ordinal = 0; ordinal < channels.Length; ordinal++)
            {
                var index = checked(native.ChannelStart + ordinal);
                var channel = NativeCziChannelInfo.Create();
                CziNative.ThrowIfFailed(CziNative.GetChannelInfo(document, index, ref channel));
                channels[ordinal] = new CziChannelDescriptor(
                    channel.ChannelIndex,
                    (VolumeVoxelType)channel.VoxelType,
                    (channel.Flags & 1u) != 0,
                    CziNative.GetChannelName(document, index),
                    channel.Red,
                    channel.Green,
                    channel.Blue,
                    channel.Alpha);
            }

            var descriptor = new CziDocumentDescriptor(
                native.TimeStart,
                native.TimeCount,
                native.ZStart,
                native.ZCount,
                (native.DocumentFlags & 1u) != 0,
                (native.DocumentFlags & 2u) != 0,
                native.UnsupportedDimensionsMask,
                native.SpacingFlags,
                native.SpacingXUm,
                native.SpacingYUm,
                native.SpacingZUm,
                scenes,
                channels);
            return new CziVolumeImporter(fullPath, document, descriptor);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public VolumeData Read(
        CziImportSelection selection,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Descriptor.Scenes.Contains(selection.Scene))
            throw new ArgumentException("The selected scene does not belong to this CZI document.", nameof(selection));
        if (selection.TimeIndex < Descriptor.TimeStart ||
            selection.TimeIndex >= checked(Descriptor.TimeStart + Descriptor.TimeCount))
            throw new ArgumentOutOfRangeException(nameof(selection), "The selected time point does not exist.");
        if (selection.Channels.Count == 0 || selection.Channels.Any(channel => !Descriptor.Channels.Contains(channel)))
            throw new ArgumentException("At least one document channel must be selected.", nameof(selection));
        if (selection.Channels.Any(channel => !channel.IsSupported))
            throw new NotSupportedException("One or more selected CZI channels use an unsupported pixel type.");
        if (selection.Channels.Select(channel => channel.VoxelType).Distinct().Count() != 1)
            throw new NotSupportedException("Selected CZI channels must use the same pixel type.");

        var voxelType = selection.Channels[0].VoxelType;
        var bytesPerSample = BytesPerSample(voxelType);
        var sampleCount = checked((long)selection.Scene.Width * selection.Scene.Height * Descriptor.ZCount * selection.Channels.Count);
        var rawByteCount = checked(sampleCount * bytesPerSample);
        if (rawByteCount > int.MaxValue)
            throw new InsufficientMemoryException("The selected CZI volume exceeds the managed contiguous-array limit.");
        ValidateAllocationBudget(checked(rawByteCount + sampleCount * sizeof(float) +
            (long)selection.Scene.Width * selection.Scene.Height * bytesPerSample * 2));
        var sourceBefore = new FileInfo(_path);
        var sourceLength = sourceBefore.Length;
        var sourceLastWriteUtc = sourceBefore.LastWriteTimeUtc;

        var channelIndices = selection.Channels.Select(channel => channel.Index).ToArray();
        var channelsHandle = GCHandle.Alloc(channelIndices, GCHandleType.Pinned);
        try
        {
            var nativeSelection = new NativeCziSelection
            {
                StructSize = checked((uint)Marshal.SizeOf<NativeCziSelection>()),
                SceneIndex = selection.Scene.Index,
                TimeIndex = selection.TimeIndex,
                ChannelIndices = channelsHandle.AddrOfPinnedObject(),
                ChannelCount = checked((nuint)channelIndices.Length),
                MaximumOutputBytes = checked((ulong)rawByteCount),
            };
            CziNativeProgress callback = (value, _) =>
            {
                progress?.Report(Math.Clamp(value, 0, 1) * 0.85);
                return cancellationToken.IsCancellationRequested ? 1 : 0;
            };
            CziNative.ThrowIfFailed(CziNative.ReadVolume(
                _document, ref nativeSelection, callback, nint.Zero, out var volume));
            using (volume)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = NativeCziVolumeInfo.Create();
                CziNative.ThrowIfFailed(CziNative.GetVolumeInfo(volume, ref info));
                if (info.Width != selection.Scene.Width || info.Height != selection.Scene.Height ||
                    info.Depth != Descriptor.ZCount || info.Channels != channelIndices.Length ||
                    info.VoxelType != (uint)voxelType || info.RawSampleBytes != (ulong)rawByteCount)
                    throw new InvalidDataException("The decoded CZI volume does not match its requested dimensions or sample type.");

                var rawSamples = new byte[checked((int)rawByteCount)];
                var rawHandle = GCHandle.Alloc(rawSamples, GCHandleType.Pinned);
                try
                {
                    CziNative.ThrowIfFailed(CziNative.CopyRawSamples(
                        volume, rawHandle.AddrOfPinnedObject(), checked((nuint)rawSamples.Length)));
                }
                finally
                {
                    rawHandle.Free();
                }

                var samples = new float[checked((int)sampleCount)];
                ConvertSamples(rawSamples, samples, voxelType);
                NormalizeChannels(samples, checked(selection.Scene.Width * selection.Scene.Height * Descriptor.ZCount), channelIndices.Length);
                var containerHash = ComputeContainerHash(progress, cancellationToken);
                var sourceAfter = new FileInfo(_path);
                if (sourceAfter.Length != sourceLength || sourceAfter.LastWriteTimeUtc != sourceLastWriteUtc)
                    throw new IOException("The CZI source changed while it was being decoded; no volume was accepted.");
                var decodedHash = Convert.ToHexString(SHA256.HashData(rawSamples)).ToLowerInvariant();
                var allSpacingValid = (Descriptor.SpacingFlags &
                    (SpacingXValid | SpacingYValid | SpacingZValid)) ==
                    (SpacingXValid | SpacingYValid | SpacingZValid);
                var calibration = allSpacingValid
                    ? new Calibration(Descriptor.SpacingXUm, Descriptor.SpacingYUm, Descriptor.SpacingZUm, true, "um")
                    : Calibration.Unit;
                var calibrationWarning = allSpacingValid
                    ? null
                    : "CZI X/Y/Z physical spacing is incomplete; the volume is uncalibrated.";
                var channels = selection.Channels.Select(channel => new VolumeChannelInfo(
                    channel.Name, channel.Red, channel.Green, channel.Blue, channel.Alpha)).ToArray();
                var identity = new VolumeImportIdentity(
                    Path.GetFileName(_path),
                    containerHash,
                    decodedHash,
                    DecoderName,
                    DecoderRevision,
                    selection.Scene.Index,
                    selection.Scene.X,
                    selection.Scene.Y,
                    selection.Scene.Width,
                    selection.Scene.Height,
                    selection.TimeIndex,
                    channelIndices,
                    0,
                    calibrationWarning);
                progress?.Report(1);
                return new VolumeData(
                    selection.Scene.Width,
                    selection.Scene.Height,
                    Descriptor.ZCount,
                    channelIndices.Length,
                    0,
                    calibration,
                    samples,
                    new VolumeSourceInfo(voxelType, "CZI", null, rawSamples, channels, null, identity));
            }
        }
        finally
        {
            channelsHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _document.Dispose();
    }

    private string ComputeContainerHash(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long completed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            completed += read;
            progress?.Report(0.85 + 0.15 * completed / stream.Length);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static int BytesPerSample(VolumeVoxelType voxelType) => voxelType switch
    {
        VolumeVoxelType.UnsignedInt8 => 1,
        VolumeVoxelType.UnsignedInt16 => 2,
        VolumeVoxelType.Float32 => 4,
        _ => throw new NotSupportedException($"CZI sample type {voxelType} is not supported."),
    };

    private static void ConvertSamples(byte[] source, float[] destination, VolumeVoxelType voxelType)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = voxelType switch
            {
                VolumeVoxelType.UnsignedInt8 => source[index],
                VolumeVoxelType.UnsignedInt16 => BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * 2)),
                VolumeVoxelType.Float32 => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(index * 4))),
                _ => throw new NotSupportedException($"CZI sample type {voxelType} is not supported."),
            };
            if (!float.IsFinite(destination[index]))
                throw new InvalidDataException("CZI contains a non-finite floating-point sample.");
        }
    }

    private static void NormalizeChannels(float[] samples, int voxelCount, int channels)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var values = samples.AsSpan(channel * voxelCount, voxelCount);
            var maximumAbsolute = 0f;
            foreach (var value in values) maximumAbsolute = Math.Max(maximumAbsolute, Math.Abs(value));
            if (maximumAbsolute == 0) continue;
            for (var index = 0; index < values.Length; index++) values[index] /= maximumAbsolute;
        }
    }

    private static void ValidateAllocationBudget(long requiredBytes)
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) return;
        var budget = available - available / 4;
        if (requiredBytes > budget)
            throw new InsufficientMemoryException(
                $"CZI decode requires at least {requiredBytes:N0} bytes; the current 75% memory budget is {budget:N0} bytes.");
    }
}

internal sealed class SafeCziDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeCziDocumentHandle() : base(true) { }
    protected override bool ReleaseHandle() => CziNative.Close(handle) == 0;
}

internal sealed class SafeCziVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeCziVolumeHandle() : base(true) { }
    protected override bool ReleaseHandle() => CziNative.ReleaseVolume(handle) == 0;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CziNativeProgress(float progress, nint userData);

internal static class CziNative
{
    private const string Library = "dslt_czi";

    [DllImport(Library, EntryPoint = "dslt_czi_get_abi_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetAbiVersion();

    [DllImport(Library, EntryPoint = "dslt_czi_open_utf8", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OpenUtf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out SafeCziDocumentHandle document);

    [DllImport(Library, EntryPoint = "dslt_czi_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Close(nint document);

    [DllImport(Library, EntryPoint = "dslt_czi_get_document_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetDocumentInfo(SafeCziDocumentHandle document, ref NativeCziDocumentInfo info);

    [DllImport(Library, EntryPoint = "dslt_czi_get_scene_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetSceneInfo(SafeCziDocumentHandle document, int ordinal, ref NativeCziSceneInfo info);

    [DllImport(Library, EntryPoint = "dslt_czi_get_channel_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetChannelInfo(SafeCziDocumentHandle document, int channel, ref NativeCziChannelInfo info);

    [DllImport(Library, EntryPoint = "dslt_czi_copy_channel_name_utf8", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CopyChannelName(
        SafeCziDocumentHandle document, int channel, nint destination, nuint destinationSize, out nuint requiredSize);

    [DllImport(Library, EntryPoint = "dslt_czi_read_volume", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadVolume(
        SafeCziDocumentHandle document,
        ref NativeCziSelection selection,
        CziNativeProgress progress,
        nint userData,
        out SafeCziVolumeHandle volume);

    [DllImport(Library, EntryPoint = "dslt_czi_get_volume_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetVolumeInfo(SafeCziVolumeHandle volume, ref NativeCziVolumeInfo info);

    [DllImport(Library, EntryPoint = "dslt_czi_copy_raw_samples", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CopyRawSamples(SafeCziVolumeHandle volume, nint destination, nuint destinationSize);

    [DllImport(Library, EntryPoint = "dslt_czi_release_volume", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReleaseVolume(nint volume);

    [DllImport(Library, EntryPoint = "dslt_czi_copy_last_error_utf8", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CopyLastError(nint destination, nuint destinationSize, out nuint requiredSize);

    internal static string GetChannelName(SafeCziDocumentHandle document, int channel)
    {
        _ = CopyChannelName(document, channel, nint.Zero, 0, out var required);
        if (required == 0 || required > int.MaxValue) return $"Channel {channel}";
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            ThrowIfFailed(CopyChannelName(document, channel, buffer, required, out _));
            return Marshal.PtrToStringUTF8(buffer) ?? $"Channel {channel}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void ThrowIfFailed(int status)
    {
        if (status == 0) return;
        var message = ReadLastError();
        throw status switch
        {
            1 => new ArgumentException(message),
            2 => new NotSupportedException(message),
            3 => new IOException(message),
            4 => new InvalidDataException(message),
            5 => new InsufficientMemoryException(message),
            6 => new OperationCanceledException(message),
            8 => new InvalidDataException(message),
            _ => new Win32Exception(status, message),
        };
    }

    private static string ReadLastError()
    {
        _ = CopyLastError(nint.Zero, 0, out var required);
        if (required == 0 || required > int.MaxValue) return "Unknown native CZI error.";
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            _ = CopyLastError(buffer, required, out _);
            return Marshal.PtrToStringUTF8(buffer) ?? "Unknown native CZI error.";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCziDocumentInfo
{
    internal uint StructSize, AbiVersion;
    internal int SceneCount, TimeStart, TimeCount, ChannelStart, ChannelCount, ZStart, ZCount;
    internal uint DocumentFlags, UnsupportedDimensionsMask, SpacingFlags, Reserved0;
    internal double SpacingXUm, SpacingYUm, SpacingZUm;
    internal ulong SubblockCount;
    internal static NativeCziDocumentInfo Create() => new() { StructSize = checked((uint)Marshal.SizeOf<NativeCziDocumentInfo>()) };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCziSceneInfo
{
    internal uint StructSize;
    internal int Ordinal, SceneIndex, X, Y, Width, Height;
    internal uint Reserved0;
    internal static NativeCziSceneInfo Create() => new() { StructSize = checked((uint)Marshal.SizeOf<NativeCziSceneInfo>()) };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCziChannelInfo
{
    internal uint StructSize;
    internal int ChannelIndex;
    internal uint VoxelType;
    internal byte Red, Green, Blue, Alpha;
    internal uint Flags, Reserved0;
    internal static NativeCziChannelInfo Create() => new() { StructSize = checked((uint)Marshal.SizeOf<NativeCziChannelInfo>()) };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCziSelection
{
    internal uint StructSize;
    internal int SceneIndex, TimeIndex;
    internal nint ChannelIndices;
    internal nuint ChannelCount;
    internal ulong MaximumOutputBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCziVolumeInfo
{
    internal uint StructSize, VoxelType;
    internal int Width, Height, Depth, Channels;
    internal uint BytesPerSample, SpacingFlags;
    internal double SpacingXUm, SpacingYUm, SpacingZUm;
    internal ulong RawSampleBytes;
    internal static NativeCziVolumeInfo Create() => new() { StructSize = checked((uint)Marshal.SizeOf<NativeCziVolumeInfo>()) };
}
