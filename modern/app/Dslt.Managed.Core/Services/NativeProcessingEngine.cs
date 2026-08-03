using System.Runtime.InteropServices;
using Dslt.Managed.Core.Interop;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Services;

public sealed class NativeProcessingEngine : IProcessingEngine
{
    private const uint SupportedAbiVersion = 1;
    private readonly DsltSafeHandle _handle;

    public NativeProcessingEngine()
    {
        var abi = NativeMethods.dslt_get_abi_version();
        if (abi != SupportedAbiVersion)
            throw new NotSupportedException($"Native ABI {abi} is not supported; expected {SupportedAbiVersion}.");

        var status = NativeMethods.dslt_create(out var rawHandle);
        if (status != NativeStatus.Ok)
            throw new InvalidOperationException($"Could not create the native engine: {status}.");

        _handle = new DsltSafeHandle(rawHandle);
        ThrowIfFailed(NativeMethods.dslt_get_backend_info(_handle, out var backend));
        Backend = MapBackend(backend);
    }

    public bool IsAvailable => true;
    public string Status => Backend.CudaAvailable
        ? $"Native core ready · CUDA: {Backend.DeviceName}"
        : "Native core ready · CPU reference backend";
    public BackendInformation Backend { get; }

    public Task<ProcessingWorkEstimate> EstimateAsync(
        VolumeData volume,
        OperationParameters parameters,
        CancellationToken cancellationToken)
    {
        volume.Validate();
        if (parameters.Operation is not ProcessingOperation.DsltThreshold and not ProcessingOperation.DsltSegmentation)
            throw new ArgumentException("Work estimates are available only for DSLT operations.", nameof(parameters));
        return Task.Run(() => Estimate(volume, parameters, cancellationToken), cancellationToken);
    }

    public Task<ProcessingResult> RunAsync(
        VolumeData volume,
        OperationParameters parameters,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        ProcessingLabelState? labelState = null)
    {
        volume.Validate();
        return Task.Run(() => Run(volume, parameters, progress, cancellationToken, labelState), cancellationToken);
    }

    public void Dispose() => _handle.Dispose();

    private ProcessingWorkEstimate Estimate(
        VolumeData volume,
        OperationParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = MapDescriptor(volume);
        ThrowIfFailed(NativeMethods.dslt_set_volume_f32(
            _handle, in descriptor, volume.Samples, (ulong)volume.Samples.LongLength));
        var request = MapRequest(parameters);
        ThrowIfFailed(NativeMethods.dslt_estimate_operation(_handle, in request, out var estimate));
        return new ProcessingWorkEstimate(
            estimate.VoxelCount,
            estimate.DirectionCount,
            estimate.LineSamplesPerVoxel,
            estimate.DirectionalWorkItems,
            estimate.EstimatedHostBytes,
            estimate.SweepPasses,
            estimate.WorkItemLimit,
            estimate.HostMemoryLimitBytes,
            estimate.WithinLimits != 0);
    }

    private ProcessingResult Run(
        VolumeData volume,
        OperationParameters parameters,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        ProcessingLabelState? labelState)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = MapDescriptor(volume);
        ThrowIfFailed(NativeMethods.dslt_set_volume_f32(
            _handle, in descriptor, volume.Samples, (ulong)volume.Samples.LongLength));
        if (parameters.Operation == ProcessingOperation.Watershed)
        {
            if (labelState is null)
                throw new ArgumentException("Watershed requires a selected label seed state.", nameof(labelState));
            labelState.Validate(volume);
            ThrowIfFailed(NativeMethods.dslt_set_label_state_i32(
                _handle,
                labelState.Labels,
                checked((ulong)labelState.Labels.LongLength),
                labelState.SelectedLabels,
                checked((ulong)labelState.SelectedLabels.LongLength)));
        }
        var crop = MapCrop(parameters);
        var heightMap = parameters.CropEnabled && parameters.CropUseHeightMap
            ? parameters.CropHeightMap
            : null;
        ThrowIfFailed(NativeMethods.dslt_set_crop(
            _handle, in crop, heightMap, checked((ulong)(heightMap?.LongLength ?? 0))));

        NativeMethods.ProgressCallback callback = (value, _) =>
        {
            progress?.Report(value);
            return cancellationToken.IsCancellationRequested ? 1 : 0;
        };
        var request = MapRequest(parameters);
        var status = NativeMethods.dslt_run_operation(
            _handle, in request, callback, nint.Zero, out var result);
        if (status == NativeStatus.Cancelled) throw new OperationCanceledException(cancellationToken);
        ThrowIfFailed(status);

        var count = checked((int)result.ElementCount);
        float[]? floats = null;
        int[]? labels = null;
        if ((OutputKind)result.OutputKind == OutputKind.LabelsInt32)
        {
            labels = new int[count];
            ThrowIfFailed(NativeMethods.dslt_copy_labels_i32(_handle, labels, result.ElementCount));
        }
        else
        {
            floats = new float[count];
            ThrowIfFailed(NativeMethods.dslt_copy_output_f32(_handle, floats, result.ElementCount));
        }

        return new ProcessingResult(
            (ProcessingBackend)result.UsedBackend,
            (OutputKind)result.OutputKind,
            checked((int)result.Width),
            checked((int)result.Height),
            checked((int)result.Depth),
            checked((int)result.ComponentCount),
            floats,
            labels,
            checked((int)result.Reserved));
    }

    private void ThrowIfFailed(NativeStatus status)
    {
        if (status == NativeStatus.Ok) return;
        _ = NativeMethods.dslt_get_last_error(_handle, nint.Zero, 0, out var required);
        string message;
        if (required == 0)
        {
            message = status.ToString();
        }
        else
        {
            var buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                _ = NativeMethods.dslt_get_last_error(_handle, buffer, required, out _);
                message = Marshal.PtrToStringUTF8(buffer) ?? status.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new NativeProcessingException(status.ToString(), message);
    }

    private static NativeVolumeDescriptor MapDescriptor(VolumeData volume) => new()
    {
        Width = checked((uint)volume.Width),
        Height = checked((uint)volume.Height),
        Depth = checked((uint)volume.Depth),
        Channels = checked((uint)volume.Channels),
        SelectedChannel = checked((uint)volume.SelectedChannel),
        VoxelType = 5,
        ElementCount = checked((ulong)volume.Samples.LongLength),
        Calibration = new NativeCalibration
        {
            SpacingX = volume.Calibration.SpacingX,
            SpacingY = volume.Calibration.SpacingY,
            SpacingZ = volume.Calibration.SpacingZ,
            Calibrated = volume.Calibration.IsCalibrated ? (byte)1 : (byte)0,
        },
    };

    private static NativeCropOptions MapCrop(OperationParameters value) => new()
    {
        Enabled = value.CropEnabled ? (byte)1 : (byte)0,
        UseHeightMap = value.CropUseHeightMap ? (byte)1 : (byte)0,
        Upper = value.CropUpper,
        Lower = value.CropLower,
        BorderXy = value.CropBorderXy,
    };

    private static NativeOperationRequest MapRequest(OperationParameters value)
    {
        var isDslt = value.Operation is ProcessingOperation.DsltThreshold or ProcessingOperation.DsltSegmentation;
        var isSegmentation = value.Operation == ProcessingOperation.DsltSegmentation;
        var isThresholdSweep = value.Operation == ProcessingOperation.ThresholdSweep;
        var isAdaptiveThreshold = value.Operation is
            ProcessingOperation.AdaptiveThreshold2D or ProcessingOperation.AdaptiveThreshold3D;
        var isHMinima = value.Operation == ProcessingOperation.HMinima;
        var isHeightMap = value.Operation == ProcessingOperation.HeightMap;
        return new NativeOperationRequest
        {
            Operation = (int)value.Operation,
            Backend = (int)value.Backend,
            Radius = isHMinima ? value.HMinimaCheckInterval :
                isHeightMap ? value.HeightMapXyRadius : value.Radius,
            Connectivity = isDslt ? (int)value.DsltKernel :
                isAdaptiveThreshold ? (int)value.AdaptiveThresholdKernel :
                isHeightMap ? (int)value.HeightMapKernel : value.Connectivity,
            MinimumComponentSize = isThresholdSweep
                ? value.ThresholdSweepMinimumComponentSize
                : value.MinimumComponentSize,
            SliceIndex = isSegmentation || isThresholdSweep ? value.ClosingRadius :
                isHeightMap ? value.HeightMapSmoothLevel : value.SliceIndex,
            LanczosOrder = isDslt ? value.DirectionLevel :
                isHeightMap ? value.HeightMapZRadius : value.LanczosOrder,
            Threshold = isSegmentation ? value.MinimumInvalidStructureArea :
                isThresholdSweep ? value.ThresholdSweepMinimumInvalidStructureArea :
                isHMinima ? value.HMinimaHeight : value.Threshold,
            ConstantC = isSegmentation ? value.MinimumC :
                isThresholdSweep ? value.MinimumThreshold : value.ConstantC,
            WindowMinimum = isSegmentation ? value.MaximumC :
                isThresholdSweep ? value.MaximumThreshold : value.WindowMinimum,
            WindowMaximum = isSegmentation ? value.CInterval :
                isThresholdSweep ? value.ThresholdInterval : value.WindowMaximum,
            TargetSpacingZ = isDslt ? value.ZCorrectionFactor : value.TargetSpacingZ,
        };
    }

    private static unsafe BackendInformation MapBackend(NativeBackendInfo value) => new(
        value.CpuAvailable != 0,
        value.CudaCompiled != 0,
        value.CudaAvailable != 0,
        value.DeviceMemoryBytes,
        value.GetDeviceName());
}

public sealed class NativeProcessingException(string status, string message) : Exception(message)
{
    public string Status { get; } = status;
}
