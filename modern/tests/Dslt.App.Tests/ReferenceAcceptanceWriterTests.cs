using System.IO;
using System.Security.Cryptography;
using Dslt.Managed.Core.IO;
using Dslt.Managed.Core.Models;
using Dslt.Managed.Core.Validation;
using Dslt.Validation.Prepare;

namespace Dslt.App.Tests;

internal static class ReferenceAcceptanceWriterTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dslt-reference-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var referencePath = Path.Combine(directory, "reference.tif");
            var outputPath = Path.Combine(directory, "reference.acceptance.json");
            LabelTiffCodec.Write(
                referencePath,
                2,
                2,
                2,
                [0, 1, 1, 0, 0, 2, 2, 0],
                new Calibration(0.5, 0.5, 1.0, true, "um"));

            var request = Request(referencePath, outputPath);
            var result = await ReferenceAcceptanceWriter.WriteAsync(request);
            await using var referenceStream = File.OpenRead(referencePath);
            var expectedReferenceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(referenceStream));
            var validated = await ReferenceAcceptanceValidator.ReadAndValidateAsync(
                outputPath, "acquisition-01", "expert", expectedReferenceHash);
            if (result.OutputSha256 != validated.FileSha256 || result.Record != validated.Record)
                throw new InvalidOperationException("Written reference acceptance identity was not reproducible.");

            await ExpectFailureAsync<IOException>(
                () => ReferenceAcceptanceWriter.WriteAsync(request),
                "Existing reference acceptance output was replaced without --force.");

            var rejectedPath = Path.Combine(directory, "rejected.acceptance.json");
            await ExpectFailureAsync<InvalidDataException>(
                () => ReferenceAcceptanceWriter.WriteAsync(Request(referencePath, rejectedPath) with
                {
                    WholeVolume3dCoverageConfirmed = false,
                }),
                "Partial-volume acceptance was written.");
            if (File.Exists(rejectedPath))
                throw new InvalidOperationException("Rejected reference acceptance left an output file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("DSLT reference acceptance writer tests passed.");
    }

    private static ReferenceAcceptanceRequest Request(string referencePath, string outputPath) => new(
        referencePath,
        outputPath,
        "acquisition-01",
        "expert",
        "fixture reviewer",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        "fixture-protocol-v1",
        WholeVolume3dCoverageConfirmed: true,
        RepresentativeLeafConfirmed: true,
        BoundaryRepresentationReviewed: true,
        Notes: "Synthetic writer fixture.",
        Force: false);

    private static async Task ExpectFailureAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
            throw new InvalidOperationException(message);
        }
        catch (TException)
        {
        }
    }
}
