using System.Globalization;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Cli;

/// <summary>
/// The switches <c>sync</c> and <c>watch</c> accept, and whatever is left over.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of the command bodies so it can be tested. It was written twice inline, once for
/// each command, and the two had already drifted -- which is the usual fate of argument parsing
/// that lives inside the thing it configures.
/// </para>
/// <para>
/// A bad value is reported, not thrown. <c>--concurrency banana</c> reaching the user as an
/// unhandled <c>FormatException</c> tells them nothing about which switch they got wrong.
/// </para>
/// </remarks>
internal sealed record CommandOptions
{
    /// <summary>Files in flight at once.</summary>
    public int Concurrency { get; init; } = 3;

    /// <summary>Whether to confirm each upload against the server's own hash.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Minutes between folder checks, for <c>watch</c>.</summary>
    public int ReconcileMinutes { get; init; } = 15;

    /// <summary>Seconds a file must be unchanged before it counts as finished.</summary>
    public int StableSeconds { get; init; } = 10;

    /// <summary>Extensions to transfer.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = new AppSettings().Extensions;

    /// <summary>Anything that was not a switch, in the order it was given.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Reads the switches out of <paramref name="args"/>.
    /// </summary>
    /// <param name="args">Arguments after the local directory.</param>
    /// <param name="options">The parsed options, or the defaults when something was wrong.</param>
    /// <param name="problem">What to tell the user, or null when it parsed.</param>
    public static bool TryParse(string[] args, out CommandOptions options, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);

        var concurrency = 3;
        var verify = true;
        var reconcileMinutes = 15;
        var stableSeconds = 10;
        var extensions = new AppSettings().Extensions;
        var paths = new List<string>();

        problem = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--concurrency":
                    if (!TryNumber(args, ref i, out concurrency, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    break;

                case "--every":
                    if (!TryNumber(args, ref i, out reconcileMinutes, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    break;

                case "--stable":
                    if (!TryNumber(args, ref i, out stableSeconds, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    break;

                case "--ext":
                    if (i + 1 >= args.Length)
                    {
                        problem = "--ext needs a list of extensions, for example --ext .raw,.d";
                        options = new CommandOptions();
                        return false;
                    }

                    extensions = AppSettings.ParseExtensions(args[++i]);
                    break;

                case "--no-verify":
                    verify = false;
                    break;

                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        problem = $"unknown option '{args[i]}'";
                        options = new CommandOptions();
                        return false;
                    }

                    paths.Add(args[i]);
                    break;
            }
        }

        options = new CommandOptions
        {
            Concurrency = concurrency,
            Verify = verify,
            ReconcileMinutes = reconcileMinutes,
            StableSeconds = stableSeconds,
            Extensions = extensions,
            Paths = paths,
        };

        return true;
    }

    private static bool TryNumber(string[] args, ref int i, out int value, out string? problem)
    {
        var name = args[i];

        if (i + 1 >= args.Length)
        {
            value = 0;
            problem = $"{name} needs a number";
            return false;
        }

        var text = args[++i];

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || value < 0)
        {
            problem = $"{name} needs a whole number, not '{text}'";
            return false;
        }

        problem = null;
        return true;
    }
}
