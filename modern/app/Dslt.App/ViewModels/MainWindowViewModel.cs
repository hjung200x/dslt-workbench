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
    private double _progress;
    private float _threshold = 0.5f;
    private int _radius = 1;
    private ProcessingBackend _backend = ProcessingBackend.Auto;
    private OperationOption _selectedOperation;
    private ProcessingResult? _lastResult;
    private OperationParameters? _lastParameters;

    public MainWindowViewModel(IProcessingEngine engine, IWorkspaceFileService files)
    {
        _engine = engine;
        _files = files;
        _status = engine.Status;
        Operations =
        [
            new("Threshold 3D", ProcessingOperation.Threshold3D),
            new("Mean smoothing", ProcessingOperation.SmoothMean),
            new("Gaussian smoothing", ProcessingOperation.SmoothGaussian),
            new("Dilate sphere", ProcessingOperation.DilateSphere),
            new("Erode sphere", ProcessingOperation.ErodeSphere),
            new("Connected components", ProcessingOperation.ConnectedComponents),
            new("Height map", ProcessingOperation.HeightMap),
            new("Depth map", ProcessingOperation.DepthMap),
        ];
        _selectedOperation = Operations[0];
        GenerateSyntheticCommand = new RelayCommand(GenerateSynthetic);
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _lastResult is not null && _lastParameters is not null);
        RunCommand = new AsyncRelayCommand(RunAsync, () => _volume is not null && _engine.IsAvailable);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        GenerateSynthetic();
    }

    public IReadOnlyList<OperationOption> Operations { get; }
    public IReadOnlyList<ProcessingBackend> Backends { get; } =
        [ProcessingBackend.Auto, ProcessingBackend.Cpu, ProcessingBackend.Cuda];

    public OperationOption SelectedOperation
    {
        get => _selectedOperation;
        set => SetProperty(ref _selectedOperation, value);
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

    public int Radius
    {
        get => _radius;
        set => SetProperty(ref _radius, Math.Clamp(value, 0, 16));
    }

    public ImageSource? SourceImage
    {
        get => _sourceImage;
        private set => SetProperty(ref _sourceImage, value);
    }

    public ImageSource? ResultImage
    {
        get => _resultImage;
        private set
        {
            SetProperty(ref _resultImage, value);
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string VolumeSummary => _volume is null
        ? "No volume"
        : $"{_volume.Width} × {_volume.Height} × {_volume.Depth} · {_volume.Channels} channel · Z {_volume.Calibration.SpacingZ.ToString("0.###", CultureInfo.InvariantCulture)}";

    public string BackendSummary => _engine.Backend.CudaAvailable
        ? $"CPU + CUDA · {_engine.Backend.DeviceName}"
        : "CPU reference backend";

    public RelayCommand GenerateSyntheticCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void GenerateSynthetic()
    {
        _volume = SyntheticVolumes.Sphere();
        SourceImage = CreateSlice(_volume.Samples, _volume.Width, _volume.Height, _volume.Depth);
        ResultImage = null;
        _lastResult = null;
        _lastParameters = null;
        SaveCommand.NotifyCanExecuteChanged();
        Progress = 0;
        Status = $"Synthetic anisotropic sphere loaded · {_engine.Status}";
        OnPropertyChanged(nameof(VolumeSummary));
        RunCommand.NotifyCanExecuteChanged();
    }

    private async Task OpenAsync()
    {
        try
        {
            var replacement = await _files.OpenVolumeAsync(CancellationToken.None);
            if (replacement is null) return;
            replacement.Validate();
            _volume = replacement;
            SourceImage = CreateSlice(replacement.Samples, replacement.Width, replacement.Height, replacement.Depth);
            ResultImage = null;
            _lastResult = null;
            _lastParameters = null;
            SaveCommand.NotifyCanExecuteChanged();
            Status = $"TIFF stack loaded · {_engine.Status}";
            OnPropertyChanged(nameof(VolumeSummary));
            RunCommand.NotifyCanExecuteChanged();
        }
        catch (Exception error)
        {
            Status = $"Open failed; the previous volume was preserved: {error.Message}";
        }
    }

    private async Task RunAsync()
    {
        if (_volume is null) return;
        _cancellation = new CancellationTokenSource();
        CancelCommand.NotifyCanExecuteChanged();
        Progress = 0;
        Status = $"Running {SelectedOperation.Name}…";
        var progress = new Progress<double>(value => Progress = value * 100);
        try
        {
            var parameters = new OperationParameters(
                SelectedOperation.Operation,
                Backend,
                Radius,
                Connectivity: 6,
                MinimumComponentSize: 1,
                SliceIndex: _volume.Depth / 2,
                LanczosOrder: 2,
                Threshold: Threshold,
                WindowMinimum: 0,
                WindowMaximum: 1,
                TargetSpacingZ: 1);
            var result = await _engine.RunAsync(_volume, parameters, progress, _cancellation.Token);
            _lastResult = result;
            _lastParameters = parameters;
            ResultImage = result.FloatData is not null
                ? CreateSlice(result.FloatData, result.Width, result.Height, result.Depth)
                : CreateLabelSlice(result.Labels!, result.Width, result.Height, result.Depth);
            Status = $"Completed on {result.UsedBackend} · {result.ComponentCount} components";
            Progress = 100;
            SaveCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            Status = "Operation cancelled; the previous result was preserved.";
        }
        catch (Exception error)
        {
            Status = $"Operation failed: {error.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SaveAsync()
    {
        if (_volume is null || _lastResult is null || _lastParameters is null) return;
        var basePath = _files.ChooseExportBasePath();
        if (basePath is null) return;
        try
        {
            await ResultPackageWriter.WriteAsync(basePath, _volume, _lastParameters, _lastResult);
            Status = $"Result package exported: {basePath}.json";
        }
        catch (Exception error)
        {
            Status = $"Export failed: {error.Message}";
        }
    }

    private static BitmapSource CreateSlice(float[] volume, int width, int height, int depth)
    {
        var slice = Math.Clamp(depth / 2, 0, Math.Max(0, depth - 1));
        var offset = checked(slice * width * height);
        var pixels = new byte[checked(width * height)];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp((int)Math.Round(volume[offset + i] * 255), 0, 255);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateLabelSlice(int[] labels, int width, int height, int depth)
    {
        var slice = Math.Clamp(depth / 2, 0, Math.Max(0, depth - 1));
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
