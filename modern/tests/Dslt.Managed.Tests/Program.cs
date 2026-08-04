using System.Runtime.InteropServices;
using System.Reflection;
using Dslt.Managed.Core.Interop;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;
using Dslt.Managed.Core.Synthetic;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Segmentation;
using Dslt.Managed.Core.Analysis;

static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

Equal(32, Marshal.SizeOf<NativeCalibration>(), "NativeCalibration ABI size");
Equal(64, Marshal.SizeOf<NativeVolumeDescriptor>(), "NativeVolumeDescriptor ABI size");
Equal(48, Marshal.SizeOf<NativeOperationRequest>(), "NativeOperationRequest ABI size");
Equal(16, Marshal.SizeOf<NativeCropOptions>(), "NativeCropOptions ABI size");
Equal(72, Marshal.SizeOf<NativeWorkEstimate>(), "NativeWorkEstimate ABI size");
Equal(40, Marshal.SizeOf<NativeOperationResult>(), "NativeOperationResult ABI size");
Equal(144, Marshal.SizeOf<NativeBackendInfo>(), "NativeBackendInfo ABI size");

var sphere = SyntheticVolumes.Sphere(width: 32, height: 24, depth: 16, radius: 7, channels: 2);
sphere.Validate();
Equal(32 * 24 * 16 * 2, sphere.Samples.Length, "Multichannel fixture size");
Equal(2.0, sphere.Calibration.SpacingZ, "Anisotropic Z calibration");

var touching = SyntheticVolumes.TouchingObjects();
touching.Validate();
Equal(3, touching.Samples.Count(value => value > 0.5f), "Touching-object foreground count");

var impulse = SyntheticVolumes.Impulse();
Equal(1, impulse.Samples.Count(value => value == 1), "Impulse foreground count");
var ramp = SyntheticVolumes.Ramp();
Equal(0f, ramp.Samples[0], "Ramp origin");
Equal(1f, ramp.Samples[^1], "Ramp endpoint");
var shell = SyntheticVolumes.Shell();
if (shell.Samples.Count(value => value > 0.5f) <= 0) throw new InvalidOperationException("Shell fixture is empty.");
var noiseA = SyntheticVolumes.Noise(seed: 42);
var noiseB = SyntheticVolumes.Noise(seed: 42);
if (!noiseA.Samples.SequenceEqual(noiseB.Samples)) throw new InvalidOperationException("Noise fixture is not deterministic.");
var composite = SyntheticVolumes.MultiChannelComposite();
composite.Validate();
Equal(3, composite.Channels, "Composite channel count");
SegmentationValidationTests.Run();
LegacyHeightMapCodecTests.Run();
HeightSurfaceAreaCalculatorTests.Run();
await RealDataValidationTests.RunAsync();

using var engine = ProcessingEngineFactory.Create();
if (!engine.IsAvailable && !engine.Status.Contains("Native core unavailable", StringComparison.Ordinal))
    throw new InvalidOperationException("Unavailable native core did not provide an actionable status.");

Console.WriteLine("DSLT managed contract and synthetic fixture tests passed.");
Console.WriteLine(engine.Status);

if (engine.IsAvailable)
{
    var nativeImpulse = SyntheticVolumes.Impulse(5, 5, 3);
    var thresholdResult = await engine.RunAsync(
        nativeImpulse,
        new OperationParameters(ProcessingOperation.Threshold3D, ProcessingBackend.Cpu, Threshold: 0.5f),
        null,
        CancellationToken.None);
    Equal(1, thresholdResult.FloatData!.Count(value => value == 1), "Native ABI threshold output");

    var componentResult = await engine.RunAsync(
        touching,
        new OperationParameters(
            ProcessingOperation.ConnectedComponents,
            ProcessingBackend.Cpu,
            Connectivity: 6,
            MinimumComponentSize: 1,
            Threshold: 0.5f),
        null,
        CancellationToken.None);
    Equal(2, componentResult.ComponentCount, "Native ABI component count");

    var watershedVolume = new VolumeData(
        5, 1, 1, 1, 0, Calibration.Unit, new float[5]);
    var watershedSeeds = new[] { 10, -1, -1, -1, 20 };
    var watershedSelection = new[] { 10, 20 };
    var watershedResult = await engine.RunAsync(
        watershedVolume,
        new OperationParameters(
            ProcessingOperation.Watershed,
            ProcessingBackend.Cpu,
            MinimumComponentSize: 0,
            SeedLabelsSha256: ProcessingProvenance.ComputeLabelSha256(watershedSeeds),
            SelectedSeedLabels: watershedSelection),
        null,
        CancellationToken.None,
        new ProcessingLabelState(5, 1, 1, watershedSeeds, watershedSelection));
    Equal(2, watershedResult.ComponentCount, "Watershed component count");
    Equal(256, watershedResult.CompletedPasses, "Watershed flood levels");
    if (!watershedResult.Labels!.SequenceEqual(new[] { 10, 10, 10, 20, 20 }))
        throw new InvalidOperationException("Watershed ABI output did not preserve legacy priority.");

    var adaptiveVolume = new VolumeData(
        1, 1, 3, 1, 0, Calibration.Unit, [0.0F, 1.0F, 0.0F]);
    var adaptive2D = await engine.RunAsync(
        adaptiveVolume,
        new OperationParameters(
            ProcessingOperation.AdaptiveThreshold2D,
            ProcessingBackend.Cpu,
            Radius: 1,
            ConstantC: 0,
            AdaptiveThresholdKernel: DsltKernelType.Mean),
        null,
        CancellationToken.None);
    Equal(3, adaptive2D.FloatData!.Count(value => value == 0.0F),
        "Adaptive threshold 2D slice-local tie output");
    var adaptive3D = await engine.RunAsync(
        adaptiveVolume,
        new OperationParameters(
            ProcessingOperation.AdaptiveThreshold3D,
            ProcessingBackend.Cpu,
            Radius: 1,
            ConstantC: 0,
            AdaptiveThresholdKernel: DsltKernelType.Mean),
        null,
        CancellationToken.None);
    var adaptive3DData = adaptive3D.FloatData ??
        throw new InvalidOperationException("Adaptive threshold 3D returned no float output.");
    Equal(1, adaptive3DData.Count(value => value == 0.8F),
        "Adaptive threshold 3D Z-local output");
    Equal(0.8F, adaptive3DData[1], "Adaptive threshold 3D center output");

    var hMinimaResult = await engine.RunAsync(
        new VolumeData(3, 1, 1, 1, 0, Calibration.Unit, [1.0F, 0.0F, 1.0F]),
        new OperationParameters(
            ProcessingOperation.HMinima,
            ProcessingBackend.Cpu,
            HMinimaHeight: 0.5F,
            HMinimaCheckInterval: 1),
        null,
        CancellationToken.None);
    var hMinimaData = hMinimaResult.FloatData ??
        throw new InvalidOperationException("H-minima returned no float output.");
    if (!hMinimaData.SequenceEqual(new[] { 0.8F, 0.0F, 0.8F }))
        throw new InvalidOperationException("H-minima ABI output did not preserve the pit fixture.");

    var heightMapResult = await engine.RunAsync(
        new VolumeData(
            1, 1, 5, 1, 0, Calibration.Unit,
            [0.0F, 0.0F, 0.25F, 0.75F, 1.0F]),
        new OperationParameters(
            ProcessingOperation.HeightMap,
            ProcessingBackend.Cpu,
            Threshold: 0.5F,
            HeightMapXyRadius: 0,
            HeightMapZRadius: 0,
            HeightMapKernel: DsltKernelType.Gaussian,
            HeightMapSmoothLevel: 0),
        null,
        CancellationToken.None);
    var heightMapData = heightMapResult.FloatData ??
        throw new InvalidOperationException("Filtered height map returned no float output.");
    Equal(1, heightMapData.Length, "Filtered height-map output size");
    if (Math.Abs(heightMapData[0] - 2.5F) > 1e-6F)
        throw new InvalidOperationException("Filtered height-map crossing interpolation is incorrect.");

    var heightProjectionResult = await engine.RunAsync(
        new VolumeData(
            1, 1, 4, 1, 0, Calibration.Unit,
            [1.0F, 0.2F, 0.8F, 0.4F]),
        new OperationParameters(
            ProcessingOperation.HeightProjection,
            ProcessingBackend.Cpu,
            Threshold: 0.5F,
            HeightMapXyRadius: 0,
            HeightMapZRadius: 0,
            HeightMapKernel: DsltKernelType.Gaussian,
            HeightMapSmoothLevel: 0,
            ProjectionMode: HeightProjectionMode.Z,
            ProjectionStartDepth: 1.0F,
            ProjectionRange: 2),
        null,
        CancellationToken.None);
    var heightProjectionData = heightProjectionResult.FloatData ??
        throw new InvalidOperationException("Height projection returned no float output.");
    Equal(1, heightProjectionData.Length, "Height-projection output size");
    if (Math.Abs(heightProjectionData[0] - 0.8F) > 1e-6F)
        throw new InvalidOperationException("Height projection did not preserve Z-range maximum sampling.");

    var zGradientResult = await engine.RunAsync(
        new VolumeData(
            1, 1, 4, 1, 0, Calibration.Unit,
            [0.1F, 0.1F, 0.1F, 0.1F]),
        new OperationParameters(
            ProcessingOperation.ZGradient,
            ProcessingBackend.Cpu,
            WindowMinimum: 0,
            WindowMaximum: 1,
            CropHeightMap: [1.0F],
            ZGradientCoefficient: 2.0F,
            ZGradientExponent: 1.0F,
            ZGradientUseHeightMap: true),
        null,
        CancellationToken.None);
    var zGradientData = zGradientResult.FloatData ??
        throw new InvalidOperationException("Z-gradient correction returned no float output.");
    if (Math.Abs(zGradientData[0] - 0.1F) > 1e-6F ||
        Math.Abs(zGradientData[2] - 0.15F) > 1e-6F ||
        Math.Abs(zGradientData[3] - 0.2F) > 1e-6F)
        throw new InvalidOperationException("Z-gradient ABI mapping did not preserve the source-derived formula.");

    var depthMapResult = await engine.RunAsync(
        new VolumeData(
            3, 1, 3, 1, 0, Calibration.Unit,
            [
                1.0F, 0.0F, 1.0F,
                0.0F, 0.0F, 0.0F,
                0.0F, 0.0F, 0.0F,
            ]),
        new OperationParameters(
            ProcessingOperation.DepthMap,
            ProcessingBackend.Cpu,
            Threshold: 0.5F,
            HeightMapXyRadius: 0,
            HeightMapZRadius: 0,
            HeightMapKernel: DsltKernelType.Gaussian,
            HeightMapSmoothLevel: 0),
        null,
        CancellationToken.None);
    var depthMapData = depthMapResult.FloatData ??
        throw new InvalidOperationException("Depth map returned no float output.");
    if (Math.Abs(depthMapData[6] - 1.0F) > 1e-6F)
        throw new InvalidOperationException("Depth map did not use the nearest 3D surface point.");

    var constantVolume = new VolumeData(
        3, 3, 3, 1, 0, Calibration.Unit,
        Enumerable.Repeat(0.5f, 27).ToArray());
    var dsltResult = await engine.RunAsync(
        constantVolume,
        new OperationParameters(
            ProcessingOperation.DsltThreshold,
            ProcessingBackend.Cpu,
            Radius: 1,
            ConstantC: 0,
            DirectionLevel: 1,
            DsltKernel: DsltKernelType.Mean,
            ZCorrectionFactor: 0.2f),
        null,
        CancellationToken.None);
    Equal(27, dsltResult.FloatData!.Count(value => value == 0), "DSLT strict threshold tie output");

    var dsltSegmentationParameters = new OperationParameters(
        ProcessingOperation.DsltSegmentation,
        ProcessingBackend.Cpu,
        Radius: 1,
        MinimumComponentSize: 0,
        DirectionLevel: 1,
        DsltKernel: DsltKernelType.Mean,
        ZCorrectionFactor: 0.2f,
        MinimumC: 0,
        MaximumC: 0,
        CInterval: 0.1f,
        ClosingRadius: 0,
        MinimumInvalidStructureArea: 0,
        CropEnabled: true,
        CropUseHeightMap: true,
        CropUpper: 0,
        CropLower: 0,
        CropBorderXy: 1,
        CropHeightMap: Enumerable.Repeat(1.0f, 9).ToArray());
    var workEstimate = await engine.EstimateAsync(
        constantVolume,
        dsltSegmentationParameters,
        CancellationToken.None);
    Equal(27UL, workEstimate.VoxelCount, "DSLT estimate voxel count");
    Equal(21UL, workEstimate.DirectionCount, "DSLT estimate direction count");
    Equal(3UL, workEstimate.LineSamplesPerVoxel, "DSLT estimate line samples");
    Equal(1701UL, workEstimate.DirectionalWorkItems, "DSLT estimate work items");
    Equal(1548UL, workEstimate.EstimatedHostBytes, "DSLT estimate host bytes");
    Equal(1UL, workEstimate.SweepPasses, "DSLT estimate sweep passes");
    Equal(true, workEstimate.WithinLimits, "DSLT estimate limit status");

    var dsltSegmentation = await engine.RunAsync(
        constantVolume,
        dsltSegmentationParameters,
        null,
        CancellationToken.None);
    Equal(1, dsltSegmentation.ComponentCount, "DSLT segmentation component count");
    Equal(1, dsltSegmentation.CompletedPasses, "DSLT segmentation completed passes");
    var croppedLabels = dsltSegmentation.Labels!;
    Equal(1, croppedLabels.Count(label => label == 0), "DSLT cropped segmentation labels");
    Equal(0, croppedLabels[1 * 9 + 1 * 3 + 1], "DSLT height-map crop center label");

    var sweepSamples = Enumerable.Repeat(1.0F, 9 * 5).ToArray();
    static int SweepIndex(int x, int y) => y * 9 + x;
    sweepSamples[SweepIndex(0, 2)] = 0.0F;
    for (var y = 1; y <= 3; y++)
        for (var x = 4; x <= 6; x++)
            if (x != 5 || y != 2) sweepSamples[SweepIndex(x, y)] = 0.0F;
    var sweepVolume = new VolumeData(9, 5, 1, 1, 0, Calibration.Unit, sweepSamples);
    var sweepResult = await engine.RunAsync(
        sweepVolume,
        new OperationParameters(
            ProcessingOperation.ThresholdSweep,
            ProcessingBackend.Cpu,
            MinimumThreshold: 0.4F,
            MaximumThreshold: 0.8F,
            ThresholdInterval: 0.4F,
            ThresholdSweepMinimumComponentSize: 0,
            ThresholdSweepMinimumInvalidStructureArea: 0,
            ClosingRadius: 0,
            MinimumInvalidStructureArea: 500),
        null,
        CancellationToken.None);
    Equal(2, sweepResult.ComponentCount, "Threshold sweep component count");
    Equal(2, sweepResult.CompletedPasses, "Threshold sweep completed passes");
    Equal(0, sweepResult.Labels![SweepIndex(0, 2)], "Threshold sweep first-pass label");
    Equal(1, sweepResult.Labels[SweepIndex(4, 1)], "Threshold sweep final-pass label");
    Equal(-1, sweepResult.Labels[SweepIndex(5, 2)], "Threshold sweep hole background");

    for (var iteration = 0; iteration < 100; iteration++)
    {
        using var lifecycleEngine = new NativeProcessingEngine();
        var lifecycleResult = await lifecycleEngine.RunAsync(
            nativeImpulse,
            new OperationParameters(ProcessingOperation.SmoothMean, ProcessingBackend.Cpu, Radius: 1),
            null,
            CancellationToken.None);
        Equal(nativeImpulse.VoxelCount, lifecycleResult.FloatData!.Length, "SafeHandle lifecycle output size");
    }
    Console.WriteLine("DSLT native ABI integration tests passed.");
    Console.WriteLine("DSLT SafeHandle lifecycle stress test passed.");
}

var temporaryBase = Path.Combine(Path.GetTempPath(), $"dslt-test-{Guid.NewGuid():N}");
var syntheticResult = new ProcessingResult(
    ProcessingBackend.Cpu,
    OutputKind.VolumeFloat32,
    sphere.Width,
    sphere.Height,
    sphere.Depth,
    0,
    sphere.Samples[..sphere.VoxelCount],
    null);
var operation = new OperationParameters(ProcessingOperation.Copy, ProcessingBackend.Cpu);
var provenanceSphere = sphere with
{
    Source = new VolumeSourceInfo(
        VolumeVoxelType.Float32,
        "LSM",
        null,
        MemoryMarshal.AsBytes(sphere.Samples.AsSpan()).ToArray(),
        [
            new VolumeChannelInfo("DAPI", 0, 0, 255, 255),
            new VolumeChannelInfo("GFP", 0, 255, 0, 255),
        ],
        [0.0, 1.25]),
};
var preprocessingStep = ProcessingStepProvenance.Create(
    new OperationParameters(
        ProcessingOperation.SmoothGaussian,
        ProcessingBackend.Cpu,
        Radius: 1),
    syntheticResult);
await ResultPackageWriter.WriteAsync(
    temporaryBase,
    provenanceSphere,
    operation,
    syntheticResult,
    ["selected label 3", "merged labels 3 and 4"],
    processingSteps: [preprocessingStep]);
var rawPath = temporaryBase + ".f32.raw";
var jsonPath = temporaryBase + ".json";
if (!File.Exists(rawPath) || !File.Exists(jsonPath))
    throw new InvalidOperationException("Result package files were not created.");
var json = await File.ReadAllTextAsync(jsonPath);
if (!json.Contains("synthetic-data-validated", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance validation level is missing.");
if (!json.Contains("\"schemaVersion\": \"1.10\"", StringComparison.Ordinal) ||
    !json.Contains("\"sourceCommit\":", StringComparison.Ordinal) ||
    !json.Contains("\"inputVoxelType\": \"Float32\"", StringComparison.Ordinal) ||
    !json.Contains("\"inputContainer\": \"LSM\"", StringComparison.Ordinal) ||
    !json.Contains("\"inputSelectedChannel\": 0", StringComparison.Ordinal) ||
    !json.Contains("\"inputCalibration\":", StringComparison.Ordinal) ||
    !json.Contains("\"inputChannelMetadata\":", StringComparison.Ordinal) ||
    !json.Contains("\"name\": \"DAPI\"", StringComparison.Ordinal) ||
    !json.Contains("\"name\": \"GFP\"", StringComparison.Ordinal) ||
    !json.Contains("\"inputTimeStampsSeconds\":", StringComparison.Ordinal) ||
    !json.Contains("\"processingSteps\":", StringComparison.Ordinal) ||
    !json.Contains("\"operation\": 5", StringComparison.Ordinal) ||
    !json.Contains("1.25", StringComparison.Ordinal) ||
    !json.Contains("\"outputSha256\":", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance input format identity is missing.");
var sourceCommitMetadata = typeof(ProcessingProvenance).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .SingleOrDefault(attribute => attribute.Key == "SourceCommit")?
    .Value;
if (!string.IsNullOrWhiteSpace(sourceCommitMetadata) &&
    !json.Contains($"\"sourceCommit\": \"{sourceCommitMetadata.ToLowerInvariant()}\"", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance source commit does not match assembly metadata.");
var expectedSourceCommit = Environment.GetEnvironmentVariable("DSLT_EXPECT_SOURCE_COMMIT");
if (!string.IsNullOrWhiteSpace(expectedSourceCommit) &&
    !json.Contains($"\"sourceCommit\": \"{expectedSourceCommit.ToLowerInvariant()}\"", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance source commit was not injected from SourceRevisionId.");
if (!json.Contains("merged labels 3 and 4", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance edit history is missing.");
File.Delete(rawPath);
File.Delete(jsonPath);
Console.WriteLine("DSLT provenance export test passed.");

var labelTiffBase = Path.Combine(Path.GetTempPath(), $"dslt-label-{Guid.NewGuid():N}");
var labelCalibration = new Calibration(0.25, 0.5, 1.75, true, "um");
var labels16 = new[]
{
    -1, 0, 1, 2,
    3, 4, 5, 6,
    7, 8, 9, short.MaxValue,
    short.MinValue, 12, 13, 14,
    15, 16, 17, 18,
    19, 20, 21, 22,
};
var label16Path = labelTiffBase + ".i16.tif";
LabelTiffCodec.Write(label16Path, 4, 3, 2, labels16, labelCalibration);
var labels16RoundTrip = LabelTiffCodec.Read(label16Path);
Equal((int)LabelTiffEncoding.SignedInt16, (int)labels16RoundTrip.Encoding, "Signed 16-bit label TIFF encoding");
Equal(2, labels16RoundTrip.Depth, "Signed 16-bit label TIFF depth");
Equal(0.25, labels16RoundTrip.Calibration.SpacingX, "Signed 16-bit TIFF X spacing");
Equal(0.5, labels16RoundTrip.Calibration.SpacingY, "Signed 16-bit TIFF Y spacing");
Equal(1.75, labels16RoundTrip.Calibration.SpacingZ, "Signed 16-bit TIFF Z spacing");
if (!labels16.SequenceEqual(labels16RoundTrip.Labels))
    throw new InvalidOperationException("Signed 16-bit label TIFF was not bit-exact after round trip.");

var labels32 = (int[])labels16.Clone();
labels32[5] = short.MaxValue + 1;
labels32[6] = 1_000_000;
var label32Path = labelTiffBase + ".i32.tif";
LabelTiffCodec.Write(label32Path, 4, 3, 2, labels32, labelCalibration);
var labels32RoundTrip = LabelTiffCodec.Read(label32Path);
Equal((int)LabelTiffEncoding.SignedInt32, (int)labels32RoundTrip.Encoding, "Signed 32-bit label TIFF encoding");
if (!labels32.SequenceEqual(labels32RoundTrip.Labels))
    throw new InvalidOperationException("Signed 32-bit label TIFF was not bit-exact after round trip.");

var labelPackageBase = labelTiffBase + ".package";
var labelResult = new ProcessingResult(
    ProcessingBackend.Cpu,
    OutputKind.LabelsInt32,
    4,
    3,
    2,
    23,
    null,
    labels32);
await ResultPackageWriter.WriteAsync(labelPackageBase, sphere, operation, labelResult);
var labelPackageTiff = labelPackageBase + ".labels.i32.tif";
var labelPackageJson = labelPackageBase + ".json";
if (!File.Exists(labelPackageTiff)) throw new InvalidOperationException("Extended signed 32-bit label TIFF was not exported.");
var labelPackageMetadata = await File.ReadAllTextAsync(labelPackageJson);
if (!labelPackageMetadata.Contains("exceed the legacy signed 16-bit TIFF range", StringComparison.Ordinal))
    throw new InvalidOperationException("Extended label TIFF compatibility warning is missing.");

File.Delete(label16Path);
File.Delete(label32Path);
File.Delete(labelPackageBase + ".i32.raw");
File.Delete(labelPackageTiff);
File.Delete(labelPackageJson);
Console.WriteLine("DSLT signed 16/32-bit label TIFF round-trip tests passed.");

var disconnected = Enumerable.Repeat(LabelEditingSession.Background, 5 * 3).ToArray();
disconnected[0] = 4;
disconnected[1] = 4;
disconnected[^1] = 4;
var editing = new LabelEditingSession(5, 3, 1, disconnected);
editing.Select([4]);
Equal(1, editing.SplitSelected(), "Disconnected label split count");
Equal(2, editing.Selection.Count, "Split selection count");
var cropped = editing.CropToSelection();
Equal(5, cropped.Width, "Selected crop width");
editing.MergeSelected();
Equal(1, editing.Labels.Span.ToArray().Where(label => label >= 0).Distinct().Count(), "Merged label count");
if (!editing.Undo()) throw new InvalidOperationException("Label edit undo was not recorded.");
Equal(2, editing.Labels.Span.ToArray().Where(label => label >= 0).Distinct().Count(), "Undo label count");

var center = Enumerable.Repeat(LabelEditingSession.Background, 27).ToArray();
center[13] = 2;
var morphology = new LabelEditingSession(3, 3, 3, center);
morphology.Select([2]);
morphology.DilateSelected();
Equal(7, morphology.Labels.Span.Count(2), "6-connected label dilation");
morphology.ErodeSelected();
Equal(1, morphology.Labels.Span.Count(2), "6-connected label erosion");

var edgeLabels = Enumerable.Repeat(5, 3).ToArray();
var clampedErosion = new LabelEditingSession(3, 1, 1, edgeLabels);
clampedErosion.SelectAll();
clampedErosion.ErodeSelected(clampImageEdges: true);
Equal(3, clampedErosion.Labels.Span.Count(5), "Clamped erosion preserves image-edge voxels");
clampedErosion.Undo();
clampedErosion.ErodeSelected(clampImageEdges: false);
Equal(0, clampedErosion.Labels.Span.Count(5), "Unclamped erosion removes image-edge voxels");
clampedErosion.SelectAll();
clampedErosion.Deselect([5]);
Equal(0, clampedErosion.Selection.Count, "Explicit deselection");

var cropSource = Enumerable.Repeat(LabelEditingSession.Background, 5 * 5 * 3).ToArray();
for (var z = 1; z <= 2; z++)
    for (var y = 2; y <= 3; y++)
        for (var x = 1; x <= 2; x++)
            cropSource[z * 25 + y * 5 + x] = 7;
var cropEditing = new LabelEditingSession(5, 5, 3, cropSource);
cropEditing.Select([7]);
var appliedCrop = cropEditing.CropSelected();
Equal(1, appliedCrop.OriginX, "Crop origin X");
Equal(2, appliedCrop.OriginY, "Crop origin Y");
Equal(1, appliedCrop.OriginZ, "Crop origin Z");
Equal(2, cropEditing.Width, "Cropped session width");
Equal(2, cropEditing.Height, "Cropped session height");
Equal(2, cropEditing.Depth, "Cropped session depth");
Equal(8, cropEditing.Labels.Length, "Cropped session label count");
if (!cropEditing.Undo()) throw new InvalidOperationException("Crop undo was not recorded.");
Equal(5, cropEditing.Width, "Crop undo width");
Equal(5, cropEditing.Height, "Crop undo height");
Equal(3, cropEditing.Depth, "Crop undo depth");
if (!cropSource.SequenceEqual(cropEditing.Labels.ToArray()))
    throw new InvalidOperationException("Crop undo did not restore labels bit-exactly.");
Console.WriteLine("DSLT label editing tests passed.");
