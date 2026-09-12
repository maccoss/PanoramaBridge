using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Func<bool, Task> _setEnabled;

    public ConfigurationRowViewModel(
        MonitoringConfiguration configuration,
        int index,
        Func<bool, Task> setEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Index = index;
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
            : configuration.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        _enabled = configuration.Enabled;
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));

        Problems = configuration.Validate();
    }

    /// <summary>Where this configuration sits in the list.</summary>
    public int Index { get; }

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
    public IReadOnlyList<string> Problems { get; }

    /// <summary>What the status column says.</summary>
    /// <remarks>
    /// Whether it would run, not whether it is running. The Transfer Status tab is where what is
    /// happening now belongs, and saying "Running" here for a configuration whose folder had just
    /// been unplugged would be the kind of tick that means less than it appears to.
    /// </remarks>
    public string Status => !Enabled
        ? "Off"
        : Problems.Count > 0 ? "Needs attention" : "Ready";

    /// <summary>The first problem, for the tooltip on the status column.</summary>
    public string? StatusDetail => Problems.Count > 0 ? string.Join("\n", Problems) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => _ = _setEnabled(value);
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

    public ConfigurationsViewModel(SettingsViewModel settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.ConfigurationsChanged += Rebuild;

        Rebuild();
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

    /// <summary>How many configurations will run when monitoring starts.</summary>
    public string Summary
    {
        get
        {
            var enabled = Rows.Count(r => r.Enabled);

            return Rows.Count switch
            {
                0 => "No configurations yet. Add one to choose a folder and where it goes.",
                1 when enabled == 1 => "1 configuration, on.",
                1 => "1 configuration, off.",
                _ => $"{Rows.Count} configurations, {enabled} on.",
            };
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_rebuilding || value < 0 || value >= Rows.Count)
        {
            return;
        }

        _ = _settings.EditConfigurationAsync(value);
    }

    /// <summary>Adds an empty configuration and selects it for editing.</summary>
    /// <remarks>
    /// Stamped with the time it was created, unlike the one carried over from a settings file
    /// written before configurations existed -- that file never recorded when monitoring was set
    /// up, and inventing a date for it would be worse than leaving the column blank.
    /// </remarks>
    [RelayCommand]
    private async Task AddAsync()
    {
        var configurations = new List<MonitoringConfiguration>(_settings.Configurations)
        {
            new() { CreatedUtc = DateTimeOffset.UtcNow },
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

        await _settings
            .ReplaceConfigurationsAsync(configurations, Math.Min(SelectedIndex, configurations.Count - 1))
            .ConfigureAwait(true);
    }

    /// <summary>Asks the user to confirm a deletion. Supplied by the view.</summary>
    public Func<string, bool>? Confirm { get; set; }

    private Task SetEnabledAsync(int index, bool enabled)
    {
        if (index < 0 || index >= _settings.Configurations.Count)
        {
            return Task.CompletedTask;
        }

        var configurations = new List<MonitoringConfiguration>(_settings.Configurations);
        configurations[index] = configurations[index] with { Enabled = enabled };

        return _settings.ReplaceConfigurationsAsync(configurations, _settings.ConfigurationIndex);
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

    private void Rebuild()
    {
        // Guarded, because filling the collection moves the selection and that would otherwise
        // ask the settings to switch configuration in the middle of being told they changed.
        _rebuilding = true;

        try
        {
            Rows.Clear();

            var configurations = _settings.Configurations;

            for (var i = 0; i < configurations.Count; i++)
            {
                var index = i;
                Rows.Add(new ConfigurationRowViewModel(
                    configurations[i], index, enabled => SetEnabledAsync(index, enabled)));
            }

            SelectedIndex = Rows.Count == 0
                ? -1
                : Math.Clamp(_settings.ConfigurationIndex, 0, Rows.Count - 1);
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Summary));
    }
}
