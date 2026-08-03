namespace Dslt.Managed.Core.Models;

public enum ProcessingBackend
{
    Auto = 0,
    Cpu = 1,
    Cuda = 2,
}

public enum ProcessingOperation
{
    Copy = 0,
    WindowLevel = 1,
    Threshold2D = 2,
    Threshold3D = 3,
    SmoothMean = 4,
    SmoothGaussian = 5,
    DilateCube = 6,
    ErodeCube = 7,
    DilateSphere = 8,
    ErodeSphere = 9,
    ConnectedComponents = 10,
    HeightMap = 11,
    DepthMap = 12,
    ResampleZArea = 13,
    ResampleZLanczos = 14,
    ExtractXy = 15,
    ExtractYz = 16,
    ExtractZx = 17,
    ThresholdSweep = 18,
}

public enum OutputKind
{
    VolumeFloat32 = 1,
    LabelsInt32 = 2,
    ImageFloat32 = 3,
}

public sealed record Calibration(double SpacingX, double SpacingY, double SpacingZ, bool IsCalibrated)
{
    public static Calibration Unit { get; } = new(1, 1, 1, false);
}

public sealed record VolumeData(
    int Width,
    int Height,
    int Depth,
    int Channels,
    int SelectedChannel,
    Calibration Calibration,
    float[] Samples)
{
    public int VoxelCount => checked(Width * Height * Depth);

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Depth <= 0 || Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Volume dimensions and channels must be positive.");
        if (SelectedChannel < 0 || SelectedChannel >= Channels)
            throw new ArgumentOutOfRangeException(nameof(SelectedChannel));
        var expected = checked(VoxelCount * Channels);
        if (Samples.Length != expected)
            throw new ArgumentException($"Expected {expected} samples but received {Samples.Length}.", nameof(Samples));
    }
}

public sealed record OperationParameters(
    ProcessingOperation Operation,
    ProcessingBackend Backend = ProcessingBackend.Auto,
    int Radius = 1,
    int Connectivity = 6,
    int MinimumComponentSize = 1,
    int SliceIndex = 0,
    int LanczosOrder = 2,
    float Threshold = 0.5f,
    float ConstantC = 0,
    float WindowMinimum = 0,
    float WindowMaximum = 1,
    float TargetSpacingZ = 1);

public sealed record BackendInformation(
    bool CpuAvailable,
    bool CudaCompiled,
    bool CudaAvailable,
    ulong DeviceMemoryBytes,
    string DeviceName);

public sealed record ProcessingResult(
    ProcessingBackend UsedBackend,
    OutputKind OutputKind,
    int Width,
    int Height,
    int Depth,
    int ComponentCount,
    float[]? FloatData,
    int[]? Labels);

