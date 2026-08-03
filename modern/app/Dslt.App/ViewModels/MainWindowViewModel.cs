using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.App.Infrastructure;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Services;
using Dslt.Managed.Core.Synthetic;

namespace Dslt.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IProcessingEngine _engine;
    private readonly IWorkspaceFileService _files;
    private VolumeData? _volume;
    private CancellationTokenSource? _cancellation;
    private ImageSource? _sourceImage;
    private ImageSource? _resultImage;
    private string _status;
    private string _workEstimateSummary = "Select a DSLT operation to calculate its work estimate.";
    private double _progress;
    private float _threshold = 0.5F;
    private float _windowMinimum;
    private float _windowMaximum = 1.0F;
    private int _radius = 1;
    private int _dsltRadius = 14;
    private int _channelIndex;
    private int _zIndex;
    private int _connectivity = 6;
    private int _minimumComponentSize;
    private int _directionLevel = 2;
    private DsltKernelType _dsltKernel = DsltKernelType.Mean;
    private float _previewOffset = 20;
    private float _zCorrectionFactor = 0.2F;
    private float _minimumC;
    private float _maximumC;
    private float _cInterval = 0.002F;
    private int _closingRadius = 2;
    private int _minimumInvalidStructureArea = 500;
    private ProcessingBackend _backend = ProcessingBackend.Auto;
    private OperationOption _selectedOperation;
    private WorkflowStage _selectedStage = WorkflowStage.Inspect;
    private ProcessingResult? _lastResult;
    private OperationParameters? _lastParameters;
    private bool _isBusy;

    public MainWindowViewModel(IProcessingEngine engine, IWorkspaceFileService files)
    {
        _engine = engine;
        _files = files;
        _status = engine.Status;
        Operations =
        [
            new("Window / level", ProcessingOperation.WindowLevel, WorkflowStage.Inspect),
            new("Threshold 3D", ProcessingOperation.Threshold3D, WorkflowStage.Process),
            new("Mean smoothing", ProcessingOperation.SmoothMean, WorkflowStage.Process),
            new("Gaussian smoothing", ProcessingOperation.SmoothGaussian, WorkflowStage.Process),
            new("Dilate sphere", ProcessingOperation.DilateSphere, WorkflowStage.Process),
            new("Erode sphere", ProcessingOperation.ErodeSphere, WorkflowStage.Process),
            new("Height map", ProcessingOperation.HeightMap, WorkflowStage.Process),
            new("Depth map", ProcessingOperation.DepthMap, WorkflowStage.Process),
            new("Connected components", ProcessingOperation.ConnectedComponents, WorkflowStage.Segment),
            new("DSLT threshold preview", ProcessingOperation.DsltThreshold, WorkflowStage.Segment),
            new("DSLT iterative segmentation", ProcessingOperation.DsltSegmentation, WorkflowStage.Segment),
        ];
        _selectedOperation = Operations[1];
        GenerateSyntheticCommand = new RelayCommand(GenerateSynthetic, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
        EstimateCommand = new AsyncRelayCommand(EstimateSelectedOperationAsync, CanEstimate);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasResult && !IsBusy);
        RunCommand = new AsyncRelayCommand(RunSelectedOperationAsync, () => HasVolume && _engine.IsAvailable && !IsBusy);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        GenerateSynthetic();
    }

    public IReadOnlyList<OperationOption> Operations { get; }
    public IReadOnlyList<WorkflowStage> WorkflowStages { get; } = Enum.GetValues<WorkflowStage>();
    public IReadOnlyList<ProcessingBackend> Backends { get; } =
        [ProcessingBackend.Auto, ProcessingBackend.Cpu, ProcessingBackend.Cuda];
    public IReadOnlyList<int> Connectivities { get; } = [6, 18, 26];
    public IReadOnlyList<DsltKernelType> DsltKernels { get; } = Enum.GetValues<DsltKernelType>();

    public OperationOption SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (!SetProperty(ref _selectedOperation, value)) return;
            SelectedStage = value.Stage;
            WorkEstimateSummary = IsDsltOperation
                ? "Estimate not calculated for the current parameters."
                : "Work estimates are available for DSLT operations.";
            OnPropertyChanged(nameof(IsDsltOperation));
            OnPropertyChanged(nameof(IsDsltSegmentation));
            OnPropertyChanged(nameof(UsesThreshold));
            OnPropertyChanged(nameof(UsesRadius));
            OnPropertyChanged(nameof(Radius));
            OnPropertyChanged(nameof(MinimumRadius));
            OnPropertyChanged(nameof(MaximumRadius));
            NotifyCommandStates();
        }
    }

    public WorkflowStage SelectedStage
    {
        get => _selectedStage;
        set => SetProperty(ref _selectedStage, value);
    }

    public ProcessingBackend Backend
    {
        get => _backend;
        set => SetProperty(ref _backend, value);
    }

    public float Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, Math.Clamp(value, 0, 1));
    }

    public float WindowMinimum
    {
        get => _windowMinimum;
        set
        {
            if (!SetProperty(ref _windowMinimum, Math.Clamp(value, 0, 0.99F))) return;
            if (_windowMaximum <= _windowMinimum) WindowMaximum = Math.Min(1, _windowMinimum + 0.01F);
            RefreshImages();
        }
    }

    public float WindowMaximum
    {
        get => _windowMaximum;
        set
        {
            if (!SetProperty(ref _windowMaximum, Math.Clamp(value, 0.01F, 1))) return;
            if (_windowMinimum >= _windowMaximum) WindowMinimum = Math.Max(0, _windowMaximum - 0.01F);
            RefreshImages();
        }
    }

    public int Radius
    {
        get => IsDsltOperation ? _dsltRadius : _radius;
        set
        {
            if (IsDsltOperation)
                SetProperty(ref _dsltRadius, Math.Clamp(value, 1, 100));
            else
                SetProperty(ref _radius, Math.Clamp(value, 0, 64));
        }
    }

    public int MinimumRadius => IsDsltOperation ? 1 : 0;
    public int MaximumRadius => IsDsltOperation ? 100 : 64;

    public int ChannelIndex
    {
        get => _channelIndex;
        set
        {
            if (_volume is null) return;
            var selected = Math.Clamp(value, 0, _volume.Channels - 1);
            if (!SetProperty(ref _channelIndex, selected)) return;
            _volume = _volume with { SelectedChannel = selected };
            SourceImage = CreateVolumeSlice(_volume, ZIndex, WindowMinimum, WindowMaximum);
            OnPropertyChanged(nameof(VolumeSummary));
        }
    }

    public int ZIndex
    {
        get => _zIndex;
        set
        {
            var maximum = Math.Max(0, (_volume?.Depth ?? 1) - 1);
            if (!SetProperty(ref _zIndex, Math.Clamp(value, 0, maximum))) return;
            RefreshImages();
        }
    }

    public int MaximumChannelIndex => Math.Max(0, (_volume?.Channels ?? 1) - 1);
    public int MaximumZIndex => Math.Max(0, (_volume?.Depth ?? 1) - 1);

    public int Connectivity
    {
        get => _connectivity;
        set => SetProperty(ref _connectivity, value is 6 or 18 or 26 ? value : 6);
    }

    public int MinimumComponentSize
    {
        get => _minimumComponentSize;
        set => SetProperty(ref _minimumComponentSize, Math.Max(0, value));
    }

    public int DirectionLevel
    {
        get => _directionLevel;
        set => SetProperty(ref _directionLevel, Math.Clamp(value, 1, 10));
    }

    public DsltKernelType DsltKernel
    {
        get => _dsltKernel;
        set => SetProperty(ref _dsltKernel, value);
    }

    public float PreviewOffset
    {
        get => _previewOffset;
        set => SetProperty(ref _previewOffset, float.IsFinite(value) ? Math.Clamp(value, 0, 200) : 20);
    }

    public float ZCorrectionFactor
    {
        get => _zCorrectionFactor;
        set => SetProperty(ref _zCorrectionFactor, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.2F);
    }

    public float MinimumC
    {
        get => _minimumC;
        set => SetProperty(ref _minimumC, float.IsFinite(value) ? value : 0);
    }

    public float MaximumC
    {
        get => _maximumC;
        set => SetProperty(ref _maximumC, float.IsFinite(value) ? value : 0);
    }

    public float CInterval
    {
        get => _cInterval;
        set => SetProperty(ref _cInterval, float.IsFinite(value) ? Math.Max(0.000001F, value) : 0.002F);
    }

    public int ClosingRadius
    {
        get => _closingRadius;
        set => SetProperty(ref _closingRadius, Math.Clamp(value, 0, 50));
    }

    public int MinimumInvalidStructureArea
    {
        get => _minimumInvalidStructureArea;
        set => SetProperty(ref _minimumInvalidStructureArea, Math.Clamp(value, 0, 10_000));
    }

    public ImageSource? SourceImage
    {
        get => _sourceImage;
        private set => SetProperty(ref _sourceImage, value);
    }

    public ImageSource? ResultImage
    {
        get => _resultImage;
        private set => SetProperty(ref _resultImage, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string WorkEstimateSummary
    {
        get => _workEstimateSummary;
        private set => SetProperty(ref _workEstimateSummary, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            NotifyCommandStates();
        }
    }

    public bool IsIdle => !IsBusy;
    public bool HasVolume => _volume is not null;
    public bool HasResult => _lastResult is not null && _lastParameters is not null;
    public bool IsDsltOperation => SelectedOperation.Operation is ProcessingOperation.DsltThreshold or ProcessingOperation.DsltSegmentation;
    public bool IsDsltSegmentation => SelectedOperation.Operation == ProcessingOperation.DsltSegmentation;
    public bool UsesThreshold => SelectedOperation.Operation is ProcessingOperation.Threshold2D or ProcessingOperation.Threshold3D or
        ProcessingOperation.ConnectedComponents or ProcessingOperation.HeightMap or ProcessingOperation.DepthMap;
    public bool UsesRadius => SelectedOperation.Operation is ProcessingOperation.SmoothMean or ProcessingOperation.SmoothGaussian or
        ProcessingOperation.DilateCube or ProcessingOperation.ErodeCube or ProcessingOperation.DilateSphere or
        ProcessingOperation.ErodeSphere or ProcessingOperation.DsltThreshold or ProcessingOperation.DsltSegmentation;
    public ProcessingResult? LastResult => _lastResult;
    public OperationParameters? LastParameters => _lastParameters;

    public string VolumeSummary => _volume is null
        ? "No volume"
        : $"{_volume.Width} × {_volume.Height} × {_volume.Depth} · channel {ChannelIndex + 1}/{_volume.Channels} · Z spacing {_volume.Calibration.SpacingZ.ToString("0.###", CultureInfo.InvariantCulture)} {_volume.Calibration.UnitName}";

    public string BackendSummary => _engine.Backend.CudaAvailable
        ? $"CPU + CUDA · {_engine.Backend.DeviceName}"
        : "CPU reference backend";

    public RelayCommand GenerateSyntheticCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand EstimateCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void GenerateSynthetic() => ReplaceVolume(
        SyntheticVolumes.Sphere(),
        $"Synthetic anisotropic sphere loaded · {_engine.Status}");

    private async Task OpenAsync()
    {
        try
        {
            var replacement = await _files.OpenVolumeAsync(CancellationToken.None);
            if (replacement is null) return;
            replacement.Validate();
            ReplaceVolume(replacement, $"TIFF stack loaded · {_engine.Status}");
        }
        catch (Exception error)
        {
            Status = $"Open failed; the previous volume was preserved: {error.Message}";
        }
    }

    internal async Task EstimateSelectedOperationAsync()
    {
        if (_volume is null || !IsDsltOperation) return;
        IsBusy = true;
        Status = "Calculating DSLT work estimate…";
        try
        {
            var estimate = await _engine.EstimateAsync(_volume, BuildParameters(), CancellationToken.None);
            WorkEstimateSummary = FormatEstimate(estimate);
            Status = estimate.WithinLimits
                ? "DSLT work estimate is within the CPU safety limits."
                : "DSLT work estimate exceeds the CPU safety limits.";
        }
        catch (Exception error)
        {
            WorkEstimateSummary = $"Estimate failed: {error.Message}";
            Status = WorkEstimateSummary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task RunSelectedOperationAsync()
    {
        if (_volume is null) return;
        _cancellation = new CancellationTokenSource();
        CancelCommand.NotifyCanExecuteChanged();
        IsBusy = true;
        Progress = 0;
        Status = $"Running {SelectedOperation.Name}…";
        var progress = new Progress<double>(value => Progress = Math.Clamp(value * 100, 0, 100));
        try
        {
            var parameters = BuildParameters();
            if (IsDsltOperation)
            {
                var estimate = await _engine.EstimateAsync(_volume, parameters, _cancellation.Token);
                WorkEstimateSummary = FormatEstimate(estimate);
                if (!estimate.WithinLimits)
                    throw new InvalidOperationException("The DSLT request exceeds the native CPU safety limits.");
            }
            var result = await _engine.RunAsync(_volume, parameters, progress, _cancellation.Token);
            _lastResult = result;
            _lastParameters = parameters;
            RefreshResultImage();
            SelectedStage = result.OutputKind == OutputKind.LabelsInt32 ? WorkflowStage.Edit : SelectedOperation.Stage;
            Status = result.CompletedPasses > 0
                ? $"Completed on {result.UsedBackend} · {result.ComponentCount} components · {result.CompletedPasses} C passes"
                : $"Completed on {result.UsedBackend} · {result.ComponentCount} components";
            Progress = 100;
            OnPropertyChanged(nameof(HasResult));
        }
        catch (OperationCanceledException)
        {
            Status = "Operation cancelled; the previous result was preserved.";
        }
        catch (Exception error)
        {
            Status = $"Operation failed; the previous result was preserved: {error.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            CancelCommand.NotifyCanExecuteChanged();
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_volume is null || _lastResult is null || _lastParameters is null) return;
        var basePath = _files.ChooseExportBasePath();
        if (basePath is null) return;
        IsBusy = true;
        try
        {
            await ResultPackageWriter.WriteAsync(basePath, _volume, _lastParameters, _lastResult);
            SelectedStage = WorkflowStage.Export;
            Status = $"Result package exported: {basePath}.json";
        }
        catch (Exception error)
        {
            Status = $"Export failed: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private OperationParameters BuildParameters() => new(
        Operation: SelectedOperation.Operation,
        Backend: Backend,
        Radius: Radius,
        Connectivity: Connectivity,
        MinimumComponentSize: SelectedOperation.Operation == ProcessingOperation.ConnectedComponents
            ? Math.Max(1, MinimumComponentSize)
            : MinimumComponentSize,
        SliceIndex: ZIndex,
        LanczosOrder: 2,
        Threshold: Threshold,
        ConstantC: SelectedOperation.Operation == ProcessingOperation.DsltThreshold
            ? -PreviewOffset * 0.002F
            : 0,
        WindowMinimum: WindowMinimum,
        WindowMaximum: WindowMaximum,
        TargetSpacingZ: 1,
        DirectionLevel: DirectionLevel,
        DsltKernel: DsltKernel,
        ZCorrectionFactor: ZCorrectionFactor,
        MinimumC: MinimumC,
        MaximumC: MaximumC,
        CInterval: CInterval,
        ClosingRadius: ClosingRadius,
        MinimumInvalidStructureArea: MinimumInvalidStructureArea);

    private void ReplaceVolume(VolumeData replacement, string status)
    {
        replacement.Validate();
        _volume = replacement;
        _channelIndex = replacement.SelectedChannel;
        _zIndex = replacement.Depth / 2;
        _lastResult = null;
        _lastParameters = null;
        ResultImage = null;
        WorkEstimateSummary = "Select a DSLT operation to calculate its work estimate.";
        Progress = 0;
        Status = status;
        SelectedStage = WorkflowStage.Inspect;
        RefreshImages();
        OnPropertyChanged(nameof(ChannelIndex));
        OnPropertyChanged(nameof(ZIndex));
        OnPropertyChanged(nameof(MaximumChannelIndex));
        OnPropertyChanged(nameof(MaximumZIndex));
        OnPropertyChanged(nameof(VolumeSummary));
        OnPropertyChanged(nameof(HasVolume));
        OnPropertyChanged(nameof(HasResult));
        NotifyCommandStates();
    }

    private void RefreshImages()
    {
        if (_volume is not null)
            SourceImage = CreateVolumeSlice(_volume, ZIndex, WindowMinimum, WindowMaximum);
        RefreshResultImage();
    }

    private void RefreshResultImage()
    {
        if (_lastResult?.FloatData is not null)
        {
            ResultImage = CreateSlice(
                _lastResult.FloatData, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                ZIndex, WindowMinimum, WindowMaximum);
        }
        else if (_lastResult?.Labels is not null)
        {
            ResultImage = CreateLabelSlice(
                _lastResult.Labels, _lastResult.Width, _lastResult.Height, _lastResult.Depth, ZIndex);
        }
    }

    private bool CanEstimate() => HasVolume && _engine.IsAvailable && IsDsltOperation && !IsBusy;

    private void NotifyCommandStates()
    {
        GenerateSyntheticCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        EstimateCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private static string FormatEstimate(ProcessingWorkEstimate estimate) =>
        $"{estimate.DirectionCount:N0} directions · {estimate.LineSamplesPerVoxel:N0} samples/voxel · " +
        $"{estimate.DirectionalWorkItems:N0} directional samples · {estimate.EstimatedHostBytes / (1024.0 * 1024.0):N1} MiB host · " +
        $"{estimate.SweepPasses:N0} pass(es) · {(estimate.WithinLimits ? "within limits" : "over limit")}";

    private static BitmapSource CreateVolumeSlice(VolumeData volume, int zIndex, float minimum, float maximum)
    {
        var voxelCount = volume.VoxelCount;
        var channelOffset = checked(volume.SelectedChannel * voxelCount);
        return CreateSlice(
            volume.Samples.AsSpan(channelOffset, voxelCount),
            volume.Width, volume.Height, volume.Depth, zIndex, minimum, maximum);
    }

    private static BitmapSource CreateSlice(
        ReadOnlySpan<float> volume,
        int width,
        int height,
        int depth,
        int zIndex,
        float minimum,
        float maximum)
    {
        var slice = Math.Clamp(zIndex, 0, Math.Max(0, depth - 1));
        var offset = checked(slice * width * height);
        var pixels = new byte[checked(width * height)];
        var scale = 1.0F / Math.Max(0.000001F, maximum - minimum);
        for (var i = 0; i < pixels.Length; i++)
        {
            var normalized = Math.Clamp((volume[offset + i] - minimum) * scale, 0, 1);
            pixels[i] = (byte)Math.Clamp((int)Math.Round(normalized * 255), 0, 255);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateLabelSlice(int[] labels, int width, int height, int depth, int zIndex)
    {
        var slice = Math.Clamp(zIndex, 0, Math.Max(0, depth - 1));
        var offset = checked(slice * width * height);
        var pixels = new byte[checked(width * height * 3)];
        for (var i = 0; i < width * height; i++)
        {
            var label = labels[offset + i];
            if (label < 0) continue;
            pixels[i * 3] = (byte)(53 + label * 97);
            pixels[i * 3 + 1] = (byte)(151 + label * 57);
            pixels[i * 3 + 2] = (byte)(211 + label * 31);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }
}
