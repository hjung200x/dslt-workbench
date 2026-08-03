using System.Text.Json;
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
        var provenance = ProcessingProvenance.Create(input, operation, result);
        await using var stream = File.Create(metadataPath);
        await JsonSerializer.SerializeAsync(stream, provenance, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}

