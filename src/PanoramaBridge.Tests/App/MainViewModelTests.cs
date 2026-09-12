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
/// No window is created. Everything here is view-model state, which is where the behavior
/// actually lives -- the XAML only binds to it. The dispatcher hops are written to run inline
/// when there is no Application, so the same code path works in a test.
/// </remarks>
public sealed class MainViewModelTests : IAsyncLifetime
{
    private sealed class NoCredentials : ICredentialStore
    {
        public bool IsAvailable => true;

        public StoredCredential? Read(string serverUrl, string account = "") => null;

        public void Write(string serverUrl, StoredCredential credential, string account = "")
        {
        }

        public void Delete(string serverUrl, string account = "")
        {
        }
    }

    private sealed class RecordingAccessor : ICredentialStoreAccessor
    {
        public List<string> Remembered { get; } = [];

        public List<string> Forgotten { get; } = [];

        public void Remember(string serverUrl, string userName, string secret, string account = "") =>
            Remembered.Add(serverUrl);

        public void Forget(string serverUrl, string account = "") => Forgotten.Add(serverUrl);
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
    private TransferService _transfers = null!;

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

        var settingsViewModel =
            new SettingsViewModel(new InMemorySettingsStore(), settings ?? Usable());

        var shell = new MainViewModel(
            settingsViewModel,
            new ConfigurationsViewModel(settingsViewModel),
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

    private AppSettings Usable() => new AppSettings().Holding(new MonitoringConfiguration
    {
        LocalDirectory = _watched,
        RemotePath = "/_webdav/MacCoss/maccoss/@files/uploads/",
        ServerUrl = "https://example.invalid",
        ReconcileMinutes = 60,
    });

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
        // Starting moved on to each configuration's own row, so the only thing left here that
        // changes with state is what Upload now means.
        using var shell = NewShell();

        shell.UploadNowButtonText.ShouldBe("Upload now");

        shell.IsMonitoring = true;

        shell.UploadNowButtonText.ShouldBe("Check now", "a second scan would only repeat the first");
    }

    [Fact]
    public async Task Stop_all_stands_everything_down_at_once()
    {
        // The half of the old toggle that survived. Starting is per configuration now, but there
        // is still one thing no row covers: stand everything down before a reboot, or when
        // something is wrong. The tray's Exit and the updater's restart call the same path.
        using var shell = NewShell();

        await _transfers.StartConfigurationAsync(
            shell.Settings.ToSettings(),
            shell.Settings.Configurations[0],
            "an-api-key");

        _transfers.IsMonitoring.ShouldBeTrue();

        await shell.StopAllCommand.ExecuteAsync(null);

        shell.IsMonitoring.ShouldBeFalse();
        _transfers.IsMonitoring.ShouldBeFalse();
        shell.StatusLine.ShouldBe("Stopped.");
    }

    [Fact]
    public async Task Upload_now_becomes_a_folder_check_and_reports_what_it_found()
    {
        // While monitoring, the button asks the running engine to walk the folder now rather
        // than starting a second scan beside it. The sweep answers on a background thread, and
        // its answer is what reaches the status line.
        using var shell = NewShell();

        await _transfers.StartConfigurationAsync(
            shell.Settings.ToSettings(),
            shell.Settings.Configurations[0],
            "an-api-key");

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

        await shell.StopAllCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_configuration_that_is_not_set_up_says_so_rather_than_starting()
    {
        // The refusal is per configuration now, which is the point: one that is not filled in is
        // its own problem rather than everybody's.
        using var shell = NewShell(
            new AppSettings().Holding(new MonitoringConfiguration { LocalDirectory = string.Empty }));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => _transfers.StartConfigurationAsync(
                shell.Settings.ToSettings(),
                shell.Settings.Configurations[0],
                "an-api-key"));

        refusal.Message.ShouldContain("Local Monitoring");
        _transfers.IsMonitoring.ShouldBeFalse();
    }

    [Fact]
    public void A_build_below_the_version_floor_may_still_stand_down()
    {
        // New work is blocked when the build is too old to be trusted with data. Stopping
        // something already running is not new work, and refusing it would strand the user.
        using var shell = NewShell();

        shell.UploadsBlocked = true;

        shell.UploadNowCommand.CanExecute(null).ShouldBeFalse();
        shell.StopAllCommand.CanExecute(null).ShouldBeFalse("nothing is running to stand down");

        shell.IsMonitoring = true;
        shell.StopAllCommand.CanExecute(null).ShouldBeTrue();
    }

    // IAsyncLifetime, not IAsyncDisposable: xUnit v2 never calls IAsyncDisposable on a test
    // class, so this teardown silently did not run at all.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
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
