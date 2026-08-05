using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Dslt.Managed.Core.Validation;

namespace Dslt.Validation.Prepare;

public sealed record ReferenceAcceptanceRequest(
    string ReferenceLabelsPath,
    string OutputPath,
    string AcquisitionId,
    string ReferenceKind,
    string AcceptedBy,
    DateTimeOffset AcceptedAtUtc,
    string ProtocolId,
    bool WholeVolume3dCoverageConfirmed,
    bool RepresentativeLeafConfirmed,
    bool BoundaryRepresentationReviewed,
    string Notes,
    bool Force);

public sealed record ReferenceAcceptanceWriteResult(
    string OutputPath,
    string OutputSha256,
    ReferenceAcceptanceRecord Record);

public static class ReferenceAcceptanceWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ReferenceAcceptanceWriteResult> WriteAsync(
        ReferenceAcceptanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReferenceLabelsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AcquisitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReferenceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AcceptedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProtocolId);
        var referencePath = Path.GetFullPath(request.ReferenceLabelsPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (!File.Exists(referencePath))
            throw new FileNotFoundException("Reference label TIFF was not found.", referencePath);
        if (File.Exists(outputPath) && !request.Force)
            throw new IOException($"Reference acceptance output already exists: {outputPath}. Pass --force to replace it.");
        if (string.Equals(referencePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Reference acceptance output cannot replace the reference label TIFF.", nameof(request));

        string referenceHash;
        await using (var stream = File.OpenRead(referencePath))
            referenceHash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

        var record = new ReferenceAcceptanceRecord
        {
            SchemaVersion = 1,
            AcquisitionId = request.AcquisitionId,
            ReferenceKind = request.ReferenceKind.ToLowerInvariant(),
            ReferenceLabelsSha256 = referenceHash,
            AcceptedBy = request.AcceptedBy,
            AcceptedAtUtc = request.AcceptedAtUtc,
            ProtocolId = request.ProtocolId,
            WholeVolume3dCoverageConfirmed = request.WholeVolume3dCoverageConfirmed,
            RepresentativeLeafConfirmed = request.RepresentativeLeafConfirmed,
            BoundaryRepresentationReviewed = request.BoundaryRepresentationReviewed,
            Notes = request.Notes,
        };
        ReferenceAcceptanceValidator.Validate(
            record, request.AcquisitionId, request.ReferenceKind, referenceHash);

        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("Reference acceptance output has no parent directory.", nameof(request));
        Directory.CreateDirectory(outputDirectory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, WriteOptions);
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, overwrite: request.Force);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new ReferenceAcceptanceWriteResult(
            outputPath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            record);
    }
}
