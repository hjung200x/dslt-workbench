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

public enum VolumeVoxelType
{
    UnsignedInt8 = 1,
    UnsignedInt16 = 2,
    SignedInt16 = 3,
    UnsignedInt32 = 4,
    SignedInt32 = 5,
    Float32 = 6,
}

public sealed record VolumeSourceInfo(
    VolumeVoxelType VoxelType,
    string Container,
    string? ImageDescription,
    byte[] ChannelPlanarRawSamples)
{
    public int BytesPerSample => VoxelType switch
    {
        VolumeVoxelType.UnsignedInt8 => 1,
        VolumeVoxelType.UnsignedInt16 or VolumeVoxelType.SignedInt16 => 2,
        VolumeVoxelType.UnsignedInt32 or VolumeVoxelType.SignedInt32 or VolumeVoxelType.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(VoxelType)),
    };
}

public sealed record Calibration(
    double SpacingX,
    double SpacingY,
    double SpacingZ,
    bool IsCalibrated,
    string UnitName = "pixel")
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
    float[] Samples,
    VolumeSourceInfo? Source = null)
{
    public int VoxelCount => checked(Width * Height * Depth);

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Depth <= 0 || Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Volume dimensions and channels must be positive.");
        if (SelectedChannel < 0 || SelectedChannel >= Channels)
            throw new ArgumentOutOfRangeException(nameof(SelectedChannel));
        if (Calibration is null ||
            !double.IsFinite(Calibration.SpacingX) || Calibration.SpacingX <= 0 ||
            !double.IsFinite(Calibration.SpacingY) || Calibration.SpacingY <= 0 ||
            !double.IsFinite(Calibration.SpacingZ) || Calibration.SpacingZ <= 0 ||
            string.IsNullOrWhiteSpace(Calibration.UnitName))
            throw new ArgumentOutOfRangeException(
                nameof(Calibration),
                "Voxel spacing must be finite and positive, and the calibration unit is required.");
        var expected = checked(VoxelCount * Channels);
        if (Samples.Length != expected)
            throw new ArgumentException($"Expected {expected} samples but received {Samples.Length}.", nameof(Samples));
        if (Source is not null)
        {
            if (string.IsNullOrWhiteSpace(Source.Container) || Source.ChannelPlanarRawSamples is null)
                throw new ArgumentException("Source container and decoded sample bytes are required.", nameof(Source));
            var expectedBytes = checked(expected * Source.BytesPerSample);
            if (Source.ChannelPlanarRawSamples.Length != expectedBytes)
                throw new ArgumentException(
                    $"Expected {expectedBytes} source bytes but received {Source.ChannelPlanarRawSamples.Length}.",
                    nameof(Source));
        }
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
