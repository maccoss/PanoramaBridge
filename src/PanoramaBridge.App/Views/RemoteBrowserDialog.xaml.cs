using System.Windows;
using System.Windows.Controls;
using PanoramaBridge.App.ViewModels;

namespace PanoramaBridge.App.Views;

public partial class RemoteBrowserDialog : Window
{
    private readonly RemoteBrowserViewModel _viewModel;

    public RemoteBrowserDialog(RemoteBrowserViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>The folder the user chose, or null if they cancelled.</summary>
    public string? ChosenPath { get; private set; }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.Selected = e.NewValue as RemoteFolderViewModel;

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanAccept)
        {
            return;
        }

        ChosenPath = _viewModel.SelectedPath;
        DialogResult = true;
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = NewFolderName.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _viewModel.CreateFolderCommand.ExecuteAsync(name).ConfigureAwait(true);
        NewFolderName.Clear();
    }
}
