using System.Windows;
using System.Windows.Controls;
using PanoramaBridge.App.ViewModels;

namespace PanoramaBridge.App.Views;

public partial class UploadsView : UserControl
{
    public UploadsView()
    {
        InitializeComponent();

        // Dismissing a row is the one thing on this tab that changes the record, so it asks
        // first. The view model takes the question as a callback rather than showing the box
        // itself, so it stays testable without a window.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is UploadsViewModel uploads)
            {
                uploads.Confirm = Ask;
            }
        };
    }

    private bool Ask(string question) =>
        MessageBox.Show(
            question,
            "PanoramaBridge",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
}
