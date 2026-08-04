using System.Text.Json;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;

namespace Dslt.Managed.Core.Provenance;

public static class ResultPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task WriteAsync(
        string basePath,
        VolumeData input,
        OperationParameters operation,
        ProcessingResult result,
        IReadOnlyList<string>? editHistory = null,
        int outputOriginX = 0,
        int outputOriginY = 0,
        int outputOriginZ = 0,
        IReadOnlyList<ProcessingStepProvenance>? processingSteps = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var fullBasePath = Path.GetFullPath(basePath);
        var directory = Path.GetDirectoryName(fullBasePath)
            ?? throw new ArgumentException("Export path does not have a parent directory.", nameof(basePath));
        Directory.CreateDirectory(directory);

        var rawPath = fullBasePath + (result.Labels is null ? ".f32.raw" : ".i32.raw");
        var metadataPath = fullBasePath + ".json";
        byte[] bytes;
        if (result.FloatData is not null)
        {
            bytes = new byte[checked(result.FloatData.Length * sizeof(float))];
            Buffer.BlockCopy(result.FloatData, 0, bytes, 0, bytes.Length);
        }
        else if (result.Labels is not null)
        {
            bytes = new byte[checked(result.Labels.Length * sizeof(int))];
            Buffer.BlockCopy(result.Labels, 0, bytes, 0, bytes.Length);
        }
        else
        {
            throw new ArgumentException("Processing result has no output payload.", nameof(result));
        }

        await File.WriteAllBytesAsync(rawPath, bytes, cancellationToken).ConfigureAwait(false);
        string? labelTiffEncoding = null;
        string? compatibilityWarning = null;
        if (result.Labels is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoding = LabelTiffCodec.SelectEncoding(result.Labels);
            var suffix = encoding == LabelTiffEncoding.SignedInt16 ? ".labels.i16.tif" : ".labels.i32.tif";
            LabelTiffCodec.Write(
                fullBasePath + suffix,
                result.Width,
                result.Height,
                result.Depth,
                result.Labels,
                input.Calibration,
                encoding);
            labelTiffEncoding = encoding.ToString();
            if (encoding == LabelTiffEncoding.SignedInt32)
                compatibilityWarning = "Labels exceed the legacy signed 16-bit TIFF range; a signed 32-bit TIFF was written.";
        }
        var provenance = ProcessingProvenance.Create(
            input,
            operation,
            result,
            labelTiffEncoding,
            compatibilityWarning,
            editHistory,
            outputOriginX,
            outputOriginY,
            outputOriginZ,
            processingSteps);
        await using var stream = File.Create(metadataPath);
        await JsonSerializer.SerializeAsync(stream, provenance, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
