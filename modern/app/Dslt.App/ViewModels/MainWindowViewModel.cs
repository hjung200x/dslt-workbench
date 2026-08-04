using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dslt.App.Infrastructure;
using Dslt.App.Services;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Segmentation;
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
    private ImageSource? _sourceYzImage;
    private ImageSource? _sourceZxImage;
    private ImageSource? _resultImage;
    private ImageSource? _resultYzImage;
    private ImageSource? _resultZxImage;
    private string _status;
    private string _workEstimateSummary = "Select a DSLT operation to calculate its work estimate.";
    private double _progress;
    private float _threshold = 0.5F;
    private float _heightMapThreshold = 0.25F;
    private float _windowMinimum;
    private float _windowMaximum = 1.0F;
    private int _radius = 1;
    private int _dsltRadius = 14;
    private int _adaptiveThresholdRadius = 14;
    private int _channelIndex;
    private int _xIndex;
    private int _yIndex;
    private int _zIndex;
    private double _zoom = 1;
    private int _connectivity = 6;
    private int _minimumComponentSize;
    private int _thresholdSweepMinimumComponentSize;
    private int _directionLevel = 2;
    private DsltKernelType _dsltKernel = DsltKernelType.Mean;
    private DsltKernelType _adaptiveThresholdKernel = DsltKernelType.Mean;
    private float _adaptiveThresholdOffset = 20;
    private float _hMinimaHeight = 0.1F;
    private int _hMinimaCheckInterval = 50;
    private int _heightMapXyRadius;
    private int _heightMapZRadius = 4;
    private DsltKernelType _heightMapKernel = DsltKernelType.Gaussian;
    private int _heightMapSmoothLevel = 1;
    private HeightProjectionMode _projectionMode = HeightProjectionMode.Z;
    private float _projectionOffset;
    private float _projectionStartDepth;
    private int _projectionRange;
    private float _projectionThreshold;
    private bool _depthColorEnabled;
    private int _depthColorRange = 100;
    private float _targetSpacingZ = 1;
    private int _lanczosOrder = 2;
    private float _previewOffset = 20;
    private float _zCorrectionFactor = 0.2F;
    private float _minimumC;
    private float _maximumC;
    private float _cInterval = 0.002F;
    private float _minimumThreshold;
    private float _maximumThreshold = 1.0F;
    private float _thresholdInterval = 0.02F;
    private int _closingRadius = 2;
    private int _minimumInvalidStructureArea = 500;
    private int _thresholdSweepMinimumInvalidStructureArea = 100;
    private ProcessingBackend _backend = ProcessingBackend.Auto;
    private OperationOption _selectedOperation;
    private WorkflowStage _selectedStage = WorkflowStage.Inspect;
    private ProcessingResult? _lastResult;
    private OperationParameters? _lastParameters;
    private byte[]? _lastDepthColorPixels;
    private LabelEditingSession? _editingSession;
    private readonly List<string> _editHistory = [];
    private readonly Stack<(int X, int Y, int Z)> _editOriginUndo = [];
    private int _resultOriginX;
    private int _resultOriginY;
    private int _resultOriginZ;
    private int _editIterations = 1;
    private bool _addToSelection;
    private string _selectionSummary = "No label selection";
    private bool _isBusy;

    public MainWindowViewModel(IProcessingEngine engine, IWorkspaceFileService files)
    {
        _engine = engine;
        _files = files;
        _status = engine.Status;
        Operations =
        [
            new("Window / level", ProcessingOperation.WindowLevel, WorkflowStage.Inspect),
            new("Threshold 2D", ProcessingOperation.Threshold2D, WorkflowStage.Process),
            new("Threshold 3D", ProcessingOperation.Threshold3D, WorkflowStage.Process),
            new("Adaptive threshold 2D", ProcessingOperation.AdaptiveThreshold2D, WorkflowStage.Process),
            new("Adaptive threshold 3D", ProcessingOperation.AdaptiveThreshold3D, WorkflowStage.Process),
            new("H-minima transform", ProcessingOperation.HMinima, WorkflowStage.Process),
            new("Mean smoothing", ProcessingOperation.SmoothMean, WorkflowStage.Process),
            new("Gaussian smoothing", ProcessingOperation.SmoothGaussian, WorkflowStage.Process),
            new("Dilate cube", ProcessingOperation.DilateCube, WorkflowStage.Process),
            new("Erode cube", ProcessingOperation.ErodeCube, WorkflowStage.Process),
            new("Dilate sphere", ProcessingOperation.DilateSphere, WorkflowStage.Process),
            new("Erode sphere", ProcessingOperation.ErodeSphere, WorkflowStage.Process),
            new("Z resample - area average", ProcessingOperation.ResampleZArea, WorkflowStage.Process),
            new("Z resample - Lanczos", ProcessingOperation.ResampleZLanczos, WorkflowStage.Process),
            new("Filtered height map", ProcessingOperation.HeightMap, WorkflowStage.Process),
            new("Depth map", ProcessingOperation.DepthMap, WorkflowStage.Process),
            new("Height projection", ProcessingOperation.HeightProjection, WorkflowStage.Process),
            new("Connected components", ProcessingOperation.ConnectedComponents, WorkflowStage.Segment),
            new("Threshold sweep segmentation", ProcessingOperation.ThresholdSweep, WorkflowStage.Segment),
            new("DSLT threshold preview", ProcessingOperation.DsltThreshold, WorkflowStage.Segment),
            new("DSLT iterative segmentation", ProcessingOperation.DsltSegmentation, WorkflowStage.Segment),
            new("Watershed from selected labels", ProcessingOperation.Watershed, WorkflowStage.Segment),
        ];
        _selectedOperation = Operations.Single(option =>
            option.Operation == ProcessingOperation.Threshold3D);
        GenerateSyntheticCommand = new RelayCommand(GenerateSynthetic, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
        EstimateCommand = new AsyncRelayCommand(EstimateSelectedOperationAsync, CanEstimate);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasResult && !IsBusy);
        RunCommand = new AsyncRelayCommand(RunSelectedOperationAsync, CanRun);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        SelectAtCursorCommand = new RelayCommand(SelectAtCursor, () => CanEdit && !IsBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection && !IsBusy);
        MergeSelectionCommand = new RelayCommand(MergeSelection, () => SelectedLabelCount >= 2 && !IsBusy);
        SplitSelectionCommand = new RelayCommand(SplitSelection, () => HasSelection && !IsBusy);
        DilateSelectionCommand = new RelayCommand(DilateSelection, () => HasSelection && !IsBusy);
        ErodeSelectionCommand = new RelayCommand(ErodeSelection, () => HasSelection && !IsBusy);
        CropSelectionCommand = new RelayCommand(CropSelection, () => HasSelection && !IsBusy);
        UndoEditCommand = new RelayCommand(UndoEdit, () => _editingSession?.CanUndo == true && !IsBusy);
        GenerateSynthetic();
    }

    public IReadOnlyList<OperationOption> Operations { get; }
    public IReadOnlyList<WorkflowStage> WorkflowStages { get; } = Enum.GetValues<WorkflowStage>();
    public IReadOnlyList<ProcessingBackend> Backends { get; } =
        [ProcessingBackend.Auto, ProcessingBackend.Cpu, ProcessingBackend.Cuda];
    public IReadOnlyList<int> Connectivities { get; } = [6, 18, 26];
    public IReadOnlyList<DsltKernelType> DsltKernels { get; } = Enum.GetValues<DsltKernelType>();
    public IReadOnlyList<HeightProjectionMode> ProjectionModes { get; } = Enum.GetValues<HeightProjectionMode>();
    public IReadOnlyList<int> LanczosOrders { get; } = [2, 3];

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
            OnPropertyChanged(nameof(IsThresholdSweep));
            OnPropertyChanged(nameof(IsAdaptiveThreshold));
            OnPropertyChanged(nameof(IsHMinima));
            OnPropertyChanged(nameof(IsWatershed));
            OnPropertyChanged(nameof(IsHeightMap));
            OnPropertyChanged(nameof(IsHeightSurfaceOperation));
            OnPropertyChanged(nameof(IsHeightProjection));
            OnPropertyChanged(nameof(IsResampleZ));
            OnPropertyChanged(nameof(IsLanczosResample));
            OnPropertyChanged(nameof(CanUseDepthColoring));
            if (!CanUseDepthColoring) DepthColorEnabled = false;
            OnPropertyChanged(nameof(MinimumComponentSizeLabel));
            OnPropertyChanged(nameof(MinimumInvalidStructureArea));
            OnPropertyChanged(nameof(MinimumComponentSize));
            OnPropertyChanged(nameof(UsesThreshold));
            OnPropertyChanged(nameof(Threshold));
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
        get => IsHeightSurfaceOperation ? _heightMapThreshold : _threshold;
        set
        {
            if (IsHeightSurfaceOperation)
                SetProperty(ref _heightMapThreshold, Math.Clamp(value, 0, 1));
            else
                SetProperty(ref _threshold, Math.Clamp(value, 0, 1));
        }
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
        get => IsDsltOperation ? _dsltRadius :
            IsAdaptiveThreshold ? _adaptiveThresholdRadius : _radius;
        set
        {
            if (IsDsltOperation)
                SetProperty(ref _dsltRadius, Math.Clamp(value, 1, 100));
            else if (IsAdaptiveThreshold)
                SetProperty(ref _adaptiveThresholdRadius, Math.Clamp(value, 0, 100));
            else
                SetProperty(ref _radius, Math.Clamp(value, 0, 64));
        }
    }

    public int MinimumRadius => IsDsltOperation ? 1 : 0;
    public int MaximumRadius => IsDsltOperation || IsAdaptiveThreshold ? 100 : 64;

    public int ChannelIndex
    {
        get => _channelIndex;
        set
        {
            if (_volume is null) return;
            var selected = Math.Clamp(value, 0, _volume.Channels - 1);
            if (!SetProperty(ref _channelIndex, selected)) return;
            _volume = _volume with { SelectedChannel = selected };
            RefreshImages();
            OnPropertyChanged(nameof(VolumeSummary));
        }
    }

    public int XIndex
    {
        get => _xIndex;
        set
        {
            var maximum = Math.Max(0, (_volume?.Width ?? 1) - 1);
            if (!SetProperty(ref _xIndex, Math.Clamp(value, 0, maximum))) return;
            RefreshImages();
        }
    }

    public int YIndex
    {
        get => _yIndex;
        set
        {
            var maximum = Math.Max(0, (_volume?.Height ?? 1) - 1);
            if (!SetProperty(ref _yIndex, Math.Clamp(value, 0, maximum))) return;
            RefreshImages();
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
    public IReadOnlyList<string> ChannelLabels
    {
        get
        {
            var count = _volume?.Channels ?? 1;
            return Enumerable.Range(0, count)
                .Select(index => $"{index + 1}: {ResolveChannelName(_volume, index)}")
                .ToArray();
        }
    }
    public int MaximumXIndex => Math.Max(0, (_volume?.Width ?? 1) - 1);
    public int MaximumYIndex => Math.Max(0, (_volume?.Height ?? 1) - 1);
    public int MaximumZIndex => Math.Max(0, (_volume?.Depth ?? 1) - 1);

    public double Zoom
    {
        get => _zoom;
        set => SetProperty(ref _zoom, Math.Clamp(value, 0.25, 8));
    }

    public int Connectivity
    {
        get => _connectivity;
        set => SetProperty(ref _connectivity, value is 6 or 18 or 26 ? value : 6);
    }

    public int MinimumComponentSize
    {
        get => IsThresholdSweep ? _thresholdSweepMinimumComponentSize : _minimumComponentSize;
        set
        {
            if (IsThresholdSweep)
                SetProperty(ref _thresholdSweepMinimumComponentSize, Math.Max(0, value));
            else
                SetProperty(ref _minimumComponentSize, Math.Max(0, value));
        }
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

    public DsltKernelType AdaptiveThresholdKernel
    {
        get => _adaptiveThresholdKernel;
        set => SetProperty(ref _adaptiveThresholdKernel, value);
    }

    public float AdaptiveThresholdOffset
    {
        get => _adaptiveThresholdOffset;
        set => SetProperty(ref _adaptiveThresholdOffset,
            float.IsFinite(value) ? Math.Clamp(value, -100, 500) : 20);
    }

    public float HMinimaHeight
    {
        get => _hMinimaHeight;
        set => SetProperty(ref _hMinimaHeight,
            float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.1F);
    }

    public int HMinimaCheckInterval
    {
        get => _hMinimaCheckInterval;
        set => SetProperty(ref _hMinimaCheckInterval, Math.Clamp(value, 1, 10_000));
    }

    public int HeightMapXyRadius
    {
        get => _heightMapXyRadius;
        set => SetProperty(ref _heightMapXyRadius, Math.Clamp(value, 0, 64));
    }

    public int HeightMapZRadius
    {
        get => _heightMapZRadius;
        set => SetProperty(ref _heightMapZRadius, Math.Clamp(value, 0, 64));
    }

    public DsltKernelType HeightMapKernel
    {
        get => _heightMapKernel;
        set => SetProperty(ref _heightMapKernel, value);
    }

    public int HeightMapSmoothLevel
    {
        get => _heightMapSmoothLevel;
        set => SetProperty(ref _heightMapSmoothLevel, Math.Clamp(value, 0, 10));
    }

    public HeightProjectionMode ProjectionMode
    {
        get => _projectionMode;
        set
        {
            if (!SetProperty(ref _projectionMode, value)) return;
            OnPropertyChanged(nameof(CanUseDepthColoring));
            if (!CanUseDepthColoring) DepthColorEnabled = false;
        }
    }

    public float ProjectionOffset
    {
        get => _projectionOffset;
        set => SetProperty(ref _projectionOffset,
            float.IsFinite(value) ? Math.Clamp(value, -MaximumProjectionDepth, MaximumProjectionDepth) : 0);
    }

    public float ProjectionStartDepth
    {
        get => _projectionStartDepth;
        set => SetProperty(ref _projectionStartDepth,
            float.IsFinite(value) ? Math.Clamp(value, -MaximumProjectionDepth, MaximumProjectionDepth) : 0);
    }

    public int ProjectionRange
    {
        get => _projectionRange;
        set => SetProperty(ref _projectionRange, Math.Clamp(value, 0, MaximumProjectionDepth));
    }

    public float ProjectionThreshold
    {
        get => _projectionThreshold;
        set => SetProperty(ref _projectionThreshold,
            float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);
    }

    public int MaximumProjectionDepth => Math.Max(0, (_volume?.Depth ?? 1) - 1);
    public int MinimumProjectionDepth => -MaximumProjectionDepth;
    public bool CanUseDepthColoring => IsHeightProjection && ProjectionMode == HeightProjectionMode.Z;

    public bool DepthColorEnabled
    {
        get => _depthColorEnabled;
        set => SetProperty(ref _depthColorEnabled, value && CanUseDepthColoring);
    }

    public int DepthColorRange
    {
        get => _depthColorRange;
        set => SetProperty(ref _depthColorRange, Math.Clamp(value, 1, 500));
    }

    public bool IsResampleZ => SelectedOperation.Operation is
        ProcessingOperation.ResampleZArea or ProcessingOperation.ResampleZLanczos;
    public bool IsLanczosResample =>
        SelectedOperation.Operation == ProcessingOperation.ResampleZLanczos;

    public float TargetSpacingZ
    {
        get => _targetSpacingZ;
        set => SetProperty(ref _targetSpacingZ,
            float.IsFinite(value) && value > 0 ? value : DefaultTargetSpacingZ());
    }

    public int LanczosOrder
    {
        get => _lanczosOrder;
        set => SetProperty(ref _lanczosOrder, value is 2 or 3 ? value : 2);
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

    public float MinimumThreshold
    {
        get => _minimumThreshold;
        set => SetProperty(ref _minimumThreshold, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);
    }

    public float MaximumThreshold
    {
        get => _maximumThreshold;
        set => SetProperty(ref _maximumThreshold, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1);
    }

    public float ThresholdInterval
    {
        get => _thresholdInterval;
        set => SetProperty(ref _thresholdInterval,
            float.IsFinite(value) ? Math.Clamp(value, 0.000001F, 1) : 0.02F);
    }

    public int ClosingRadius
    {
        get => _closingRadius;
        set => SetProperty(ref _closingRadius, Math.Clamp(value, 0, 50));
    }

    public int MinimumInvalidStructureArea
    {
        get => IsThresholdSweep
            ? _thresholdSweepMinimumInvalidStructureArea
            : _minimumInvalidStructureArea;
        set
        {
            if (IsThresholdSweep)
                SetProperty(ref _thresholdSweepMinimumInvalidStructureArea, Math.Clamp(value, 0, 10_000));
            else
                SetProperty(ref _minimumInvalidStructureArea, Math.Clamp(value, 0, 10_000));
        }
    }

    public ImageSource? SourceImage
    {
        get => _sourceImage;
        private set => SetProperty(ref _sourceImage, value);
    }

    public ImageSource? SourceYzImage
    {
        get => _sourceYzImage;
        private set => SetProperty(ref _sourceYzImage, value);
    }

    public ImageSource? SourceZxImage
    {
        get => _sourceZxImage;
        private set => SetProperty(ref _sourceZxImage, value);
    }

    public ImageSource? ResultImage
    {
        get => _resultImage;
        private set => SetProperty(ref _resultImage, value);
    }

    public ImageSource? ResultYzImage
    {
        get => _resultYzImage;
        private set => SetProperty(ref _resultYzImage, value);
    }

    public ImageSource? ResultZxImage
    {
        get => _resultZxImage;
        private set => SetProperty(ref _resultZxImage, value);
    }

    public int EditIterations
    {
        get => _editIterations;
        set => SetProperty(ref _editIterations, Math.Clamp(value, 1, 64));
    }

    public bool AddToSelection
    {
        get => _addToSelection;
        set => SetProperty(ref _addToSelection, value);
    }

    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => SetProperty(ref _selectionSummary, value);
    }

    public int ResultOriginX => _resultOriginX;
    public int ResultOriginY => _resultOriginY;
    public int ResultOriginZ => _resultOriginZ;
    public string ResultGeometrySummary => _lastResult is null
        ? "No result geometry"
        : $"Origin ({ResultOriginX}, {ResultOriginY}, {ResultOriginZ}) · " +
          $"size {_lastResult.Width} × {_lastResult.Height} × {_lastResult.Depth}";

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
    public bool CanEdit => _editingSession is not null;
    public bool HasSelection => SelectedLabelCount > 0;
    public int SelectedLabelCount => _editingSession?.Selection.Count ?? 0;
    public bool IsDsltOperation => SelectedOperation.Operation is ProcessingOperation.DsltThreshold or ProcessingOperation.DsltSegmentation;
    public bool IsDsltSegmentation => SelectedOperation.Operation == ProcessingOperation.DsltSegmentation;
    public bool IsThresholdSweep => SelectedOperation.Operation == ProcessingOperation.ThresholdSweep;
    public bool IsAdaptiveThreshold => SelectedOperation.Operation is
        ProcessingOperation.AdaptiveThreshold2D or ProcessingOperation.AdaptiveThreshold3D;
    public bool IsHMinima => SelectedOperation.Operation == ProcessingOperation.HMinima;
    public bool IsWatershed => SelectedOperation.Operation == ProcessingOperation.Watershed;
    public bool IsHeightMap => SelectedOperation.Operation == ProcessingOperation.HeightMap;
    public bool IsHeightSurfaceOperation => SelectedOperation.Operation is ProcessingOperation.HeightMap or
        ProcessingOperation.DepthMap or ProcessingOperation.HeightProjection;
    public bool IsHeightProjection => SelectedOperation.Operation == ProcessingOperation.HeightProjection;
    public string MinimumComponentSizeLabel => IsWatershed
        ? "Minimum selected seed size"
        : "Exclusive minimum component size";
    public bool UsesThreshold => SelectedOperation.Operation is ProcessingOperation.Threshold2D or ProcessingOperation.Threshold3D or
        ProcessingOperation.ConnectedComponents or ProcessingOperation.HeightMap or ProcessingOperation.DepthMap or
        ProcessingOperation.HeightProjection;
    public bool UsesRadius => SelectedOperation.Operation is ProcessingOperation.SmoothMean or ProcessingOperation.SmoothGaussian or
        ProcessingOperation.DilateCube or ProcessingOperation.ErodeCube or ProcessingOperation.DilateSphere or
        ProcessingOperation.ErodeSphere or ProcessingOperation.DsltThreshold or ProcessingOperation.DsltSegmentation or
        ProcessingOperation.AdaptiveThreshold2D or ProcessingOperation.AdaptiveThreshold3D;
    public ProcessingResult? LastResult => _lastResult;
    public OperationParameters? LastParameters => _lastParameters;

    public string VolumeSummary => _volume is null
        ? "No volume"
        : $"{_volume.Width} × {_volume.Height} × {_volume.Depth} · channel {ChannelIndex + 1}/{_volume.Channels} ({ResolveChannelName(_volume, ChannelIndex)}) · Z spacing {_volume.Calibration.SpacingZ.ToString("0.###", CultureInfo.InvariantCulture)} {_volume.Calibration.UnitName}";

    public string BackendSummary => _engine.Backend.CudaAvailable
        ? $"CPU + CUDA · {_engine.Backend.DeviceName}"
        : "CPU reference backend";

    public RelayCommand GenerateSyntheticCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand EstimateCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAtCursorCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand MergeSelectionCommand { get; }
    public RelayCommand SplitSelectionCommand { get; }
    public RelayCommand DilateSelectionCommand { get; }
    public RelayCommand ErodeSelectionCommand { get; }
    public RelayCommand CropSelectionCommand { get; }
    public RelayCommand UndoEditCommand { get; }

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
            ProcessingLabelState? labelState = null;
            var previousEditingSession = IsWatershed ? _editingSession : null;
            if (IsWatershed)
            {
                if (_editingSession is null || _editingSession.Selection.Count == 0)
                    throw new InvalidOperationException("Watershed requires at least one selected label seed.");
                labelState = new ProcessingLabelState(
                    _editingSession.Width,
                    _editingSession.Height,
                    _editingSession.Depth,
                    _editingSession.Labels.ToArray(),
                    _editingSession.Selection.Order().ToArray());
                labelState.Validate(_volume);
            }
            var parameters = BuildParameters(labelState);
            if (IsDsltOperation)
            {
                var estimate = await _engine.EstimateAsync(_volume, parameters, _cancellation.Token);
                WorkEstimateSummary = FormatEstimate(estimate);
                if (!estimate.WithinLimits)
                    throw new InvalidOperationException("The DSLT request exceeds the native CPU safety limits.");
            }
            var useDepthColor = parameters.DepthColorEnabled;
            var primaryProgress = useDepthColor
                ? new Progress<double>(value => Progress = Math.Clamp(value * 50, 0, 50))
                : progress;
            var result = await _engine.RunAsync(
                _volume, parameters, primaryProgress, _cancellation.Token, labelState);
            byte[]? depthColorPixels = null;
            if (useDepthColor)
            {
                var heightResult = await _engine.RunAsync(
                    _volume,
                    parameters with
                    {
                        Operation = ProcessingOperation.HeightMap,
                        DepthColorEnabled = false,
                    },
                    new Progress<double>(value => Progress = Math.Clamp(50 + value * 20, 50, 70)),
                    _cancellation.Token);
                var depthResult = await _engine.RunAsync(
                    _volume,
                    parameters with
                    {
                        Operation = ProcessingOperation.DepthMap,
                        DepthColorEnabled = false,
                    },
                    new Progress<double>(value => Progress = Math.Clamp(70 + value * 30, 70, 100)),
                    _cancellation.Token);
                depthColorPixels = DepthColorProjectionRenderer.CreateRgb24(
                    _volume,
                    heightResult.FloatData ??
                        throw new InvalidOperationException("Height-map output is required for depth coloring."),
                    depthResult.FloatData ??
                        throw new InvalidOperationException("Depth-map output is required for depth coloring."),
                    result.FloatData ??
                        throw new InvalidOperationException("Height-projection output is required for depth coloring."),
                    parameters);
            }
            if (IsWatershed && previousEditingSession is not null && result.Labels is not null)
            {
                previousEditingSession.ReplaceLabels(result.Labels);
                _editingSession = previousEditingSession;
                result = result with { Labels = previousEditingSession.Labels.ToArray() };
                _editHistory.Add("watershed");
            }
            else
            {
                _editHistory.Clear();
                _editingSession = result.Labels is null
                    ? null
                    : new LabelEditingSession(result.Width, result.Height, result.Depth, result.Labels);
            }
            _lastResult = result;
            _lastParameters = parameters;
            _lastDepthColorPixels = depthColorPixels;
            ResetResultGeometry();
            UpdateSelectionState();
            RefreshResultImage();
            SelectedStage = result.OutputKind == OutputKind.LabelsInt32 ? WorkflowStage.Edit : SelectedOperation.Stage;
            var passName = parameters.Operation switch
            {
                ProcessingOperation.ThresholdSweep => "threshold passes",
                ProcessingOperation.Watershed => "flood levels",
                _ => "C passes",
            };
            Status = result.CompletedPasses > 0
                ? $"Completed on {result.UsedBackend} · {result.ComponentCount} components · {result.CompletedPasses} {passName}"
                : $"Completed on {result.UsedBackend} · {result.ComponentCount} components";
            Progress = 100;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultGeometrySummary));
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
            await ResultPackageWriter.WriteAsync(
                basePath,
                _volume,
                _lastParameters,
                _lastResult,
                editHistory: _editHistory,
                outputOriginX: ResultOriginX,
                outputOriginY: ResultOriginY,
                outputOriginZ: ResultOriginZ);
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

    private OperationParameters BuildParameters(ProcessingLabelState? labelState = null) => new(
        Operation: SelectedOperation.Operation,
        Backend: Backend,
        Radius: Radius,
        Connectivity: Connectivity,
        MinimumComponentSize: SelectedOperation.Operation == ProcessingOperation.ConnectedComponents
            ? Math.Max(1, MinimumComponentSize)
            : MinimumComponentSize,
        SliceIndex: ZIndex,
        LanczosOrder: LanczosOrder,
        Threshold: Threshold,
        ConstantC: SelectedOperation.Operation == ProcessingOperation.DsltThreshold
            ? -PreviewOffset * 0.002F
            : IsAdaptiveThreshold ? -AdaptiveThresholdOffset * 0.002F : 0,
        WindowMinimum: WindowMinimum,
        WindowMaximum: WindowMaximum,
        TargetSpacingZ: TargetSpacingZ,
        DirectionLevel: DirectionLevel,
        DsltKernel: DsltKernel,
        ZCorrectionFactor: ZCorrectionFactor,
        MinimumC: MinimumC,
        MaximumC: MaximumC,
        CInterval: CInterval,
        MinimumThreshold: MinimumThreshold,
        MaximumThreshold: MaximumThreshold,
        ThresholdInterval: ThresholdInterval,
        ThresholdSweepMinimumComponentSize: IsThresholdSweep ? MinimumComponentSize : 0,
        ThresholdSweepMinimumInvalidStructureArea: IsThresholdSweep ? MinimumInvalidStructureArea : 100,
        AdaptiveThresholdKernel: AdaptiveThresholdKernel,
        HMinimaHeight: HMinimaHeight,
        HMinimaCheckInterval: HMinimaCheckInterval,
        HeightMapXyRadius: HeightMapXyRadius,
        HeightMapZRadius: HeightMapZRadius,
        HeightMapKernel: HeightMapKernel,
        HeightMapSmoothLevel: HeightMapSmoothLevel,
        ProjectionMode: ProjectionMode,
        ProjectionOffset: ProjectionOffset,
        ProjectionStartDepth: ProjectionStartDepth,
        ProjectionRange: ProjectionRange,
        ProjectionThreshold: ProjectionThreshold,
        DepthColorEnabled: DepthColorEnabled && CanUseDepthColoring,
        DepthColorRange: DepthColorRange,
        ClosingRadius: ClosingRadius,
        MinimumInvalidStructureArea: MinimumInvalidStructureArea,
        SeedLabelsSha256: labelState is null
            ? null
            : ProcessingProvenance.ComputeLabelSha256(labelState.Labels),
        SelectedSeedLabels: labelState?.SelectedLabels);

    private void ReplaceVolume(VolumeData replacement, string status)
    {
        replacement.Validate();
        _volume = replacement;
        _targetSpacingZ = DefaultTargetSpacingZ();
        _channelIndex = replacement.SelectedChannel;
        _xIndex = replacement.Width / 2;
        _yIndex = replacement.Height / 2;
        _zIndex = replacement.Depth / 2;
        _projectionOffset = Math.Clamp(_projectionOffset, -MaximumProjectionDepth, MaximumProjectionDepth);
        _projectionStartDepth = Math.Clamp(_projectionStartDepth, -MaximumProjectionDepth, MaximumProjectionDepth);
        _projectionRange = Math.Clamp(_projectionRange, 0, MaximumProjectionDepth);
        OnPropertyChanged(nameof(MaximumProjectionDepth));
        OnPropertyChanged(nameof(MinimumProjectionDepth));
        OnPropertyChanged(nameof(ProjectionOffset));
        OnPropertyChanged(nameof(ProjectionStartDepth));
        OnPropertyChanged(nameof(ProjectionRange));
        OnPropertyChanged(nameof(TargetSpacingZ));
        _lastResult = null;
        _lastParameters = null;
        _lastDepthColorPixels = null;
        _editingSession = null;
        _editHistory.Clear();
        ResetResultGeometry();
        ResultImage = null;
        ResultYzImage = null;
        ResultZxImage = null;
        UpdateSelectionState();
        WorkEstimateSummary = "Select a DSLT operation to calculate its work estimate.";
        Progress = 0;
        Status = status;
        SelectedStage = WorkflowStage.Inspect;
        RefreshImages();
        OnPropertyChanged(nameof(ChannelIndex));
        OnPropertyChanged(nameof(ChannelLabels));
        OnPropertyChanged(nameof(XIndex));
        OnPropertyChanged(nameof(YIndex));
        OnPropertyChanged(nameof(ZIndex));
        OnPropertyChanged(nameof(MaximumChannelIndex));
        OnPropertyChanged(nameof(MaximumXIndex));
        OnPropertyChanged(nameof(MaximumYIndex));
        OnPropertyChanged(nameof(MaximumZIndex));
        OnPropertyChanged(nameof(VolumeSummary));
        OnPropertyChanged(nameof(HasVolume));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultGeometrySummary));
        NotifyCommandStates();
    }

    private void RefreshImages()
    {
        if (_volume is not null)
        {
            var source = SelectedChannelSamples(_volume);
            SourceImage = CreateFloatPlane(
                source, _volume.Width, _volume.Height, _volume.Depth,
                OrthogonalPlane.Xy, ZIndex, WindowMinimum, WindowMaximum);
            SourceYzImage = CreateFloatPlane(
                source, _volume.Width, _volume.Height, _volume.Depth,
                OrthogonalPlane.Yz, XIndex, WindowMinimum, WindowMaximum);
            SourceZxImage = CreateFloatPlane(
                source, _volume.Width, _volume.Height, _volume.Depth,
                OrthogonalPlane.Zx, YIndex, WindowMinimum, WindowMaximum);
        }
        RefreshResultImage();
    }

    private void RefreshResultImage()
    {
        if (_lastResult is { } colorResult && _lastDepthColorPixels is not null)
        {
            ResultImage = CreateRgb24Image(_lastDepthColorPixels, colorResult.Width, colorResult.Height);
            ResultYzImage = null;
            ResultZxImage = null;
        }
        else if (_lastResult?.FloatData is not null)
        {
            ResultImage = CreateFloatPlane(
                _lastResult.FloatData, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Xy, ZIndex - ResultOriginZ, WindowMinimum, WindowMaximum);
            ResultYzImage = CreateFloatPlane(
                _lastResult.FloatData, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Yz, XIndex - ResultOriginX, WindowMinimum, WindowMaximum);
            ResultZxImage = CreateFloatPlane(
                _lastResult.FloatData, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Zx, YIndex - ResultOriginY, WindowMinimum, WindowMaximum);
        }
        else if (_lastResult?.Labels is not null)
        {
            ResultImage = CreateLabelPlane(
                _lastResult.Labels, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Xy, ZIndex - ResultOriginZ, _editingSession?.Selection);
            ResultYzImage = CreateLabelPlane(
                _lastResult.Labels, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Yz, XIndex - ResultOriginX, _editingSession?.Selection);
            ResultZxImage = CreateLabelPlane(
                _lastResult.Labels, _lastResult.Width, _lastResult.Height, _lastResult.Depth,
                OrthogonalPlane.Zx, YIndex - ResultOriginY, _editingSession?.Selection);
        }
        else
        {
            ResultImage = null;
            ResultYzImage = null;
            ResultZxImage = null;
        }
    }

    private void SelectAtCursor()
    {
        if (_editingSession is null) return;
        var x = XIndex - ResultOriginX;
        var y = YIndex - ResultOriginY;
        var z = ZIndex - ResultOriginZ;
        if (x < 0 || y < 0 || z < 0 ||
            x >= _editingSession.Width || y >= _editingSession.Height || z >= _editingSession.Depth)
        {
            Status = $"Cursor ({XIndex}, {YIndex}, {ZIndex}) is outside the cropped result; selection was preserved.";
            return;
        }
        var label = _editingSession.Labels.Span[z * _editingSession.Width * _editingSession.Height +
                                                 y * _editingSession.Width + x];
        if (label == LabelEditingSession.Background)
        {
            Status = $"Cursor ({XIndex}, {YIndex}, {ZIndex}) is on background; selection was preserved.";
            return;
        }
        _editingSession.Select([label], replace: !AddToSelection);
        UpdateSelectionState();
        RefreshResultImage();
        Status = $"Selected label {label} at ({XIndex}, {YIndex}, {ZIndex}).";
    }

    private void ClearSelection()
    {
        _editingSession?.ClearSelection();
        UpdateSelectionState();
        RefreshResultImage();
        Status = "Label selection cleared.";
    }

    private void MergeSelection() => ApplyLabelEdit(() =>
    {
        var destination = _editingSession!.MergeSelected();
        return $"Merged selected labels into label {destination}.";
    });

    private void SplitSelection() => ApplyLabelEdit(() =>
    {
        var created = _editingSession!.SplitSelected(Connectivity);
        return $"Split selection with {Connectivity}-connectivity; created {created} label(s).";
    });

    private void DilateSelection() => ApplyLabelEdit(() =>
    {
        _editingSession!.DilateSelected(EditIterations, Connectivity);
        return $"Dilated selected labels by {EditIterations} iteration(s).";
    });

    private void ErodeSelection() => ApplyLabelEdit(() =>
    {
        _editingSession!.ErodeSelected(EditIterations, Connectivity);
        return $"Eroded selected labels by {EditIterations} iteration(s).";
    });

    private void CropSelection()
    {
        if (!ApplyLabelEdit(() =>
            {
                var crop = _editingSession!.CropSelected();
                _resultOriginX = checked(_resultOriginX + crop.OriginX);
                _resultOriginY = checked(_resultOriginY + crop.OriginY);
                _resultOriginZ = checked(_resultOriginZ + crop.OriginZ);
                return $"Cropped selection to origin ({ResultOriginX}, {ResultOriginY}, {ResultOriginZ}) " +
                       $"and size {crop.Width} × {crop.Height} × {crop.Depth}.";
            })) return;
        XIndex = Math.Clamp(ResultOriginX + _editingSession!.Width / 2, 0, MaximumXIndex);
        YIndex = Math.Clamp(ResultOriginY + _editingSession.Height / 2, 0, MaximumYIndex);
        ZIndex = Math.Clamp(ResultOriginZ + _editingSession.Depth / 2, 0, MaximumZIndex);
    }

    private void UndoEdit()
    {
        if (_editingSession?.Undo() != true) return;
        if (_editOriginUndo.TryPop(out var origin))
        {
            _resultOriginX = origin.X;
            _resultOriginY = origin.Y;
            _resultOriginZ = origin.Z;
        }
        PublishEditedLabels();
        _editHistory.Add("undo");
        Status = "Undid the last label edit.";
    }

    private bool ApplyLabelEdit(Func<string> edit)
    {
        var previousOrigin = (ResultOriginX, ResultOriginY, ResultOriginZ);
        try
        {
            var message = edit();
            _editOriginUndo.Push(previousOrigin);
            PublishEditedLabels();
            _editHistory.Add(message);
            Status = message;
            return true;
        }
        catch (Exception error)
        {
            _resultOriginX = previousOrigin.ResultOriginX;
            _resultOriginY = previousOrigin.ResultOriginY;
            _resultOriginZ = previousOrigin.ResultOriginZ;
            Status = $"Label edit failed; the current result was preserved: {error.Message}";
            return false;
        }
    }

    private void PublishEditedLabels()
    {
        if (_editingSession is null || _lastResult is null) return;
        var labels = _editingSession.Labels.ToArray();
        _lastResult = _lastResult with
        {
            Width = _editingSession.Width,
            Height = _editingSession.Height,
            Depth = _editingSession.Depth,
            Labels = labels,
            ComponentCount = labels.Where(label => label >= 0).Distinct().Count(),
        };
        OnPropertyChanged(nameof(ResultOriginX));
        OnPropertyChanged(nameof(ResultOriginY));
        OnPropertyChanged(nameof(ResultOriginZ));
        OnPropertyChanged(nameof(ResultGeometrySummary));
        UpdateSelectionState();
        RefreshResultImage();
    }

    private void ResetResultGeometry()
    {
        _resultOriginX = 0;
        _resultOriginY = 0;
        _resultOriginZ = 0;
        _editOriginUndo.Clear();
        OnPropertyChanged(nameof(ResultOriginX));
        OnPropertyChanged(nameof(ResultOriginY));
        OnPropertyChanged(nameof(ResultOriginZ));
        OnPropertyChanged(nameof(ResultGeometrySummary));
    }

    private void UpdateSelectionState()
    {
        var selection = _editingSession?.Selection.Order().ToArray() ?? [];
        SelectionSummary = selection.Length == 0
            ? (_editingSession is null ? "No editable label result" : "No label selection")
            : $"Selected ({selection.Length}): {string.Join(", ", selection)}";
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedLabelCount));
        NotifyCommandStates();
    }

    private bool CanEstimate() => HasVolume && _engine.IsAvailable && IsDsltOperation && !IsBusy;

    private bool CanRun() => HasVolume && _engine.IsAvailable && !IsBusy &&
        (!IsWatershed || (_editingSession is not null && _editingSession.Selection.Count > 0 &&
            _volume is not null && _editingSession.Width == _volume.Width &&
            _editingSession.Height == _volume.Height && _editingSession.Depth == _volume.Depth));

    private void NotifyCommandStates()
    {
        GenerateSyntheticCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        EstimateCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        SelectAtCursorCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        MergeSelectionCommand.NotifyCanExecuteChanged();
        SplitSelectionCommand.NotifyCanExecuteChanged();
        DilateSelectionCommand.NotifyCanExecuteChanged();
        ErodeSelectionCommand.NotifyCanExecuteChanged();
        CropSelectionCommand.NotifyCanExecuteChanged();
        UndoEditCommand.NotifyCanExecuteChanged();
    }

    private static string FormatEstimate(ProcessingWorkEstimate estimate) =>
        $"{estimate.DirectionCount:N0} directions · {estimate.LineSamplesPerVoxel:N0} samples/voxel · " +
        $"{estimate.DirectionalWorkItems:N0} directional samples · {estimate.EstimatedHostBytes / (1024.0 * 1024.0):N1} MiB host · " +
        $"{estimate.SweepPasses:N0} pass(es) · {(estimate.WithinLimits ? "within limits" : "over limit")}";

    private static string ResolveChannelName(VolumeData? volume, int channel)
    {
        if (volume?.Source?.ChannelMetadata is { } metadata &&
            metadata.Count == volume.Channels &&
            channel >= 0 && channel < metadata.Count)
            return metadata[channel].Name;
        return $"Channel {channel + 1}";
    }

    private float DefaultTargetSpacingZ()
    {
        var spacing = _volume?.Calibration.SpacingX ?? 1;
        return (float)Math.Min(spacing, float.MaxValue);
    }

    private static ReadOnlySpan<float> SelectedChannelSamples(VolumeData volume)
    {
        var voxelCount = volume.VoxelCount;
        return volume.Samples.AsSpan(checked(volume.SelectedChannel * voxelCount), voxelCount);
    }

    private static BitmapSource CreateFloatPlane(
        ReadOnlySpan<float> volume,
        int width,
        int height,
        int depth,
        OrthogonalPlane plane,
        int coordinate,
        float minimum,
        float maximum)
    {
        var (planeWidth, planeHeight) = PlaneDimensions(width, height, depth, plane);
        var pixels = new byte[checked(planeWidth * planeHeight)];
        var scale = 1.0F / Math.Max(0.000001F, maximum - minimum);
        for (var vertical = 0; vertical < planeHeight; vertical++)
        for (var horizontal = 0; horizontal < planeWidth; horizontal++)
        {
            var (x, y, z) = PlaneCoordinates(
                horizontal, vertical, coordinate, width, height, depth, plane);
            var sourceIndex = checked(z * width * height + y * width + x);
            var normalized = Math.Clamp((volume[sourceIndex] - minimum) * scale, 0, 1);
            pixels[vertical * planeWidth + horizontal] =
                (byte)Math.Clamp((int)Math.Round(normalized * 255), 0, 255);
        }
        var bitmap = BitmapSource.Create(
            planeWidth, planeHeight, 96, 96, PixelFormats.Gray8, null, pixels, planeWidth);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateRgb24Image(byte[] pixels, int width, int height)
    {
        if (pixels.Length != checked(width * height * 3))
            throw new ArgumentException("RGB24 pixels do not match the image dimensions.", nameof(pixels));
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Rgb24, null, pixels, checked(width * 3));
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateLabelPlane(
        int[] labels,
        int width,
        int height,
        int depth,
        OrthogonalPlane plane,
        int coordinate,
        IReadOnlySet<int>? selection)
    {
        var (planeWidth, planeHeight) = PlaneDimensions(width, height, depth, plane);
        var pixels = new byte[checked(planeWidth * planeHeight * 3)];
        for (var vertical = 0; vertical < planeHeight; vertical++)
        for (var horizontal = 0; horizontal < planeWidth; horizontal++)
        {
            var (x, y, z) = PlaneCoordinates(
                horizontal, vertical, coordinate, width, height, depth, plane);
            var label = labels[checked(z * width * height + y * width + x)];
            if (label < 0) continue;
            var destination = checked((vertical * planeWidth + horizontal) * 3);
            if (selection?.Contains(label) == true)
            {
                pixels[destination] = 255;
                pixels[destination + 1] = 218;
                pixels[destination + 2] = 74;
            }
            else
            {
                pixels[destination] = (byte)(53 + label * 97);
                pixels[destination + 1] = (byte)(151 + label * 57);
                pixels[destination + 2] = (byte)(211 + label * 31);
            }
        }
        var bitmap = BitmapSource.Create(
            planeWidth, planeHeight, 96, 96, PixelFormats.Rgb24, null, pixels, planeWidth * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height) PlaneDimensions(
        int width,
        int height,
        int depth,
        OrthogonalPlane plane) => plane switch
    {
        OrthogonalPlane.Xy => (width, height),
        OrthogonalPlane.Yz => (depth, height),
        OrthogonalPlane.Zx => (width, depth),
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    private static (int X, int Y, int Z) PlaneCoordinates(
        int horizontal,
        int vertical,
        int coordinate,
        int width,
        int height,
        int depth,
        OrthogonalPlane plane) => plane switch
    {
        OrthogonalPlane.Xy =>
            (horizontal, vertical, Math.Clamp(coordinate, 0, depth - 1)),
        OrthogonalPlane.Yz =>
            (Math.Clamp(coordinate, 0, width - 1), vertical, horizontal),
        OrthogonalPlane.Zx =>
            (horizontal, Math.Clamp(coordinate, 0, height - 1), vertical),
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    private enum OrthogonalPlane
    {
        Xy,
        Yz,
        Zx,
    }
}
