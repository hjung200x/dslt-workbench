using System.Security.Cryptography;
using System.Text.Json;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;

namespace Dslt.Managed.Core.Validation;

public sealed record RealDataValidationManifest
{
    public int SchemaVersion { get; init; }
    public string DatasetName { get; init; } = string.Empty;
    public string CandidateSourceCommit { get; init; } = string.Empty;
    public IReadOnlyList<RealDataValidationCase> Cases { get; init; } = [];
}

public sealed record RealDataValidationCase
{
    public string Id { get; init; } = string.Empty;
    public string AcquisitionId { get; init; } = string.Empty;
    public string DataClassification { get; init; } = string.Empty;
    public string ReferenceKind { get; init; } = string.Empty;
    public string InputPath { get; init; } = string.Empty;
    public string InputFileSha256 { get; init; } = string.Empty;
    public string InputDecodedSha256 { get; init; } = string.Empty;
    public string ReferenceLabelsPath { get; init; } = string.Empty;
    public string ReferenceLabelsSha256 { get; init; } = string.Empty;
    public string CandidateLabelsPath { get; init; } = string.Empty;
    public string CandidateLabelsSha256 { get; init; } = string.Empty;
    public string CandidateProvenancePath { get; init; } = string.Empty;
    public string CandidateProvenanceSha256 { get; init; } = string.Empty;
    public string VoxelType { get; init; } = string.Empty;
    public string Container { get; init; } = string.Empty;
    public int Channels { get; init; }
    public double SpacingZ { get; init; }
    public int ReferenceBackgroundLabel { get; init; }
    public int CandidateBackgroundLabel { get; init; }
    public int ObjectConnectivity { get; init; } = 26;
}

public sealed record RealDataCoverageResult(
    int CaseCount,
    int UniqueAcquisitionCount,
    bool HasSingleChannel,
    bool HasMultipleChannels,
    IReadOnlyList<string> CoveredVoxelTypes,
    IReadOnlyList<string> CoveredContainers,
    int DistinctZSpacingCount,
    bool Passed,
    IReadOnlyList<string> Failures);

public sealed record RealDataCaseValidationResult(
    string Id,
    string AcquisitionId,
    string ReferenceKind,
    string VoxelType,
    string Container,
    int Channels,
    double SpacingZ,
    SegmentationValidationResult? Metrics,
    bool Passed,
    IReadOnlyList<string> Failures);

public sealed record RealDataValidationReport(
    int SchemaVersion,
    string DatasetName,
    string ManifestSha256,
    string CandidateSourceCommit,
    DateTimeOffset EvaluatedAtUtc,
    SegmentationValidationThresholds Thresholds,
    RealDataCoverageResult Coverage,
    IReadOnlyList<RealDataCaseValidationResult> Cases,
    bool ReleaseGatePassed);

public static class RealDataValidationRunner
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<RealDataValidationReport> EvaluateAsync(
        string manifestPath,
        SegmentationValidationThresholds? thresholds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        thresholds ??= SegmentationValidationThresholds.V1;
        thresholds.Validate();
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifestBytes = await File.ReadAllBytesAsync(fullManifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<RealDataValidationManifest>(manifestBytes, ManifestJsonOptions)
            ?? throw new InvalidDataException("Validation manifest is empty.");
        if (manifest.SchemaVersion is not (1 or 2))
            throw new InvalidDataException($"Unsupported validation manifest schema {manifest.SchemaVersion}.");
        if (manifest.SchemaVersion == 2 && !IsGitCommit(manifest.CandidateSourceCommit))
            throw new InvalidDataException("Schema-2 validation manifest candidateSourceCommit must be a full Git commit.");
        if (string.IsNullOrWhiteSpace(manifest.DatasetName))
            throw new InvalidDataException("Validation manifest datasetName is required.");
        if (manifest.Cases is null)
            throw new InvalidDataException("Validation manifest cases are required.");

        var baseDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("Validation manifest has no parent directory.");
        var results = new List<RealDataCaseValidationResult>(manifest.Cases.Count);
        var duplicateIds = manifest.Cases
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EvaluateCaseAsync(
                item, baseDirectory, manifest.CandidateSourceCommit, thresholds, duplicateIds.Contains(item.Id), cancellationToken)
                .ConfigureAwait(false));
        }

        var coverage = EvaluateCoverage(manifest.Cases);
        return new RealDataValidationReport(
            manifest.SchemaVersion,
            manifest.DatasetName,
            Sha256(manifestBytes),
            manifest.CandidateSourceCommit,
            DateTimeOffset.UtcNow,
            thresholds,
            coverage,
            results,
            coverage.Passed && results.All(result => result.Passed));
    }

    private static async Task<RealDataCaseValidationResult> EvaluateCaseAsync(
        RealDataValidationCase item,
        string baseDirectory,
        string candidateSourceCommit,
        SegmentationValidationThresholds thresholds,
        bool duplicateId,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Id)) failures.Add("Case id is required.");
        if (duplicateId) failures.Add($"Case id '{item.Id}' is duplicated.");
        if (string.IsNullOrWhiteSpace(item.AcquisitionId)) failures.Add("Acquisition id is required.");
        if (!string.Equals(item.DataClassification, "representative-real", StringComparison.OrdinalIgnoreCase))
            failures.Add("dataClassification must be 'representative-real'.");
        if (item.ReferenceKind is null ||
            !item.ReferenceKind.Equals("legacy", StringComparison.OrdinalIgnoreCase) &&
            !item.ReferenceKind.Equals("expert", StringComparison.OrdinalIgnoreCase))
            failures.Add("referenceKind must be 'legacy' or 'expert'.");
        if (!SupportedVoxelTypes.Contains(item.VoxelType))
            failures.Add("voxelType must be uint8, uint16, or float32.");
        if (!SupportedContainers.Contains(item.Container))
            failures.Add("container must be tiff or lsm.");
        if (item.Channels <= 0) failures.Add("channels must be positive.");
        if (!double.IsFinite(item.SpacingZ) || item.SpacingZ <= 0) failures.Add("spacingZ must be finite and positive.");
        if (item.ObjectConnectivity is not (6 or 18 or 26)) failures.Add("objectConnectivity must be 6, 18, or 26.");

        var inputPath = ResolvePath(baseDirectory, item.InputPath, "inputPath", failures);
        var referencePath = ResolvePath(baseDirectory, item.ReferenceLabelsPath, "referenceLabelsPath", failures);
        var candidatePath = ResolvePath(baseDirectory, item.CandidateLabelsPath, "candidateLabelsPath", failures);
        var provenancePath = ResolvePath(baseDirectory, item.CandidateProvenancePath, "candidateProvenancePath", failures);
        await VerifyFileAsync(inputPath, item.InputFileSha256, "input", failures, cancellationToken).ConfigureAwait(false);
        await VerifyFileAsync(referencePath, item.ReferenceLabelsSha256, "reference labels", failures, cancellationToken)
            .ConfigureAwait(false);
        await VerifyFileAsync(candidatePath, item.CandidateLabelsSha256, "candidate labels", failures, cancellationToken)
            .ConfigureAwait(false);
        await VerifyFileAsync(provenancePath, item.CandidateProvenanceSha256, "candidate provenance", failures, cancellationToken)
            .ConfigureAwait(false);

        LabelTiffVolume? reference = null;
        LabelTiffVolume? candidate = null;
        if (referencePath is not null && File.Exists(referencePath))
        {
            try { reference = LabelTiffCodec.Read(referencePath); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or OverflowException)
            {
                failures.Add($"Reference label TIFF could not be read: {exception.Message}");
            }
        }
        if (candidatePath is not null && File.Exists(candidatePath))
        {
            try { candidate = LabelTiffCodec.Read(candidatePath); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or OverflowException)
            {
                failures.Add($"Candidate label TIFF could not be read: {exception.Message}");
            }
        }

        if (reference is not null && candidate is not null)
        {
            if (reference.Width != candidate.Width || reference.Height != candidate.Height || reference.Depth != candidate.Depth)
                failures.Add("Reference and candidate label dimensions differ.");
            if (!CalibrationMatches(reference.Calibration, candidate.Calibration))
                failures.Add("Reference and candidate label calibration differs.");
            if (!NearlyEqual(reference.Calibration.SpacingZ, item.SpacingZ))
                failures.Add("Manifest spacingZ differs from the label TIFF calibration.");
        }

        if (provenancePath is not null && File.Exists(provenancePath))
            await ValidateProvenanceAsync(
                provenancePath, item, candidateSourceCommit, candidate, failures, cancellationToken).ConfigureAwait(false);

        SegmentationValidationResult? metrics = null;
        if (failures.Count == 0 && reference is not null && candidate is not null)
        {
            metrics = SegmentationValidator.Evaluate(
                reference.Labels,
                candidate.Labels,
                reference.Width,
                reference.Height,
                reference.Depth,
                reference.Calibration,
                item.ReferenceBackgroundLabel,
                item.CandidateBackgroundLabel,
                item.ObjectConnectivity,
                thresholds);
            failures.AddRange(metrics.Failures);
        }

        return new RealDataCaseValidationResult(
            item.Id ?? string.Empty,
            item.AcquisitionId ?? string.Empty,
            item.ReferenceKind ?? string.Empty,
            item.VoxelType ?? string.Empty,
            item.Container ?? string.Empty,
            item.Channels,
            item.SpacingZ,
            metrics,
            failures.Count == 0 && metrics is not null && metrics.Passed,
            failures);
    }

    private static RealDataCoverageResult EvaluateCoverage(IReadOnlyList<RealDataValidationCase> cases)
    {
        var failures = new List<string>();
        var acquisitions = cases.Select(item => item.AcquisitionId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasSingle = cases.Any(item => item.Channels == 1);
        var hasMultiple = cases.Any(item => item.Channels > 1);
        var voxelTypes = cases.Select(item => item.VoxelType?.ToLowerInvariant() ?? string.Empty)
            .Where(SupportedVoxelTypes.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var spacingCount = cases.Where(item => double.IsFinite(item.SpacingZ) && item.SpacingZ > 0)
            .Select(item => Math.Round(item.SpacingZ, 9))
            .Distinct()
            .Count();
        var containers = cases.Select(item => item.Container?.ToLowerInvariant() ?? string.Empty)
            .Where(SupportedContainers.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (cases.Count < 5) failures.Add("At least five validation cases are required.");
        if (acquisitions < 5) failures.Add("At least five unique acquisitions are required.");
        if (!hasSingle) failures.Add("At least one single-channel case is required.");
        if (!hasMultiple) failures.Add("At least one multi-channel case is required.");
        foreach (var requiredType in SupportedVoxelTypes)
        {
            if (!voxelTypes.Contains(requiredType, StringComparer.Ordinal))
                failures.Add($"A {requiredType} input case is required.");
        }
        if (spacingCount < 2) failures.Add("At least two distinct Z spacings are required.");
        return new RealDataCoverageResult(
            cases.Count,
            acquisitions,
            hasSingle,
            hasMultiple,
            voxelTypes,
            containers,
            spacingCount,
            failures.Count == 0,
            failures);
    }

    private static async Task ValidateProvenanceAsync(
        string path,
        RealDataValidationCase item,
        string candidateSourceCommit,
        LabelTiffVolume? candidate,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!TryGetString(root, "schemaVersion", out var schemaVersion) ||
                (schemaVersion != "1.5" && schemaVersion != "1.6" && schemaVersion != "1.7" &&
                 schemaVersion != "1.8" && schemaVersion != "1.9"))
                failures.Add("Candidate provenance schemaVersion must be 1.5, 1.6, 1.7, 1.8, or 1.9.");
            if (!string.IsNullOrWhiteSpace(candidateSourceCommit))
            {
                if (schemaVersion != "1.9")
                    failures.Add("A source-locked manifest requires candidate provenance schemaVersion 1.9.");
                if (!TryGetString(root, "sourceCommit", out var sourceCommit) ||
                    !sourceCommit.Equals(candidateSourceCommit, StringComparison.OrdinalIgnoreCase))
                    failures.Add("Candidate provenance sourceCommit does not match candidateSourceCommit.");
            }
            if (schemaVersion == "1.9") ValidateProcessingSteps(root, failures);
            if (!TryGetString(root, "validationLevel", out var validationLevel) ||
                validationLevel != "synthetic-data-validated")
                failures.Add("Candidate provenance validationLevel is missing or invalid.");
            if (!TryGetString(root, "inputSha256", out var decodedHash) ||
                !HashEquals(decodedHash, item.InputDecodedSha256))
                failures.Add("Candidate provenance inputSha256 does not match inputDecodedSha256.");
            if (!TryGetInt32(root, "inputChannels", out var inputChannels) || inputChannels != item.Channels)
                failures.Add("Candidate provenance inputChannels does not match the manifest.");
            if (!TryGetString(root, "inputVoxelType", out var inputVoxelType) ||
                !inputVoxelType.Equals(ExpectedProvenanceVoxelType(item.VoxelType), StringComparison.OrdinalIgnoreCase))
                failures.Add("Candidate provenance inputVoxelType does not match the manifest.");
            if (!TryGetString(root, "inputContainer", out var inputContainer) ||
                !inputContainer.Equals(item.Container, StringComparison.OrdinalIgnoreCase))
                failures.Add("Candidate provenance inputContainer does not match the manifest.");
            if (!root.TryGetProperty("calibration", out var calibration) ||
                !calibration.TryGetProperty("spacingZ", out var spacingZElement) ||
                !spacingZElement.TryGetDouble(out var provenanceSpacingZ) ||
                !NearlyEqual(provenanceSpacingZ, item.SpacingZ))
                failures.Add("Candidate provenance calibration.spacingZ does not match the manifest.");
            if (!root.TryGetProperty("operation", out var operation) ||
                !TryGetEnum(operation, "operation", out ProcessingOperation operationValue) ||
                operationValue is not (ProcessingOperation.DsltSegmentation or ProcessingOperation.Watershed))
                failures.Add("Candidate provenance operation must be DsltSegmentation or Watershed.");
            else
                ValidateSegmentationChain(root, operation, operationValue, failures);
            if (!TryGetEnum(root, "usedBackend", out ProcessingBackend usedBackend) ||
                usedBackend is not (ProcessingBackend.Cpu or ProcessingBackend.Cuda))
                failures.Add("Candidate provenance usedBackend must be CPU or CUDA.");
            if (!TryGetEnum(root, "outputKind", out OutputKind outputKind) || outputKind != OutputKind.LabelsInt32)
                failures.Add("Candidate provenance outputKind must be LabelsInt32.");
            if (candidate is not null)
            {
                if (!TryGetInt32(root, "outputWidth", out var width) || width != candidate.Width ||
                    !TryGetInt32(root, "outputHeight", out var height) || height != candidate.Height ||
                    !TryGetInt32(root, "outputDepth", out var depth) || depth != candidate.Depth)
                    failures.Add("Candidate provenance output dimensions do not match the label TIFF.");
                if (!TryGetString(root, "outputSha256", out var outputHash) ||
                    !HashEquals(outputHash, ProcessingProvenance.ComputeLabelSha256(candidate.Labels)))
                    failures.Add("Candidate provenance outputSha256 does not match the decoded label TIFF.");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            failures.Add($"Candidate provenance could not be read: {exception.Message}");
        }
    }

    private static void ValidateProcessingSteps(JsonElement root, List<string> failures)
    {
        if (!root.TryGetProperty("processingSteps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            failures.Add("Candidate provenance processingSteps must be an array.");
            return;
        }
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var prefix = $"Candidate provenance processingSteps[{index}]";
            var operationValue = default(ProcessingOperation);
            if (step.ValueKind != JsonValueKind.Object ||
                !step.TryGetProperty("operation", out var operation) ||
                !TryGetEnum(operation, "operation", out operationValue) ||
                operationValue == ProcessingOperation.Watershed)
                failures.Add($"{prefix}.operation is missing or invalid.");
            if (!TryGetEnum(step, "usedBackend", out ProcessingBackend backend) ||
                backend is not (ProcessingBackend.Cpu or ProcessingBackend.Cuda))
                failures.Add($"{prefix}.usedBackend must be CPU or CUDA.");
            if (!TryGetEnum(step, "outputKind", out OutputKind outputKind) ||
                operationValue == ProcessingOperation.DsltSegmentation && outputKind != OutputKind.LabelsInt32 ||
                operationValue != ProcessingOperation.DsltSegmentation && outputKind != OutputKind.VolumeFloat32)
                failures.Add($"{prefix}.outputKind does not match its operation.");
            if (!TryGetInt32(step, "outputWidth", out var width) || width <= 0 ||
                !TryGetInt32(step, "outputHeight", out var height) || height <= 0 ||
                !TryGetInt32(step, "outputDepth", out var depth) || depth <= 0)
                failures.Add($"{prefix} output dimensions must be positive.");
            if (!TryGetString(step, "outputSha256", out var hash) || !IsSha256(hash))
                failures.Add($"{prefix}.outputSha256 is invalid.");
            index++;
        }
    }

    private static void ValidateSegmentationChain(
        JsonElement root,
        JsonElement finalOperation,
        ProcessingOperation finalOperationValue,
        List<string> failures)
    {
        JsonElement? lastStep = null;
        if (root.TryGetProperty("processingSteps", out var availableSteps) &&
            availableSteps.ValueKind == JsonValueKind.Array && availableSteps.GetArrayLength() > 0)
            lastStep = availableSteps[availableSteps.GetArrayLength() - 1];

        var dimensionsSource = lastStep ?? root;
        var dimensionsPrefix = lastStep is null ? "input" : "output";
        if (!TryGetInt32(dimensionsSource, $"{dimensionsPrefix}Width", out var expectedWidth) ||
            !TryGetInt32(dimensionsSource, $"{dimensionsPrefix}Height", out var expectedHeight) ||
            !TryGetInt32(dimensionsSource, $"{dimensionsPrefix}Depth", out var expectedDepth) ||
            !TryGetInt32(root, "outputWidth", out var outputWidth) ||
            !TryGetInt32(root, "outputHeight", out var outputHeight) ||
            !TryGetInt32(root, "outputDepth", out var outputDepth) ||
            expectedWidth != outputWidth || expectedHeight != outputHeight || expectedDepth != outputDepth)
            failures.Add(
                "Candidate provenance final segmentation dimensions must match its immediately preceding input or processing step.");

        if (finalOperationValue != ProcessingOperation.Watershed) return;
        if (lastStep is null)
        {
            failures.Add("A Watershed candidate requires a prior DsltSegmentation processing step.");
            return;
        }
        var seedStep = lastStep.Value;
        if (!seedStep.TryGetProperty("operation", out var seedOperation) ||
            !TryGetEnum(seedOperation, "operation", out ProcessingOperation seedOperationValue) ||
            seedOperationValue != ProcessingOperation.DsltSegmentation ||
            !TryGetEnum(seedStep, "outputKind", out OutputKind seedOutputKind) ||
            seedOutputKind != OutputKind.LabelsInt32 ||
            !TryGetString(seedStep, "outputSha256", out var seedHash) || !IsSha256(seedHash))
        {
            failures.Add("The final processing step before Watershed must be a hashed DsltSegmentation label result.");
            return;
        }
        if (!TryGetString(finalOperation, "seedLabelsSha256", out var requestedSeedHash) ||
            !HashEquals(requestedSeedHash, seedHash))
            failures.Add("Watershed seedLabelsSha256 does not match the prior DsltSegmentation output.");
        if (!finalOperation.TryGetProperty("selectedSeedLabels", out var selected) ||
            selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() == 0)
            failures.Add("Watershed selectedSeedLabels must contain at least one DSLT seed label.");
    }

    private static string? ResolvePath(string baseDirectory, string path, string field, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{field} is required.");
            return null;
        }
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failures.Add($"{field} is invalid: {exception.Message}");
            return null;
        }
    }

    private static async Task VerifyFileAsync(
        string? path,
        string expectedHash,
        string description,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        if (path is null) return;
        if (!File.Exists(path))
        {
            failures.Add($"The {description} file does not exist: {path}");
            return;
        }
        if (!IsSha256(expectedHash))
        {
            failures.Add($"The {description} SHA-256 is missing or invalid.");
            return;
        }
        try
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                failures.Add($"The {description} SHA-256 does not match; actual {actual}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"The {description} file could not be hashed: {exception.Message}");
        }
    }

    private static bool CalibrationMatches(Calibration left, Calibration right) =>
        NearlyEqual(left.SpacingX, right.SpacingX) &&
        NearlyEqual(left.SpacingY, right.SpacingY) &&
        NearlyEqual(left.SpacingZ, right.SpacingZ) &&
        left.UnitName.Equals(right.UnitName, StringComparison.OrdinalIgnoreCase);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var found) &&
            found.ValueKind == JsonValueKind.String &&
            (value = found.GetString() ?? string.Empty).Length != 0;
    }

    private static bool TryGetInt32(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var found) && found.TryGetInt32(out value);
    }

    private static bool TryGetEnum<TEnum>(JsonElement element, string property, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (!element.TryGetProperty(property, out var found)) return false;
        if (found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var numeric))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return Enum.IsDefined(value);
        }
        return found.ValueKind == JsonValueKind.String &&
            Enum.TryParse(found.GetString(), ignoreCase: true, out value) &&
            Enum.IsDefined(value);
    }

    private static string ExpectedProvenanceVoxelType(string voxelType) =>
        voxelType?.ToLowerInvariant() switch
        {
            "uint8" => VolumeVoxelType.UnsignedInt8.ToString(),
            "uint16" => VolumeVoxelType.UnsignedInt16.ToString(),
            "float32" => VolumeVoxelType.Float32.ToString(),
            _ => string.Empty,
        };

    private static bool HashEquals(string actual, string expected) =>
        IsSha256(actual) && IsSha256(expected) && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsGitCommit(string value) =>
        value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly HashSet<string> SupportedVoxelTypes =
        new(["uint8", "uint16", "float32"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SupportedContainers =
        new(["tiff", "lsm"], StringComparer.OrdinalIgnoreCase);
}
