using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// One configuration as the list shows it.
/// </summary>
/// <remarks>
/// A snapshot, rebuilt whenever the settings change, rather than a live wrapper around a
/// <see cref="MonitoringConfiguration"/>. The record is immutable, so a wrapper would have to
/// write back on every keystroke; the only thing editable from the list is the tick, and that
/// goes through the settings the way everything else does.
/// </remarks>
public sealed partial class ConfigurationRowViewModel : ObservableObject
{
    private readonly Func<bool, Task> _run;

    /// <param name="run">
    /// Starts this configuration when passed true and stops it when passed false. The row does
    /// not do it itself, because what running means belongs to the transfer service.
    /// </param>
    public ConfigurationRowViewModel(
        MonitoringConfiguration configuration,
        int index,
        bool running,
        Func<bool, Task> run)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Index = index;
        Configuration = configuration;
        _running = running;
        Name = configuration.DisplayName;
        LocalDirectory = configuration.LocalDirectory;
        RemotePath = configuration.RemotePath;
        ServerUrl = configuration.ServerUrl;

        // The account the credential is filed under is deliberately not shown; it is an internal
        // key. What a person recognizes is who signs in, and with an API key nobody does -- which
        // is worth saying rather than leaving the column blank and looking broken.
        User = configuration.AuthMode == AuthMode.ApiKey
            ? "API key"
            : string.IsNullOrWhiteSpace(configuration.UserName)
                ? "(not set)"
                : configuration.UserName;

        Created = configuration.CreatedUtc == default
            ? string.Empty
            // The provider is named rather than left to the ambient culture. ':' in a custom
            // format string is the culture's time separator, not a literal, so without this the
            // Created column and the Verified column on Uploads -- same format string, a few
            // pixels apart -- render differently on a machine whose locale uses something else.
            : configuration.CreatedUtc.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        _run = run ?? throw new ArgumentNullException(nameof(run));

        // Deliberately not Validate() here. That calls Directory.Exists, and on a share whose
        // server is down the answer takes the SMB timeout to arrive -- seconds, on the UI thread,
        // once per row, every time the list is rebuilt. The real answer arrives from
        // ConfigurationsViewModel a moment later, off this thread.
        _problems = [];
    }

    /// <summary>Where this configuration sits in the list.</summary>
    public int Index { get; }

    /// <summary>The configuration this row stands for.</summary>
    public MonitoringConfiguration Configuration { get; }

    /// <summary>What to call it. The folder it watches, when it has no name of its own.</summary>
    public string Name { get; }

    public string LocalDirectory { get; }

    public string RemotePath { get; }

    public string ServerUrl { get; }

    /// <summary>Who it signs in as, for the column AutoQC shows.</summary>
    public string User { get; }

    /// <summary>When it was added, or blank for one carried over from before that was recorded.</summary>
    public string Created { get; }

    /// <summary>Anything that would stop this configuration transferring.</summary>
    /// <remarks>
    /// Filled in after the row is built, because working it out touches the disk. Until then the
    /// status reads as still being checked rather than as ready, which would be a claim nothing
    /// had yet established.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    private IReadOnlyList<string> _problems = [];

    /// <summary>Whether the disk-touching part of the check has come back yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _checked;

    /// <summary>What the status column says.</summary>
    /// <remarks>
    /// <para>
    /// Whether it would run, not whether it is running. The Transfer Status tab is where what is
    /// happening now belongs, and saying "Running" here for a configuration whose folder had just
    /// been unplugged would be the kind of tick that means less than it appears to.
    /// </para>
    /// <para>
    /// Three kinds of not-running are told apart, because they want different things done. One
    /// nobody has filled in is "Not set up" -- it is waiting on somebody rather than resting. One
    /// that has a folder but something wrong with it is "Needs attention", which is a fault to
    /// look at. One with nothing wrong is "Ready", because that is what it is: ready to be run.
    /// </para>
    /// </remarks>
    public string Status => Running
        ? "Running"
        : Problems.Count > 0
            ? string.IsNullOrWhiteSpace(LocalDirectory) ? "Not set up" : "Needs attention"
            : Checked ? "Ready" : "Checking...";

    /// <summary>The first problem, for the tooltip on the status column.</summary>
    public string? StatusDetail => Problems.Count > 0 ? string.Join("\n", Problems) : null;

    /// <summary>Whether this configuration is being watched right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private bool _running;

    /// <summary>True while this row's button is being acted on, so it cannot be pressed twice.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private bool _busy;

    /// <summary>What the button says. Green when it will start, red when it will stop.</summary>
    public string RunButtonText => Running ? "Stop" : "Run";

    /// <summary>Records what the off-thread check found.</summary>
    public void Report(IReadOnlyList<string> problems)
    {
        Problems = problems ?? [];
        Checked = true;
    }

    /// <summary>
    /// Starts or stops this configuration.
    /// </summary>
    /// <remarks>
    /// The button is the run control, and there is no second one: a tick saying a configuration
    /// was included plus a button saying monitoring was on were two switches in series for one
    /// outcome, and a configuration only ran when both agreed.
    /// <para>
    /// Failures are shown rather than discarded, and the button goes back to what it was. A
    /// button that reads Stop for something that never started is the same defect as a tick that
    /// was never saved.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanToggleRun))]
    private async Task ToggleRunAsync()
    {
        var wanted = !Running;

        Busy = true;

        try
        {
            // Running is not set here. Starting saves first, which rebuilds the list, so this row
            // may already have been replaced by the time the call returns -- and setting it on an
            // orphan would leave the row on screen showing the opposite of the truth. The list
            // re-reads what is actually running instead.
            await _run(wanted).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"{Name} could not be {(wanted ? "started" : "stopped")}: {ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    private bool CanToggleRun() => !Busy;

    /// <summary>Raised when starting or stopping failed. The view model above shows it.</summary>
    public event Action<string>? Failed;
}

/// <summary>
/// The Configurations tab: which folder-to-destination pairings exist, and which are running.
/// </summary>
/// <remarks>
/// <para>
/// A view over what <see cref="SettingsViewModel"/> owns, not a second owner of it. Everything
/// that changes a configuration goes back through that class, because two objects holding the
/// same settings and each deciding when to save is the trap its own remarks describe.
/// </para>
/// <para>
/// The list deliberately shows what AutoQC Loader shows -- name, who it signs in as, when it was
/// created, whether it will run -- because that is the screen the lab asked for by name and
/// already reads without having to learn it.
/// </para>
/// </remarks>
public sealed partial class ConfigurationsViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private bool _rebuilding;

    /// <param name="run">
    /// Starts and stops one configuration. Supplied rather than reached for, so this class stays
    /// testable without a transfer service and a server behind it.
    /// </param>
    public ConfigurationsViewModel(
        SettingsViewModel settings,
        IConfigurationRunControl? run = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _run = run ?? new NothingRuns();
        _settings.ConfigurationsChanged += Rebuild;

        Rebuild();
    }

    private readonly IConfigurationRunControl _run;

    /// <summary>Stands in when nothing can actually run, which is every test but the lifecycle ones.</summary>
    private sealed class NothingRuns : IConfigurationRunControl
    {
        public bool IsRunning(MonitoringConfiguration configuration) => false;

        public Task StartAsync(MonitoringConfiguration configuration) => Task.CompletedTask;

        public Task StopAsync(MonitoringConfiguration configuration) => Task.CompletedTask;
    }

    /// <summary>The configurations, in the order they are kept.</summary>
    public ObservableCollection<ConfigurationRowViewModel> Rows { get; } = [];

    /// <summary>Which row the editor tabs are showing.</summary>
    /// <remarks>
    /// Bound two-way to the list's selection. Setting it points the Local Monitoring and Remote
    /// Settings tabs at that configuration, which is what makes those tabs the editor for
    /// whatever is selected here rather than for a fixed one.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private int _selectedIndex = -1;

    /// <summary>Whether a row is selected, so the buttons that need one can be disabled.</summary>
    public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < Rows.Count;

    /// <summary>How many configurations are running now.</summary>
    public string Summary
    {
        get
        {
            var running = Rows.Count(r => r.Running);

            return Rows.Count switch
            {
                0 => "No configurations yet. Add one to choose a folder and where it goes.",
                1 when running == 1 => "1 configuration, running.",
                1 => "1 configuration, not running.",
                _ when running == 0 => $"{Rows.Count} configurations, none running.",
                _ => $"{Rows.Count} configurations, {running} running.",
            };
        }
    }

    /// <summary>
    /// Anything that stopped a change being saved, for the line under the list.
    /// </summary>
    /// <remarks>
    /// Cleared by the next successful rebuild, so it describes now rather than accumulating.
    /// </remarks>
    [ObservableProperty]
    private string _problem = string.Empty;

    async partial void OnSelectedIndexChanged(int value)
    {
        if (_rebuilding || value < 0 || value >= Rows.Count)
        {
            return;
        }

        try
        {
            // Switching saves the configuration being left, so this can fail for the same reason
            // a tick can: the settings file is momentarily somebody else's.
            await _settings.EditConfigurationAsync(value).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Problem = $"Could not open that configuration: {ex.Message}";
        }
    }

    /// <summary>Adds an empty configuration, switched off, and selects it for editing.</summary>
    /// <remarks>
    /// <para>
    /// Off, because it has no folder and no destination and so there is nothing it could do. That
    /// is not merely tidiness: an enabled configuration with no folder makes the whole settings
    /// record invalid, and Start monitoring refuses on the first problem it finds. Adding a second
    /// configuration to set up later therefore stopped the first one -- which was working --
    /// from transferring at all.
    /// </para>
    /// <para>
    /// Nothing turns it on by itself once the boxes are filled in. A configuration somebody is
    /// still working on, or has deliberately left off, must stay off; the list says "Not set up"
    /// until it is usable, so what is waiting on whom is visible without guessing.
    /// </para>
    /// <para>
    /// Stamped with the time it was created, unlike the one carried over from a settings file
    /// written before configurations existed -- that file never recorded when monitoring was set
    /// up, and inventing a date for it would be worse than leaving the column blank.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task AddAsync()
    {
        var configurations = new List<MonitoringConfiguration>(_settings.Configurations)
        {
            new() { Enabled = false, CreatedUtc = DateTimeOffset.UtcNow },
        };

        await _settings
            .ReplaceConfigurationsAsync(configurations, configurations.Count - 1)
            .ConfigureAwait(true);
    }

    /// <summary>Copies the selected configuration and selects the copy.</summary>
    /// <remarks>
    /// The reason the lab asked for this: a second instrument writing the same file types to the
    /// same Panorama project differs from the first by one folder. What is deliberately not
    /// copied is the account -- see <see cref="MonitoringConfiguration.Account"/>. Two
    /// configurations sharing one credential slot is fine and common, but a copy is a new
    /// pairing, and giving it its own slot is what lets it be signed in as somebody else later
    /// without disturbing the one it came from.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyAsync()
    {
        if (!HasSelection)
        {
            return;
        }

        var source = _settings.Configurations[SelectedIndex];

        var copy = source with
        {
            Name = UnusedName(source.DisplayName),
            CreatedUtc = DateTimeOffset.UtcNow,
            Account = NewAccount(),
        };

        var configurations = new List<MonitoringConfiguration>(_settings.Configurations);
        configurations.Insert(SelectedIndex + 1, copy);

        await _settings
            .ReplaceConfigurationsAsync(configurations, SelectedIndex + 1)
            .ConfigureAwait(true);
    }

    /// <summary>Removes the selected configuration.</summary>
    /// <remarks>
    /// The confirmation is the view's, through <see cref="Confirm"/>, because a view model that
    /// opens a dialog cannot be tested. What is not done here is removing the credential: the
    /// account may be shared with another configuration, and signing that one out because this
    /// one was deleted would be a surprise nothing on screen predicted.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (!HasSelection)
        {
            return;
        }

        var row = Rows[SelectedIndex];

        if (Confirm is not null && !Confirm($"Remove the configuration for {row.Name}?"))
        {
            return;
        }

        var configurations = new List<MonitoringConfiguration>(_settings.Configurations);
        configurations.RemoveAt(SelectedIndex);

        // Removing the last one leaves a fresh empty configuration rather than nothing. An empty
        // list is a state the editor tabs cannot represent -- they would go on showing the
        // configuration just deleted and silently re-add it on the next save, so the file said
        // none and the window said one. This is also what a fresh install starts with, so there
        // is one shape rather than two.
        if (configurations.Count == 0)
        {
            configurations.Add(new MonitoringConfiguration { CreatedUtc = DateTimeOffset.UtcNow });
        }

        await _settings
            .ReplaceConfigurationsAsync(
                configurations, Math.Min(SelectedIndex, configurations.Count - 1))
            .ConfigureAwait(true);
    }

    /// <summary>Asks the user to confirm a deletion. Supplied by the view.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>
    /// Starts or stops the configuration at this position.
    /// </summary>
    /// <remarks>
    /// Saves first when starting, so what runs is what is on screen. Pressing Run with an edit
    /// still in the boxes and having the old settings start instead would be the sort of
    /// difference nobody can account for afterwards.
    /// </remarks>
    private async Task RunAsync(int index, bool wanted)
    {
        if (index < 0 || index >= _settings.Configurations.Count)
        {
            return;
        }

        if (wanted)
        {
            await _settings.SaveAsync().ConfigureAwait(true);
            await _run.StartAsync(_settings.Configurations[index]).ConfigureAwait(true);
        }
        else
        {
            await _run.StopAsync(_settings.Configurations[index]).ConfigureAwait(true);
        }

        RefreshRunState();
    }

    /// <summary>A name not already in the list, so two rows are never called the same thing.</summary>
    private string UnusedName(string wanted)
    {
        var taken = _settings.Configurations
            .Select(c => c.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(wanted))
        {
            return wanted;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{wanted} ({i})";

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// An account for a configuration that needs its own credential slot.
    /// </summary>
    /// <remarks>
    /// Opaque and never shown. It has to be stable for the life of the configuration and unlike
    /// any other, and it must not be derived from the name -- renaming a configuration would then
    /// sign it out.
    /// </remarks>
    private static string NewAccount() => "config-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Re-reads which configurations are running, after something stopped them all.</summary>
    public void RefreshRunState()
    {
        foreach (var row in Rows)
        {
            row.Running = _run.IsRunning(row.Configuration);
        }

        OnPropertyChanged(nameof(Summary));
    }

    private void Rebuild()
    {
        // Guarded, because filling the collection moves the selection and that would otherwise
        // ask the settings to switch configuration in the middle of being told they changed.
        _rebuilding = true;

        // Read once. Each access rebuilds the whole settings record, extension lists and all.
        var configurations = _settings.Configurations;

        try
        {
            Rows.Clear();

            for (var i = 0; i < configurations.Count; i++)
            {
                var index = i;
                var row = new ConfigurationRowViewModel(
                    configurations[i],
                    index,
                    _run.IsRunning(configurations[i]),
                    wanted => RunAsync(index, wanted));

                row.Failed += message => Problem = message;

                Rows.Add(row);
            }

            SelectedIndex = Rows.Count == 0
                ? -1
                : Math.Clamp(_settings.ConfigurationIndex, 0, Rows.Count - 1);
        }
        finally
        {
            _rebuilding = false;
        }

        // Whatever went wrong last time was about the list as it was; this is a new one.
        Problem = string.Empty;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Summary));

        StatusesChecked = CheckStatusesAsync([.. configurations], [.. Rows]);
    }

    /// <summary>
    /// The most recent status pass, so a test can wait for it.
    /// </summary>
    /// <remarks>
    /// Exposed only because the alternative is a test that sleeps. Nothing in the application
    /// waits on it: the rows update themselves when it finishes.
    /// </remarks>
    public Task StatusesChecked { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Works out each configuration's status away from the UI thread, then fills it in.
    /// </summary>
    /// <remarks>
    /// Validate calls Directory.Exists, and on a share whose server is down that takes the SMB
    /// timeout to answer. Doing it once per row while building the list froze the window for the
    /// sum of those timeouts, on every save and every tick of an Enabled box -- on the instrument
    /// computer this is supposed to stay out of the way of.
    /// <para>
    /// The await returns to the UI thread under WPF, because that is where this was called from.
    /// In a test there is no such context and the assignment happens on a pool thread, which is
    /// harmless: nothing is bound to it there.
    /// </para>
    /// </remarks>
    private static async Task CheckStatusesAsync(
        MonitoringConfiguration[] configurations,
        ConfigurationRowViewModel[] rows)
    {
        if (rows.Length == 0)
        {
            return;
        }

        var problems = await Task
            .Run(() => configurations.Select(c => c.Validate()).ToArray())
            .ConfigureAwait(true);

        for (var i = 0; i < rows.Length && i < problems.Length; i++)
        {
            rows[i].Report(problems[i]);
        }
    }
}
