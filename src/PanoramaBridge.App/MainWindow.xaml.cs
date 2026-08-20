using System.Windows;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.App.Views;

namespace PanoramaBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TransferService _transfers;

    public MainWindow(MainViewModel viewModel, TransferService transfers)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));

        InitializeComponent();
        DataContext = _viewModel;

        // The secret stays in the password box; the view model reads it only when a command
        // needs it, so it never becomes bound or serialized state.
        _viewModel.SecretProvider = () => RemoteSettings.Secret;

        RemoteSettings.BrowseRemoteRequested += OnBrowseRemoteRequested;
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

    protected override void OnClosed(EventArgs e)
    {
        RemoteSettings.BrowseRemoteRequested -= OnBrowseRemoteRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
