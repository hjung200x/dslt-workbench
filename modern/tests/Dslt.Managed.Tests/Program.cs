using System.Runtime.InteropServices;
using Dslt.Managed.Core.Interop;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Services;
using Dslt.Managed.Core.Synthetic;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Segmentation;

static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

Equal(32, Marshal.SizeOf<NativeCalibration>(), "NativeCalibration ABI size");
Equal(64, Marshal.SizeOf<NativeVolumeDescriptor>(), "NativeVolumeDescriptor ABI size");
Equal(48, Marshal.SizeOf<NativeOperationRequest>(), "NativeOperationRequest ABI size");
Equal(16, Marshal.SizeOf<NativeCropOptions>(), "NativeCropOptions ABI size");
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

    var dsltSegmentation = await engine.RunAsync(
        constantVolume,
        new OperationParameters(
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
            CropHeightMap: Enumerable.Repeat(1.0f, 9).ToArray()),
        null,
        CancellationToken.None);
    Equal(1, dsltSegmentation.ComponentCount, "DSLT segmentation component count");
    Equal(1, dsltSegmentation.CompletedPasses, "DSLT segmentation completed passes");
    var croppedLabels = dsltSegmentation.Labels!;
    Equal(1, croppedLabels.Count(label => label == 0), "DSLT cropped segmentation labels");
    Equal(0, croppedLabels[1 * 9 + 1 * 3 + 1], "DSLT height-map crop center label");

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
await ResultPackageWriter.WriteAsync(temporaryBase, sphere, operation, syntheticResult);
var rawPath = temporaryBase + ".f32.raw";
var jsonPath = temporaryBase + ".json";
if (!File.Exists(rawPath) || !File.Exists(jsonPath))
    throw new InvalidOperationException("Result package files were not created.");
var json = await File.ReadAllTextAsync(jsonPath);
if (!json.Contains("synthetic-data-validated", StringComparison.Ordinal))
    throw new InvalidOperationException("Provenance validation level is missing.");
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
Console.WriteLine("DSLT label editing tests passed.");
