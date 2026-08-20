using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The service the shell drives: starting and stopping monitoring, and being torn down.
/// </summary>
/// <remarks>
/// No server is involved. Monitoring an empty folder never has anything to offer, so nothing
/// reaches the network -- which leaves the lifetime questions on their own, and those are what
/// have actually gone wrong: a run left holding a cancellation source, a container disposing a
/// service twice, a monitor that stopped without saying so.
/// </remarks>
public sealed class TransferServiceTests : IAsyncDisposable
{
    private sealed class NoCredentials : ICredentialStore
    {
        public bool IsAvailable => true;

        public StoredCredential? Read(string serverUrl) => null;

        public void Write(string serverUrl, StoredCredential credential)
        {
        }

        public void Delete(string serverUrl)
        {
        }
    }

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly string _watched = Directory.CreateTempSubdirectory("pb-service-").FullName;

    private TransferService NewService() => new(
        _store,
        new NoCredentials(),
        new ResourceGovernor(NullLogger<ResourceGovernor>.Instance),
        NullLoggerFactory.Instance);

    private AppSettings Settings() => new()
    {
        LocalDirectory = _watched,
        RemotePath = "/_webdav/MacCoss/maccoss/@files/uploads/",
        ServerUrl = "https://example.invalid",

        // Long enough that no sweep runs during a test, so nothing reaches the network even if
        // the folder is not as empty as it should be.
        ReconcileMinutes = 60,
    };

    [Fact]
    public async Task Monitoring_starts_and_stops()
    {
        await using var service = NewService();

        service.IsMonitoring.ShouldBeFalse();

        await service.StartMonitoringAsync(Settings(), "an-api-key");
        service.IsMonitoring.ShouldBeTrue();
        service.Monitor.ShouldNotBeNull();

        await service.StopMonitoringAsync();
        service.IsMonitoring.ShouldBeFalse();
        service.Monitor.ShouldBeNull();
    }

    [Fact]
    public async Task Starting_twice_is_not_an_error()
    {
        // The toggle command, a restored setting and a retry can all arrive at once.
        await using var service = NewService();

        await service.StartMonitoringAsync(Settings(), "an-api-key");
        await service.StartMonitoringAsync(Settings(), "an-api-key");

        service.IsMonitoring.ShouldBeTrue();

        await service.StopMonitoringAsync();
    }

    [Fact]
    public async Task Stopping_when_nothing_is_running_is_not_an_error()
    {
        await using var service = NewService();

        await service.StopMonitoringAsync();
        await service.StopMonitoringAsync();

        service.IsMonitoring.ShouldBeFalse();
    }

    [Fact]
    public async Task Monitoring_reports_state_changes_so_the_buttons_can_follow()
    {
        await using var service = NewService();

        var changes = 0;
        service.RunStateChanged += () => Interlocked.Increment(ref changes);

        await service.StartMonitoringAsync(Settings(), "an-api-key");
        await service.StopMonitoringAsync();

        changes.ShouldBeGreaterThanOrEqualTo(2, "once on the way up and once on the way down");
    }

    [Fact]
    public async Task A_folder_check_can_be_asked_for_only_while_monitoring()
    {
        await using var service = NewService();

        service.RequestSweep("test").ShouldBeFalse("there is nothing to ask");

        await service.StartMonitoringAsync(Settings(), "an-api-key");
        service.RequestSweep("test").ShouldBeTrue();

        await service.StopMonitoringAsync();
        service.RequestSweep("test").ShouldBeFalse();
    }

    [Fact]
    public async Task A_scan_is_refused_while_the_folder_is_being_monitored()
    {
        // Not a limitation to work around: the engine is already running and already knows what
        // it has transferred, so a second walk of the same folder gains nothing. The shell turns
        // Upload now into Check now instead.
        await using var service = NewService();

        await service.StartMonitoringAsync(Settings(), "an-api-key");

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => service.ScanAndUploadAsync(Settings(), "an-api-key"));

        refusal.Message.ShouldContain("monitored");

        await service.StopMonitoringAsync();
    }

    [Fact]
    public async Task Starting_without_a_credential_says_so_rather_than_failing_later()
    {
        await using var service = NewService();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartMonitoringAsync(Settings(), secret: null));

        refusal.Message.ShouldContain("credential");
        service.IsMonitoring.ShouldBeFalse();
    }

    [Fact]
    public async Task Starting_with_unusable_settings_reports_the_first_thing_to_fix()
    {
        await using var service = NewService();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartMonitoringAsync(
                Settings() with { LocalDirectory = string.Empty },
                "an-api-key"));

        refusal.Message.ShouldContain("Local Monitoring");
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        // On a normal exit this happens: the window disposes what it owns, and the service
        // container disposes the same objects again. Getting this wrong took the application
        // down on the way out, and reported it as a failure to start.
        var service = NewService();

        await service.StartMonitoringAsync(Settings(), "an-api-key");

        await service.DisposeAsync();
        await service.DisposeAsync();

        service.IsMonitoring.ShouldBeFalse();
    }

    [Fact]
    public async Task Disposing_synchronously_is_safe_because_the_container_does_it_that_way()
    {
        // A service that offers only IAsyncDisposable makes a synchronously disposed container
        // throw rather than skip it, and Main returning disposes it synchronously.
        var service = NewService();

        await service.StartMonitoringAsync(Settings(), "an-api-key");

        service.Dispose();
        service.Dispose();

        service.IsMonitoring.ShouldBeFalse();
    }

    [Fact]
    public async Task A_connection_test_reports_what_to_fix_before_it_dials_out()
    {
        await using var service = NewService();

        var check = await service.TestConnectionAsync(
            Settings() with { LocalDirectory = string.Empty },
            "an-api-key");

        check.Succeeded.ShouldBeFalse();
        check.Summary.ShouldContain("Local Monitoring");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        try
        {
            Directory.Delete(_watched, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left behind is better than a failed run.
        }
    }
}
