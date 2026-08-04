using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dslt.Managed.Core.Models;
using Dslt.Validation.Prepare;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        var explicitCommand = !args[0].StartsWith("--", StringComparison.Ordinal);
        var command = explicitCommand ? args[0].ToLowerInvariant() : "normalize-reference";
        var commandArgs = explicitCommand ? args[1..] : args;
        return command switch
        {
            "normalize-reference" => RunNormalizeReference(commandArgs),
            "add-case" => await RunAddCaseAsync(commandArgs).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command: {args[0]}"),
        };
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                      NotSupportedException or OverflowException or JsonException)
    {
        Console.Error.WriteLine($"Validation preparation failed: {exception.Message}");
        return 2;
    }
}

static int RunNormalizeReference(string[] args)
{
    var options = Parse(args, IsNormalizeOption, IsCommonFlag);
    var calibration = new Calibration(
        ParsePositiveDouble(options, "--spacing-x"),
        ParsePositiveDouble(options, "--spacing-y"),
        ParsePositiveDouble(options, "--spacing-z"),
        true,
        Require(options, "--unit"));
    var result = ReferenceLabelImporter.Import(
        Require(options, "--input"),
        Require(options, "--output"),
        new ReferenceLabelImportOptions(
            calibration,
            options.ContainsKey("--binary"),
            ParseInt32(options, "--source-background", 0),
            ParseInt32(options, "--output-background", 0),
            ParseInt32(options, "--foreground-label", 1)),
        options.ContainsKey("--force"));
    WriteJson(result);
    return 0;
}

static async Task<int> RunAddCaseAsync(string[] args)
{
    var options = Parse(args, IsAddCaseOption, IsCommonFlag);
    var result = await RealDataManifestAssembler.AddCaseAsync(new RealDataManifestCaseRequest(
        Require(options, "--manifest"),
        Require(options, "--dataset-name"),
        Require(options, "--candidate-source-commit"),
        Require(options, "--id"),
        Require(options, "--acquisition-id"),
        Require(options, "--reference-kind"),
        Require(options, "--input-volume"),
        Require(options, "--reference-labels"),
        Require(options, "--candidate-labels"),
        Require(options, "--candidate-provenance"),
        ParseInt32(options, "--reference-background", 0),
        ParseInt32(options, "--candidate-background", 0),
        ParseInt32(options, "--connectivity", 26),
        options.ContainsKey("--representative-real"),
        options.ContainsKey("--append"))).ConfigureAwait(false);
    WriteJson(result);
    return 0;
}

static Dictionary<string, string?> Parse(
    string[] args,
    Func<string, bool> isKnownOption,
    Func<string, bool> isFlagOption)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var key = args[index];
        if (!key.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Unexpected argument: {key}");
        if (!isKnownOption(key)) throw new ArgumentException($"Unknown option: {key}");
        if (!result.TryAdd(key, null)) throw new ArgumentException($"Option was specified more than once: {key}");
        if (isFlagOption(key)) continue;
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option requires a value: {key}");
        result[key] = args[index];
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string?> options, string key) =>
    options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required option is missing: {key}");

static double ParsePositiveDouble(IReadOnlyDictionary<string, string?> options, string key)
{
    var text = Require(options, key);
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
        !double.IsFinite(value) || value <= 0)
        throw new ArgumentException($"{key} must be a finite positive number.");
    return value;
}

static int ParseInt32(IReadOnlyDictionary<string, string?> options, string key, int defaultValue)
{
    if (!options.TryGetValue(key, out var text)) return defaultValue;
    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        throw new ArgumentException($"{key} must be a 32-bit integer.");
    return value;
}

static void WriteJson<T>(T value)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    options.Converters.Add(new JsonStringEnumConverter());
    Console.WriteLine(JsonSerializer.Serialize(value, options));
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          Dslt.Validation.Prepare normalize-reference
            --input <reference.tif> --output <normalized.tif>
            --spacing-x <value> --spacing-y <value> --spacing-z <value> --unit <name>
            [--binary] [--source-background <label>] [--output-background <label>]
            [--foreground-label <label>] [--force]

          Dslt.Validation.Prepare add-case
            --manifest <manifest.json> --dataset-name <name>
            --candidate-source-commit <40-hex-commit>
            --id <case-id> --acquisition-id <acquisition-id>
            --reference-kind <legacy|expert>
            --input-volume <input.tif|input.lsm>
            --reference-labels <reference.tif>
            --candidate-labels <candidate.tif>
            --candidate-provenance <candidate.json>
            --representative-real
            [--reference-background <label>] [--candidate-background <label>]
            [--connectivity <6|18|26>] [--append]

        normalize-reference accepts compressed 1- to 16-bit grayscale/indexed TIFF.
        With --binary, other WIC-supported mask images such as PNG are also accepted.
        Existing normalized output is never replaced unless --force is used.

        add-case derives voxel type, container, channels, Z spacing, and decoded input
        hash from provenance 1.8. It verifies the candidate decoded-label hash and both
        label volumes before atomically creating or extending a schema-2 manifest.
        Existing manifests require --append. Classification requires the explicit
        --representative-real acknowledgement.
        """);
}

static bool IsNormalizeOption(string key) => key.ToLowerInvariant() is
    "--input" or "--output" or "--spacing-x" or "--spacing-y" or "--spacing-z" or "--unit" or
    "--binary" or "--source-background" or "--output-background" or "--foreground-label" or "--force";

static bool IsAddCaseOption(string key) => key.ToLowerInvariant() is
    "--manifest" or "--dataset-name" or "--candidate-source-commit" or "--id" or "--acquisition-id" or
    "--reference-kind" or "--input-volume" or "--reference-labels" or "--candidate-labels" or
    "--candidate-provenance" or "--representative-real" or "--reference-background" or
    "--candidate-background" or "--connectivity" or "--append";

static bool IsCommonFlag(string key) => key.ToLowerInvariant() is
    "--binary" or "--force" or "--representative-real" or "--append";
