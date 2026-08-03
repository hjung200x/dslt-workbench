using Dslt.App.Infrastructure;
using Dslt.App.Services;
using Dslt.App.ViewModels;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Dslt.App.Tests;

internal static class WorkflowViewModelTests
{
    public static async Task RunAsync()
    {
        RunScrollSyncOnSta();
        var engine = new FakeProcessingEngine();
        var files = new FakeWorkspaceFileService();
        using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
        var target = viewModel.Value;

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.DsltSegmentation);
        await target.EstimateCommand.ExecuteAsync();
        Assert(engine.EstimateCallCount == 1, "The DSLT work estimate was not requested.");
        Assert(target.WorkEstimateSummary.Contains("directions", StringComparison.Ordinal),
            "The work estimate summary was not exposed to the UI.");
        Assert(target.SelectedStage == WorkflowStage.Segment,
            "Selecting DSLT segmentation did not activate the segmentation stage.");
        Assert(target.Radius == 14 && target.DirectionLevel == 2,
            "The legacy DSLT radius and direction defaults were not applied.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.DsltThreshold);
        engine.RunBehavior = FakeRunBehavior.Success;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is { Radius: 14 } &&
               Math.Abs(target.LastParameters.ConstantC - -0.04F) < 1e-6F,
            "The legacy preview C offset was not mapped to core Cxy.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.DsltSegmentation);

        engine.RunBehavior = FakeRunBehavior.Success;
        await target.RunCommand.ExecuteAsync();
        var successfulResult = target.LastResult ??
            throw new InvalidOperationException("A successful operation did not publish a result.");
        var successfulImage = target.ResultImage;
        Assert(target.HasResult,
            "A successful operation did not publish a result.");
        Assert(successfulResult.OutputKind == OutputKind.LabelsInt32,
            "The fake segmentation result did not reach the ViewModel.");
        Assert(target.SelectedStage == WorkflowStage.Edit,
            "A label result did not advance the workflow to editing.");
        Assert(target.Progress == 100, "A successful operation did not finish at 100 percent progress.");
        Assert(target.SourceYzImage is BitmapSource sourceYz &&
               sourceYz.PixelWidth == successfulResult.Depth &&
               sourceYz.PixelHeight == successfulResult.Height,
            "The source YZ plane dimensions are incorrect.");
        Assert(target.SourceZxImage is BitmapSource sourceZx &&
               sourceZx.PixelWidth == successfulResult.Width &&
               sourceZx.PixelHeight == successfulResult.Depth,
            "The source ZX plane dimensions are incorrect.");
        Assert(target.ResultYzImage is not null && target.ResultZxImage is not null,
            "The label result did not publish all orthogonal planes.");

        engine.RunBehavior = FakeRunBehavior.WaitForCancellation;
        var cancelledRun = target.RunCommand.ExecuteAsync();
        await engine.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(target.IsBusy && target.CancelCommand.CanExecute(null),
            "The cancellation command was not available while processing.");
        target.CancelCommand.Execute(null);
        await cancelledRun;
        Assert(ReferenceEquals(successfulResult, target.LastResult) && ReferenceEquals(successfulImage, target.ResultImage),
            "Cancellation replaced the last valid result.");
        Assert(target.Status.Contains("cancelled", StringComparison.OrdinalIgnoreCase) &&
               target.Status.Contains("preserved", StringComparison.OrdinalIgnoreCase),
            "Cancellation did not report result preservation.");

        engine.RunBehavior = FakeRunBehavior.Fail;
        await target.RunCommand.ExecuteAsync();
        Assert(ReferenceEquals(successfulResult, target.LastResult) && ReferenceEquals(successfulImage, target.ResultImage),
            "Failure replaced the last valid result.");
        Assert(target.Status.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
               target.Status.Contains("preserved", StringComparison.OrdinalIgnoreCase),
            "Failure did not report result preservation.");

        var labelCountBeforeEdit = target.LastResult!.Labels!.Count(label => label == 0);
        target.SelectAtCursorCommand.Execute(null);
        Assert(target.SelectedLabelCount == 1 && target.SelectionSummary.Contains("0", StringComparison.Ordinal),
            "Selecting the cursor label did not update editing state.");
        target.DilateSelectionCommand.Execute(null);
        Assert(target.LastResult!.Labels!.Count(label => label == 0) > labelCountBeforeEdit,
            "Label dilation did not update the published result.");
        target.UndoEditCommand.Execute(null);
        Assert(target.LastResult!.Labels!.Count(label => label == 0) == labelCountBeforeEdit,
            "Undo did not restore the label result.");

        var labelsBeforeCrop = target.LastResult.Labels!.ToArray();
        var widthBeforeCrop = target.LastResult.Width;
        var heightBeforeCrop = target.LastResult.Height;
        var depthBeforeCrop = target.LastResult.Depth;
        target.CropSelectionCommand.Execute(null);
        Assert(target.LastResult is { Width: 1, Height: 1, Depth: 1 } &&
               target.ResultOriginX == widthBeforeCrop / 2 &&
               target.ResultOriginY == heightBeforeCrop / 2 &&
               target.ResultOriginZ == depthBeforeCrop / 2,
            "Crop did not publish the selected bounding box in source coordinates.");
        Assert(target.ResultImage is BitmapSource croppedXy && croppedXy.PixelWidth == 1 && croppedXy.PixelHeight == 1,
            "Crop did not refresh the result planes to the cropped dimensions.");
        target.XIndex = 0;
        target.SelectAtCursorCommand.Execute(null);
        Assert(target.Status.Contains("outside the cropped result", StringComparison.Ordinal) &&
               target.SelectedLabelCount == 1,
            "A source cursor outside the cropped result did not preserve selection.");
        target.UndoEditCommand.Execute(null);
        Assert(target.LastResult is not null &&
               target.LastResult.Width == widthBeforeCrop &&
               target.LastResult.Height == heightBeforeCrop &&
               target.LastResult.Depth == depthBeforeCrop &&
               target.ResultOriginX == 0 && target.ResultOriginY == 0 && target.ResultOriginZ == 0 &&
               labelsBeforeCrop.SequenceEqual(target.LastResult.Labels!),
            "Crop undo did not restore dimensions, origin, and labels bit-exactly.");
        target.CropSelectionCommand.Execute(null);

        var exportBase = Path.Combine(Path.GetTempPath(), $"dslt-workflow-{Guid.NewGuid():N}");
        files.ExportBasePath = exportBase;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            var provenance = await File.ReadAllTextAsync(exportBase + ".json");
            Assert(provenance.Contains("Dilated selected labels", StringComparison.Ordinal) &&
                   provenance.Contains("Cropped selection to origin", StringComparison.Ordinal) &&
                   provenance.Contains("\"outputWidth\": 1", StringComparison.Ordinal) &&
                   provenance.Contains($"\"outputOriginX\": {widthBeforeCrop / 2}", StringComparison.Ordinal) &&
                   provenance.Contains($"\"outputOriginY\": {heightBeforeCrop / 2}", StringComparison.Ordinal) &&
                   provenance.Contains($"\"outputOriginZ\": {depthBeforeCrop / 2}", StringComparison.Ordinal) &&
                   provenance.Contains("undo", StringComparison.Ordinal),
                "The UI export did not retain label-edit history.");
            Assert(target.SelectedStage == WorkflowStage.Export,
                "Successful export did not advance the workflow.");
        }
        finally
        {
            foreach (var suffix in new[] { ".i32.raw", ".labels.i16.tif", ".labels.i32.tif", ".json" })
            {
                var path = exportBase + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var channelPlanar = new float[]
        {
            0, 0.1F, 0.2F, 0.3F, 0.4F, 0.5F, 0.6F, 0.7F,
            0.8F, 0.9F, 1, 0.9F, 0.8F, 0.7F, 0.6F, 0.5F,
        };
        files.NextVolume = new VolumeData(
            Width: 2,
            Height: 2,
            Depth: 2,
            Channels: 2,
            SelectedChannel: 0,
            Calibration: new Calibration(0.5, 0.5, 1.25, true, "um"),
            Samples: channelPlanar);
        await target.OpenCommand.ExecuteAsync();
        var firstChannelImage = target.SourceImage;
        target.ChannelIndex = 1;
        target.ZIndex = 1;
        Assert(target.MaximumChannelIndex == 1 && target.MaximumZIndex == 1,
            "Channel and Z navigation bounds were not updated after opening a volume.");
        Assert(!ReferenceEquals(firstChannelImage, target.SourceImage),
            "Changing channels did not refresh the source image.");
        Assert(target.VolumeSummary.Contains("channel 2/2", StringComparison.Ordinal),
            "The selected channel was not reflected in the volume summary.");
        Assert(ReadGray8((BitmapSource)target.SourceYzImage!).SequenceEqual(new byte[] { 230, 178, 230, 128 }),
            "The YZ plane did not preserve Y-vertical and Z-horizontal coordinate order.");
        Assert(ReadGray8((BitmapSource)target.SourceZxImage!).SequenceEqual(new byte[] { 255, 230, 153, 128 }),
            "The ZX plane did not preserve Z-vertical and X-horizontal coordinate order.");
    }

    private static void RunScrollSyncTest()
    {
        var source = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 1_000, Height = 1_000 },
        };
        var target = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 500, Height = 500 },
        };
        ScrollSyncBehavior.SetGroup(source, "test-orthogonal");
        ScrollSyncBehavior.SetGroup(target, "test-orthogonal");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(source);
        panel.Children.Add(target);
        panel.Measure(new Size(200, 100));
        panel.Arrange(new Rect(0, 0, 200, 100));
        panel.UpdateLayout();
        source.ScrollToHorizontalOffset(source.ScrollableWidth / 2);
        source.ScrollToVerticalOffset(source.ScrollableHeight / 2);
        source.UpdateLayout();
        target.UpdateLayout();
        Assert(Math.Abs(target.HorizontalOffset - target.ScrollableWidth / 2) < 0.5 &&
               Math.Abs(target.VerticalOffset - target.ScrollableHeight / 2) < 0.5,
            "Orthogonal viewport scroll offsets were not synchronized proportionally.");
        ScrollSyncBehavior.SetGroup(source, null);
        ScrollSyncBehavior.SetGroup(target, null);
    }

    private static void RunScrollSyncOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunScrollSyncTest();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Scroll synchronization test failed.", failure);
    }

    private static byte[] ReadGray8(BitmapSource source)
    {
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight)];
        source.CopyPixels(pixels, source.PixelWidth, 0);
        return pixels;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ViewModelScope(MainWindowViewModel value) : IDisposable
    {
        public MainWindowViewModel Value { get; } = value;
        public void Dispose() { }
    }

    private enum FakeRunBehavior
    {
        Success,
        WaitForCancellation,
        Fail,
    }

    private sealed class FakeProcessingEngine : IProcessingEngine
    {
        public bool IsAvailable => true;
        public string Status => "Fake CPU backend ready";
        public BackendInformation Backend { get; } = new(true, false, false, 0, string.Empty);
        public int EstimateCallCount { get; private set; }
        public FakeRunBehavior RunBehavior { get; set; }
        public TaskCompletionSource RunStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessingWorkEstimate> EstimateAsync(
            VolumeData volume,
            OperationParameters parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EstimateCallCount++;
            return Task.FromResult(new ProcessingWorkEstimate(
                VoxelCount: (ulong)volume.VoxelCount,
                DirectionCount: 42,
                LineSamplesPerVoxel: 9,
                DirectionalWorkItems: (ulong)volume.VoxelCount * 42 * 9,
                EstimatedHostBytes: (ulong)volume.VoxelCount * 16,
                SweepPasses: parameters.Operation == ProcessingOperation.DsltSegmentation ? 3UL : 1UL,
                WorkItemLimit: ulong.MaxValue,
                HostMemoryLimitBytes: ulong.MaxValue,
                WithinLimits: true));
        }

        public async Task<ProcessingResult> RunAsync(
            VolumeData volume,
            OperationParameters parameters,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunBehavior == FakeRunBehavior.WaitForCancellation)
            {
                RunStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (RunBehavior == FakeRunBehavior.Fail)
                throw new InvalidOperationException("Synthetic engine failure.");

            var labels = Enumerable.Repeat(-1, volume.VoxelCount).ToArray();
            var center = volume.Depth / 2 * volume.Width * volume.Height +
                         volume.Height / 2 * volume.Width + volume.Width / 2;
            labels[center] = 0;
            progress?.Report(1);
            return new ProcessingResult(
                UsedBackend: ProcessingBackend.Cpu,
                OutputKind: OutputKind.LabelsInt32,
                Width: volume.Width,
                Height: volume.Height,
                Depth: volume.Depth,
                ComponentCount: labels.Any(label => label >= 0) ? 1 : 0,
                FloatData: null,
                Labels: labels,
                CompletedPasses: 3);
        }

        public void Dispose() { }
    }

    private sealed class FakeWorkspaceFileService : IWorkspaceFileService
    {
        public VolumeData? NextVolume { get; set; }
        public string? ExportBasePath { get; set; }
        public Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextVolume);
        public string? ChooseExportBasePath() => ExportBasePath;
    }
}
