using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// Running several configurations at once.
/// </summary>
/// <remarks>
/// <para>
/// No server is involved. The folders are empty, so monitoring never has anything to offer and
/// nothing reaches the network -- which leaves the lifetime questions on their own, and with a
/// set of runners instead of one those are exactly what can go wrong: a configuration that half
/// starts, a stop that leaves one watching, a credential that reaches the wrong server.
/// </para>
/// <para>
/// What is deliberately not asserted here is that files transfer. That is the coordinator's job
/// and is covered where the coordinator is; this is about there being several of them.
/// </para>
/// </remarks>
public sealed class MultipleConfigurationTests : IAsyncLifetime
{
    /// <summary>
    /// Records which server and account each credential was read for.
    /// </summary>
    /// <remarks>
    /// The reads are the assertion. Two configurations on one server as different people is the
    /// case phase 2 keyed credentials for, and the only way to see that it works from here is to
    /// watch what gets asked for.
    /// </remarks>
    private sealed class RecordingCredentials : ICredentialStore
    {
        public List<(string Server, string Account)> Reads { get; } = [];

        public Dictionary<(string Server, string Account), StoredCredential> Stored { get; } = [];

        public bool IsAvailable => true;

        public StoredCredential? Read(string serverUrl, string account = "")
        {
            Reads.Add((serverUrl, account));

            return Stored.TryGetValue((serverUrl, account), out var credential)
                ? credential
                : null;
        }

        public void Write(string serverUrl, StoredCredential credential, string account = "") =>
            Stored[(serverUrl, account)] = credential;

        public void Delete(string serverUrl, string account = "") =>
            Stored.Remove((serverUrl, account));
    }

    private readonly List<string> _folders = [];
    private RecordingCredentials _credentials = null!;
    private SqliteStateStore _store = null!;

    public Task InitializeAsync()
    {
        _store = SqliteStateStore.InMemory();
        _credentials = new RecordingCredentials();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        foreach (var folder in _folders.Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private TransferService NewService() => new(
        _store,
        _credentials,
        new ResourceGovernor(NullLogger<ResourceGovernor>.Instance),
        NullLoggerFactory.Instance);

    private string NewFolder()
    {
        var folder = Directory.CreateTempSubdirectory("pb-multi-").FullName;
        _folders.Add(folder);
        return folder;
    }

    private MonitoringConfiguration Watching(
        string name,
        string? server = null,
        string? account = null,
        bool enabled = true) => new()
        {
            Name = name,
            Enabled = enabled,
            Account = account ?? string.Empty,
            LocalDirectory = NewFolder(),
            RemotePath = $"/_webdav/MacCoss/{name}/@files/",
            ServerUrl = server ?? "https://example.invalid",

            // Long enough that no sweep runs during a test, so nothing reaches the network even
            // if a folder is not as empty as it should be.
            ReconcileMinutes = 60,
        };

    [Fact]
    public async Task Every_enabled_configuration_is_watched()
    {
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations =
            [
                Watching("Lumos"),
                Watching("Exploris"),
                Watching("Astral"),
            ],
        };

        await service.StartMonitoringAsync(settings, "an-api-key");

        service.IsMonitoring.ShouldBeTrue();
        service.MonitoredConfigurations.ShouldBe(3);

        await service.StopMonitoringAsync();

        service.IsMonitoring.ShouldBeFalse();
        service.MonitoredConfigurations.ShouldBe(0, "stopping has to stop all of them");
    }

    [Fact]
    public async Task A_configuration_that_is_switched_off_is_not_watched()
    {
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations =
            [
                Watching("Lumos"),
                Watching("Away for service", enabled: false),
            ],
        };

        await service.StartMonitoringAsync(settings, "an-api-key");

        service.MonitoredConfigurations.ShouldBe(1);
    }

    [Fact]
    public async Task A_configuration_that_will_not_start_leaves_nothing_running()
    {
        // All or nothing on purpose. Starting four of five and reporting success would leave the
        // window saying it was monitoring while one instrument quietly filled its disk -- and
        // with several configurations nobody can see at a glance that one folder is uncovered.
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations =
            [
                Watching("Lumos"),

                // No secret is typed for this one and nothing is stored for it, so resolving its
                // credential fails after the first configuration has already started.
                Watching("Exploris", server: "https://other.invalid", account: "config-exploris"),
            ],
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartMonitoringAsync(settings, secret: null));

        service.IsMonitoring.ShouldBeFalse();
        service.MonitoredConfigurations.ShouldBe(0, "the one that did start has to be stopped again");
    }

    [Fact]
    public async Task The_failure_says_which_configuration_could_not_start()
    {
        // "No credential is available for this server" says nothing useful when five servers are
        // in play.
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations = [Watching("Exploris", account: "config-exploris")],
        };

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartMonitoringAsync(settings, secret: null));

        refusal.Message.ShouldContain("Exploris");
        refusal.Message.ShouldContain("https://example.invalid");
    }

    [Fact]
    public async Task The_typed_secret_reaches_only_the_configuration_it_was_typed_for()
    {
        // There is one password box and it belongs to the configuration the tabs are showing.
        // Handing what was typed there to the others would sign them in to their own servers with
        // somebody else's key -- and worse, make them ignore the credential they each have,
        // because a secret typed this session takes precedence over a stored one.
        await using var service = NewService();

        var edited = Watching("Lumos");
        var other = Watching("Exploris", server: "https://other.invalid", account: "config-exploris");

        _credentials.Write(
            other.ServerUrl, new StoredCredential("apikey", "exploris-own-key"), other.Account);

        var settings = new AppSettings { Configurations = [edited, other] };

        await service.StartMonitoringAsync(settings, "typed-for-lumos");

        service.MonitoredConfigurations.ShouldBe(2);

        // Lumos used what was typed and never asked the store. Exploris was not given it, so it
        // fell back to its own.
        _credentials.Reads.ShouldNotContain((edited.ServerUrl, edited.Account));
        _credentials.Reads.ShouldContain((other.ServerUrl, other.Account));
    }

    [Fact]
    public async Task The_typed_secret_follows_the_configuration_being_edited_not_the_first_one()
    {
        // The password box belongs to whichever configuration the tabs are showing. Assuming it
        // is always the first wrote one configuration's key into another's credential slot --
        // and because a typed secret takes precedence over a stored one, the configuration it
        // was actually typed for then signed in with somebody else's.
        await using var service = NewService();

        var first = Watching("Lumos", server: "https://lumos.invalid", account: "config-lumos");
        var second = Watching("Exploris", server: "https://other.invalid", account: "config-exploris");

        _credentials.Write(
            first.ServerUrl, new StoredCredential("apikey", "lumos-own-key"), first.Account);

        var settings = new AppSettings { Configurations = [first, second] };

        // Typed while the tabs were showing the second one.
        await service.StartMonitoringAsync(settings, "typed-for-exploris", edited: second);

        service.MonitoredConfigurations.ShouldBe(2);

        // Exploris used what was typed and never asked the store; Lumos was not given it and fell
        // back to its own. Reversed, this is the defect: Lumos would have taken the typed key and
        // Exploris would have gone looking for a credential nobody had stored.
        _credentials.Reads.ShouldNotContain((second.ServerUrl, second.Account));
        _credentials.Reads.ShouldContain((first.ServerUrl, first.Account));
    }

    [Fact]
    public async Task A_configuration_that_stops_watching_stops_being_counted()
    {
        // A runner cancels its own linked token when its monitor dies, and cancelling a child
        // does not cancel the parent. Counting it regardless left the window saying it was
        // monitoring a folder nobody was looking at, with the button still offering to stop it.
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations = [Watching("Lumos"), Watching("Exploris")],
        };

        await service.StartMonitoringAsync(settings, "an-api-key");

        service.MonitoredConfigurations.ShouldBe(2);
        service.IsMonitoring.ShouldBeTrue();

        // Stopping every runner is what a monitor failing on each of them amounts to, as far as
        // the service can see: each has cancelled its own token and stopped watching.
        await service.StopMonitoringAsync();

        service.MonitoredConfigurations.ShouldBe(0);
        service.IsMonitoring.ShouldBeFalse("nothing is being watched, so nothing should say it is");
    }

    [Fact]
    public async Task Starting_again_after_a_previous_session_does_not_leak_it()
    {
        // IsMonitoring goes false once every runner has given up, but the runners are still there
        // holding a connection and an engine each. Starting over the top of them would drop them
        // silently -- an HttpClient and a set of worker tasks per configuration, every time
        // somebody pressed the button after a failure.
        await using var service = NewService();

        var settings = new AppSettings { Configurations = [Watching("Lumos")] };

        await service.StartMonitoringAsync(settings, "an-api-key");
        await service.StopMonitoringAsync();
        await service.StartMonitoringAsync(settings, "an-api-key");

        service.MonitoredConfigurations.ShouldBe(1, "one session, not two");
        service.IsMonitoring.ShouldBeTrue();
    }

    [Fact]
    public async Task Each_configuration_reads_the_credential_for_its_own_account()
    {
        // Two configurations on one server as different people: the case phase 2 keyed
        // credentials for, seen from the end that uses them.
        await using var service = NewService();

        const string Server = "https://panorama.invalid";

        var first = Watching("Kyle", server: Server, account: "config-kyle");
        var second = Watching("Brian", server: Server, account: "config-brian");

        _credentials.Write(Server, new StoredCredential("apikey", "kyle-key"), "config-kyle");
        _credentials.Write(Server, new StoredCredential("apikey", "brian-key"), "config-brian");

        await service.StartMonitoringAsync(
            new AppSettings { Configurations = [first, second] }, secret: null);

        service.MonitoredConfigurations.ShouldBe(2);

        _credentials.Reads.ShouldContain((Server, "config-kyle"));
        _credentials.Reads.ShouldContain((Server, "config-brian"));
    }

    [Fact]
    public async Task Monitoring_status_is_reported_across_all_of_them()
    {
        await using var service = NewService();

        service.Monitor.ShouldBeNull("nothing is being watched yet");

        await service.StartMonitoringAsync(
            new AppSettings { Configurations = [Watching("Lumos"), Watching("Exploris")] },
            "an-api-key");

        service.Monitor.ShouldNotBeNull();

        await service.StopMonitoringAsync();

        service.Monitor.ShouldBeNull();
    }

    [Fact]
    public async Task A_check_now_reaches_every_configuration()
    {
        await using var service = NewService();

        service.RequestSweep("nothing is running").ShouldBeFalse(
            "so the window knows to scan instead");

        await service.StartMonitoringAsync(
            new AppSettings { Configurations = [Watching("Lumos"), Watching("Exploris")] },
            "an-api-key");

        service.RequestSweep("the user asked for a check.").ShouldBeTrue();
    }

    [Fact]
    public async Task Disposing_stops_every_configuration()
    {
        // On a normal exit this happens: the window disposes what it owns, and the service
        // container disposes the same objects again a moment later.
        var service = NewService();

        await service.StartMonitoringAsync(
            new AppSettings { Configurations = [Watching("Lumos"), Watching("Exploris")] },
            "an-api-key");

        await service.DisposeAsync();
        await service.DisposeAsync();

        service.IsMonitoring.ShouldBeFalse();
        service.MonitoredConfigurations.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_synchronously_stops_every_configuration()
    {
        // A service offering only IAsyncDisposable makes a synchronously disposed container throw
        // rather than skip it, and Main returning disposes it synchronously.
        var service = NewService();

        await service.StartMonitoringAsync(
            new AppSettings { Configurations = [Watching("Lumos"), Watching("Exploris")] },
            "an-api-key");

        service.Dispose();
        service.Dispose();

        service.IsMonitoring.ShouldBeFalse();
        service.MonitoredConfigurations.ShouldBe(0);
    }

    [Fact]
    public async Task A_scan_covers_every_enabled_configuration()
    {
        // Pressing Upload now with three configurations and having it walk one of them would be
        // surprising in the way that costs data: the other two look finished and are not.
        await using var service = NewService();

        var settings = new AppSettings
        {
            Configurations =
            [
                Watching("Lumos"),
                Watching("Exploris"),
                Watching("Away for service", enabled: false),
            ],
        };

        var summary = await service.ScanAndUploadAsync(settings, "an-api-key");

        // The folders are empty, so the interesting part is that it completed rather than
        // throwing, and that the switched-off configuration was not walked.
        summary.Uploaded.ShouldBe(0);
        summary.Failed.ShouldBe(0);

        _credentials.Reads.ShouldNotContain(
            (settings.Configurations[2].ServerUrl, settings.Configurations[2].Account));
    }
}
