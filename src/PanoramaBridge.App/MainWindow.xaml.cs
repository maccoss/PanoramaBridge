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

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
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
    /// Turning the setting off while the window is hidden would otherwise remove the only way
    /// back to it, so that case brings the window with it.
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_viewModel.IsMonitoring))
        {
            UpdateTrayTooltip();
        }
    }

    private void ApplyTraySetting() => _tray.Visible = _viewModel.Settings.MinimizeToTray;

    private void UpdateTrayTooltip() => _tray.SetTooltip(
        _viewModel.IsMonitoring
            ? $"{_viewModel.ProductName} - monitoring"
            : $"{_viewModel.ProductName} - not monitoring");

    /// <summary>
    /// Hides rather than closes when the user asked for that, and an icon exists to return by.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (TrayPolicy.ShouldHideInsteadOfClosing(
                _viewModel.Settings.MinimizeToTray,
                _tray.IsAvailable,
                _exiting))
        {
            e.Cancel = true;
            Hide();
            _tray.AnnounceStillRunning();
            return;
        }

        base.OnClosing(e);
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
