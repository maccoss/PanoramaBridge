using System.Globalization;
using PanoramaBridge.App.Services;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The log pipeline: what it will and will not write down.
/// </summary>
/// <remarks>
/// A secret that reaches a log file cannot be recalled -- the file is rotated, copied into
/// support requests, and synced. The transport is written never to log a header in the first
/// place; this is the second line, and the one that catches a property somebody adds later
/// without thinking about it.
/// </remarks>
public sealed class SecretRedactingEnricherTests
{
    private sealed class Factory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    private static LogEvent EventWith(params (string Name, object Value)[] properties)
    {
        var log = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("test", []),
            []);

        foreach (var (name, value) in properties)
        {
            log.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(value)));
        }

        return log;
    }

    private static string Read(LogEvent log, string property) =>
        log.Properties[property].ToString().Trim('"');

    [Theory]
    [InlineData("Password")]
    [InlineData("passwd")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Secret")]
    [InlineData("AccessToken")]
    [InlineData("Authorization")]
    [InlineData("Credential")]
    [InlineData("CREDENTIALS")]
    public void Anything_that_looks_like_a_credential_is_scrubbed(string property)
    {
        var log = EventWith((property, "hunter2-the-actual-secret"));

        new SecretRedactingEnricher().Enrich(log, new Factory());

        Read(log, property).ShouldBe("[redacted]");
        log.Properties[property].ToString().ShouldNotContain("hunter2");
    }

    [Fact]
    public void The_name_is_matched_anywhere_in_the_property_not_just_at_the_start()
    {
        // A property called RequestAuthorizationHeader is exactly the sort of thing that gets
        // added in a hurry while debugging.
        var log = EventWith(("RequestAuthorizationHeader", "Basic YXBpa2V5OnNlY3JldA=="));

        new SecretRedactingEnricher().Enrich(log, new Factory());

        Read(log, "RequestAuthorizationHeader").ShouldBe("[redacted]");
    }

    [Fact]
    public void Everything_else_is_left_alone()
    {
        // Over-scrubbing would make the log useless, which is its own kind of failure.
        var log = EventWith(
            ("Path", @"C:\data\run.raw"),
            ("Bytes", 7_323_298_011L),
            ("Server", "https://panoramaweb.org"));

        new SecretRedactingEnricher().Enrich(log, new Factory());

        Read(log, "Path").ShouldBe(@"C:\data\run.raw");
        Read(log, "Server").ShouldBe("https://panoramaweb.org");
        log.Properties["Bytes"].ToString(null, CultureInfo.InvariantCulture).ShouldBe("7323298011");
    }

    [Fact]
    public void An_event_carrying_nothing_sensitive_is_untouched()
    {
        var log = EventWith(("Path", "run.raw"));

        new SecretRedactingEnricher().Enrich(log, new Factory());

        log.Properties.Count.ShouldBe(1);
    }
}

/// <summary>
/// The verbosity toggle.
/// </summary>
/// <remarks>
/// It had no effect at all for the life of the project: the level switch existed, the checkbox
/// existed, and nothing connected them. Worth a test precisely because a setting that does
/// nothing looks exactly like one that works.
/// </remarks>
public sealed class LoggingVerbosityTests : IDisposable
{
    private readonly LogEventLevel _original = LoggingSetup.LevelSwitch.MinimumLevel;

    [Fact]
    public void Turning_verbose_logging_on_lowers_the_level_and_off_raises_it()
    {
        LoggingSetup.ApplyVerbosity(true);
        LoggingSetup.LevelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Debug);

        LoggingSetup.ApplyVerbosity(false);
        LoggingSetup.LevelSwitch.MinimumLevel.ShouldBe(LogEventLevel.Information);
    }

    /// <inheritdoc />
    public void Dispose() => LoggingSetup.LevelSwitch.MinimumLevel = _original;
}
