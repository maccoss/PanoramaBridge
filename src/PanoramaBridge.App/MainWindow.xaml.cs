using System.ComponentModel;
using System.Windows;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.App.Views;

namespace PanoramaBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TransferService _transfers;
    private readonly TrayIcon _tray;

    /// <summary>Set only by the tray menu's Exit, so a close request means what it says.</summary>
    private bool _exiting;

    public MainWindow(MainViewModel viewModel, TransferService transfers, TrayIcon tray)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));

        InitializeComponent();
        DataContext = _viewModel;

        // The secret stays in the password box; the view model reads it only when a command
        // needs it, so it never becomes bound or serialized state.
        _viewModel.SecretProvider = () => RemoteSettings.Secret;

        RemoteSettings.BrowseRemoteRequested += OnBrowseRemoteRequested;

        _tray.OpenRequested += OnTrayOpenRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        _viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ApplyTraySetting();
        UpdateTrayTooltip();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _viewModel.Start();
    }

    /// <summary>
    /// Opens the remote folder browser.
    /// </summary>
    /// <remarks>
    /// Handled here rather than in a view model because it needs the connected client and owns a
    /// window's lifetime. If nothing is connected yet, the connection is established first --
    /// otherwise the browser would open empty and look broken.
    /// </remarks>
    private void OnBrowseRemoteRequested(object? sender, BrowseRemoteRequest request) =>
        request.Handler = async startingPath =>
        {
            if (_transfers.Client is null)
            {
                var check = await _transfers
                    .TestConnectionAsync(_viewModel.Settings.ToSettings(), RemoteSettings.Secret)
                    .ConfigureAwait(true);

                if (!check.Succeeded)
                {
                    MessageBox.Show(
                        check.Summary,
                        "Connect to Panorama first",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    return null;
                }
            }

            var dialog = new RemoteBrowserDialog(
                new RemoteBrowserViewModel(_transfers.Client!, startingPath))
            {
                Owner = this,
            };

            return dialog.ShowDialog() == true ? dialog.ChosenPath : null;
        };

    // -- Notification area -------------------------------------------------------------------

    private void OnTrayOpenRequested(object? sender, EventArgs e) => RestoreFromTray();

    /// <summary>
    /// Exits from the tray menu, asking first if that would abandon a transfer.
    /// </summary>
    /// <remarks>
    /// Exit is now the ordinary way to quit -- the close button hides -- and it is one unguarded
    /// click next to Open. Closing tears down the view model, which cancels the shutdown token
    /// and aborts a PUT part-way through. <see cref="ViewModels.MainViewModel"/> already refuses
    /// to restart for an update while a transfer runs, for exactly this reason; this path is more
    /// reachable and had no equivalent. The window is brought back first, because a modal dialog
    /// owned by a hidden window is a dialog nobody can find.
    /// </remarks>
    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        if (_transfers.IsRunning)
        {
            RestoreFromTray();

            var answer = MessageBox.Show(
                this,
                "A transfer is still running. Exiting now will stop it part-way, and the "
                + "incomplete copy on the server will be replaced next time it is uploaded. "
                + "Exit anyway?",
                "Transfer in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _exiting = true;
        Close();
    }

    private void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// Keeps the icon in step with the setting.
    /// </summary>
    /// <remarks>
    /// The restore is unreachable today and kept anyway: the only control that writes this
    /// setting lives inside this window, so the setting cannot change while the window is
    /// hidden. It costs one comparison, and the failure it guards against -- turning off the
    /// only way back to a hidden window -- is one the user could not undo.
    /// </remarks>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_viewModel.Settings.MinimizeToTray))
        {
            return;
        }

        ApplyTraySetting();

        if (!_viewModel.Settings.MinimizeToTray && !IsVisible)
        {
            RestoreFromTray();
        }
    }

    /// <summary>
    /// Keeps the icon telling the truth, and gives a hidden window a voice.
    /// </summary>
    /// <remarks>
    /// While the window is hidden the status line and the failure strip are bound to nothing the
    /// user can see, so a rejected credential or an unreachable server would change only a
    /// tooltip nobody is hovering over. On an instrument computer the window can stay closed for
    /// weeks. A failure therefore raises a balloon as well, and only when hidden -- when the
    /// window is open the strip has already said it.
    /// </remarks>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(_viewModel.IsMonitoring) or nameof(_viewModel.StatusLine))
        {
            UpdateTrayTooltip();
        }

        if (e.PropertyName == nameof(_viewModel.ConnectionFailed)
            && _viewModel.ConnectionFailed
            && !IsVisible)
        {
            _tray.Notify(
                $"{_viewModel.ProductName} needs attention",
                _viewModel.StatusLine,
                warning: true);
        }
    }

    private void ApplyTraySetting() => _tray.Visible = _viewModel.Settings.MinimizeToTray;

    /// <summary>
    /// Hover text: the product, and what it is actually doing.
    /// </summary>
    /// <remarks>
    /// The status line rather than a fixed label, because with the window hidden this is the
    /// only thing that reports progress without a balloon. It is also what makes the tooltip
    /// long enough to need truncating -- "Monitoring" plus a UNC path clears 63 characters
    /// easily.
    /// </remarks>
    private void UpdateTrayTooltip() => _tray.SetTooltip(
        $"{_viewModel.ProductName} - {_viewModel.StatusLine}");

    /// <summary>
    /// Hides rather than closes when the user asked for that, and an icon exists to return by.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Raised first so a subscriber sees every close attempt and can cancel one, rather than
        // seeing only the closes that happen to be real. Nothing subscribes today; the moment
        // something does -- an unsaved-settings prompt is the obvious candidate -- skipping this
        // would make it fire only when the tray setting is off, which is not the default.
        base.OnClosing(e);

        if (e.Cancel)
        {
            return;
        }

        if (TrayPolicy.ShouldHideInsteadOfClosing(
                _viewModel.Settings.MinimizeToTray,
                _tray.IsAvailable,
                _exiting))
        {
            e.Cancel = true;
            Hide();
            _tray.AnnounceStillRunning(_viewModel.IsMonitoring);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoteSettings.BrowseRemoteRequested -= OnBrowseRemoteRequested;

        _tray.OpenRequested -= OnTrayOpenRequested;
        _tray.ExitRequested -= OnTrayExitRequested;
        _viewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // Disposed here as well as by the container so the icon goes at once rather than
        // lingering until the pointer next crosses it. Dispose is idempotent.
        _tray.Dispose();

        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
