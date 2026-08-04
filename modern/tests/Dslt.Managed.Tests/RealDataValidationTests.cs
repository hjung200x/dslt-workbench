using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Provenance;
using Dslt.Managed.Core.Validation;

internal static class RealDataValidationTests
{
    private const string CandidateSourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dslt-real-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cases = new List<RealDataValidationCase>();
            var candidatePaths = new List<string>();
            for (var index = 0; index < 5; index++)
            {
                var inputPath = Path.Combine(root, $"input-{index}.tif");
                await File.WriteAllBytesAsync(inputPath, Encoding.UTF8.GetBytes($"fixture-input-{index}"));
                var calibration = new Calibration(0.5, 0.5, index < 3 ? 1.0 : 2.0, true, "um");
                var labels = new int[4 * 3 * 2];
                labels[1] = 10;
                labels[^2] = 20;
                var referencePath = Path.Combine(root, $"reference-{index}.tif");
                var candidatePath = Path.Combine(root, $"candidate-{index}.tif");
                LabelTiffCodec.Write(referencePath, 4, 3, 2, labels, calibration);
                LabelTiffCodec.Write(candidatePath, 4, 3, 2, labels, calibration);
                candidatePaths.Add(candidatePath);

                var decodedHash = Sha256(Encoding.UTF8.GetBytes($"decoded-input-{index}"));
                var provenancePath = Path.Combine(root, $"candidate-{index}.json");
                await File.WriteAllTextAsync(
                    provenancePath,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "1.8",
                        sourceCommit = CandidateSourceCommit,
                        validationLevel = "synthetic-data-validated",
                        inputSha256 = decodedHash,
                        inputChannels = index is 1 or 3 ? 2 : 1,
                        inputVoxelType = index switch
                        {
                            0 => "UnsignedInt8",
                            1 => "UnsignedInt16",
                            2 => "Float32",
                            3 => "UnsignedInt8",
                            _ => "UnsignedInt16",
                        },
                        inputContainer = "TIFF",
                        calibration,
                        operation = new { operation = (int)ProcessingOperation.DsltSegmentation },
                        usedBackend = (int)ProcessingBackend.Cpu,
                        outputKind = (int)OutputKind.LabelsInt32,
                        outputWidth = 4,
                        outputHeight = 3,
                        outputDepth = 2,
                        outputSha256 = ProcessingProvenance.ComputeLabelSha256(labels),
                    }, JsonOptions));
                cases.Add(new RealDataValidationCase
                {
                    Id = $"case-{index}",
                    AcquisitionId = $"acquisition-{index}",
                    DataClassification = "representative-real",
                    ReferenceKind = index % 2 == 0 ? "legacy" : "expert",
                    InputPath = Path.GetFileName(inputPath),
                    InputFileSha256 = await Sha256FileAsync(inputPath),
                    InputDecodedSha256 = decodedHash,
                    ReferenceLabelsPath = Path.GetFileName(referencePath),
                    ReferenceLabelsSha256 = await Sha256FileAsync(referencePath),
                    CandidateLabelsPath = Path.GetFileName(candidatePath),
                    CandidateLabelsSha256 = await Sha256FileAsync(candidatePath),
                    CandidateProvenancePath = Path.GetFileName(provenancePath),
                    CandidateProvenanceSha256 = await Sha256FileAsync(provenancePath),
                    VoxelType = index switch { 0 => "uint8", 1 => "uint16", 2 => "float32", 3 => "uint8", _ => "uint16" },
                    Container = "tiff",
                    Channels = index is 1 or 3 ? 2 : 1,
                    SpacingZ = calibration.SpacingZ,
                    ObjectConnectivity = 26,
                });
            }

            var manifestPath = Path.Combine(root, "manifest.json");
            await WriteManifestAsync(manifestPath, cases);
            var passed = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            Assert(passed.ReleaseGatePassed, "Five-case validation oracle should pass.");
            Equal(CandidateSourceCommit, passed.CandidateSourceCommit, "Candidate source commit");
            Equal(5, passed.Coverage.UniqueAcquisitionCount, "Unique acquisition coverage");
            Equal(3, passed.Coverage.CoveredVoxelTypes.Count, "Voxel type coverage");
            Equal("tiff", passed.Coverage.CoveredContainers.Single(), "Container evidence");
            Equal(2, passed.Coverage.DistinctZSpacingCount, "Z spacing coverage");
            var sourceLockedProvenancePath = Path.Combine(root, "candidate-0.json");
            var sourceLockedProvenance = JsonNode.Parse(
                await File.ReadAllTextAsync(sourceLockedProvenancePath))!.AsObject();
            sourceLockedProvenance["sourceCommit"] = new string('0', 40);
            await File.WriteAllTextAsync(
                sourceLockedProvenancePath, sourceLockedProvenance.ToJsonString(JsonOptions));
            cases[0] = cases[0] with
            {
                CandidateProvenanceSha256 = await Sha256FileAsync(sourceLockedProvenancePath),
            };
            await WriteManifestAsync(manifestPath, cases);
            var sourceFailure = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            Assert(!sourceFailure.ReleaseGatePassed, "A different candidate source commit must fail the release gate.");
            Assert(sourceFailure.Cases[0].Failures.Any(
                value => value.Contains("sourceCommit", StringComparison.Ordinal)),
                "Source-lock failure should identify sourceCommit.");

            sourceLockedProvenance["sourceCommit"] = CandidateSourceCommit;
            await File.WriteAllTextAsync(
                sourceLockedProvenancePath, sourceLockedProvenance.ToJsonString(JsonOptions));
            cases[0] = cases[0] with
            {
                CandidateProvenanceSha256 = await Sha256FileAsync(sourceLockedProvenancePath),
            };

            var altered = LabelTiffCodec.Read(candidatePaths[0]);
            altered.Labels[1] = 0;
            LabelTiffCodec.Write(
                candidatePaths[0], altered.Width, altered.Height, altered.Depth, altered.Labels, altered.Calibration);
            cases[0] = cases[0] with { CandidateLabelsSha256 = await Sha256FileAsync(candidatePaths[0]) };
            await WriteManifestAsync(manifestPath, cases);
            var provenanceFailure = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            Assert(provenanceFailure.Cases[0].Metrics is null,
                "Stale output provenance must prevent metric evaluation.");
            Assert(provenanceFailure.Cases[0].Failures.Any(value => value.Contains("outputSha256", StringComparison.Ordinal)),
                "Stale output provenance should identify outputSha256.");

            var updatedProvenancePath = Path.Combine(root, "candidate-0.json");
            var provenance = JsonNode.Parse(await File.ReadAllTextAsync(updatedProvenancePath))!.AsObject();
            provenance["outputSha256"] = ProcessingProvenance.ComputeLabelSha256(altered.Labels);
            await File.WriteAllTextAsync(updatedProvenancePath, provenance.ToJsonString(JsonOptions));
            cases[0] = cases[0] with { CandidateProvenanceSha256 = await Sha256FileAsync(updatedProvenancePath) };
            await WriteManifestAsync(manifestPath, cases);
            var metricFailure = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            Assert(!metricFailure.ReleaseGatePassed, "A missing candidate object must fail the release gate.");
            Assert(metricFailure.Cases[0].Failures.Any(value => value.Contains("Dice", StringComparison.Ordinal)),
                "Metric failure should identify Dice.");

            cases[0] = cases[0] with { InputFileSha256 = new string('0', 64) };
            await WriteManifestAsync(manifestPath, cases);
            var hashFailure = await RealDataValidationRunner.EvaluateAsync(manifestPath);
            Assert(hashFailure.Cases[0].Metrics is null, "Hash mismatch must prevent metric evaluation.");
            Assert(hashFailure.Cases[0].Failures.Any(value => value.Contains("SHA-256 does not match", StringComparison.Ordinal)),
                "Hash mismatch should be explicit.");

            Console.WriteLine("DSLT real-data manifest and release-gate tests passed.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task WriteManifestAsync(string path, IReadOnlyList<RealDataValidationCase> cases) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RealDataValidationManifest
        {
            SchemaVersion = 2,
            CandidateSourceCommit = CandidateSourceCommit,
            DatasetName = "validation-oracle",
            Cases = cases,
        }, JsonOptions));

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}.");
    }
}
