using Dslt.App.Infrastructure;
using Dslt.App.Services;
using Dslt.App.ViewModels;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        var internalOnly = new HashSet<ProcessingOperation>
        {
            ProcessingOperation.Copy,
            ProcessingOperation.ExtractXy,
            ProcessingOperation.ExtractYz,
            ProcessingOperation.ExtractZx,
        };
        var expectedOperations = Enum.GetValues<ProcessingOperation>()
            .Where(operation => !internalOnly.Contains(operation))
            .Order()
            .ToArray();
        Assert(target.Operations.Select(option => option.Operation).Order()
                .SequenceEqual(expectedOperations),
            "The WPF operation selector does not expose every user-facing native operation.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.Threshold2D);
        var originalZIndex = target.ZIndex;
        target.ZIndex = 2;
        target.Threshold = 0.4F;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.Threshold2D,
                SliceIndex: 2,
                Threshold: 0.4F,
            } && target.LastResult?.OutputKind == OutputKind.VolumeFloat32,
            "The 2D threshold UI did not preserve the active Z slice and threshold.");
        target.ZIndex = originalZIndex;

        foreach (var operation in new[]
                 {
                     ProcessingOperation.DilateCube,
                     ProcessingOperation.ErodeCube,
                 })
        {
            target.SelectedOperation = target.Operations.Single(option =>
                option.Operation == operation);
            target.Radius = 3;
            await target.RunCommand.ExecuteAsync();
            Assert(target.LastParameters is { Radius: 3 } parameters &&
                   parameters.Operation == operation &&
                   target.LastResult?.OutputKind == OutputKind.VolumeFloat32,
                $"The {operation} UI did not preserve the cubic morphology radius.");
        }

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ResampleZArea);
        Assert(target.IsResampleZ && !target.IsLanczosResample &&
               Math.Abs(target.TargetSpacingZ - 1.0F) < 1e-6F,
            "Area-average Z resampling did not expose the calibrated X-spacing default.");
        target.TargetSpacingZ = 4.0F;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.ResampleZArea,
                TargetSpacingZ: 4.0F,
                LanczosOrder: 2,
            } && target.LastResult is { OutputKind: OutputKind.VolumeFloat32, Depth: 24 },
            "Area-average Z resampling did not preserve spacing or output geometry.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ResampleZLanczos);
        target.TargetSpacingZ = 2.0F;
        target.LanczosOrder = 3;
        await target.RunCommand.ExecuteAsync();
        Assert(target.IsResampleZ && target.IsLanczosResample &&
               target.LastParameters is
               {
                   Operation: ProcessingOperation.ResampleZLanczos,
                   TargetSpacingZ: 2.0F,
                   LanczosOrder: 3,
               } && target.LastResult is { OutputKind: OutputKind.VolumeFloat32, Depth: 48 } &&
               target.ResultYzImage is BitmapSource resampledYz && resampledYz.PixelWidth == 48,
            "Lanczos Z resampling did not preserve order, spacing, or result geometry.");

        var resampleExport = Path.Combine(
            Path.GetTempPath(), $"dslt-resample-{Guid.NewGuid():N}");
        files.ExportBasePath = resampleExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            var provenance = await File.ReadAllTextAsync(resampleExport + ".json");
            Assert(provenance.Contains("\"targetSpacingZ\": 2", StringComparison.Ordinal) &&
                   provenance.Contains("\"lanczosOrder\": 3", StringComparison.Ordinal) &&
                   File.Exists(resampleExport + ".f32.raw"),
                "Z-resampling provenance or Float32 payload was not exported.");
        }
        finally
        {
            foreach (var suffix in new[] { ".f32.raw", ".json" })
            {
                var path = resampleExport + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        var successfulResampleResult = target.LastResult;
        var successfulResampleImage = target.ResultImage;
        engine.WaitOnOperation = ProcessingOperation.ResampleZLanczos;
        engine.ResetRunStarted();
        var cancelledResample = target.RunCommand.ExecuteAsync();
        await engine.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        target.CancelCommand.Execute(null);
        await cancelledResample;
        Assert(ReferenceEquals(successfulResampleResult, target.LastResult) &&
               ReferenceEquals(successfulResampleImage, target.ResultImage),
            "Cancellation replaced the last valid Z-resampling result.");
        engine.WaitOnOperation = null;

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
            option.Operation == ProcessingOperation.AdaptiveThreshold3D);
        Assert(target.Radius == 14 && target.AdaptiveThresholdKernel == DsltKernelType.Mean &&
               target.AdaptiveThresholdOffset == 20,
            "Adaptive threshold legacy defaults were not exposed by the UI.");
        target.Radius = 7;
        target.AdaptiveThresholdKernel = DsltKernelType.Gaussian;
        target.AdaptiveThresholdOffset = 35;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.AdaptiveThreshold3D,
                Radius: 7,
                AdaptiveThresholdKernel: DsltKernelType.Gaussian,
            } && Math.Abs(target.LastParameters.ConstantC - -0.07F) < 1e-6F,
            "Adaptive threshold UI parameters were not mapped to the native contract.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.HMinima);
        Assert(Math.Abs(target.HMinimaHeight - 0.1F) < 1e-6F && target.HMinimaCheckInterval == 50,
            "H-minima legacy defaults were not exposed by the UI.");
        target.HMinimaHeight = 0.25F;
        target.HMinimaCheckInterval = 25;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.HMinima,
                HMinimaHeight: 0.25F,
                HMinimaCheckInterval: 25,
            }, "H-minima UI parameters were not preserved in the processing request.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.HeightMap);
        Assert(target.IsHeightMap && Math.Abs(target.Threshold - 0.25F) < 1e-6F &&
               target.HeightMapXyRadius == 0 && target.HeightMapZRadius == 4 &&
               target.HeightMapKernel == DsltKernelType.Gaussian && target.HeightMapSmoothLevel == 1,
            "Height-map legacy defaults were not exposed by the UI.");
        target.HeightMapXyRadius = 2;
        target.HeightMapZRadius = 3;
        target.HeightMapKernel = DsltKernelType.Mean;
        target.HeightMapSmoothLevel = 2;
        target.Threshold = 0.4F;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.HeightMap,
                HeightMapXyRadius: 2,
                HeightMapZRadius: 3,
                HeightMapKernel: DsltKernelType.Mean,
                HeightMapSmoothLevel: 2,
                Threshold: 0.4F,
            }, "Height-map UI parameters were not preserved in the processing request.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.HeightProjection);
        Assert(target.IsHeightSurfaceOperation && target.IsHeightProjection &&
               target.ProjectionMode == HeightProjectionMode.Z && target.CanUseDepthColoring &&
               target.ProjectionOffset == 0 && target.ProjectionStartDepth == 0 &&
               target.ProjectionRange == 0 && target.ProjectionThreshold == 0 &&
               !target.DepthColorEnabled && target.DepthColorRange == 100,
            "Height-projection legacy defaults were not exposed by the UI.");
        target.ProjectionMode = HeightProjectionMode.Normal;
        target.ProjectionOffset = 1.5F;
        target.ProjectionStartDepth = 2.0F;
        target.ProjectionRange = 3;
        target.ProjectionThreshold = 0.6F;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.HeightProjection,
                ProjectionMode: HeightProjectionMode.Normal,
                ProjectionOffset: 1.5F,
                ProjectionStartDepth: 2.0F,
                ProjectionRange: 3,
                ProjectionThreshold: 0.6F,
                DepthColorEnabled: false,
                DepthColorRange: 100,
            }, "Height-projection UI parameters were not preserved in the processing request.");
        Assert(!target.CanUseDepthColoring && !target.DepthColorEnabled,
            "Normal projection did not disable Z-only depth coloring.");

        target.ProjectionMode = HeightProjectionMode.Z;
        target.ProjectionOffset = 0;
        target.ProjectionStartDepth = 0;
        target.ProjectionRange = 3;
        target.ProjectionThreshold = 0;
        target.DepthColorRange = 4;
        target.DepthColorEnabled = true;
        var previousProjectionResult = target.LastResult;
        var previousProjectionImage = target.ResultImage;
        engine.WaitOnOperation = ProcessingOperation.DepthMap;
        engine.ResetRunStarted();
        var cancelledDepthColorRun = target.RunCommand.ExecuteAsync();
        await engine.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        target.CancelCommand.Execute(null);
        await cancelledDepthColorRun;
        Assert(ReferenceEquals(previousProjectionResult, target.LastResult) &&
               ReferenceEquals(previousProjectionImage, target.ResultImage),
            "Cancellation during auxiliary depth-map work replaced the previous projection.");
        engine.WaitOnOperation = null;

        engine.RunOperations.Clear();
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.HeightProjection,
                ProjectionMode: HeightProjectionMode.Z,
                DepthColorEnabled: true,
                DepthColorRange: 4,
            }, "Depth-color UI parameters were not preserved in provenance parameters.");
        Assert(engine.RunOperations.SequenceEqual(new[]
            {
                ProcessingOperation.HeightProjection,
                ProcessingOperation.HeightMap,
                ProcessingOperation.DepthMap,
            }), "Depth coloring did not run scalar projection, height map, and depth map in order.");
        Assert(target.ResultImage is BitmapSource depthColorImage &&
               depthColorImage.Format == PixelFormats.Rgb24 &&
               target.ResultYzImage is null && target.ResultZxImage is null,
            "Depth coloring did not publish one RGB24 XY presentation image.");
        var successfulDepthColorResult = target.LastResult;
        var successfulDepthColorImage = target.ResultImage;
        engine.FailOnOperation = ProcessingOperation.DepthMap;
        await target.RunCommand.ExecuteAsync();
        Assert(ReferenceEquals(successfulDepthColorResult, target.LastResult) &&
               ReferenceEquals(successfulDepthColorImage, target.ResultImage) &&
               target.Status.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "Failure during auxiliary depth-map work replaced the valid RGB projection.");
        engine.FailOnOperation = null;

        var depthColorExport = Path.Combine(
            Path.GetTempPath(), $"dslt-depth-color-{Guid.NewGuid():N}");
        files.ExportBasePath = depthColorExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            var provenance = await File.ReadAllTextAsync(depthColorExport + ".json");
            Assert(provenance.Contains("\"depthColorEnabled\": true", StringComparison.Ordinal) &&
                   provenance.Contains("\"depthColorRange\": 4", StringComparison.Ordinal) &&
                   File.Exists(depthColorExport + ".f32.raw"),
                "Depth-color provenance or scalar Float32 payload was not exported.");
        }
        finally
        {
            foreach (var suffix in new[] { ".f32.raw", ".json" })
            {
                var path = depthColorExport + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ThresholdSweep);
        Assert(target.MinimumThreshold == 0 && target.MaximumThreshold == 1 &&
               Math.Abs(target.ThresholdInterval - 0.02F) < 1e-6F &&
               target.MinimumComponentSize == 0 && target.MinimumInvalidStructureArea == 100,
            "Threshold sweep legacy defaults were not exposed by the UI.");
        target.MinimumThreshold = 0.2F;
        target.MaximumThreshold = 0.9F;
        target.ThresholdInterval = 0.05F;
        target.ClosingRadius = 3;
        target.MinimumInvalidStructureArea = 125;
        await target.RunCommand.ExecuteAsync();
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.ThresholdSweep,
                MinimumThreshold: 0.2F,
                MaximumThreshold: 0.9F,
                ThresholdInterval: 0.05F,
                ClosingRadius: 3,
                ThresholdSweepMinimumInvalidStructureArea: 125,
            }, "Threshold sweep UI parameters were not preserved in the processing request.");
        Assert(target.SelectedStage == WorkflowStage.Edit,
            "Threshold sweep labels did not advance the workflow to editing.");

        target.SelectAtCursorCommand.Execute(null);
        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.Watershed);
        Assert(target.IsWatershed && target.RunCommand.CanExecute(null),
            "Watershed was not enabled for the selected label seed.");
        await target.RunCommand.ExecuteAsync();
        Assert(engine.LastLabelState is { SelectedLabels.Length: 1 } &&
               engine.LastLabelState.SelectedLabels[0] == 0,
            "Watershed did not pass the selected label seed to the processing engine.");
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.Watershed,
                SeedLabelsSha256: not null,
                SelectedSeedLabels.Length: 1,
            }, "Watershed seed provenance was not captured.");
        target.UndoEditCommand.Execute(null);
        Assert(target.HasSelection,
            "Watershed undo did not preserve the selected seed state.");

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
        engine.ResetRunStarted();
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
        var channelPlanarRaw = new byte[channelPlanar.Length * sizeof(float)];
        Buffer.BlockCopy(channelPlanar, 0, channelPlanarRaw, 0, channelPlanarRaw.Length);
        files.NextVolume = new VolumeData(
            Width: 2,
            Height: 2,
            Depth: 2,
            Channels: 2,
            SelectedChannel: 0,
            Calibration: new Calibration(0.5, 0.5, 1.25, true, "um"),
            Samples: channelPlanar,
            Source: new VolumeSourceInfo(
                VolumeVoxelType.Float32,
                "LSM",
                null,
                channelPlanarRaw,
                [
                    new VolumeChannelInfo("DAPI", 0, 0, 255, 255),
                    new VolumeChannelInfo("GFP", 0, 255, 0, 255),
                ],
                [0, 1.25]));
        await target.OpenCommand.ExecuteAsync();
        Assert(Math.Abs(target.TargetSpacingZ - 0.5F) < 1e-6F,
            "Opening calibrated data did not reset target Z spacing to input X spacing.");
        target.TargetSpacingZ = 0;
        Assert(Math.Abs(target.TargetSpacingZ - 0.5F) < 1e-6F,
            "The WPF layer accepted a non-positive target Z spacing.");
        var firstChannelImage = target.SourceImage;
        target.ChannelIndex = 1;
        target.ZIndex = 1;
        Assert(target.MaximumChannelIndex == 1 && target.MaximumZIndex == 1,
            "Channel and Z navigation bounds were not updated after opening a volume.");
        Assert(target.ChannelLabels.SequenceEqual(new[] { "1: DAPI", "2: GFP" }),
            "LSM channel names were not exposed by the channel selector.");
        Assert(!ReferenceEquals(firstChannelImage, target.SourceImage),
            "Changing channels did not refresh the source image.");
        Assert(target.VolumeSummary.Contains("channel 2/2 (GFP)", StringComparison.Ordinal),
            "The selected LSM channel name was not reflected in the volume summary.");
        Assert(ReadGray8((BitmapSource)target.SourceYzImage!).SequenceEqual(new byte[] { 230, 178, 230, 128 }),
            "The YZ plane did not preserve Y-vertical and Z-horizontal coordinate order.");
        Assert(ReadGray8((BitmapSource)target.SourceZxImage!).SequenceEqual(new byte[] { 255, 230, 153, 128 }),
            "The ZX plane did not preserve Z-vertical and X-horizontal coordinate order.");

        await RunLargeVolumeCancellationSmokeAsync();
    }

    private static async Task RunLargeVolumeCancellationSmokeAsync()
    {
        const int width = 512;
        const int height = 512;
        const int depth = 64;
        var samples = new float[checked(width * height * depth)];
        samples[^1] = 1;
        var files = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(
                width,
                height,
                depth,
                1,
                0,
                new Calibration(0.25, 0.25, 1.0, true, "um"),
                samples),
        };
        var engine = new FakeProcessingEngine { RunBehavior = FakeRunBehavior.WaitForCancellation };
        using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
        var target = viewModel.Value;

        await target.OpenCommand.ExecuteAsync();
        Assert(target.MaximumXIndex == width - 1 &&
               target.MaximumYIndex == height - 1 &&
               target.MaximumZIndex == depth - 1,
            "The large-volume navigation bounds were not published.");
        Assert(!target.HasResult, "Opening a large volume retained a stale result.");

        var navigationTimer = Stopwatch.StartNew();
        target.ZIndex = depth - 1;
        navigationTimer.Stop();
        Assert(navigationTimer.Elapsed < TimeSpan.FromSeconds(2),
            "Large-volume Z navigation blocked the caller for two seconds or longer.");
        var navigatedSourceImage = target.SourceImage;

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.Threshold3D);
        var cancelledRun = target.RunCommand.ExecuteAsync();
        await engine.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(target.IsBusy && target.CancelCommand.CanExecute(null),
            "Large-volume processing did not expose cancellation.");

        var cancelTimer = Stopwatch.StartNew();
        target.CancelCommand.Execute(null);
        cancelTimer.Stop();
        Assert(cancelTimer.Elapsed < TimeSpan.FromSeconds(1),
            "The large-volume cancellation command blocked the caller for one second or longer.");
        await cancelledRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert(!target.IsBusy && !target.HasResult && ReferenceEquals(navigatedSourceImage, target.SourceImage),
            "Large-volume cancellation did not preserve the source view and empty result state.");
        Assert(target.Status.Contains("cancelled", StringComparison.OrdinalIgnoreCase) &&
               target.Status.Contains("preserved", StringComparison.OrdinalIgnoreCase),
            "Large-volume cancellation did not report state preservation.");
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
        public ProcessingLabelState? LastLabelState { get; private set; }
        public List<ProcessingOperation> RunOperations { get; } = [];
        public ProcessingOperation? WaitOnOperation { get; set; }
        public ProcessingOperation? FailOnOperation { get; set; }
        public TaskCompletionSource RunStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetRunStarted() => RunStarted =
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
            CancellationToken cancellationToken,
            ProcessingLabelState? labelState = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLabelState = labelState;
            RunOperations.Add(parameters.Operation);
            if (RunBehavior == FakeRunBehavior.WaitForCancellation ||
                WaitOnOperation == parameters.Operation)
            {
                RunStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (RunBehavior == FakeRunBehavior.Fail || FailOnOperation == parameters.Operation)
                throw new InvalidOperationException("Synthetic engine failure.");

            if (!IsLabelOperation(parameters.Operation))
            {
                var resultWidth = volume.Width;
                var resultHeight = volume.Height;
                var resultDepth = volume.Depth;
                var outputKind = OutputKind.VolumeFloat32;
                if (parameters.Operation is ProcessingOperation.HeightMap or
                    ProcessingOperation.HeightProjection)
                {
                    outputKind = OutputKind.ImageFloat32;
                    resultDepth = 1;
                }
                else if (parameters.Operation is ProcessingOperation.ResampleZArea or
                         ProcessingOperation.ResampleZLanczos)
                {
                    resultDepth = Math.Max(1, checked((int)Math.Floor(
                        volume.Depth * volume.Calibration.SpacingZ /
                        parameters.TargetSpacingZ + 0.5)));
                }
                else if (parameters.Operation == ProcessingOperation.ExtractXy)
                {
                    outputKind = OutputKind.ImageFloat32;
                    resultDepth = 1;
                }
                else if (parameters.Operation == ProcessingOperation.ExtractYz)
                {
                    outputKind = OutputKind.ImageFloat32;
                    resultWidth = volume.Depth;
                    resultDepth = 1;
                }
                else if (parameters.Operation == ProcessingOperation.ExtractZx)
                {
                    outputKind = OutputKind.ImageFloat32;
                    resultHeight = volume.Depth;
                    resultDepth = 1;
                }

                var values = new float[checked(resultWidth * resultHeight * resultDepth)];
                if (parameters.Operation == ProcessingOperation.DepthMap)
                {
                    var plane = checked(volume.Width * volume.Height);
                    values = Enumerable.Range(0, volume.VoxelCount)
                        .Select(index => (float)(index / plane))
                        .ToArray();
                }
                else if (parameters.Operation == ProcessingOperation.HeightProjection)
                {
                    Array.Fill(values, 0.8F);
                }
                else if (parameters.Operation != ProcessingOperation.HeightMap)
                {
                    Array.Fill(values, 0.5F);
                }
                progress?.Report(1);
                return new ProcessingResult(
                    ProcessingBackend.Cpu,
                    outputKind,
                    resultWidth,
                    resultHeight,
                    resultDepth,
                    0,
                    values,
                    null);
            }

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

        private static bool IsLabelOperation(ProcessingOperation operation) => operation is
            ProcessingOperation.ConnectedComponents or
            ProcessingOperation.ThresholdSweep or
            ProcessingOperation.DsltSegmentation or
            ProcessingOperation.Watershed;

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
