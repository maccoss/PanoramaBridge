using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Cli;

/// <summary>
/// Headless harness for the transport layer.
/// </summary>
/// <remarks>
/// Exists so the WebDAV client can be exercised against the real Panorama server before any
/// XAML is written, which is what de-risks the rest of the port. Later it becomes the
/// unattended mode for scheduled transfers.
/// <para>
/// Credentials come from the environment and are never accepted as arguments -- a command line
/// ends up in shell history and in the process list.
/// </para>
/// </remarks>
internal static class Program
{
    private const string UrlVariable = "PANORAMABRIDGE_IT_URL";
    private const string KeyVariable = "PANORAMABRIDGE_IT_APIKEY";
    private const string PathVariable = "PANORAMABRIDGE_IT_PATH";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        var url = Environment.GetEnvironmentVariable(UrlVariable);
        var key = Environment.GetEnvironmentVariable(KeyVariable);

        // watch --no-upload walks folders and reports what that costs. It contacts nothing, so
        // requiring a credential for it was a rule with no purpose -- and it stopped the one
        // command whose whole job is measuring idle cost from running on a machine with no
        // credential, which is most of them.
        var needsServer = !IsOfflineWatch(args);

        if (needsServer && (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)))
        {
            Console.Error.WriteLine(
                $"Set {UrlVariable} and {KeyVariable} before running. "
                + $"{PathVariable} supplies a default remote path. "
                + "To walk a folder without contacting a server, use: watch <dir> --no-upload");
            return 2;
        }

        var options = new WebDavClientOptions
        {
            // A placeholder when nothing will be sent. Nothing reads it in that mode, and
            // WebDavClientOptions requires an absolute address.
            BaseAddress = new Uri(
                string.IsNullOrWhiteSpace(url) ? "https://offline.invalid" : url,
                UriKind.Absolute),

            // A placeholder credential too, and deliberately one that could never work: nothing
            // is sent in this mode, and a value that would be accepted somewhere is exactly what
            // should not be sitting in a client built for a run that contacts nothing.
            Credential = PanoramaCredential.ApiKey(
                string.IsNullOrWhiteSpace(key) ? "offline-no-credential" : key),
        };

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(Environment.GetEnvironmentVariable("PANORAMABRIDGE_VERBOSE") is null
                ? LogLevel.Warning
                : LogLevel.Debug)
            .AddSimpleConsole(c => c.SingleLine = true));

        using var http = options.CreateHttpClient();
        var client = new WebDavClient(http, options, loggerFactory.CreateLogger<WebDavClient>());

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("Cancelling...");
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(client, args, cancellation.Token).ConfigureAwait(false);
        }
        catch (WebDavException ex)
        {
            Console.Error.WriteLine($"error: {ex.ToUserMessage()}");
            Console.Error.WriteLine($"       {ex.Method} {ex.Path} -> {(int)ex.StatusCode}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Whether these arguments are a watch that will not contact a server.
    /// </summary>
    /// <remarks>
    /// Read straight off the arguments rather than from the parsed options, because the decision
    /// has to be made before the client is built and the parser runs per command.
    /// <para>
    /// A bare scan is safe because <c>TryList</c> and <c>TryPath</c> refuse a value beginning with
    /// two dashes, so no option's value can be the string <c>--no-upload</c>. That is the guard,
    /// and it lives in <c>CommandOptions</c> rather than here: relax it there and this becomes
    /// wrong, which is worth knowing. An earlier version of this comment claimed no other option
    /// takes a value, which is false of seven of them.
    /// </para>
    /// </remarks>
    private static bool IsOfflineWatch(string[] args) =>
        args.Length > 0
        && string.Equals(args[0], "watch", StringComparison.OrdinalIgnoreCase)
        && args.Contains("--no-upload", StringComparer.Ordinal);

    private static async Task<int> RunAsync(
        IWebDavClient client,
        string[] args,
        CancellationToken cancellationToken)
    {
        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
            "caps" => await CapsAsync(client, Target(rest, 0), cancellationToken).ConfigureAwait(false),
            "ls" => await ListAsync(client, Target(rest, 0), cancellationToken).ConfigureAwait(false),
            "mkdir" => await MkdirAsync(client, Target(rest, 0), cancellationToken).ConfigureAwait(false),
            "md5" => await Md5Async(client, Target(rest, 0), cancellationToken).ConfigureAwait(false),
            "put" => await PutAsync(client, rest, cancellationToken).ConfigureAwait(false),
            "rm" => await RemoveAsync(client, Target(rest, 0), cancellationToken).ConfigureAwait(false),
            "sync" => await SyncAsync(client, rest, cancellationToken).ConfigureAwait(false),
            "watch" => await WatchAsync(client, rest, cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(cancellationToken).ConfigureAwait(false),
            _ => Unknown(command),
        };
    }

    private static async Task<int> CapsAsync(
        IWebDavClient client,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var caps = await client.GetCapabilitiesAsync(path, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"server           : {caps.ServerName ?? "(not reported)"}");
        Console.WriteLine($"DAV              : {caps.DavCompliance ?? "(not reported)"}");
        Console.WriteLine($"methods          : {string.Join(", ", caps.AllowedMethods)}");
        Console.WriteLine($"atomic publish   : {(caps.SupportsAtomicPublish ? "yes (PUT + MOVE)" : "no")}");
        return 0;
    }

    private static async Task<int> ListAsync(
        IWebDavClient client,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var entries = await client.ListAsync(path, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries.OrderByDescending(e => e.IsCollection).ThenBy(e => e.Name))
        {
            var kind = entry.IsCollection ? "DIR " : "FILE";
            var size = entry.IsCollection ? string.Empty : FormatBytes(entry.Length);
            var write = entry.Permissions.CanUpload ? "rw" : "r-";

            Console.WriteLine($"{kind} {write} {size,10}  {entry.Name}");
        }

        Console.WriteLine($"({entries.Count} entries)");
        return 0;
    }

    private static async Task<int> MkdirAsync(
        IWebDavClient client,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        await client.EnsureCollectionAsync(path, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"created {path}");
        return 0;
    }

    private static async Task<int> Md5Async(
        IWebDavClient client,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        // A trailing slash means "hash the whole collection in one request".
        if (path.IsCollection)
        {
            var hashes = await client
                .GetCollectionHashesAsync(path, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (name, hash) in hashes.OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{hash}  {name}");
            }

            Console.WriteLine($"({hashes.Count} files, one request)");
            return 0;
        }

        var single = await client.GetFileHashAsync(path, cancellationToken).ConfigureAwait(false);
        if (single is null)
        {
            Console.Error.WriteLine($"not found: {path}");
            return 1;
        }

        Console.WriteLine($"{single}  {path.Name}");
        return 0;
    }

    private static async Task<int> PutAsync(
        IWebDavClient client,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: pbctl put <local-file> [remote-dir]");
            return 2;
        }

        var localFile = args[0];
        if (!File.Exists(localFile))
        {
            Console.Error.WriteLine($"no such file: {localFile}");
            return 2;
        }

        var directory = Target(args[1..], 0).AsCollection();
        var destination = directory.Append(Path.GetFileName(localFile));

        var total = new FileInfo(localFile).Length;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        var progress = new Progress<long>(sent =>
        {
            // Throttled: a 1 MiB granularity on a multi-gigabyte file would otherwise flood
            // the console with thousands of lines.
            if (stopwatch.Elapsed - lastReport < TimeSpan.FromSeconds(1) && sent < total)
            {
                return;
            }

            lastReport = stopwatch.Elapsed;
            var percent = total == 0 ? 100 : sent * 100.0 / total;
            var rate = stopwatch.Elapsed.TotalSeconds > 0 ? sent / stopwatch.Elapsed.TotalSeconds : 0;

            Console.Write($"\r  {percent,6:F1}%  {FormatBytes(sent)} of {FormatBytes(total)}  {FormatBytes((long)rate)}/s   ");
        });

        var result = await client
            .UploadAsync(localFile, destination, progress, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"uploaded   {destination}");
        Console.WriteLine($"  bytes    {result.BytesUploaded:N0}");
        Console.WriteLine($"  elapsed  {result.Elapsed.TotalSeconds:F1}s ({FormatBytes((long)result.BytesPerSecond)}/s)");
        Console.WriteLine($"  local    md5 {result.Hashes.Md5}");

        // The point of the exercise: compare the hash computed while streaming against the one
        // the server computes over what it actually stored.
        var remote = await client.GetFileHashAsync(destination, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  server   md5 {remote ?? "(none)"}");

        if (!string.Equals(remote, result.Hashes.Md5, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("  VERIFY   FAILED - the server's hash does not match");
            return 1;
        }

        Console.WriteLine("  verify   OK");
        return 0;
    }

    private static async Task<int> RemoveAsync(
        IWebDavClient client,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        await client.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"deleted {path}");
        return 0;
    }

    /// <summary>
    /// Mirrors a local directory into a remote folder, then reports what it cost.
    /// </summary>
    /// <remarks>
    /// The counters printed at the end are the point: a second run over an unchanged directory
    /// should report every file skipped, zero bytes sent, and no hashing.
    /// </remarks>
    private static async Task<int> SyncAsync(
        IWebDavClient client,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: pbctl sync <local-dir> [remote-dir] [--concurrency N] [--no-verify]");
            return 2;
        }

        var localDirectory = Path.GetFullPath(args[0]);

        if (!CommandOptions.TryParse(args[1..], out var options, out var problem))
        {
            Console.Error.WriteLine($"error: {problem}");
            return 2;
        }

        // sync mirrors the directory whole; only watch builds a CandidateFilter. Accepting a
        // filter switch and ignoring it would send exactly the files the caller asked to keep
        // back, and report success doing it.
        if (options.FiltersGiven)
        {
            Console.Error.WriteLine(
                "error: sync mirrors the whole directory and has no file filter. "
                + "--ext and --exclude apply to watch.");
            return 2;
        }

        var concurrency = options.Concurrency;
        var verify = options.Verify;
        var destination = Target([.. options.Paths], 0).AsCollection();

        await using var store = new SqliteStateStore(StateDatabasePath());

        await using var coordinator = new TransferCoordinator(
            client,
            store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = localDirectory,
                DestinationRoot = destination,
                MaxConcurrentTransfers = concurrency,
                VerifyUploads = verify,
            });

        var tiers = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        coordinator.Progress += progress =>
        {
            if (progress.State is TransferState.Uploading)
            {
                return;
            }

            tiers.AddOrUpdate(progress.Phase, 1, (_, count) => count + 1);
            Console.WriteLine($"  {progress.State,-11} {progress.FileName}  {progress.Message}");
        };

        Console.WriteLine($"syncing {localDirectory}");
        Console.WriteLine($"     to {destination}");
        Console.WriteLine($"  concurrency {concurrency}, verify {(verify ? "on" : "off")}");
        Console.WriteLine();

        await coordinator.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);

        var files = Directory.EnumerateFiles(
            localDirectory,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            });

        var offered = 0;
        foreach (var file in files)
        {
            if (await coordinator.EnqueueAsync(file, cancellationToken).ConfigureAwait(false))
            {
                offered++;
            }
        }

        coordinator.CompleteAdding();

        var summary = await coordinator.RunAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"offered   {offered}");
        Console.WriteLine($"uploaded  {summary.Uploaded}");
        Console.WriteLine($"skipped   {summary.Skipped}");
        Console.WriteLine($"conflicts {summary.Conflicts}");
        Console.WriteLine($"failed    {summary.Failed}");
        Console.WriteLine($"bytes     {summary.BytesUploaded:N0} ({FormatBytes((long)summary.BytesPerSecond)}/s)");
        Console.WriteLine($"elapsed   {summary.Elapsed.TotalSeconds:F1}s");

        return summary.Failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Watches a directory and transfers files as they finish being written, until interrupted.
    /// </summary>
    /// <remarks>
    /// The same components the window uses, with no XAML in the way. That is what makes it the
    /// right place to measure what monitoring actually costs while idle: this process does
    /// nothing else, so its processor time is monitoring's processor time and nothing else's.
    /// </remarks>
    private static async Task<int> WatchAsync(
        IWebDavClient client,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: pbctl watch <local-dir> [remote-dir] [--also <local-dir>]... "
                + "[--concurrency N] [--no-verify] [--no-upload] [--for MINUTES] "
                + "[--every MINUTES] [--stable SECONDS] [--ext .raw,.d] [--exclude .skyd]");
            return 2;
        }

        if (!CommandOptions.TryParse(args[1..], out var options, out var problem))
        {
            Console.Error.WriteLine($"error: {problem}");
            return 2;
        }

        var roots = new List<string> { Path.GetFullPath(args[0]) };
        roots.AddRange(options.AlsoWatch.Select(Path.GetFullPath));

        // Every watcher mirrors into the same remote root, because there is one destination
        // argument. With one folder that is the whole point; with two it means D:\A\run.raw and
        // D:\B\run.raw both resolve to <destination>/run.raw, and under Overwrite the second
        // replaces the first on the server without a word.
        //
        // --also exists to measure what several watchers cost, and that is what --no-upload does.
        // Rather than invent a per-folder destination syntax for a diagnostic switch, the two are
        // required together: the measurement still works and the collision cannot happen.
        if (options.AlsoWatch.Count > 0 && !options.NoUpload)
        {
            Console.Error.WriteLine(
                "error: --also needs --no-upload. Every watched folder would otherwise mirror "
                + "into the same remote directory, so two files with one name would overwrite "
                + "each other there. To transfer several folders, run pbctl once per folder.");
            return 2;
        }

        var missing = roots.Where(r => !Directory.Exists(r)).ToArray();
        if (missing.Length > 0)
        {
            Console.Error.WriteLine($"error: no such directory: {missing[0]}");
            return 2;
        }

        // A destination is still needed even with nothing to upload: the sweep resolves each file
        // to where it would go and asks the ledger whether it is already there. Given a real one,
        // "would upload" is the truth; without one a placeholder makes every file look untouched,
        // which is fine for measuring what walking costs and wrong for anything else -- so the
        // header says which of the two this run is.
        var offlinePlaceholder = options.NoUpload
            && options.Paths.Count == 0
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable));

        var destination = offlinePlaceholder
            ? RemotePath.Parse("/_webdav/offline/@files/").AsCollection()
            : Target([.. options.Paths], 0).AsCollection();

        await using var store = new SqliteStateStore(StateDatabasePath());

        // Shared, so several watched folders add up to the concurrency asked for rather than to a
        // multiple of it. The same budget the application gives its configurations.
        using var budget = new TransferBudget(options.Concurrency);

        var watchers = new List<Watcher>();

        try
        {
            foreach (var root in roots)
            {
                watchers.Add(Watcher.For(root, destination, client, store, budget, options));
            }

            Describe(roots, destination, options, offlinePlaceholder);

            foreach (var watcher in watchers)
            {
                await watcher.RecoverAsync(cancellationToken).ConfigureAwait(false);
            }

            var process = Process.GetCurrentProcess();
            var startedAt = DateTimeOffset.UtcNow;
            var processorAtStart = process.TotalProcessorTime;

            // A run that stops itself when asked to, so the measurement does not depend on
            // somebody being there to press Ctrl+C at the right moment.
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (options.ForMinutes > 0)
            {
                stopping.CancelAfter(TimeSpan.FromMinutes(options.ForMinutes));
            }

            var running = watchers.Select(w => w.RunAsync(stopping.Token)).ToArray();

            TransferSummary[] summaries;
            try
            {
                summaries = await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C. Everything below still has to be reported.
                summaries = [];
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var processor = process.TotalProcessorTime - processorAtStart;

            var uploaded = summaries.Sum(x => x.Uploaded);
            var skipped = summaries.Sum(x => x.Skipped);
            var conflicts = summaries.Sum(x => x.Conflicts);
            var failed = summaries.Sum(x => x.Failed);
            var bytes = summaries.Sum(x => x.BytesUploaded);
            var offered = watchers.Sum(w => w.Offered);

            Console.WriteLine();

            if (options.NoUpload)
            {
                Console.WriteLine($"would upload {offered}");
            }
            else
            {
                Console.WriteLine($"uploaded  {uploaded}");
                Console.WriteLine($"skipped   {skipped}");
                Console.WriteLine($"conflicts {conflicts}");
                Console.WriteLine($"failed    {failed}");
                Console.WriteLine($"bytes     {bytes:N0}");
            }

            // What this run cost the machine, which is the number that decides whether monitoring
            // is welcome on a computer attached to a mass spectrometer. Reported per watched
            // folder as well as in total, because the question several configurations raise is
            // whether the cost multiplies.
            Console.WriteLine($"watching  {roots.Count} folder(s)");
            Console.WriteLine($"watched   {elapsed.TotalMinutes:F1} min");
            Console.WriteLine(
                $"processor {processor.TotalSeconds:F1}s "
                + $"({Percent(processor, elapsed):F3}% of one core, "
                + $"{Percent(processor, elapsed) / roots.Count:F3}% per folder)");

            process.Refresh();
            Console.WriteLine($"memory    {FormatBytes(process.WorkingSet64)} working set");

            return failed > 0 ? 1 : 0;
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static double Percent(TimeSpan processor, TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0 ? processor.TotalSeconds / elapsed.TotalSeconds * 100 : 0;

    private static void Describe(
        IReadOnlyList<string> roots,
        RemotePath destination,
        CommandOptions options,
        bool offlinePlaceholder)
    {
        foreach (var root in roots)
        {
            Console.WriteLine($"watching {root}");
        }

        Console.WriteLine(
            offlinePlaceholder
                ? "      to (no destination given; nothing counts as already transferred)"
                : options.NoUpload
                    ? $"      to {destination} (--no-upload; nothing will be sent)"
                    : $"      to {destination}");
        Console.WriteLine(
            $"  extensions {(options.Extensions.Count == 0 ? "(all)" : string.Join(", ", options.Extensions))}");
        Console.WriteLine(
            $"  excluding  {(options.ExcludedExtensions.Count == 0 ? "(nothing)" : string.Join(", ", options.ExcludedExtensions))}");
        Console.WriteLine(
            $"  every {options.ReconcileMinutes} min, stable after {options.StableSeconds}s, "
            + $"concurrency {options.Concurrency} shared, verify {(options.Verify ? "on" : "off")}");
        Console.WriteLine(
            options.ForMinutes > 0
                ? $"  stopping after {options.ForMinutes} min, or Ctrl+C."
                : "  Ctrl+C to stop.");
        Console.WriteLine();
    }

    /// <summary>
    /// One watched folder: its monitor, and the engine that transfers what the monitor offers.
    /// </summary>
    /// <remarks>
    /// The same pairing the application builds per configuration, with no XAML in the way. That is
    /// what makes several of these the right way to measure what N configurations cost while idle:
    /// this process does nothing else, so its processor time is monitoring's and nothing else's.
    /// <para>
    /// Under <c>--no-upload</c> there is no engine at all. Offered files are counted and left, so
    /// the run reaches no network and needs no credential -- and what it measures is the watcher
    /// and the sweep, which is the part that multiplies.
    /// </para>
    /// </remarks>
    private sealed class Watcher : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ContinuousMonitor _monitor;
        private readonly TransferCoordinator? _engine;
        private int _offered;

        private Watcher(string root, ContinuousMonitor monitor, TransferCoordinator? engine)
        {
            _root = root;
            _monitor = monitor;
            _engine = engine;
        }

        /// <summary>How many files the sweep handed on, whether or not they were transferred.</summary>
        public int Offered => Volatile.Read(ref _offered);

        public static Watcher For(
            string root,
            RemotePath destination,
            IWebDavClient client,
            IStateStore store,
            TransferBudget budget,
            CommandOptions options)
        {
            var monitor = new ContinuousMonitor(
                store,
                new MonitorOptions
                {
                    Root = root,
                    DestinationRoot = destination,
                    Filter = new CandidateFilter(options.Extensions, options.ExcludedExtensions),
                    StabilityPeriod = TimeSpan.FromSeconds(options.StableSeconds),
                    ReconcileInterval = TimeSpan.FromMinutes(Math.Max(1, options.ReconcileMinutes)),
                });

            var label = Path.GetFileName(root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            monitor.Swept += result => Console.WriteLine(
                result.Failed
                    ? $"  sweep  {label}  {result.Problem}"
                    : $"  sweep  {label}  {result.Examined} examined, {result.Offered} offered, "
                      + $"{result.AlreadyAccountedFor} already settled, "
                      + $"{result.Elapsed.TotalMilliseconds:F0} ms");

            monitor.Waiting += report =>
            {
                if (report.StillWatching)
                {
                    Console.WriteLine(
                        $"  waiting {label}  {Path.GetFileName(report.Path)}  {report.Readiness.Detail}");
                }
            };

            if (options.NoUpload)
            {
                return new Watcher(root, monitor, engine: null);
            }

            var engine = new TransferCoordinator(
                client,
                store,
                new TransferEngineOptions
                {
                    LocalBaseDirectory = root,
                    DestinationRoot = destination,
                    MaxConcurrentTransfers = budget.Capacity,
                    Budget = budget,
                    VerifyUploads = options.Verify,
                });

            engine.Progress += progress =>
            {
                if (progress.State is TransferState.Uploading)
                {
                    return;
                }

                Console.WriteLine($"  {progress.State,-11} {progress.FileName}  {progress.Message}");
            };

            return new Watcher(root, monitor, engine);
        }

        public Task RecoverAsync(CancellationToken cancellationToken) =>
            _engine?.RecoverInterruptedAsync(cancellationToken) ?? Task.CompletedTask;

        public async Task<TransferSummary> RunAsync(CancellationToken cancellationToken)
        {
            var transfers = _engine?.RunAsync(cancellationToken);

            try
            {
                await _monitor.RunAsync(Offer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C. The engine still has to be wound down.
            }

            _engine?.CompleteAdding();

            if (transfers is null)
            {
                return default;
            }

            try
            {
                return await transfers.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return default;
            }

            async Task Offer(string path)
            {
                Interlocked.Increment(ref _offered);

                if (_engine is not null)
                {
                    await _engine.EnqueueAsync(path, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _monitor.DisposeAsync().ConfigureAwait(false);

            if (_engine is not null)
            {
                await _engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Prints the ledger, which is what "did that actually get uploaded" means.</summary>
    private static async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        await using var store = new SqliteStateStore(StateDatabasePath());

        var counts = await store.CountByStateAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"ledger: {StateDatabasePath()}");
        Console.WriteLine();

        if (counts.Count == 0)
        {
            Console.WriteLine("  (empty)");
            return 0;
        }

        foreach (var (state, count) in counts.OrderByDescending(c => c.Value))
        {
            Console.WriteLine($"  {state,-14} {count,6}");
        }

        // Anything unresolved is what a person actually needs to act on.
        var attention = await store
            .GetByStateAsync(
                [TransferState.Failed, TransferState.Conflict, TransferState.Superseded],
                limit: 20,
                cancellationToken)
            .ConfigureAwait(false);

        if (attention.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("needs attention:");
            foreach (var record in attention)
            {
                Console.WriteLine($"  {record.State,-11} {Path.GetFileName(record.LocalPath)}");
                Console.WriteLine($"    {record.LastError}");
            }
        }

        return 0;
    }

    private static string StateDatabasePath()
    {
        // Kept apart from the application's own database so the harness can never disturb it.
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanoramaBridge");

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "pbctl-state.db");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        PrintUsage();
        return 2;
    }

    /// <summary>
    /// Resolves the path argument, falling back to the configured default.
    /// </summary>
    internal static RemotePath Target(string[] args, int index)
    {
        if (args.Length > index && !string.IsNullOrWhiteSpace(args[index]))
        {
            return RemotePath.Parse(args[index]);
        }

        var configured = Environment.GetEnvironmentVariable(PathVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? throw new InvalidOperationException(
                $"No remote path given and {PathVariable} is not set.")
            : RemotePath.Parse(configured);
    }

    /// <summary>
    /// Byte counts for the console, shared with the window so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// Kept as a method rather than inlining ByteSize at each call site because its tests name
    /// it, and because every size pbctl prints should go through one place.
    /// </remarks>
    internal static string FormatBytes(long bytes) => ByteSize.Describe(bytes);

    private static void PrintUsage() => Console.WriteLine(
        """
        pbctl - PanoramaBridge transport harness

          caps  [remote-path]              report server, DAV class and allowed verbs
          ls    [remote-path]              list a collection, with write permission
          mkdir <remote-path>              create a collection and any missing parents
          md5   <remote-path>              server-computed MD5; a trailing slash hashes
                                           the whole collection in one request
          put   <local-file> [remote-dir]  upload, then verify against the server's hash
          rm    <remote-path>              delete
          sync  <local-dir> [remote-dir]    mirror a directory, then report what it cost
                  --concurrency N            files in flight at once (default 3)
                  --no-verify                skip hash verification
          watch <local-dir> [remote-dir]    monitor a directory until interrupted, and
                                             report what it cost the machine
                  --every N                  minutes between folder checks (default 15)
                  --stable N                 seconds a file must be unchanged (default 10)
                  --ext .raw,.d              extensions to transfer
                  --exclude .skyd            extensions that are never data, even sitting on
                                             one that is (run.raw.skyd is Skyline's cache,
                                             not an acquisition). Pass "" for none.
                  --concurrency N            files in flight at once, shared across every
                                             watched folder (default 3)
                  --no-verify                skip hash verification
                  --also <local-dir>         watch another folder alongside, repeatable.
                                             Each gets its own watcher and sweep timer, which
                                             is how the cost of several configurations is
                                             measured. Needs --no-upload: every folder would
                                             otherwise mirror into the same remote directory.
                  --no-upload                walk and report without transferring anything.
                                             Contacts no server, so it needs no credential.
                  --for N                    stop after N minutes and report, instead of
                                             waiting to be interrupted
          status                           what the upload ledger currently holds

        Environment:
          PANORAMABRIDGE_IT_URL      server, e.g. https://panoramaweb.org
          PANORAMABRIDGE_IT_APIKEY   LabKey API key (user menu, External Tool Access)
          PANORAMABRIDGE_IT_PATH     default remote path
          PANORAMABRIDGE_VERBOSE     set to anything for debug logging
        """);
}
