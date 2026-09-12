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
    /// <summary>The settings screen's own defaults, read once rather than per option.</summary>
    private static readonly MonitoringConfiguration Defaults = new();

    /// <summary>Files in flight at once.</summary>
    public int Concurrency { get; init; } = 3;

    /// <summary>Whether to confirm each upload against the server's own hash.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Minutes between folder checks, for <c>watch</c>.</summary>
    public int ReconcileMinutes { get; init; } = 15;

    /// <summary>Seconds a file must be unchanged before it counts as finished.</summary>
    public int StableSeconds { get; init; } = 10;

    /// <summary>Extensions to transfer.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = Defaults.Extensions;

    /// <summary>Extensions the companion walk must not look past.</summary>
    public IReadOnlyList<string> ExcludedExtensions { get; init; } =
        Defaults.ExcludedExtensions;

    /// <summary>
    /// Whether <c>--ext</c> or <c>--exclude</c> was actually given.
    /// </summary>
    /// <remarks>
    /// One parser serves both commands, but only <c>watch</c> builds a filter -- <c>sync</c>
    /// mirrors the directory whole. Without this, <c>pbctl sync --exclude .skyd</c> parsed
    /// cleanly, mirrored the caches anyway and exited zero, which is the opposite of what was
    /// asked for and says nothing about it.
    /// </remarks>
    public bool FiltersGiven { get; init; }

    /// <summary>Anything that was not a switch, in the order it was given.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Extra directories to watch alongside the first, one per <c>--also</c>.
    /// </summary>
    /// <remarks>
    /// A named switch rather than more positional arguments, because <c>watch</c> already takes an
    /// optional remote directory second: a variadic list of locals would make
    /// <c>pbctl watch A B</c> mean two different things depending on whether B happened to look
    /// like a remote path.
    /// <para>
    /// This is how the cost of several configurations is measured. Each directory gets its own
    /// watcher and its own sweep timer, which is exactly what the application does and exactly
    /// what multiplies.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AlsoWatch { get; init; } = [];

    /// <summary>
    /// Whether to walk and report without transferring anything.
    /// </summary>
    /// <remarks>
    /// Contacts no server, so it needs no credential -- which is what makes it usable for
    /// measuring idle cost on a machine that has none, and on one where nobody wants a real
    /// upload starting by accident. What a run would have offered is still counted and printed.
    /// </remarks>
    public bool NoUpload { get; init; }

    /// <summary>
    /// Minutes to watch before stopping and reporting, or zero to watch until interrupted.
    /// </summary>
    /// <remarks>
    /// Ctrl+C is what produces the report, which makes the idle-cost measurement something a
    /// person has to sit and do. A run that stops itself is repeatable, scriptable, and gives the
    /// same number to whoever asks later -- and the numbers here have been wrong by two orders of
    /// magnitude before, so being able to re-measure cheaply is the point.
    /// </remarks>
    public int ForMinutes { get; init; }

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
        var extensions = Defaults.Extensions;
        var excluded = Defaults.ExcludedExtensions;
        var paths = new List<string>();
        var alsoWatch = new List<string>();
        var noUpload = false;
        var forMinutes = 0;
        var filtersGiven = false;

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
                    if (!TryList(args, ref i, ".raw,.d", out extensions, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    filtersGiven = true;
                    break;

                case "--exclude":
                    // An empty argument is how a caller asks for no exclusions at all, which is
                    // the behavior before .skyd was excluded. ParseExtensions returns an empty
                    // list for it rather than treating it as a mistake.
                    if (!TryList(args, ref i, ".skyd", out excluded, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    filtersGiven = true;
                    break;

                case "--no-verify":
                    verify = false;
                    break;

                case "--no-upload":
                    noUpload = true;
                    break;

                case "--for":
                    if (!TryNumber(args, ref i, out forMinutes, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    break;

                case "--also":
                    if (!TryPath(args, ref i, out var also, out problem))
                    {
                        options = new CommandOptions();
                        return false;
                    }

                    alsoWatch.Add(also);
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
            ExcludedExtensions = excluded,
            FiltersGiven = filtersGiven,
            Paths = paths,
            AlsoWatch = alsoWatch,
            NoUpload = noUpload,
            ForMinutes = forMinutes,
        };

        return true;
    }

    /// <summary>
    /// Reads a comma-separated extension list, refusing one that looks like another switch.
    /// </summary>
    /// <remarks>
    /// <c>--exclude --no-verify</c> passes a bare "is there a next argument?" check, and the
    /// switch is then swallowed as the value: the run silently excludes nothing useful AND
    /// verifies after being told not to, with nothing on screen about either. Argument parsing
    /// here fails quietly by nature, so the check has to be about the shape of the value.
    /// <para>
    /// An empty string is still accepted -- it is how a caller asks for an empty list.
    /// </para>
    /// </remarks>
    private static bool TryList(
        string[] args,
        ref int i,
        string example,
        out IReadOnlyList<string> value,
        out string? problem)
    {
        var name = args[i];

        value = [];

        if (i + 1 >= args.Length)
        {
            problem = $"{name} needs a list of extensions, for example {name} {example}";
            return false;
        }

        var text = args[++i];

        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            problem = $"{name} needs a list of extensions, but got the option '{text}'";
            return false;
        }

        value = AppSettings.ParseExtensions(text);
        problem = null;
        return true;
    }

    /// <summary>
    /// Reads a directory, refusing one that looks like another switch.
    /// </summary>
    /// <remarks>
    /// The same trap <see cref="TryList"/> guards: <c>--also --no-upload</c> passes a bare "is
    /// there a next argument?" check and then watches a directory named "--no-upload" while
    /// silently not applying the switch.
    /// </remarks>
    private static bool TryPath(string[] args, ref int i, out string value, out string? problem)
    {
        var name = args[i];

        value = string.Empty;

        if (i + 1 >= args.Length)
        {
            problem = $"{name} needs a directory";
            return false;
        }

        var text = args[++i];

        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            problem = $"{name} needs a directory, but got the option '{text}'";
            return false;
        }

        value = text;
        problem = null;
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
