using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Validation;

namespace Dslt.Validation.Prepare;

public sealed record RealDataManifestCaseRequest(
    string ManifestPath,
    string DatasetName,
    string CandidateSourceCommit,
    string Id,
    string AcquisitionId,
    string ReferenceKind,
    string InputPath,
    string ReferenceLabelsPath,
    string CandidateLabelsPath,
    string CandidateProvenancePath,
    int ReferenceBackgroundLabel,
    int CandidateBackgroundLabel,
    int ObjectConnectivity,
    bool RepresentativeReal,
    bool Append);

public sealed record RealDataManifestAssemblyResult(
    string ManifestPath,
    int CaseCount,
    string AddedCaseId,
    string CandidateSourceCommit,
    string ManifestSha256);

public static class RealDataManifestAssembler
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<RealDataManifestAssemblyResult> AddCaseAsync(
        RealDataManifestCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var manifestPath = Path.GetFullPath(request.ManifestPath);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new ArgumentException("Manifest path has no parent directory.", nameof(request));
        var inputPath = RequireFile(request.InputPath, "Input volume");
        var referencePath = RequireFile(request.ReferenceLabelsPath, "Reference labels");
        var candidatePath = RequireFile(request.CandidateLabelsPath, "Candidate labels");
        var provenancePath = RequireFile(request.CandidateProvenancePath, "Candidate provenance");

        var provenanceBytes = await File.ReadAllBytesAsync(provenancePath, cancellationToken).ConfigureAwait(false);
        var provenance = JsonSerializer.Deserialize<ProcessingProvenance>(provenanceBytes, ReadOptions)
            ?? throw new InvalidDataException("Candidate provenance is empty.");
        ValidateProvenance(provenance, request.CandidateSourceCommit);

        var reference = LabelTiffCodec.Read(referencePath);
        var candidate = LabelTiffCodec.Read(candidatePath);
        ValidateLabels(reference, candidate, provenance);

        var manifestExists = File.Exists(manifestPath);
        if (manifestExists && !request.Append)
            throw new IOException($"Manifest already exists: {manifestPath}. Pass --append to add a case.");
        if (!manifestExists && request.Append)
            throw new IOException("--append requires an existing schema-2 manifest.");

        RealDataValidationManifest manifest;
        if (manifestExists)
        {
            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<RealDataValidationManifest>(bytes, ReadOptions)
                ?? throw new InvalidDataException("Existing validation manifest is empty.");
            if (manifest.SchemaVersion != 2)
                throw new InvalidDataException("Only schema-2 validation manifests can be extended.");
            if (!string.Equals(manifest.DatasetName, request.DatasetName, StringComparison.Ordinal))
                throw new InvalidDataException("Existing manifest datasetName does not match --dataset-name.");
            if (!string.Equals(manifest.CandidateSourceCommit, request.CandidateSourceCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing manifest candidateSourceCommit does not match the requested commit.");
            if (manifest.Cases is null)
                throw new InvalidDataException("Existing validation manifest cases are missing.");
            if (manifest.Cases.Any(item => string.Equals(item?.Id, request.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Manifest already contains case id '{request.Id}'.");
        }
        else
        {
            manifest = new RealDataValidationManifest
            {
                SchemaVersion = 2,
                DatasetName = request.DatasetName,
                CandidateSourceCommit = request.CandidateSourceCommit.ToLowerInvariant(),
                Cases = [],
            };
        }

        var cases = manifest.Cases.ToList();
        cases.Add(new RealDataValidationCase
        {
            Id = request.Id,
            AcquisitionId = request.AcquisitionId,
            DataClassification = "representative-real",
            ReferenceKind = request.ReferenceKind.ToLowerInvariant(),
            InputPath = PortablePath(manifestDirectory, inputPath),
            InputFileSha256 = await Sha256FileAsync(inputPath, cancellationToken).ConfigureAwait(false),
            InputDecodedSha256 = provenance.InputSha256.ToLowerInvariant(),
            ReferenceLabelsPath = PortablePath(manifestDirectory, referencePath),
            ReferenceLabelsSha256 = await Sha256FileAsync(referencePath, cancellationToken).ConfigureAwait(false),
            CandidateLabelsPath = PortablePath(manifestDirectory, candidatePath),
            CandidateLabelsSha256 = await Sha256FileAsync(candidatePath, cancellationToken).ConfigureAwait(false),
            CandidateProvenancePath = PortablePath(manifestDirectory, provenancePath),
            CandidateProvenanceSha256 = Convert.ToHexString(SHA256.HashData(provenanceBytes)).ToLowerInvariant(),
            VoxelType = ManifestVoxelType(provenance.InputVoxelType),
            Container = provenance.InputContainer.ToLowerInvariant(),
            Channels = provenance.InputChannels,
            SelectedChannel = provenance.InputSelectedChannel,
            SpacingZ = provenance.Calibration.SpacingZ,
            ReferenceBackgroundLabel = request.ReferenceBackgroundLabel,
            CandidateBackgroundLabel = request.CandidateBackgroundLabel,
            ObjectConnectivity = request.ObjectConnectivity,
        });
        manifest = manifest with { Cases = cases };

        Directory.CreateDirectory(manifestDirectory);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
        var temporaryPath = Path.Combine(manifestDirectory, $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, manifestBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, manifestPath, overwrite: manifestExists);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new RealDataManifestAssemblyResult(
            manifestPath,
            cases.Count,
            request.Id,
            request.CandidateSourceCommit.ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    private static void ValidateRequest(RealDataManifestCaseRequest request)
    {
        RequireText(request.ManifestPath, nameof(request.ManifestPath));
        RequireText(request.DatasetName, nameof(request.DatasetName));
        RequireText(request.Id, nameof(request.Id));
        RequireText(request.AcquisitionId, nameof(request.AcquisitionId));
        RequireText(request.InputPath, nameof(request.InputPath));
        RequireText(request.ReferenceLabelsPath, nameof(request.ReferenceLabelsPath));
        RequireText(request.CandidateLabelsPath, nameof(request.CandidateLabelsPath));
        RequireText(request.CandidateProvenancePath, nameof(request.CandidateProvenancePath));
        if (!IsGitCommit(request.CandidateSourceCommit))
            throw new ArgumentException("Candidate source commit must be a full 40-hex Git commit.", nameof(request));
        if (!request.RepresentativeReal)
            throw new ArgumentException("--representative-real is required to explicitly confirm cohort classification.", nameof(request));
        if (!request.ReferenceKind.Equals("legacy", StringComparison.OrdinalIgnoreCase) &&
            !request.ReferenceKind.Equals("expert", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Reference kind must be legacy or expert.", nameof(request));
        if (request.ObjectConnectivity is not (6 or 18 or 26))
            throw new ArgumentException("Object connectivity must be 6, 18, or 26.", nameof(request));
    }

    private static void ValidateProvenance(ProcessingProvenance provenance, string expectedCommit)
    {
        if (provenance.SchemaVersion != "1.10")
            throw new InvalidDataException("Candidate provenance schemaVersion must be 1.10.");
        if (provenance.ValidationLevel != "synthetic-data-validated")
            throw new InvalidDataException("Candidate provenance validationLevel is invalid.");
        if (!string.Equals(provenance.SourceCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate provenance sourceCommit does not match --candidate-source-commit.");
        if (!IsSha256(provenance.InputSha256))
            throw new InvalidDataException("Candidate provenance inputSha256 is invalid.");
        if (provenance.InputChannels <= 0)
            throw new InvalidDataException("Candidate provenance inputChannels must be positive.");
        if (provenance.InputSelectedChannel < 0 || provenance.InputSelectedChannel >= provenance.InputChannels)
            throw new InvalidDataException("Candidate provenance inputSelectedChannel is invalid.");
        if (provenance.InputCalibration is null ||
            !double.IsFinite(provenance.InputCalibration.SpacingX) || provenance.InputCalibration.SpacingX <= 0 ||
            !double.IsFinite(provenance.InputCalibration.SpacingY) || provenance.InputCalibration.SpacingY <= 0 ||
            !double.IsFinite(provenance.InputCalibration.SpacingZ) || provenance.InputCalibration.SpacingZ <= 0)
            throw new InvalidDataException("Candidate provenance input calibration must be finite and positive.");
        _ = ManifestVoxelType(provenance.InputVoxelType);
        if (!string.Equals(provenance.InputContainer, "tiff", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(provenance.InputContainer, "lsm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate provenance inputContainer must be tiff or lsm.");
        if (provenance.Calibration is null ||
            !double.IsFinite(provenance.Calibration.SpacingX) || provenance.Calibration.SpacingX <= 0 ||
            !double.IsFinite(provenance.Calibration.SpacingY) || provenance.Calibration.SpacingY <= 0 ||
            !double.IsFinite(provenance.Calibration.SpacingZ) || provenance.Calibration.SpacingZ <= 0)
            throw new InvalidDataException("Candidate provenance output calibration must be finite and positive.");
        if (provenance.Operation is null || provenance.Operation.Operation is not
            (ProcessingOperation.DsltSegmentation or ProcessingOperation.Watershed))
            throw new InvalidDataException("Candidate provenance operation must be DsltSegmentation or Watershed.");
        if (provenance.UsedBackend is not (ProcessingBackend.Cpu or ProcessingBackend.Cuda))
            throw new InvalidDataException("Candidate provenance usedBackend must be CPU or CUDA.");
        if (provenance.OutputKind != OutputKind.LabelsInt32)
            throw new InvalidDataException("Candidate provenance outputKind must be LabelsInt32.");
        if (!IsSha256(provenance.OutputSha256))
            throw new InvalidDataException("Candidate provenance outputSha256 is invalid.");
        if (provenance.ProcessingSteps is null || provenance.ProcessingSteps.Any(step =>
                step is null || step.Operation is null ||
                step.Operation.Operation == ProcessingOperation.Watershed ||
                step.UsedBackend is not (ProcessingBackend.Cpu or ProcessingBackend.Cuda) ||
                step.Operation.Operation == ProcessingOperation.DsltSegmentation &&
                    step.OutputKind != OutputKind.LabelsInt32 ||
                step.Operation.Operation != ProcessingOperation.DsltSegmentation &&
                    step.OutputKind != OutputKind.VolumeFloat32 ||
                step.OutputWidth <= 0 || step.OutputHeight <= 0 || step.OutputDepth <= 0 ||
                !IsSha256(step.OutputSha256)))
            throw new InvalidDataException("Candidate provenance processingSteps is invalid.");
        var finalInput = provenance.ProcessingSteps.LastOrDefault();
        var finalInputWidth = finalInput?.OutputWidth ?? provenance.InputWidth;
        var finalInputHeight = finalInput?.OutputHeight ?? provenance.InputHeight;
        var finalInputDepth = finalInput?.OutputDepth ?? provenance.InputDepth;
        if (finalInputWidth != provenance.OutputWidth ||
            finalInputHeight != provenance.OutputHeight ||
            finalInputDepth != provenance.OutputDepth)
            throw new InvalidDataException(
                "Candidate provenance final segmentation dimensions must match its immediately preceding input or processing step.");
        if (provenance.Operation.Operation == ProcessingOperation.Watershed)
        {
            var seedStep = provenance.ProcessingSteps.LastOrDefault();
            if (seedStep?.Operation.Operation != ProcessingOperation.DsltSegmentation ||
                seedStep.OutputKind != OutputKind.LabelsInt32 ||
                !string.Equals(
                    provenance.Operation.SeedLabelsSha256,
                    seedStep.OutputSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                provenance.Operation.SelectedSeedLabels is not { Length: > 0 })
                throw new InvalidDataException(
                    "A Watershed candidate requires a matching prior DsltSegmentation seed step.");
        }
    }

    private static void ValidateLabels(
        LabelTiffVolume reference,
        LabelTiffVolume candidate,
        ProcessingProvenance provenance)
    {
        if (reference.Width != candidate.Width || reference.Height != candidate.Height || reference.Depth != candidate.Depth)
            throw new InvalidDataException("Reference and candidate label dimensions differ.");
        if (!CalibrationMatches(reference.Calibration, candidate.Calibration))
            throw new InvalidDataException("Reference and candidate label calibration differs.");
        if (!CalibrationMatches(candidate.Calibration, provenance.Calibration))
            throw new InvalidDataException("Candidate label calibration does not match provenance output calibration.");
        if (candidate.Width != provenance.OutputWidth || candidate.Height != provenance.OutputHeight ||
            candidate.Depth != provenance.OutputDepth)
            throw new InvalidDataException("Candidate label dimensions do not match provenance.");
        var decodedHash = ProcessingProvenance.ComputeLabelSha256(candidate.Labels);
        if (!decodedHash.Equals(provenance.OutputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate decoded label hash does not match provenance outputSha256.");
    }

    private static string ManifestVoxelType(string value) => value?.ToLowerInvariant() switch
    {
        "unsignedint8" => "uint8",
        "unsignedint16" => "uint16",
        "float32" => "float32",
        _ => throw new InvalidDataException("Candidate provenance inputVoxelType must be UnsignedInt8, UnsignedInt16, or Float32."),
    };

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"{description} file was not found.", fullPath);
        return fullPath;
    }

    private static string PortablePath(string manifestDirectory, string filePath) =>
        Path.GetRelativePath(manifestDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static bool CalibrationMatches(Calibration left, Calibration right) =>
        NearlyEqual(left.SpacingX, right.SpacingX) && NearlyEqual(left.SpacingY, right.SpacingY) &&
        NearlyEqual(left.SpacingZ, right.SpacingZ) &&
        left.UnitName.Equals(right.UnitName, StringComparison.OrdinalIgnoreCase);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static bool IsGitCommit(string value) => value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}
