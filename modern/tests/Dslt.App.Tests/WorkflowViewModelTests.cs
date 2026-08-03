using Dslt.App.Services;
using Dslt.App.ViewModels;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;

namespace Dslt.App.Tests;

internal static class WorkflowViewModelTests
{
    public static async Task RunAsync()
    {
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

            var labels = new int[volume.VoxelCount];
            for (var index = 0; index < labels.Length; index++)
                labels[index] = volume.Samples[index] >= parameters.Threshold ? 0 : -1;
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
        public Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextVolume);
        public string? ChooseExportBasePath() => null;
    }
}
