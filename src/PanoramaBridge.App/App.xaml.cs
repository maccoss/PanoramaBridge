using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Infrastructure;
using Serilog;

namespace PanoramaBridge.App;

/// <summary>
/// WPF application object. Constructed by <see cref="Program"/> rather than by a generated
/// entry point, so that Velopack can bootstrap first.
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly SingleInstance _instance;

    public App(IServiceProvider services, SingleInstance instance)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
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

        // Velopack applies an update by calling Environment.Exit, which runs no container
        // disposal and no Closed handler. Without this the tray icon is never removed and the
        // shell keeps drawing a dead one until the pointer next crosses it -- while the
        // restarted instance adds a second, so an updated machine shows two. ProcessExit is
        // raised by Environment.Exit, which is what makes it the right hook rather than the
        // window's.
        var tray = _services.GetRequiredService<TrayIcon>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tray.Dispose();

        // Starting it again is what a user does when the window is hidden and they cannot tell
        // it is running. That second launch exits immediately; this is what makes it feel like
        // it simply reopened the window rather than doing nothing at all.
        _instance.ListenForSecondLaunch(() => Dispatcher.Invoke(() =>
        {
            window.Show();

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }));

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
