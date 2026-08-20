using System.IO;
using System.Text.RegularExpressions;
using PanoramaBridge.Core.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PanoramaBridge.App.Services;

/// <summary>
/// Builds the Serilog pipeline.
/// </summary>
/// <remarks>
/// Replaces the Python version's <c>basicConfig(level=DEBUG)</c> writing to a relative,
/// unrotated <c>panoramabridge.log</c> in whatever directory the process happened to start in.
/// </remarks>
public static partial class LoggingSetup
{
    /// <summary>
    /// Flipped at runtime by the "Verbose logging" toggle. No restart required.
    /// </summary>
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    /// <summary>In-memory tail that backs the Activity Log pane.</summary>
    public static RingBufferSink Buffer { get; } = new(capacity: 2000);

    /// <summary>Applies the "Verbose logging" setting. Takes effect immediately.</summary>
    public static void ApplyVerbosity(bool verbose) =>
        LevelSwitch.MinimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    public static ILogger Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()
            .Enrich.With<SecretRedactingEnricher>()
            .WriteTo.File(
                path: paths.LogFileTemplate,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .WriteTo.Sink(Buffer)
            .CreateLogger();
    }
}

/// <summary>
/// Scrubs anything that looks like a credential out of rendered log messages.
/// </summary>
/// <remarks>
/// Belt and braces. The transport layer is written never to log headers in the first place,
/// but a secret reaching a log file is unrecoverable, so it is worth paying for twice.
/// </remarks>
public sealed partial class SecretRedactingEnricher : ILogEventEnricher
{
    private const string Replacement = "[redacted]";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var name in logEvent.Properties.Keys.ToArray())
        {
            if (IsSensitiveName(name))
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty(name, Replacement));
            }
        }
    }

    private static bool IsSensitiveName(string name) =>
        SensitivePropertyName().IsMatch(name);

    [GeneratedRegex(
        "password|passwd|apikey|api_key|secret|token|authorization|credential",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePropertyName();
}
