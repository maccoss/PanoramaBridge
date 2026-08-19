using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine(
                $"Set {UrlVariable} and {KeyVariable} before running. "
                + $"{PathVariable} supplies a default remote path.");
            return 2;
        }

        var options = new WebDavClientOptions
        {
            BaseAddress = new Uri(url, UriKind.Absolute),
            Credential = PanoramaCredential.ApiKey(key),
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

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        PrintUsage();
        return 2;
    }

    /// <summary>
    /// Resolves the path argument, falling back to the configured default.
    /// </summary>
    private static RemotePath Target(string[] args, int index)
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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

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

        Environment:
          PANORAMABRIDGE_IT_URL      server, e.g. https://panoramaweb.org
          PANORAMABRIDGE_IT_APIKEY   LabKey API key (user menu, External Tool Access)
          PANORAMABRIDGE_IT_PATH     default remote path
          PANORAMABRIDGE_VERBOSE     set to anything for debug logging
        """);
}
