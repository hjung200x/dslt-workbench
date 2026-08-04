using Dslt.App.Infrastructure;
using Dslt.App.Services;
using Dslt.App.ViewModels;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Analysis;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        await RunOrthogonalViewExportTestsAsync();
        await RunHeightSurfaceAreaExportTestsAsync();
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
            ProcessingOperation.ImportLabels,
            ProcessingOperation.ImportHeightMap,
            ProcessingOperation.HeightSurfaceArea,
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
        target.TargetSpacingZ = 1.0F;
        target.LanczosOrder = 3;
        await target.RunCommand.ExecuteAsync();
        Assert(target.IsResampleZ && target.IsLanczosResample &&
               target.LastParameters is
               {
                   Operation: ProcessingOperation.ResampleZLanczos,
                   TargetSpacingZ: 1.0F,
                   LanczosOrder: 3,
               } && target.LastResult is { OutputKind: OutputKind.VolumeFloat32, Depth: 96 } &&
               target.ResultYzImage is BitmapSource resampledYz && resampledYz.PixelWidth == 96,
            "Lanczos Z resampling did not preserve order, spacing, or result geometry.");

        var resampleExport = Path.Combine(
            Path.GetTempPath(), $"dslt-resample-{Guid.NewGuid():N}");
        files.ExportBasePath = resampleExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(resampleExport + ".json"));
            var root = provenance.RootElement;
            Assert(root.GetProperty("operation").GetProperty("targetSpacingZ").GetSingle() == 1 &&
                   root.GetProperty("operation").GetProperty("lanczosOrder").GetInt32() == 3 &&
                   root.GetProperty("inputCalibration").GetProperty("spacingZ").GetDouble() == 2 &&
                   root.GetProperty("calibration").GetProperty("spacingZ").GetDouble() == 1 &&
                   File.Exists(resampleExport + ".f32.raw"),
                "Z-resampling input/result calibration, parameters, or Float32 payload was not exported.");
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
        Assert(target.Radius == 14 && target.DirectionLevel == 2 &&
               target.DsltKernel == DsltKernelType.Gaussian &&
               Math.Abs(target.MinimumC - -0.020F) < 1e-6F &&
               Math.Abs(target.MaximumC - -0.008F) < 1e-6F &&
               Math.Abs(target.CInterval - 0.002F) < 1e-6F &&
               target.MinimumInvalidStructureArea == 800 && target.ClosingRadius == 2,
            "The documented DSLT v1.11 segmentation defaults were not applied.");

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
            option.Operation == ProcessingOperation.ZGradient);
        Assert(target.IsZGradient && target.IsHeightSurfaceOperation &&
               Math.Abs(target.ZGradientCoefficient - 10.0F) < 1e-6F &&
               Math.Abs(target.ZGradientExponent - 1.0F) < 1e-6F &&
               !target.ZGradientUseHeightMap,
            "Z-gradient legacy defaults were not exposed by the UI.");
        target.ZGradientCoefficient = 3.5F;
        target.ZGradientExponent = 2.0F;
        target.ZGradientUseHeightMap = true;
        engine.RunOperations.Clear();
        await target.RunCommand.ExecuteAsync();
        Assert(engine.RunOperations.SequenceEqual([ProcessingOperation.ZGradient]) &&
               engine.RunParameters[^1].CropHeightMap is not null,
            "Z-gradient height-map mode did not reuse the active surface.");
        Assert(target.LastParameters is
            {
                Operation: ProcessingOperation.ZGradient,
                ZGradientCoefficient: 3.5F,
                ZGradientExponent: 2.0F,
                ZGradientUseHeightMap: true,
                CropHeightMap: null,
            }, "Z-gradient parameters were not preserved without embedding the derived height map.");

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
                UseHeightSurface: true,
                HeightSurface: null,
            } && engine.RunParameters[^1] is
            {
                Operation: ProcessingOperation.HeightProjection,
                UseHeightSurface: true,
                HeightSurface: not null,
            }, "Height projection did not reuse the active surface or preserve its transient contract.");
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
                ProcessingOperation.DepthMap,
            }) && engine.RunParameters.TakeLast(2).All(parameters =>
                parameters.UseHeightSurface && parameters.HeightSurface is not null),
            "Depth coloring did not reuse one active surface for scalar projection and depth map.");
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
        target.SelectionThreshold = 0.5F;
        target.SelectByMeanIntensityCommand.Execute(null);
        Assert(target.SelectedLabelCount == 1 &&
               target.Status.Contains("mean intensity above", StringComparison.OrdinalIgnoreCase),
            "Mean-intensity selection did not select the bright label.");
        target.DeselectAboveThreshold = true;
        target.SelectByMeanIntensityCommand.Execute(null);
        Assert(target.SelectedLabelCount == 0,
            "Mean-intensity deselection did not remove the bright label.");
        target.DeselectAboveThreshold = false;
        target.SelectAllCommand.Execute(null);
        Assert(target.SelectedLabelCount == 1,
            "Select-all did not select every editable label.");
        target.ClearSelectionCommand.Execute(null);
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

        await RunProcessingChainTestsAsync();
        await RunSegmentImportAndDisplayFilterTestsAsync();
        await RunLegacyHeightMapWorkflowTestsAsync();
        await RunLargeVolumeCancellationSmokeAsync();
    }

    private static async Task RunSegmentImportAndDisplayFilterTestsAsync()
    {
        var samples = Enumerable.Range(0, 12).Select(index => index / 11.0F).ToArray();
        var calibration = new Calibration(0.5, 0.5, 1.25, true, "um");
        var files = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(3, 2, 2, 1, 0, calibration, samples),
        };
        var engine = new FakeProcessingEngine();
        using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
        var target = viewModel.Value;
        await target.OpenCommand.ExecuteAsync();

        var labels = Enumerable.Repeat(-1, 12).ToArray();
        labels[6] = 3;
        labels[7] = 7;
        labels[8] = 7;
        labels[9] = -2;
        files.NextLabels = new LabelTiffVolume(
            3, 2, 2, LabelTiffEncoding.SignedInt16, Calibration.Unit, labels);
        await target.LoadSegmentsCommand.ExecuteAsync();
        Assert(target.CanEdit && target.LastResult is { ComponentCount: 2 } &&
               target.LastParameters?.Operation == ProcessingOperation.ImportLabels &&
               target.Status.Contains("Loaded 2 segment", StringComparison.Ordinal) &&
               target.Status.Contains("working-volume calibration was retained", StringComparison.Ordinal) &&
               target.LastResult.Labels![6] == 0 && target.LastResult.Labels[7] == 1 &&
               target.LastResult.Labels[8] == 1 && target.LastResult.Labels[9] == -1,
            "A compatible legacy segment TIFF was not installed as an editable result.");

        target.MinimumDisplayedSegmentSize = 1;
        var pixels = ReadRgb24((BitmapSource)target.ResultImage!);
        Assert(pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 0 &&
               pixels[3] != 0 && pixels[4] != 0 && pixels[5] != 0,
            "The strict legacy minimum-size display filter did not hide only the one-voxel segment.");
        target.MinimumDisplayedSegmentSize = 2;
        pixels = ReadRgb24((BitmapSource)target.ResultImage!);
        Assert(pixels.Take(9).All(value => value == 0),
            "The strict legacy minimum-size display filter did not hide the two-voxel segment at equality.");

        var exportBase = Path.Combine(Path.GetTempPath(), $"dslt-imported-labels-{Guid.NewGuid():N}");
        var expectedExportedLabels = target.LastResult!.Labels!.ToArray();
        files.ExportBasePath = exportBase;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(exportBase + ".json"));
            var exportedLabels = LabelTiffCodec.Read(exportBase + ".labels.i16.tif");
            Assert(provenance.RootElement.GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.ImportLabels &&
                   provenance.RootElement.GetProperty("editHistory")[0].GetString()!.Contains(
                       ProcessingProvenance.ComputeLabelSha256(labels), StringComparison.Ordinal) &&
                   exportedLabels.Labels.SequenceEqual(expectedExportedLabels),
                "Imported labels, their source hash, or their managed-only provenance identity changed during export/display filtering.");
        }
        finally
        {
            DeletePackage(exportBase);
        }

        var preservedResult = target.LastResult;
        var preservedImage = target.ResultImage;
        files.NextLabels = new LabelTiffVolume(
            2, 2, 2, LabelTiffEncoding.SignedInt16, calibration, new int[8]);
        await target.LoadSegmentsCommand.ExecuteAsync();
        Assert(ReferenceEquals(preservedResult, target.LastResult) &&
               ReferenceEquals(preservedImage, target.ResultImage) &&
               target.Status.Contains("previous result was preserved", StringComparison.OrdinalIgnoreCase),
            "A mismatched segment TIFF replaced the last valid editable result.");
    }

    private static async Task RunOrthogonalViewExportTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-orthogonal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var xy = CreateGray8Bitmap(2, 1, [10, 20]);
            var yz = CreateGray8Bitmap(1, 2, [30, 40]);
            var zx = CreateGray8Bitmap(2, 2, [50, 60, 70, 80]);
            var paths = await Task.Run(() => WpfWorkspaceFileService.WriteOrthogonalViews(
                Path.Combine(directory, "planes.tif"), xy, yz, zx, CancellationToken.None));
            Assert(paths.Select(Path.GetFileName).SequenceEqual(
                    ["planes.tif", "planesYZ.tif", "planesZX.tif"]) &&
                   ReadGray8Tiff(paths[0]).SequenceEqual(new byte[] { 10, 20 }) &&
                   ReadGray8Tiff(paths[1]).SequenceEqual(new byte[] { 30, 40 }) &&
                   ReadGray8Tiff(paths[2]).SequenceEqual(new byte[] { 50, 60, 70, 80 }),
                "Orthogonal-view TIFF export did not preserve the legacy plane names or rendered pixels.");

            var files = new FakeWorkspaceFileService();
            var engine = new FakeProcessingEngine();
            using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
            var target = viewModel.Value;
            await target.SaveSourceViewsCommand.ExecuteAsync();
            Assert(files.LastOrthogonalViewName == "source" &&
                   files.LastOrthogonalViews is { Length: 3 } sourceViews &&
                   sourceViews[0].PixelWidth == target.SourceImage!.Width &&
                   target.Status.Contains("Saved source XY/YZ/ZX TIFF views", StringComparison.Ordinal),
                "The source orthogonal-view command did not export the rendered XY/YZ/ZX planes.");

            target.SelectedOperation = target.Operations.Single(option =>
                option.Operation == ProcessingOperation.Threshold3D);
            await target.RunCommand.ExecuteAsync();
            var previousResult = target.LastResult;
            files.OrthogonalViewFailure = new IOException("simulated view export failure");
            await target.SaveResultViewsCommand.ExecuteAsync();
            Assert(files.LastOrthogonalViewName == "result" &&
                   files.LastOrthogonalViews is { Length: 3 } resultViews &&
                   ReferenceEquals(resultViews[0], target.ResultImage) &&
                   ReferenceEquals(previousResult, target.LastResult) &&
                   target.Status.Contains("workspace was preserved", StringComparison.OrdinalIgnoreCase),
                "A result-view export failure changed the last valid result or hid recovery status.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunHeightSurfaceAreaExportTestsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-height-area-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var areaMap = new HeightSurfaceAreaMap(
                3,
                3,
                [0F, 0F, 0F, 0F, 1.25F, 0F, 0F, 0F, 0F],
                [0, 0, 0, 0, 255, 0, 0, 0, 0],
                1.25,
                HeightSurfaceAreaCalculator.LegacyIntegrationResolution);
            var paths = await Task.Run(() => WpfWorkspaceFileService.WriteHeightSurfaceAreaMaps(
                Path.Combine(directory, "area_map.tif"), areaMap, CancellationToken.None));
            var (floatFormat, floatPixels) = ReadFloat32Tiff(paths[1]);
            var namesMatch = paths.Select(Path.GetFileName).SequenceEqual(["area_map.tif", "area_map32.tif"]);
            var previewMatches = ReadGray8Tiff(paths[0]).SequenceEqual(areaMap.PreviewGray8);
            var floatMatches = floatPixels.SequenceEqual(areaMap.ScaleFactors);
            Assert(namesMatch && previewMatches && floatFormat == PixelFormats.Gray32Float && floatMatches,
                $"Height-surface area TIFF export mismatch: names={namesMatch}, preview={previewMatches}, " +
                $"format={floatFormat}, floats={floatMatches} ({string.Join(",", floatPixels)}).");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunLegacyHeightMapWorkflowTestsAsync()
    {
        var calibration = new Calibration(0.5, 0.5, 1.5, true, "um");
        var files = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(3, 2, 2, 1, 0, calibration, new float[12]),
        };
        var engine = new FakeProcessingEngine();
        using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
        var target = viewModel.Value;
        await target.OpenCommand.ExecuteAsync();

        var surface = new LegacyHeightMap(3, 2, [0F, 0.25F, 0.5F, 0.75F, 1F, 1.25F]);
        files.NextHeightMap = surface;
        await target.LoadHeightMapCommand.ExecuteAsync();
        Assert(target.HasActiveHeightMap &&
               target.LastParameters?.Operation == ProcessingOperation.ImportHeightMap &&
               target.LastResult is { OutputKind: OutputKind.ImageFloat32, Depth: 1 } importedResult &&
               importedResult.FloatData!.SequenceEqual(surface.Values) &&
               target.ActiveHeightMapSummary.Contains(
                   ProcessingProvenance.ComputeFloatSha256(surface.Values)[..12], StringComparison.Ordinal),
            "A compatible legacy .hmp surface was not installed as the active height map.");

        await target.SaveHeightMapCommand.ExecuteAsync();
        Assert(files.LastSavedHeightMap is not null &&
               files.LastSavedHeightMap.Values.SequenceEqual(surface.Values),
            "The active height map was not routed to legacy .hmp export.");

        foreach (var operation in new[]
                 {
                     ProcessingOperation.DepthMap,
                     ProcessingOperation.HeightProjection,
                 })
        {
            target.SelectedOperation = target.Operations.Single(option => option.Operation == operation);
            await target.RunCommand.ExecuteAsync();
            Assert(engine.RunParameters[^1] is
                   {
                       UseHeightSurface: true,
                       HeightSurface: not null,
                   } request && request.Operation == operation &&
                   request.HeightSurface.SequenceEqual(surface.Values) &&
                   target.LastParameters is { UseHeightSurface: true, HeightSurface: null } persisted &&
                   persisted.Operation == operation,
                $"{operation} did not consume the imported .hmp surface or strip its transient array.");
        }

        var directSurfaceExport = Path.Combine(
            Path.GetTempPath(), $"dslt-direct-height-surface-{Guid.NewGuid():N}");
        files.ExportBasePath = directSurfaceExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var provenance = JsonDocument.Parse(
                await File.ReadAllBytesAsync(directSurfaceExport + ".json"));
            var operation = provenance.RootElement.GetProperty("operation");
            Assert(operation.GetProperty("useHeightSurface").GetBoolean() &&
                   operation.GetProperty("heightSurface").ValueKind == JsonValueKind.Null &&
                   provenance.RootElement.GetProperty("editHistory").EnumerateArray().Any(item =>
                       item.GetString()!.Contains(
                           ProcessingProvenance.ComputeFloatSha256(surface.Values),
                           StringComparison.Ordinal)),
                "Direct imported-surface provenance did not retain the mode/hash or stripped-array contract.");
        }
        finally
        {
            DeletePackage(directSurfaceExport);
        }

        var preservedResult = target.LastResult;
        var preservedSummary = target.ActiveHeightMapSummary;
        files.NextHeightMap = new LegacyHeightMap(2, 2, [0F, 0F, 0F, 0F]);
        await target.LoadHeightMapCommand.ExecuteAsync();
        Assert(ReferenceEquals(preservedResult, target.LastResult) &&
               target.ActiveHeightMapSummary == preservedSummary &&
               target.Status.Contains("preserved", StringComparison.OrdinalIgnoreCase),
            "A mismatched legacy .hmp file replaced the prior result or active surface.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ThresholdSweep);
        target.CropEnabled = true;
        target.CropUseHeightMap = true;
        target.CropUpper = 0;
        target.CropLower = 1;
        target.CropBorderXy = 0;
        await target.RunCommand.ExecuteAsync();
        var cropRequest = engine.RunParameters.Last(parameters =>
            parameters.Operation == ProcessingOperation.ThresholdSweep);
        Assert(cropRequest is
               {
                   CropEnabled: true,
                   CropUseHeightMap: true,
                   CropUpper: 0,
                   CropLower: 1,
                   CropBorderXy: 0,
                   CropHeightMap: not null,
               } && cropRequest.CropHeightMap.SequenceEqual(surface.Values) &&
               target.LastParameters is { CropEnabled: true, CropUseHeightMap: true, CropHeightMap: null },
            "The active legacy height map was not passed to height-relative segmentation crop or stripped from JSON parameters.");

        var exportBase = Path.Combine(Path.GetTempPath(), $"dslt-height-crop-{Guid.NewGuid():N}");
        files.ExportBasePath = exportBase;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(exportBase + ".json"));
            Assert(provenance.RootElement.GetProperty("editHistory").EnumerateArray().Any(item =>
                       item.GetString()!.Contains(
                           ProcessingProvenance.ComputeFloatSha256(surface.Values), StringComparison.Ordinal)),
                "Height-relative crop provenance did not retain the active height-map hash.");
        }
        finally
        {
            DeletePackage(exportBase);
        }

        var generatedFiles = new FakeWorkspaceFileService();
        var generatedEngine = new FakeProcessingEngine();
        using var generatedViewModel = new ViewModelScope(
            new MainWindowViewModel(generatedEngine, generatedFiles));
        var generatedTarget = generatedViewModel.Value;
        generatedTarget.SelectedOperation = generatedTarget.Operations.Single(option =>
            option.Operation == ProcessingOperation.DsltSegmentation);
        generatedTarget.CropEnabled = true;
        generatedTarget.CropUseHeightMap = true;
        Assert(generatedTarget.ShowsHeightMapParameters,
            "Height-map generation parameters were hidden for a height-relative segmentation crop.");
        await generatedTarget.RunCommand.ExecuteAsync();
        Assert(generatedEngine.RunOperations.TakeLast(2).SequenceEqual(
                   [ProcessingOperation.HeightMap, ProcessingOperation.DsltSegmentation]) &&
               generatedTarget.HasActiveHeightMap,
            "Height-relative crop did not generate and retain a surface when no .hmp was loaded.");

        var areaFiles = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(9, 9, 2, 1, 0, calibration, new float[162]),
        };
        var areaEngine = new FakeProcessingEngine();
        using var areaViewModel = new ViewModelScope(new MainWindowViewModel(areaEngine, areaFiles));
        var areaTarget = areaViewModel.Value;
        await areaTarget.OpenCommand.ExecuteAsync();
        var rampValues = new float[81];
        for (var y = 0; y < 9; y++)
            for (var x = 0; x < 9; x++)
                rampValues[y * 9 + x] = x;
        areaFiles.NextHeightMap = new LegacyHeightMap(9, 9, rampValues);
        await areaTarget.LoadHeightMapCommand.ExecuteAsync();
        var rampSha256 = ProcessingProvenance.ComputeFloatSha256(rampValues);
        await areaTarget.CalculateHeightSurfaceAreaCommand.ExecuteAsync();
        Assert(areaTarget.HasHeightSurfaceAreaMap &&
               areaTarget.LastParameters?.Operation == ProcessingOperation.HeightSurfaceArea &&
               areaTarget.LastResult is { OutputKind: OutputKind.ImageFloat32, Depth: 1 } areaResult &&
               MathF.Abs(areaResult.FloatData![3 * 9 + 3] - MathF.Sqrt(1.25F)) <= 1e-6F &&
               areaTarget.ResultImage is BitmapSource { Format: var resultFormat } &&
               resultFormat == PixelFormats.Gray8 &&
               areaTarget.HeightSurfaceAreaSummary.Contains("10 x 10 Simpson", StringComparison.Ordinal),
            "The legacy A command did not install the source-derived area result and normalized preview.");

        await areaTarget.SaveHeightSurfaceAreaCommand.ExecuteAsync();
        Assert(areaFiles.LastSavedHeightSurfaceArea is { IntegrationResolution: 10 } savedArea &&
               savedArea.ScaleFactors.SequenceEqual(areaTarget.LastResult!.FloatData!),
            "The calculated area map was not routed to paired 8/32-bit TIFF export.");

        var areaExportBase = Path.Combine(Path.GetTempPath(), $"dslt-height-area-result-{Guid.NewGuid():N}");
        areaFiles.ExportBasePath = areaExportBase;
        try
        {
            await areaTarget.SaveCommand.ExecuteAsync();
            using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(areaExportBase + ".json"));
            Assert(provenance.RootElement.GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.HeightSurfaceArea &&
                   provenance.RootElement.GetProperty("editHistory").EnumerateArray().Any(item =>
                       item.GetString()!.Contains(rampSha256, StringComparison.Ordinal) &&
                       item.GetString()!.Contains("10x10 Simpson", StringComparison.Ordinal)),
                "Height-surface area provenance did not retain the source surface hash and integration resolution.");
        }
        finally
        {
            DeletePackage(areaExportBase);
        }

        var preservedAreaResult = areaTarget.LastResult;
        areaFiles.HeightSurfaceAreaSaveFailure = new IOException("simulated area-map save failure");
        await areaTarget.SaveHeightSurfaceAreaCommand.ExecuteAsync();
        Assert(ReferenceEquals(preservedAreaResult, areaTarget.LastResult) &&
               areaTarget.HasHeightSurfaceAreaMap &&
               areaTarget.Status.Contains("workspace was preserved", StringComparison.OrdinalIgnoreCase),
            "An area-map save failure changed the last valid calculation or hid recovery status.");

        var invalidFiles = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(2, 2, 2, 1, 0, calibration, new float[8]),
            NextHeightMap = new LegacyHeightMap(2, 2, new float[4]),
        };
        using var invalidViewModel = new ViewModelScope(
            new MainWindowViewModel(new FakeProcessingEngine(), invalidFiles));
        var invalidTarget = invalidViewModel.Value;
        await invalidTarget.OpenCommand.ExecuteAsync();
        await invalidTarget.LoadHeightMapCommand.ExecuteAsync();
        var preservedImportedMap = invalidTarget.LastResult;
        await invalidTarget.CalculateHeightSurfaceAreaCommand.ExecuteAsync();
        Assert(ReferenceEquals(preservedImportedMap, invalidTarget.LastResult) &&
               !invalidTarget.HasHeightSurfaceAreaMap &&
               invalidTarget.Status.Contains("previous result was preserved", StringComparison.OrdinalIgnoreCase),
            "An invalid area calculation replaced the imported height map or hid recovery status.");
    }

    private static async Task RunProcessingChainTestsAsync()
    {
        var samples = Enumerable.Range(0, 48).Select(index => index / 47.0F).ToArray();
        var raw = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, raw, 0, raw.Length);
        var files = new FakeWorkspaceFileService
        {
            NextVolume = new VolumeData(
                3,
                2,
                4,
                2,
                1,
                new Calibration(0.5, 0.5, 1.25, true, "um"),
                samples,
                new VolumeSourceInfo(
                    VolumeVoxelType.Float32,
                    "TIFF",
                    null,
                    raw,
                    [new VolumeChannelInfo("A", 255, 0, 0, 255), new VolumeChannelInfo("B", 0, 255, 0, 255)],
                    [])),
        };
        var engine = new FakeProcessingEngine();
        using var viewModel = new ViewModelScope(new MainWindowViewModel(engine, files));
        var target = viewModel.Value;
        await target.OpenCommand.ExecuteAsync();

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.SmoothGaussian);
        target.Radius = 2;
        await target.RunCommand.ExecuteAsync();
        Assert(target.CanUseResultAsInput && target.UseResultAsInputCommand.CanExecute(null),
            "A Float32 volume result could not be promoted to the working input.");
        target.UseResultAsInputCommand.Execute(null);
        Assert(target.ProcessingStepCount == 1 && !target.HasResult &&
               target.ProcessingChainSummary.Contains(nameof(ProcessingOperation.SmoothGaussian), StringComparison.Ordinal),
            "Promoting a preprocessing result did not update and clear the expected working state.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ZGradient);
        target.ZGradientUseHeightMap = false;
        await target.RunCommand.ExecuteAsync();
        var floatExport = Path.Combine(Path.GetTempPath(), $"dslt-chain-float-{Guid.NewGuid():N}");
        files.ExportBasePath = floatExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(floatExport + ".json"));
            var root = document.RootElement;
            var steps = root.GetProperty("processingSteps");
            Assert(root.GetProperty("schemaVersion").GetString() == "1.10" &&
                   root.GetProperty("inputChannels").GetInt32() == 2 &&
                   root.GetProperty("inputSelectedChannel").GetInt32() == 1 &&
                   steps.GetArrayLength() == 1 &&
                   steps[0].GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.SmoothGaussian &&
                   root.GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.ZGradient,
                "The WPF export did not preserve the root channel and ordered preprocessing chain.");
        }
        finally
        {
            DeletePackage(floatExport);
        }

        Assert(target.ResetProcessingChainCommand.CanExecute(null),
            "An applied processing chain could not be reset.");
        target.ResetProcessingChainCommand.Execute(null);
        Assert(target.ProcessingStepCount == 0 && !target.HasResult &&
               target.ChannelIndex == 1 && target.MaximumChannelIndex == 1,
            "Resetting the processing chain did not restore the selected channel of the loaded input.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.ResampleZArea);
        target.TargetSpacingZ = 0.5F;
        await target.RunCommand.ExecuteAsync();
        target.UseResultAsInputCommand.Execute(null);
        Assert(target.ProcessingStepCount == 1 && target.VolumeSummary.Contains("Z spacing 0.5", StringComparison.Ordinal),
            "Promoting a Z-resampled result did not preserve its working calibration.");

        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.DsltSegmentation);
        await target.RunCommand.ExecuteAsync();
        target.SelectAllCommand.Execute(null);
        target.SelectedOperation = target.Operations.Single(option =>
            option.Operation == ProcessingOperation.Watershed);
        await target.RunCommand.ExecuteAsync();
        var labelExport = Path.Combine(Path.GetTempPath(), $"dslt-chain-label-{Guid.NewGuid():N}");
        files.ExportBasePath = labelExport;
        try
        {
            await target.SaveCommand.ExecuteAsync();
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(labelExport + ".json"));
            var root = document.RootElement;
            var steps = root.GetProperty("processingSteps");
            var seedStep = steps[steps.GetArrayLength() - 1];
            var labelTiff = LabelTiffCodec.Read(labelExport + ".labels.i16.tif");
            Assert(steps.GetArrayLength() == 2 &&
                   steps[0].GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.ResampleZArea &&
                   seedStep.GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.DsltSegmentation &&
                   root.GetProperty("operation").GetProperty("operation").GetInt32() ==
                       (int)ProcessingOperation.Watershed &&
                   root.GetProperty("operation").GetProperty("seedLabelsSha256").GetString() ==
                       seedStep.GetProperty("outputSha256").GetString() &&
                   Math.Abs(root.GetProperty("inputCalibration").GetProperty("spacingZ").GetDouble() - 1.25) < 1e-9 &&
                   Math.Abs(root.GetProperty("calibration").GetProperty("spacingZ").GetDouble() - 0.5) < 1e-9 &&
                   Math.Abs(labelTiff.Calibration.SpacingZ - 0.5) < 1e-9,
                "The WPF Watershed export did not bind its seed chain or preserve input/result calibration.");
        }
        finally
        {
            DeletePackage(labelExport);
        }
    }

    private static void DeletePackage(string basePath)
    {
        foreach (var suffix in new[] { ".f32.raw", ".i32.raw", ".labels.i16.tif", ".labels.i32.tif", ".json" })
        {
            var path = basePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
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

    private static byte[] ReadRgb24(BitmapSource source)
    {
        Assert(source.Format == PixelFormats.Rgb24, "Expected an RGB24 label image.");
        var stride = checked(source.PixelWidth * 3);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapSource CreateGray8Bitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ReadGray8Tiff(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new TiffBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Single();
        Assert(frame.Format == PixelFormats.Gray8, "Expected an exported Gray8 TIFF view.");
        var pixels = new byte[checked(frame.PixelWidth * frame.PixelHeight)];
        frame.CopyPixels(pixels, frame.PixelWidth, 0);
        return pixels;
    }

    private static (PixelFormat Format, float[] Pixels) ReadFloat32Tiff(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new TiffBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Single();
        var pixels = new float[checked(frame.PixelWidth * frame.PixelHeight)];
        frame.CopyPixels(pixels, checked(frame.PixelWidth * sizeof(float)), 0);
        return (frame.Format, pixels);
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
        public List<OperationParameters> RunParameters { get; } = [];
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
            RunParameters.Add(parameters);
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
        public LabelTiffVolume? NextLabels { get; set; }
        public LegacyHeightMap? NextHeightMap { get; set; }
        public LegacyHeightMap? LastSavedHeightMap { get; private set; }
        public HeightSurfaceAreaMap? LastSavedHeightSurfaceArea { get; private set; }
        public string? ExportBasePath { get; set; }
        public string? LastOrthogonalViewName { get; private set; }
        public BitmapSource[]? LastOrthogonalViews { get; private set; }
        public Exception? OrthogonalViewFailure { get; set; }
        public Exception? HeightSurfaceAreaSaveFailure { get; set; }
        public Task<VolumeData?> OpenVolumeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextVolume);
        public Task<LabelTiffVolume?> OpenLabelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextLabels);
        public Task<LegacyHeightMap?> OpenHeightMapAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextHeightMap);
        public Task<string?> SaveHeightMapAsync(
            LegacyHeightMap heightMap,
            CancellationToken cancellationToken)
        {
            LastSavedHeightMap = new LegacyHeightMap(
                heightMap.Width, heightMap.Height, heightMap.Values.ToArray());
            return Task.FromResult<string?>("surface.hmp");
        }
        public Task<IReadOnlyList<string>?> SaveHeightSurfaceAreaAsync(
            HeightSurfaceAreaMap areaMap,
            CancellationToken cancellationToken)
        {
            if (HeightSurfaceAreaSaveFailure is not null) throw HeightSurfaceAreaSaveFailure;
            LastSavedHeightSurfaceArea = new HeightSurfaceAreaMap(
                areaMap.Width,
                areaMap.Height,
                areaMap.ScaleFactors.ToArray(),
                areaMap.PreviewGray8.ToArray(),
                areaMap.MaximumScaleFactor,
                areaMap.IntegrationResolution);
            return Task.FromResult<IReadOnlyList<string>?>(["area_map.tif", "area_map32.tif"]);
        }
        public Task<IReadOnlyList<string>?> SaveOrthogonalViewsAsync(
            string viewName,
            BitmapSource xy,
            BitmapSource yz,
            BitmapSource zx,
            CancellationToken cancellationToken)
        {
            LastOrthogonalViewName = viewName;
            LastOrthogonalViews = [xy, yz, zx];
            if (OrthogonalViewFailure is not null) throw OrthogonalViewFailure;
            return Task.FromResult<IReadOnlyList<string>?>(
                [$"{viewName}.tif", $"{viewName}YZ.tif", $"{viewName}ZX.tif"]);
        }
        public string? ChooseExportBasePath() => ExportBasePath;
    }
}
