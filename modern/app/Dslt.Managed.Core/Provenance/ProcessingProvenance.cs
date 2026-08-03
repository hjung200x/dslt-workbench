using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Provenance;

public sealed record ProcessingProvenance(
    string SchemaVersion,
    string ValidationLevel,
    DateTimeOffset CreatedAtUtc,
    string InputSha256,
    int InputWidth,
    int InputHeight,
    int InputDepth,
    int InputChannels,
    Calibration Calibration,
    OperationParameters Operation,
    ProcessingBackend UsedBackend,
    OutputKind OutputKind,
    int OutputWidth,
    int OutputHeight,
    int OutputDepth,
    int OutputOriginX,
    int OutputOriginY,
    int OutputOriginZ,
    int ComponentCount,
    string? LabelTiffEncoding,
    string? CompatibilityWarning,
    IReadOnlyList<string> EditHistory)
{
    public static ProcessingProvenance Create(
        VolumeData input,
        OperationParameters operation,
        ProcessingResult result,
        string? labelTiffEncoding = null,
        string? compatibilityWarning = null,
        IReadOnlyList<string>? editHistory = null,
        int outputOriginX = 0,
        int outputOriginY = 0,
        int outputOriginZ = 0)
    {
        ReadOnlySpan<byte> bytes = input.Source is null
            ? MemoryMarshal.AsBytes(input.Samples.AsSpan())
            : input.Source.ChannelPlanarRawSamples;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ProcessingProvenance(
            "1.3",
            "synthetic-data-validated",
            DateTimeOffset.UtcNow,
            hash,
            input.Width,
            input.Height,
            input.Depth,
            input.Channels,
            input.Calibration,
            operation,
            result.UsedBackend,
            result.OutputKind,
            result.Width,
            result.Height,
            result.Depth,
            outputOriginX,
            outputOriginY,
            outputOriginZ,
            result.ComponentCount,
            labelTiffEncoding,
            compatibilityWarning,
            editHistory?.ToArray() ?? []);
    }
}
