using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dslt.Managed.Core.Models;
using Dslt.Validation.Prepare;

return Run(args);

static int Run(string[] args)
{
    try
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        var options = Parse(args);
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

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                      NotSupportedException or OverflowException)
    {
        Console.Error.WriteLine($"Reference preparation failed: {exception.Message}");
        return 2;
    }
}

static Dictionary<string, string?> Parse(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var key = args[index];
        if (!key.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Unexpected argument: {key}");
        if (!IsKnownOption(key)) throw new ArgumentException($"Unknown option: {key}");
        if (!result.TryAdd(key, null)) throw new ArgumentException($"Option was specified more than once: {key}");
        if (IsFlagOption(key)) continue;
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

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          Dslt.Validation.Prepare --input <reference.tif> --output <normalized.tif>
            --spacing-x <value> --spacing-y <value> --spacing-z <value> --unit <name>
            [--binary] [--source-background <label>] [--output-background <label>]
            [--foreground-label <label>] [--force]

        The source may be a compressed 1- to 16-bit grayscale/indexed TIFF stack.
        With --binary, other WIC-supported mask images such as PNG are also accepted.
        The output is an uncompressed signed 16- or 32-bit TIFF accepted by Dslt.Validation.
        --binary maps the source background to the output background and every other value
        to the foreground label. Existing output is never replaced unless --force is used.
        """);
}

static bool IsKnownOption(string key) => key.ToLowerInvariant() is
    "--input" or "--output" or "--spacing-x" or "--spacing-y" or "--spacing-z" or "--unit" or
    "--binary" or "--source-background" or "--output-background" or "--foreground-label" or "--force";

static bool IsFlagOption(string key) => key.Equals("--binary", StringComparison.OrdinalIgnoreCase) ||
                                        key.Equals("--force", StringComparison.OrdinalIgnoreCase);
