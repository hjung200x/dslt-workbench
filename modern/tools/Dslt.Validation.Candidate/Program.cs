using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dslt.App.Services;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Services;
using Dslt.Managed.Core.Validation;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }
        var options = Parse(args);
        ConfigureNativeLibrary(Require(options, "--native-directory"));
        var inputPath = Path.GetFullPath(Require(options, "--input"));
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Candidate input TIFF was not found.", inputPath);
        var volume = WpfWorkspaceFileService.ReadStack(inputPath, CancellationToken.None);
        string? referencePath = null;
        LabelTiffVolume? reference = null;
        if (options.TryGetValue("--reference", out var referenceValue) && !string.IsNullOrWhiteSpace(referenceValue))
        {
            referencePath = Path.GetFullPath(referenceValue);
            if (!File.Exists(referencePath))
                throw new FileNotFoundException("Reference label TIFF was not found.", referencePath);
            reference = LabelTiffCodec.Read(referencePath);
            ValidateReference(reference, volume.Width, volume.Height, volume.Depth, volume.Calibration);
        }

        string? outputBase = null;
        if (options.TryGetValue("--output-base", out var outputValue) && !string.IsNullOrWhiteSpace(outputValue))
        {
            outputBase = Path.GetFullPath(outputValue);
            ValidateOutputBase(outputBase, options.ContainsKey("--force-output"));
        }

        var dsltParameters = BuildParameters(options);
        var preprocessingParameters = BuildPreprocessingParameters(options);
        using var engine = new NativeProcessingEngine();
        var estimate = await engine.EstimateAsync(volume, dsltParameters, CancellationToken.None).ConfigureAwait(false);
        if (options.ContainsKey("--estimate-only"))
        {
            WriteJson(new
            {
                inputPath,
                volume.Width,
                volume.Height,
                volume.Depth,
                volume.Channels,
                preprocessingParameters,
                dsltParameters,
                estimate,
                engine.Backend,
            });
            return estimate.WithinLimits ? 0 : 1;
        }
        if (!estimate.WithinLimits)
            throw new InvalidOperationException("Native work estimate rejected this parameter set before execution.");

        var started = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (processingVolume, preprocessingSteps) = await RunPreprocessingAsync(
            engine, volume, preprocessingParameters).ConfigureAwait(false);
        var processingSteps = preprocessingSteps.ToList();
        var dsltResult = await engine.RunAsync(
            processingVolume, dsltParameters, null, CancellationToken.None).ConfigureAwait(false);
        if (dsltResult.Labels is null || dsltResult.OutputKind != OutputKind.LabelsInt32)
            throw new InvalidDataException("DSLT segmentation did not return labels.");

        var parameters = dsltParameters;
        var result = dsltResult;
        if (options.ContainsKey("--apply-watershed"))
        {
            processingSteps.Add(ProcessingStepProvenance.Create(dsltParameters, dsltResult));
            var selectedSeeds = dsltResult.Labels
                .Where(label => label >= 0)
                .Distinct()
                .Order()
                .ToArray();
            if (selectedSeeds.Length == 0)
                throw new InvalidDataException("DSLT segmentation produced no labels for Watershed seeds.");
            parameters = new OperationParameters(
                ProcessingOperation.Watershed,
                ParseBackend(options),
                Connectivity: ParseInt(options, "--watershed-connectivity", 6),
                MinimumComponentSize: ParseInt(options, "--watershed-minimum-component-size", 0),
                SeedLabelsSha256: ProcessingProvenance.ComputeLabelSha256(dsltResult.Labels),
                SelectedSeedLabels: selectedSeeds);
            result = await engine.RunAsync(
                processingVolume,
                parameters,
                null,
                CancellationToken.None,
                new ProcessingLabelState(
                    dsltResult.Width,
                    dsltResult.Height,
                    dsltResult.Depth,
                    dsltResult.Labels,
                    selectedSeeds)).ConfigureAwait(false);
        }
        stopwatch.Stop();
        if (result.Labels is null || result.OutputKind != OutputKind.LabelsInt32)
            throw new InvalidDataException("DSLT segmentation did not return labels.");

        if (outputBase is not null)
        {
            if (options.ContainsKey("--force-output")) ClearOutputBase(outputBase);
            await ResultPackageWriter.WriteAsync(
                outputBase,
                volume,
                parameters,
                result,
                editHistory: ["headless production DSLT pipeline and real-data candidate generation"],
                processingSteps: processingSteps,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        SegmentationValidationResult? metrics = null;
        if (reference is not null)
        {
            metrics = SegmentationValidator.Evaluate(
                reference.Labels,
                result.Labels,
                result.Width,
                result.Height,
                result.Depth,
                reference.Calibration,
                referenceBackgroundLabel: ParseInt(options, "--reference-background", 0),
                candidateBackgroundLabel: ParseInt(options, "--candidate-background", -1),
                objectConnectivity: ParseInt(options, "--connectivity", 26));
        }

        WriteJson(new CandidateRunReport(
            inputPath,
            referencePath,
            outputBase,
            started,
            stopwatch.Elapsed,
            processingSteps,
            parameters,
            estimate,
            engine.Backend,
            result.UsedBackend,
            result.Width,
            result.Height,
            result.Depth,
            result.ComponentCount,
            result.CompletedPasses,
            ProcessingProvenance.ComputeLabelSha256(result.Labels),
            metrics));
        return metrics is null || metrics.Passed ? 0 : 1;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                      NotSupportedException or OverflowException or InvalidOperationException or
                                      NativeProcessingException)
    {
        Console.Error.WriteLine($"Candidate generation failed: {exception.Message}");
        return 2;
    }
}

static async Task<(VolumeData Volume, IReadOnlyList<ProcessingStepProvenance> Steps)> RunPreprocessingAsync(
    NativeProcessingEngine engine,
    VolumeData source,
    IReadOnlyList<OperationParameters> operations)
{
    var current = source;
    var steps = new List<ProcessingStepProvenance>(operations.Count);
    foreach (var operation in operations)
    {
        var result = await engine.RunAsync(current, operation, null, CancellationToken.None).ConfigureAwait(false);
        if (result.FloatData is null || result.OutputKind != OutputKind.VolumeFloat32)
            throw new InvalidDataException($"Preprocessing operation {operation.Operation} did not return a float volume.");
        steps.Add(ProcessingStepProvenance.Create(operation, result));
        current = new VolumeData(
            result.Width,
            result.Height,
            result.Depth,
            1,
            0,
            source.Calibration,
            result.FloatData);
    }
    return (current, steps);
}

static void ValidateReference(
    LabelTiffVolume reference,
    int width,
    int height,
    int depth,
    Calibration inputCalibration)
{
    if (reference.Width != width || reference.Height != height || reference.Depth != depth)
        throw new InvalidDataException(
            $"Reference dimensions {reference.Width}x{reference.Height}x{reference.Depth} do not match " +
            $"input dimensions {width}x{height}x{depth}.");

    var calibration = reference.Calibration;
    if (!NearlyEqual(calibration.SpacingX, inputCalibration.SpacingX) ||
        !NearlyEqual(calibration.SpacingY, inputCalibration.SpacingY) ||
        !NearlyEqual(calibration.SpacingZ, inputCalibration.SpacingZ) ||
        !string.Equals(calibration.UnitName, inputCalibration.UnitName, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("Reference calibration does not match the candidate input calibration.");
    }
}

static bool NearlyEqual(double left, double right)
{
    var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
    return Math.Abs(left - right) <= 1e-9 * scale;
}

static void ValidateOutputBase(string outputBase, bool force)
{
    var existing = CandidateOutputPaths(outputBase).Where(File.Exists).ToArray();
    if (existing.Length != 0 && !force)
        throw new IOException(
            $"Candidate output already exists: {existing[0]}. Pass --force-output to replace this exact output base.");
}

static void ClearOutputBase(string outputBase)
{
    foreach (var path in CandidateOutputPaths(outputBase))
        if (File.Exists(path)) File.Delete(path);
}

static string[] CandidateOutputPaths(string outputBase) =>
[
    outputBase + ".f32.raw",
    outputBase + ".i32.raw",
    outputBase + ".json",
    outputBase + ".labels.i16.tif",
    outputBase + ".labels.i32.tif",
];

static OperationParameters BuildParameters(IReadOnlyDictionary<string, string?> options) => new(
    Operation: ProcessingOperation.DsltSegmentation,
    Backend: ParseBackend(options),
    Radius: ParseInt(options, "--radius", 14),
    MinimumComponentSize: ParseInt(options, "--minimum-component-size", 0),
    DirectionLevel: ParseInt(options, "--direction-level", 2),
    DsltKernel: ParseKernel(options),
    ZCorrectionFactor: ParseFloat(options, "--z-correction-factor", 0.2f),
    MinimumC: ParseFloat(options, "--minimum-c", -0.020f),
    MaximumC: ParseFloat(options, "--maximum-c", -0.008f),
    CInterval: ParseFloat(options, "--c-interval", 0.002f),
    ClosingRadius: ParseInt(options, "--closing-radius", 2),
    MinimumInvalidStructureArea: ParseInt(options, "--minimum-invalid-structure-area", 800));

static IReadOnlyList<OperationParameters> BuildPreprocessingParameters(
    IReadOnlyDictionary<string, string?> options)
{
    var backend = ParseBackend(options);
    var operations = new List<OperationParameters>();
    var smoothingRadius = ParseInt(options, "--gaussian-smoothing-radius", 0);
    if (smoothingRadius < 0 || smoothingRadius > 64)
        throw new ArgumentOutOfRangeException(
            "--gaussian-smoothing-radius", "Gaussian smoothing radius must be between 0 and 64.");
    if (smoothingRadius > 0)
        operations.Add(new OperationParameters(
            ProcessingOperation.SmoothGaussian,
            backend,
            Radius: smoothingRadius));
    if (options.ContainsKey("--apply-z-gradient"))
        operations.Add(new OperationParameters(
            ProcessingOperation.ZGradient,
            backend,
            ZGradientCoefficient: ParseFloat(options, "--z-gradient-coefficient", 10),
            ZGradientExponent: ParseFloat(options, "--z-gradient-exponent", 1)));
    return operations;
}

static ProcessingBackend ParseBackend(IReadOnlyDictionary<string, string?> options) =>
    Get(options, "--backend", "cpu").ToLowerInvariant() switch
    {
        "auto" => ProcessingBackend.Auto,
        "cpu" => ProcessingBackend.Cpu,
        "cuda" => ProcessingBackend.Cuda,
        var value => throw new ArgumentException($"--backend must be auto, cpu, or cuda; received {value}."),
    };

static DsltKernelType ParseKernel(IReadOnlyDictionary<string, string?> options) =>
    Get(options, "--kernel", "gaussian").ToLowerInvariant() switch
    {
        "mean" => DsltKernelType.Mean,
        "gaussian" => DsltKernelType.Gaussian,
        var value => throw new ArgumentException($"--kernel must be mean or gaussian; received {value}."),
    };

static void ConfigureNativeLibrary(string nativeDirectory)
{
    var directory = Path.GetFullPath(nativeDirectory);
    var library = Path.Combine(directory, "dslt_core.dll");
    if (!File.Exists(library)) throw new FileNotFoundException("dslt_core.dll was not found.", library);
    NativeLibrary.SetDllImportResolver(typeof(NativeProcessingEngine).Assembly, (name, _, _) =>
        name.Equals("dslt_core", StringComparison.OrdinalIgnoreCase)
            ? NativeLibrary.Load(library)
            : nint.Zero);
}

static Dictionary<string, string?> Parse(string[] args)
{
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--estimate-only", "--force-output", "--apply-z-gradient", "--apply-watershed",
    };
    var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--input", "--reference", "--output-base", "--native-directory", "--backend", "--radius",
        "--direction-level", "--kernel", "--z-correction-factor", "--minimum-c", "--maximum-c",
        "--c-interval", "--closing-radius", "--minimum-component-size", "--minimum-invalid-structure-area",
        "--connectivity", "--reference-background", "--candidate-background", "--estimate-only", "--force-output",
        "--gaussian-smoothing-radius", "--apply-z-gradient", "--z-gradient-coefficient", "--z-gradient-exponent",
        "--apply-watershed", "--watershed-connectivity", "--watershed-minimum-component-size",
    };
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var key = args[index];
        if (!known.Contains(key)) throw new ArgumentException($"Unknown option: {key}");
        if (!result.TryAdd(key, null)) throw new ArgumentException($"Option was specified twice: {key}");
        if (flags.Contains(key)) continue;
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option requires a value: {key}");
        result[key] = args[index];
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string?> options, string key) =>
    options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required option is missing: {key}");

static string Get(IReadOnlyDictionary<string, string?> options, string key, string fallback) =>
    options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

static int ParseInt(IReadOnlyDictionary<string, string?> options, string key, int fallback)
{
    var value = Get(options, key, fallback.ToString(CultureInfo.InvariantCulture));
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw new ArgumentException($"{key} must be an integer.");
}

static float ParseFloat(IReadOnlyDictionary<string, string?> options, string key, float fallback)
{
    var value = Get(options, key, fallback.ToString("R", CultureInfo.InvariantCulture));
    return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && float.IsFinite(result)
        ? result
        : throw new ArgumentException($"{key} must be a finite number.");
}

static void WriteJson<T>(T value)
{
    var settings = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    settings.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(value, settings));
}

static void PrintUsage() => Console.WriteLine("""
    Usage:
      Dslt.Validation.Candidate
        --input <input.tif> --native-directory <directory-containing-dslt_core.dll>
        [--reference <reference.tif>] [--output-base <candidate-base>] [--force-output]
        [--backend <cpu|cuda|auto>] [--estimate-only]
        [--radius <1..127>] [--direction-level <1..5>] [--kernel <mean|gaussian>]
        [--z-correction-factor <value>]
        [--gaussian-smoothing-radius <0..64>] [--apply-z-gradient]
        [--z-gradient-coefficient <value>] [--z-gradient-exponent <value>]
        [--apply-watershed] [--watershed-connectivity <6|18|26>]
        [--watershed-minimum-component-size <exclusive-size>]
        [--minimum-c <value>] [--maximum-c <value>] [--c-interval <positive>]
        [--closing-radius <0..64>] [--minimum-component-size <exclusive-size>]
        [--minimum-invalid-structure-area <value>]
        [--connectivity <6|18|26>] [--reference-background <label>]
        [--candidate-background <label>]

    The tool always executes the production DsltSegmentation operation through
    NativeProcessingEngine. --estimate-only performs resource preflight without
    creating a candidate. Candidate packages use provenance schema 1.10 and
    record every prior processing step with its actual backend and hash. With
    --apply-watershed, the DSLT labels become selected marker seeds and the
    Watershed result is the final candidate.
    """);

internal sealed record CandidateRunReport(
    string InputPath,
    string? ReferencePath,
    string? OutputBase,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    IReadOnlyList<ProcessingStepProvenance> ProcessingSteps,
    OperationParameters Parameters,
    ProcessingWorkEstimate Estimate,
    BackendInformation BackendInformation,
    ProcessingBackend UsedBackend,
    int Width,
    int Height,
    int Depth,
    int ComponentCount,
    int CompletedPasses,
    string OutputSha256,
    SegmentationValidationResult? Metrics);
