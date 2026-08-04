using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Validation;
using Dslt.Validation.Prepare;

namespace Dslt.App.Tests;

internal static class RealDataManifestAssemblerTests
{
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-manifest-assembly-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var manifestPath = Path.Combine(directory, "cohort.json");
            var inputPath = Path.Combine(dataDirectory, "input.tif");
            var referencePath = Path.Combine(dataDirectory, "reference.tif");
            var candidatePath = Path.Combine(dataDirectory, "candidate.tif");
            var provenancePath = Path.Combine(dataDirectory, "candidate.json");
            await File.WriteAllBytesAsync(inputPath, new byte[] { 1, 2, 3, 4, 5 });

            var calibration = new Calibration(0.25, 0.5, 1.5, true, "um");
            var labels = new[]
            {
                0, 0, 1, 1,
                0, 1, 1, 0,
                0, 0, 0, 0,
                0, 2, 2, 0,
                0, 2, 2, 0,
                0, 0, 0, 0,
            };
            LabelTiffCodec.Write(referencePath, 4, 3, 2, labels, calibration);
            LabelTiffCodec.Write(candidatePath, 4, 3, 2, labels, calibration);
            await WriteProvenanceAsync(provenancePath, labels, calibration, ProcessingProvenance.ComputeLabelSha256(labels));

            var unclassifiedPath = Path.Combine(directory, "unclassified.json");
            var unclassified = Request(
                unclassifiedPath, "case-00", "acquisition-00", inputPath, referencePath, candidatePath, provenancePath) with
            {
                RepresentativeReal = false,
            };
            await ExpectFailureAsync<ArgumentException>(() => RealDataManifestAssembler.AddCaseAsync(unclassified),
                "Manifest case was created without explicit representative-real classification.");
            if (File.Exists(unclassifiedPath))
                throw new InvalidOperationException("Rejected classification created a manifest file.");

            var first = await RealDataManifestAssembler.AddCaseAsync(Request(
                manifestPath, "case-01", "acquisition-01", inputPath, referencePath, candidatePath, provenancePath));
            if (first.CaseCount != 1 || first.ManifestSha256 != await Sha256FileAsync(manifestPath))
                throw new InvalidOperationException("Manifest assembly evidence is incomplete.");

            var manifest = JsonSerializer.Deserialize<RealDataValidationManifest>(
                await File.ReadAllBytesAsync(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Assembled manifest could not be read.");
            var item = manifest.Cases.Single();
            if (manifest.SchemaVersion != 2 || manifest.CandidateSourceCommit != SourceCommit ||
                item.VoxelType != "uint16" || item.Container != "tiff" || item.Channels != 2 ||
                item.SelectedChannel != 1 ||
                Math.Abs(item.SpacingZ - 1.5) > 1e-9 || item.InputDecodedSha256 != new string('b', 64) ||
                !item.InputPath.StartsWith("data/", StringComparison.Ordinal))
                throw new InvalidOperationException("Manifest fields were not derived from provenance and local paths.");

            var firstReport = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            if (firstReport.Cases.Count != 1 || !firstReport.Cases[0].Passed || firstReport.Coverage.Passed)
                throw new InvalidOperationException("Assembled case is not structurally compatible with the release validator.");

            var watershedProvenancePath = Path.Combine(dataDirectory, "watershed-candidate.json");
            await WriteProvenanceAsync(
                watershedProvenancePath,
                labels,
                calibration,
                ProcessingProvenance.ComputeLabelSha256(labels),
                watershed: true);
            var watershedManifestPath = Path.Combine(directory, "watershed.json");
            var watershed = await RealDataManifestAssembler.AddCaseAsync(Request(
                watershedManifestPath,
                "watershed-01",
                "watershed-acquisition-01",
                inputPath,
                referencePath,
                candidatePath,
                watershedProvenancePath));
            if (watershed.CaseCount != 1)
                throw new InvalidOperationException("A valid DSLT-to-Watershed candidate was not assembled.");

            var invalidWatershedProvenancePath = Path.Combine(dataDirectory, "invalid-watershed-candidate.json");
            await WriteProvenanceAsync(
                invalidWatershedProvenancePath,
                labels,
                calibration,
                ProcessingProvenance.ComputeLabelSha256(labels),
                watershed: true,
                includeSeedStep: false);
            await ExpectFailureAsync<InvalidDataException>(() => RealDataManifestAssembler.AddCaseAsync(Request(
                    Path.Combine(directory, "invalid-watershed.json"),
                    "watershed-02",
                    "watershed-acquisition-02",
                    inputPath,
                    referencePath,
                    candidatePath,
                    invalidWatershedProvenancePath)),
                "A Watershed candidate without a hashed DSLT seed step was accepted.");

            await ExpectFailureAsync<IOException>(() => RealDataManifestAssembler.AddCaseAsync(Request(
                manifestPath, "case-02", "acquisition-02", inputPath, referencePath, candidatePath, provenancePath)),
                "Existing manifest was changed without --append.");

            var secondRequest = Request(
                manifestPath, "case-02", "acquisition-02", inputPath, referencePath, candidatePath, provenancePath) with
            {
                Append = true,
            };
            var second = await RealDataManifestAssembler.AddCaseAsync(secondRequest);
            if (second.CaseCount != 2)
                throw new InvalidOperationException("Manifest append did not retain the existing case.");

            await ExpectFailureAsync<InvalidDataException>(() => RealDataManifestAssembler.AddCaseAsync(secondRequest),
                "Duplicate manifest case id was accepted.");

            var invalidProvenancePath = Path.Combine(dataDirectory, "invalid-candidate.json");
            await WriteProvenanceAsync(invalidProvenancePath, labels, calibration, new string('c', 64));
            var invalidRequest = Request(
                manifestPath, "case-03", "acquisition-03", inputPath, referencePath, candidatePath, invalidProvenancePath) with
            {
                Append = true,
            };
            await ExpectFailureAsync<InvalidDataException>(() => RealDataManifestAssembler.AddCaseAsync(invalidRequest),
                "Candidate provenance with a mismatched decoded-label hash was accepted.");

            var finalManifest = JsonSerializer.Deserialize<RealDataValidationManifest>(
                await File.ReadAllBytesAsync(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (finalManifest?.Cases.Count != 2)
                throw new InvalidOperationException("Failed append changed the existing manifest.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RealDataManifestCaseRequest Request(
        string manifestPath,
        string id,
        string acquisitionId,
        string inputPath,
        string referencePath,
        string candidatePath,
        string provenancePath) => new(
            manifestPath,
            "leaf-validation-cohort",
            SourceCommit,
            id,
            acquisitionId,
            "expert",
            inputPath,
            referencePath,
            candidatePath,
            provenancePath,
            0,
            0,
            26,
            RepresentativeReal: true,
            Append: false);

    private static async Task WriteProvenanceAsync(
        string path,
        int[] labels,
        Calibration calibration,
        string outputSha256,
        bool watershed = false,
        bool includeSeedStep = true)
    {
        IReadOnlyList<ProcessingStepProvenance> processingSteps = watershed && includeSeedStep
            ?
            [
                new ProcessingStepProvenance(
                    new OperationParameters(ProcessingOperation.DsltSegmentation, ProcessingBackend.Cpu),
                    ProcessingBackend.Cpu,
                    OutputKind.LabelsInt32,
                    4,
                    3,
                    2,
                    outputSha256),
            ]
            : [];
        var operation = watershed
            ? new OperationParameters(
                ProcessingOperation.Watershed,
                ProcessingBackend.Cpu,
                SeedLabelsSha256: outputSha256,
                SelectedSeedLabels: [1, 2])
            : new OperationParameters(ProcessingOperation.DsltSegmentation, ProcessingBackend.Cpu);
        var provenance = new ProcessingProvenance(
            "1.10",
            "synthetic-data-validated",
            SourceCommit,
            DateTimeOffset.UtcNow,
            new string('b', 64),
            4,
            3,
            2,
            2,
            1,
            nameof(VolumeVoxelType.UnsignedInt16),
            "tiff",
            [],
            [],
            calibration,
            calibration,
            processingSteps,
            operation,
            ProcessingBackend.Cpu,
            OutputKind.LabelsInt32,
            4,
            3,
            2,
            0,
            0,
            0,
            outputSha256,
            labels.Where(label => label > 0).Distinct().Count(),
            LabelTiffEncoding.SignedInt16.ToString(),
            null,
            []);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(provenance, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
    }

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task ExpectFailureAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
