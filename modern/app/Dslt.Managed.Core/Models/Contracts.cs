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
    DsltThreshold = 19,
    DsltSegmentation = 20,
    AdaptiveThreshold2D = 21,
    AdaptiveThreshold3D = 22,
    HMinima = 23,
    Watershed = 24,
    HeightProjection = 25,
    ZGradient = 26,
    ImportLabels = 27,
    ImportHeightMap = 28,
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

public enum DsltKernelType
{
    Gaussian = 0,
    Mean = 1,
}

public enum HeightProjectionMode
{
    Normal = 0,
    Z = 1,
}

public sealed record VolumeChannelInfo(
    string Name,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);

public sealed record VolumeSourceInfo(
    VolumeVoxelType VoxelType,
    string Container,
    string? ImageDescription,
    byte[] ChannelPlanarRawSamples,
    IReadOnlyList<VolumeChannelInfo>? ChannelMetadata = null,
    IReadOnlyList<double>? TimeStampsSeconds = null)
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
            var channelMetadata = Source.ChannelMetadata ?? [];
            if (channelMetadata.Count != 0 && channelMetadata.Count != Channels)
                throw new ArgumentException(
                    $"Expected zero or {Channels} channel metadata entries but received {channelMetadata.Count}.",
                    nameof(Source));
            if (channelMetadata.Any(channel => channel is null || string.IsNullOrWhiteSpace(channel.Name)))
                throw new ArgumentException("Every channel metadata entry must have a name.", nameof(Source));

            var timeStamps = Source.TimeStampsSeconds ?? [];
            var previousTimeStamp = double.NegativeInfinity;
            foreach (var timeStamp in timeStamps)
            {
                if (!double.IsFinite(timeStamp) || timeStamp < 0 || timeStamp < previousTimeStamp)
                    throw new ArgumentException(
                        "Source timestamps must be finite, non-negative, and nondecreasing.",
                        nameof(Source));
                previousTimeStamp = timeStamp;
            }
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
    float TargetSpacingZ = 1,
    int DirectionLevel = 2,
    DsltKernelType DsltKernel = DsltKernelType.Mean,
    float ZCorrectionFactor = 0.2f,
    float MinimumC = 0,
    float MaximumC = 0,
    float CInterval = 0.002f,
    float MinimumThreshold = 0,
    float MaximumThreshold = 1,
    float ThresholdInterval = 0.02f,
    int ThresholdSweepMinimumComponentSize = 0,
    int ThresholdSweepMinimumInvalidStructureArea = 100,
    DsltKernelType AdaptiveThresholdKernel = DsltKernelType.Mean,
    float HMinimaHeight = 0.1f,
    int HMinimaCheckInterval = 50,
    int HeightMapXyRadius = 0,
    int HeightMapZRadius = 4,
    DsltKernelType HeightMapKernel = DsltKernelType.Gaussian,
    int HeightMapSmoothLevel = 1,
    int ClosingRadius = 2,
    int MinimumInvalidStructureArea = 500,
    bool CropEnabled = false,
    bool CropUseHeightMap = false,
    int CropUpper = 0,
    int CropLower = 0,
    int CropBorderXy = 0,
    float[]? CropHeightMap = null,
    string? SeedLabelsSha256 = null,
    int[]? SelectedSeedLabels = null,
    HeightProjectionMode ProjectionMode = HeightProjectionMode.Z,
    float ProjectionOffset = 0,
    float ProjectionStartDepth = 0,
    int ProjectionRange = 0,
    float ProjectionThreshold = 0,
    bool DepthColorEnabled = false,
    int DepthColorRange = 100,
    float ZGradientCoefficient = 10,
    float ZGradientExponent = 1,
    bool ZGradientUseHeightMap = false);

public sealed record ProcessingLabelState(
    int Width,
    int Height,
    int Depth,
    int[] Labels,
    int[] SelectedLabels)
{
    public void Validate(VolumeData volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (Width != volume.Width || Height != volume.Height || Depth != volume.Depth)
            throw new ArgumentException("Label state dimensions must match the source volume.");
        if (Labels is null || Labels.Length != volume.VoxelCount)
            throw new ArgumentException("Label state length must match the source volume.");
        if (SelectedLabels is null || SelectedLabels.Length == 0)
            throw new ArgumentException("At least one watershed seed label must be selected.");
        if (Labels.Any(label => label < -1))
            throw new ArgumentException("Labels must be -1 or non-negative.");
        var available = Labels.Where(label => label >= 0).ToHashSet();
        if (SelectedLabels.Any(label => label < 0 || !available.Contains(label)))
            throw new ArgumentException("Every selected watershed seed must exist in the label state.");
    }
}

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
    int[]? Labels,
    int CompletedPasses = 0);

public sealed record ProcessingWorkEstimate(
    ulong VoxelCount,
    ulong DirectionCount,
    ulong LineSamplesPerVoxel,
    ulong DirectionalWorkItems,
    ulong EstimatedHostBytes,
    ulong SweepPasses,
    ulong WorkItemLimit,
    ulong HostMemoryLimitBytes,
    bool WithinLimits);
