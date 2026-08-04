using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Provenance;

public sealed record ProcessingStepProvenance(
    OperationParameters Operation,
    ProcessingBackend UsedBackend,
    OutputKind OutputKind,
    int OutputWidth,
    int OutputHeight,
    int OutputDepth,
    string OutputSha256)
{
    public static ProcessingStepProvenance Create(OperationParameters operation, ProcessingResult result) =>
        new(
            operation,
            result.UsedBackend,
            result.OutputKind,
            result.Width,
            result.Height,
            result.Depth,
            ProcessingProvenance.ComputeOutputSha256(result));
}

public sealed record ProcessingProvenance(
    string SchemaVersion,
    string ValidationLevel,
    string SourceCommit,
    DateTimeOffset CreatedAtUtc,
    string InputSha256,
    int InputWidth,
    int InputHeight,
    int InputDepth,
    int InputChannels,
    string InputVoxelType,
    string InputContainer,
    IReadOnlyList<VolumeChannelInfo> InputChannelMetadata,
    IReadOnlyList<double> InputTimeStampsSeconds,
    Calibration Calibration,
    IReadOnlyList<ProcessingStepProvenance> ProcessingSteps,
    OperationParameters Operation,
    ProcessingBackend UsedBackend,
    OutputKind OutputKind,
    int OutputWidth,
    int OutputHeight,
    int OutputDepth,
    int OutputOriginX,
    int OutputOriginY,
    int OutputOriginZ,
    string OutputSha256,
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
        int outputOriginZ = 0,
        IReadOnlyList<ProcessingStepProvenance>? processingSteps = null)
    {
        ReadOnlySpan<byte> bytes = input.Source is null
            ? MemoryMarshal.AsBytes(input.Samples.AsSpan())
            : input.Source.ChannelPlanarRawSamples;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ProcessingProvenance(
            "1.9",
            "synthetic-data-validated",
            ResolveSourceCommit(),
            DateTimeOffset.UtcNow,
            hash,
            input.Width,
            input.Height,
            input.Depth,
            input.Channels,
            (input.Source?.VoxelType ?? VolumeVoxelType.Float32).ToString(),
            input.Source?.Container ?? "memory-float32",
            input.Source?.ChannelMetadata?.ToArray() ?? [],
            input.Source?.TimeStampsSeconds?.ToArray() ?? [],
            input.Calibration,
            processingSteps?.ToArray() ?? [],
            operation,
            result.UsedBackend,
            result.OutputKind,
            result.Width,
            result.Height,
            result.Depth,
            outputOriginX,
            outputOriginY,
            outputOriginZ,
            ComputeOutputSha256(result),
            result.ComponentCount,
            labelTiffEncoding,
            compatibilityWarning,
            editHistory?.ToArray() ?? []);
    }

    public static string ComputeLabelSha256(ReadOnlySpan<int> labels) =>
        ComputeLittleEndianSha256(labels, static value => value);

    private static string ResolveSourceCommit()
    {
        var value = typeof(ProcessingProvenance).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SourceCommit")?
            .Value?
            .ToLowerInvariant();
        return value is { Length: 40 } && value.All(Uri.IsHexDigit)
            ? value
            : "unavailable";
    }

    public static string ComputeFloatSha256(ReadOnlySpan<float> values) =>
        ComputeLittleEndianSha256(values, static value => BitConverter.SingleToInt32Bits(value));

    public static string ComputeOutputSha256(ProcessingResult result)
    {
        if (result.Labels is not null) return ComputeLabelSha256(result.Labels);
        if (result.FloatData is not null) return ComputeFloatSha256(result.FloatData);
        throw new ArgumentException("Processing result has no output payload.", nameof(result));
    }

    private static string ComputeLittleEndianSha256<T>(
        ReadOnlySpan<T> values,
        Func<T, int> bits)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        const int valuesPerBuffer = 4096 / sizeof(int);
        for (var offset = 0; offset < values.Length; offset += valuesPerBuffer)
        {
            var count = Math.Min(valuesPerBuffer, values.Length - offset);
            for (var index = 0; index < count; index++)
                BinaryPrimitives.WriteInt32LittleEndian(buffer[(index * sizeof(int))..], bits(values[offset + index]));
            hash.AppendData(buffer[..(count * sizeof(int))]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
