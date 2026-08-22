using System.Text.Json;
using System.Text.Json.Serialization;
using PanoramaBridge.ThermoRaw;

namespace PanoramaBridge.ThermoRawCheck;

/// <summary>
/// Checks Thermo RAW files for truncation from a command line.
/// </summary>
/// <remarks>
/// Exit codes follow thermo-raw-file-validator so results from the two can be compared directly
/// in a script: 0 when nothing is wrong or nothing could be determined, 1 when a file is
/// positively short, 3 when a file could not be read. "Could not be determined" is not a failure;
/// treating it as one would make an unrecognised revision look like a broken file.
/// </remarks>
public static class Program
{
    private const int Ok = 0;
    private const int Truncated = 1;
    private const int Usage = 2;
    private const int Unreadable = 3;

    public static int Main(string[] args)
    {
        var paths = new List<string>();
        var json = false;
        var quiet = false;
        var strict = false;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return Ok;
                case "--version":
                    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0");
                    return Ok;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        PrintUsage();
                        return Usage;
                    }

                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            PrintUsage();
            return Usage;
        }

        var results = Expand(paths).Select(ThermoRawValidator.Validate).ToList();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, JsonContext.ListThermoRawResult));
        }
        else if (!quiet)
        {
            foreach (var r in results)
            {
                Report(r);
            }
        }

        if (results.Any(r => r.Verdict == ThermoRawVerdict.Truncated))
        {
            return Truncated;
        }

        if (results.Any(r => r.Verdict == ThermoRawVerdict.Error))
        {
            return Unreadable;
        }

        // Under --strict anything short of a clean answer fails, for a pipeline that would rather
        // stop than carry a file nothing could vouch for.
        if (strict && results.Any(r => r.Verdict != ThermoRawVerdict.NoTruncationDetected))
        {
            return Truncated;
        }

        return Ok;
    }

    private static readonly ResultJsonContext JsonContext = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });

    /// <summary>Expands directories to the RAW files inside them, one level down.</summary>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory
                             .EnumerateFiles(path, "*.raw", SearchOption.TopDirectoryOnly)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }

                continue;
            }

            yield return path;
        }
    }

    private static void Report(ThermoRawResult r)
    {
        Console.WriteLine($"{Path.GetFileName(r.Path)}: {r.Summary}");

        // Evidence only where it changes what someone would do. A clean file does not need to
        // explain itself; anything else does.
        if (r.Verdict is ThermoRawVerdict.NoTruncationDetected or ThermoRawVerdict.NotThermoRaw)
        {
            return;
        }

        foreach (var line in r.Evidence)
        {
            Console.WriteLine($"    {line}");
        }
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        thermoraw-check - report whether a Thermo RAW file has been truncated.

        Usage:
          thermoraw-check [options] <file-or-directory>...

        Options:
          --json      Emit every result as JSON.
          --quiet,-q  Say nothing; report through the exit code alone.
          --strict    Fail unless every file is positively free of truncation.
          --version   Print the version.
          --help,-h   Print this.

        Exit codes:
          0  Nothing was found wrong. Includes files nothing could be established about.
          1  At least one file is positively short, or --strict was not satisfied.
          2  The command line could not be understood.
          3  At least one file could not be read.

        What it checks:
          The fixed header, and that every pointer in the run header addresses bytes the file
          actually contains. Reads are bounded, so a 40 GB acquisition costs the same as a small
          one.

        What it does not check:
          That a file is complete. Bytes can be missing from the end of a region whose pointer
          still lands inside the file. "No truncation detected" is not "verified whole".

        The file layout is a port of thermo-raw-file-validator by Michael Riffle (Apache-2.0):
        https://github.com/mriffle/thermo-raw-file-validator
        """);
}

/// <summary>
/// Source-generated serialization for the results.
/// </summary>
/// <remarks>
/// Not reflection, because this publishes trimmed: reflection-based serialization is exactly what
/// trimming removes, and it fails at run time rather than at build time. Generating it keeps the
/// binary a quarter of the size and the failure mode at compile time.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ThermoRawResult>))]
internal sealed partial class ResultJsonContext : JsonSerializerContext;
