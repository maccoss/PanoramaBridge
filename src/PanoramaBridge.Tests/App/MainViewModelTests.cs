using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.Updates;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The shell: its command bar, and its lifetime.
/// </summary>
/// <remarks>
/// No window is created. Everything here is view-model state, which is where the behaviour
/// actually lives -- the XAML only binds to it. The dispatcher hops are written to run inline
/// when there is no Application, so the same code path works in a test.
/// </remarks>
public sealed class MainViewModelTests : IAsyncDisposable
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

    private sealed class RecordingAccessor : ICredentialStoreAccessor
    {
        public List<string> Remembered { get; } = [];

        public List<string> Forgotten { get; } = [];

        public void Remember(string serverUrl, string userName, string secret) =>
            Remembered.Add(serverUrl);

        public void Forget(string serverUrl) => Forgotten.Add(serverUrl);
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private AppSettings _saved = new();

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_saved);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _saved = settings;
            return Task.CompletedTask;
        }
    }

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly string _watched = Directory.CreateTempSubdirectory("pb-shell-").FullName;
    private readonly StubHttpMessageHandler _http =
        StubHttpMessageHandler.Returning(HttpStatusCode.NotFound);

    private readonly RecordingAccessor _credentials = new();
    private TransferService? _transfers;

    private MainViewModel NewShell(AppSettings? settings = null)
    {
        _transfers = new TransferService(
            _store,
            new NoCredentials(),
            new ResourceGovernor(NullLogger<ResourceGovernor>.Instance),
            NullLoggerFactory.Instance);

        var updates = new UpdateService(
            new VersionPolicyClient(_http.CreateClient(), policyUrl: null),
            NullLogger<UpdateService>.Instance);

        var shell = new MainViewModel(
            new SettingsViewModel(new InMemorySettingsStore(), settings ?? Usable()),
            new TransferStatusViewModel(_transfers.Progress),
            new UploadsViewModel(_store),
            _transfers,
            updates,
            _credentials,
            new ResourceGovernor(NullLogger<ResourceGovernor>.Instance),
            new AppPaths(Path.Combine(_watched, "appdata")),
            NullLogger<MainViewModel>.Instance)
        {
            SecretProvider = () => "an-api-key",
        };

        return shell;
    }

    private AppSettings Usable() => new()
    {
        LocalDirectory = _watched,
        RemotePath = "/_webdav/MacCoss/maccoss/@files/uploads/",
        ServerUrl = "https://example.invalid",
        ReconcileMinutes = 60,
    };

    [Fact]
    public void Restarting_for_an_update_says_why_when_it_will_not()
    {
        // Reported as "the Restart now button does nothing". It was refusing on purpose -- a
        // transfer was in flight -- and saying so only in the status line, which is not where
        // somebody who just pressed a button is looking. A refusal nobody sees is a broken
        // button.
        using var shell = NewShell();

        var explained = new List<(string Title, string Message)>();
        shell.Explain = (title, message) => explained.Add((title, message));

        // Nothing is staged here, so this is the second of the two refusals. Either way the
        // point is the same: pressing it must produce something the user can see.
        shell.ApplyUpdateCommand.Execute(null);

        explained.ShouldHaveSingleItem();
        explained[0].Title.ShouldBe("Restart now");
        explained[0].Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        // What closing the application actually does: the window disposes the shell as it
        // closes, and the service container disposes it again a moment later. The second
        // cancellation used to throw, the exception escaped Main, and the user was told
        // "PanoramaBridge could not start" while shutting it down.
        var shell = NewShell();

        shell.Dispose();
        shell.Dispose();
    }

    [Fact]
    public void Disposing_after_the_background_work_has_started_is_safe()
    {
        // Start is what creates the update loop and its timer, so disposing without it would
        // miss the half of Dispose that has anything to release.
        var shell = NewShell();

        shell.Start();

        shell.Dispose();
        shell.Dispose();
    }

    [Fact]
    public void The_buttons_say_what_they_will_do()
    {
        using var shell = NewShell();

        shell.MonitoringButtonText.ShouldBe("Start monitoring");
        shell.UploadNowButtonText.ShouldBe("Upload now");

        shell.IsMonitoring = true;

        shell.MonitoringButtonText.ShouldBe("Stop monitoring");
        shell.UploadNowButtonText.ShouldBe("Check now", "a second scan would only repeat the first");
    }

    [Fact]
    public async Task Monitoring_can_be_turned_on_and_off_from_the_command_bar()
    {
        using var shell = NewShell();

        await shell.ToggleMonitoringCommand.ExecuteAsync(null);

        shell.IsMonitoring.ShouldBeTrue();
        shell.StatusLine.ShouldContain(_watched);
        _credentials.Remembered.ShouldContain("https://example.invalid");

        await shell.ToggleMonitoringCommand.ExecuteAsync(null);

        shell.IsMonitoring.ShouldBeFalse();
        shell.StatusLine.ShouldBe("Monitoring stopped.");
    }

    [Fact]
    public async Task Upload_now_becomes_a_folder_check_and_reports_what_it_found()
    {
        // While monitoring, the button asks the running engine to walk the folder now rather
        // than starting a second scan beside it. The sweep answers on a background thread, and
        // its answer is what reaches the status line.
        using var shell = NewShell();

        await shell.ToggleMonitoringCommand.ExecuteAsync(null);
        await shell.UploadNowCommand.ExecuteAsync(null);

        shell.IsBusy.ShouldBeFalse("a scan was not started");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!shell.StatusLine.StartsWith("Monitoring -", StringComparison.Ordinal)
            && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        shell.StatusLine.ShouldStartWith("Monitoring -");
        shell.StatusLine.ShouldContain("up to date");

        await shell.ToggleMonitoringCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Unusable_settings_are_reported_rather_than_started_with()
    {
        using var shell = NewShell(new AppSettings { LocalDirectory = string.Empty });

        await shell.ToggleMonitoringCommand.ExecuteAsync(null);

        shell.IsMonitoring.ShouldBeFalse();
        shell.ConnectionFailed.ShouldBeTrue();
        shell.StatusLine.ShouldContain("Local Monitoring");
    }

    [Fact]
    public void A_build_below_the_version_floor_may_still_stand_down()
    {
        // New work is blocked when the build is too old to be trusted with data. Stopping
        // something already running is not new work, and refusing it would strand the user.
        using var shell = NewShell();

        shell.UploadsBlocked = true;

        shell.UploadNowCommand.CanExecute(null).ShouldBeFalse();
        shell.ToggleMonitoringCommand.CanExecute(null).ShouldBeFalse();

        shell.IsMonitoring = true;
        shell.ToggleMonitoringCommand.CanExecute(null).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transfers is not null)
        {
            await _transfers.DisposeAsync();
        }

        await _store.DisposeAsync();
        _http.Dispose();

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
