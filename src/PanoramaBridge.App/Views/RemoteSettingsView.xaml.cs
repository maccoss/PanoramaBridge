using System.Windows;
using System.Windows.Controls;
using PanoramaBridge.App.ViewModels;

namespace PanoramaBridge.App.Views;

/// <summary>
/// The Remote Settings tab.
/// </summary>
/// <remarks>
/// The password box is deliberately unbound. WPF's <see cref="PasswordBox.Password"/> is not a
/// dependency property precisely so a secret does not end up in the binding system, and honouring
/// that is worth more than the convenience of a binding: the value stays in this control and is
/// read only when a command asks for it.
/// </remarks>
public partial class RemoteSettingsView : UserControl
{
    public RemoteSettingsView() => InitializeComponent();

    /// <summary>The secret currently typed, or null when the box is empty.</summary>
    public string? Secret =>
        string.IsNullOrEmpty(SecretBox.Password) ? null : SecretBox.Password;

    /// <summary>Clears the box, for when a credential is forgotten.</summary>
    public void ClearSecret() => SecretBox.Clear();

    private async void BrowseRemote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        // Raised as an event rather than a command so the window can supply the connected client
        // and own the dialog's lifetime; a view model should not be opening windows.
        var request = new BrowseRemoteRequest(settings.RemotePath);
        BrowseRemoteRequested?.Invoke(this, request);

        if (request.Handler is null)
        {
            return;
        }

        var chosen = await request.Handler(request.StartingPath).ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            settings.RemotePath = chosen;
        }
    }

    /// <summary>Raised when the user asks to browse the server.</summary>
    public event EventHandler<BrowseRemoteRequest>? BrowseRemoteRequested;
}

/// <summary>Carries the browse request out to whoever can service it.</summary>
/// <param name="startingPath">Where the tree should start.</param>
public sealed class BrowseRemoteRequest(string startingPath)
{
    /// <summary>The currently configured destination.</summary>
    public string StartingPath { get; } = startingPath;

    /// <summary>Set by the handler; returns the chosen path, or null if cancelled.</summary>
    public Func<string, Task<string?>>? Handler { get; set; }
}
