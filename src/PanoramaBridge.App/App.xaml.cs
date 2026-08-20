using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace PanoramaBridge.App;

/// <summary>
/// WPF application object. Constructed by <see cref="Program"/> rather than by a generated
/// entry point, so that Velopack can bootstrap first.
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The most-reported complaint about the Python version was the window vanishing
        // during a file copy. An unhandled exception should surface a diagnostic and let the
        // user keep working, not close the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread.");

        MessageBox.Show(
            $"Something went wrong, but PanoramaBridge is still running.\n\n{e.Exception.Message}",
            "PanoramaBridge",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Cannot be handled, only recorded. Flush so the log survives the crash.
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; the process is terminating.");
        Log.CloseAndFlush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
