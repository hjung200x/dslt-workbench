using System.Text.Json;
using Dslt.Managed.Core.Validation;

internal static class ReferenceAcceptanceTests
{
    private const string ReferenceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task RunAsync()
    {
        var valid = ValidRecord();
        ReferenceAcceptanceValidator.Validate(valid, "acquisition-01", "expert", ReferenceHash);
        ExpectInvalidData(
            () => ReferenceAcceptanceValidator.Validate(
                valid with { ReferenceLabelsSha256 = new string('b', 64) },
                "acquisition-01",
                "expert",
                ReferenceHash),
            "A reference acceptance record was not bound to the exact label hash.");
        ExpectInvalidData(
            () => ReferenceAcceptanceValidator.Validate(
                valid with { WholeVolume3dCoverageConfirmed = false },
                "acquisition-01",
                "expert",
                ReferenceHash),
            "A partial-volume reference acceptance record was allowed.");
        ExpectInvalidData(
            () => ReferenceAcceptanceValidator.Validate(
                valid with { BoundaryRepresentationReviewed = false },
                "acquisition-01",
                "expert",
                ReferenceHash),
            "An unreviewed boundary representation was allowed.");

        var directory = Path.Combine(Path.GetTempPath(), $"dslt-reference-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "reference.acceptance.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(valid));
            var loaded = await ReferenceAcceptanceValidator.ReadAndValidateAsync(
                path, "acquisition-01", "expert", ReferenceHash);
            if (loaded.Record != valid || loaded.FileSha256.Length != 64)
                throw new InvalidOperationException("Reference acceptance file identity was not preserved.");

            await File.WriteAllTextAsync(path, "{ invalid json");
            await ExpectInvalidDataAsync(
                () => ReferenceAcceptanceValidator.ReadAndValidateAsync(
                    path, "acquisition-01", "expert", ReferenceHash),
                "Invalid reference acceptance JSON was allowed.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("DSLT reference acceptance contract tests passed.");
    }

    private static ReferenceAcceptanceRecord ValidRecord() => new()
    {
        SchemaVersion = 1,
        AcquisitionId = "acquisition-01",
        ReferenceKind = "expert",
        ReferenceLabelsSha256 = ReferenceHash,
        AcceptedBy = "fixture reviewer",
        AcceptedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ProtocolId = "fixture-protocol-v1",
        WholeVolume3dCoverageConfirmed = true,
        RepresentativeLeafConfirmed = true,
        BoundaryRepresentationReviewed = true,
        Notes = "Synthetic contract fixture.",
    };

    private static void ExpectInvalidData(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException(message);
        }
        catch (InvalidDataException)
        {
        }
    }

    private static async Task ExpectInvalidDataAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
            throw new InvalidOperationException(message);
        }
        catch (InvalidDataException)
        {
        }
    }
}
