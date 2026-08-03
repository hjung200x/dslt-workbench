using System.Text.Json;
using Dslt.Managed.Core.Validation;

if (args.Length is not (1 or 3) || args.Length == 3 && !args[1].Equals("--output", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: Dslt.Validation <manifest.json> [--output <report.json>]");
    return 2;
}

try
{
    var manifestPath = Path.GetFullPath(args[0]);
    var reportPath = args.Length == 3
        ? Path.GetFullPath(args[2])
        : Path.ChangeExtension(manifestPath, ".report.json");
    if (reportPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Report path must not overwrite the validation manifest.");
    var report = await RealDataValidationRunner.EvaluateAsync(manifestPath);
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    var directory = Path.GetDirectoryName(reportPath)
        ?? throw new InvalidOperationException("Report path has no parent directory.");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, options));

    Console.WriteLine($"Dataset: {report.DatasetName}");
    foreach (var item in report.Cases)
    {
        var metrics = item.Metrics;
        Console.WriteLine(metrics is null
            ? $"[{(item.Passed ? "PASS" : "FAIL")}] {item.Id}: metrics unavailable"
            : $"[{(item.Passed ? "PASS" : "FAIL")}] {item.Id}: Dice={metrics.Dice:F6}, " +
              $"objects={metrics.CandidateObjectCount}/{metrics.ReferenceObjectCount}, " +
              $"volume-diff={metrics.VolumeDifferenceFraction:P4}, HD95={metrics.Hausdorff95Voxels?.ToString("F4") ?? "undefined"}");
        foreach (var failure in item.Failures) Console.WriteLine($"  - {failure}");
    }
    foreach (var failure in report.Coverage.Failures) Console.WriteLine($"Coverage: {failure}");
    Console.WriteLine($"Report: {reportPath}");
    Console.WriteLine(report.ReleaseGatePassed ? "V1.0 REAL-DATA GATE PASSED" : "V1.0 REAL-DATA GATE FAILED");
    return report.ReleaseGatePassed ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Validation could not run: {exception.Message}");
    return 2;
}
