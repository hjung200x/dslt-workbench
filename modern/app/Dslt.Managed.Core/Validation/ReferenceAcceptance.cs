using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dslt.Managed.Core.Validation;

public sealed record ReferenceAcceptanceRecord
{
    public int SchemaVersion { get; init; }
    public string AcquisitionId { get; init; } = string.Empty;
    public string ReferenceKind { get; init; } = string.Empty;
    public string ReferenceLabelsSha256 { get; init; } = string.Empty;
    public string AcceptedBy { get; init; } = string.Empty;
    public DateTimeOffset AcceptedAtUtc { get; init; }
    public string ProtocolId { get; init; } = string.Empty;
    public bool WholeVolume3dCoverageConfirmed { get; init; }
    public bool RepresentativeLeafConfirmed { get; init; }
    public bool BoundaryRepresentationReviewed { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record ValidatedReferenceAcceptance(
    ReferenceAcceptanceRecord Record,
    string FileSha256);

public static class ReferenceAcceptanceValidator
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<ValidatedReferenceAcceptance> ReadAndValidateAsync(
        string path,
        string expectedAcquisitionId,
        string expectedReferenceKind,
        string expectedReferenceLabelsSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Reference acceptance record was not found.", fullPath);

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        ReferenceAcceptanceRecord record;
        try
        {
            record = JsonSerializer.Deserialize<ReferenceAcceptanceRecord>(bytes, ReadOptions)
                ?? throw new InvalidDataException("Reference acceptance record is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Reference acceptance record is invalid JSON: {exception.Message}", exception);
        }

        Validate(record, expectedAcquisitionId, expectedReferenceKind, expectedReferenceLabelsSha256);
        return new ValidatedReferenceAcceptance(
            record,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static void Validate(
        ReferenceAcceptanceRecord record,
        string expectedAcquisitionId,
        string expectedReferenceKind,
        string expectedReferenceLabelsSha256)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAcquisitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedReferenceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedReferenceLabelsSha256);

        if (record.SchemaVersion != 1)
            throw new InvalidDataException("Reference acceptance schemaVersion must be 1.");
        if (!string.Equals(record.AcquisitionId, expectedAcquisitionId, StringComparison.Ordinal))
            throw new InvalidDataException("Reference acceptance acquisitionId does not match the manifest case.");
        if (record.ReferenceKind is null ||
            !record.ReferenceKind.Equals("legacy", StringComparison.OrdinalIgnoreCase) &&
            !record.ReferenceKind.Equals("expert", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reference acceptance referenceKind must be legacy or expert.");
        if (!string.Equals(record.ReferenceKind, expectedReferenceKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reference acceptance referenceKind does not match the manifest case.");
        if (!IsSha256(record.ReferenceLabelsSha256))
            throw new InvalidDataException("Reference acceptance referenceLabelsSha256 must be 64 lowercase hexadecimal characters.");
        if (!string.Equals(record.ReferenceLabelsSha256, expectedReferenceLabelsSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Reference acceptance does not identify the exact reference label TIFF.");
        if (string.IsNullOrWhiteSpace(record.AcceptedBy))
            throw new InvalidDataException("Reference acceptance acceptedBy is required.");
        if (record.AcceptedAtUtc == default)
            throw new InvalidDataException("Reference acceptance acceptedAtUtc is required.");
        if (record.AcceptedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("Reference acceptance acceptedAtUtc cannot be in the future.");
        if (record.AcceptedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Reference acceptance acceptedAtUtc must use a zero UTC offset.");
        if (string.IsNullOrWhiteSpace(record.ProtocolId))
            throw new InvalidDataException("Reference acceptance protocolId is required.");
        if (!record.WholeVolume3dCoverageConfirmed)
            throw new InvalidDataException("Reference acceptance must confirm whole-volume 3D coverage.");
        if (!record.RepresentativeLeafConfirmed)
            throw new InvalidDataException("Reference acceptance must confirm representative leaf classification.");
        if (!record.BoundaryRepresentationReviewed)
            throw new InvalidDataException("Reference acceptance must confirm boundary-representation review.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
