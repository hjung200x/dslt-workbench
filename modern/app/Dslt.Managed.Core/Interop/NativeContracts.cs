using System.Runtime.InteropServices;

namespace Dslt.Managed.Core.Interop;

internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidState = 2,
    OutOfMemory = 3,
    Cancelled = 4,
    BackendUnavailable = 5,
    IoError = 6,
    NotImplemented = 7,
    InternalError = 100,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCalibration
{
    public double SpacingX;
    public double SpacingY;
    public double SpacingZ;
    public byte Calibrated;
    public fixed byte Reserved[7];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVolumeDescriptor
{
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint Channels;
    public uint SelectedChannel;
    public int VoxelType;
    public ulong ElementCount;
    public NativeCalibration Calibration;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOperationRequest
{
    public int Operation;
    public int Backend;
    public int Radius;
    public int Connectivity;
    public int MinimumComponentSize;
    public int SliceIndex;
    public int LanczosOrder;
    public float Threshold;
    public float ConstantC;
    public float WindowMinimum;
    public float WindowMaximum;
    public float TargetSpacingZ;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOperationResult
{
    public int UsedBackend;
    public int OutputKind;
    public uint Width;
    public uint Height;
    public uint Depth;
    public ulong ElementCount;
    public uint ComponentCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativeBackendInfo
{
    public byte CpuAvailable;
    public byte CudaCompiled;
    public byte CudaAvailable;
    public byte Reserved;
    public ulong DeviceMemoryBytes;
    public fixed byte DeviceName[128];

    public string GetDeviceName()
    {
        fixed (byte* pointer = DeviceName)
            return Marshal.PtrToStringUTF8((nint)pointer) ?? string.Empty;
    }
}

