using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Updates;
using Serilog;
using Serilog.Extensions.Logging;
using Velopack;

namespace PanoramaBridge.App;

/// <summary>
/// Explicit entry point.
/// </summary>
/// <remarks>
/// WPF normally generates its own Main, but Velopack requires <see cref="VelopackApp"/> to run
/// before anything else in the process -- it is what handles the install, update and uninstall
/// hooks the launcher invokes. App.xaml is therefore demoted from ApplicationDefinition to Page
/// so no entry point is generated, and this takes its place.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be first. On an update or uninstall hook this call never returns.
        VelopackApp.Build().Run();

        // Before anything opens the ledger. A second copy would share one SQLite file, walk the
        // same folder and race PUTs of the same acquisition, and since the window now hides
        // rather than exits, starting one by accident is easy and invisible.
        using var instance = SingleInstance.Acquire("PanoramaBridge");

        if (!instance.IsFirst)
        {
            instance.SignalExisting();
            return 0;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();

        Serilog.Log.Logger = LoggingSetup.Create(paths);

        // Applied before anything else runs, so even startup work is polite. This lives on the
        // computer attached to a mass spectrometer: losing an acquisition because a transfer
        // utility was competing for the processor or the disk would be far worse than a transfer
        // finishing later.
        var governor = new ResourceGovernor(
            new SerilogLoggerFactory(Serilog.Log.Logger).CreateLogger<ResourceGovernor>());

        try
        {
            Serilog.Log.Information(
                "Starting {Product} {Version} ({Rid}); data directory {Root}",
                AppInfo.ProductName,
                AppInfo.InformationalVersion,
                AppInfo.RuntimeIdentifier,
                paths.Root);

            using var services = BuildServiceProvider(paths, governor);

            var settings = services.GetRequiredService<ISettingsStore>()
                .LoadAsync().GetAwaiter().GetResult();

            governor.ApplyPoliteDefaults(settings.YieldToInstrumentSoftware);

            // The verbose-logging toggle had no effect at all: the level switch existed and
            // nothing ever set it. Exactly the defect this codebase criticises the Python
            // version for, and it hid a monitoring bug for an afternoon.
            LoggingSetup.ApplyVerbosity(settings.VerboseLogging);

            var app = new App(services, instance);
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Startup failed.");

            // Anything thrown this early happens before there is a window to report into.
            MessageBox.Show(
                $"PanoramaBridge could not start.\n\n{ex.Message}\n\nSee {paths.LogDirectory}",
                "PanoramaBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return 1;
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static ServiceProvider BuildServiceProvider(AppPaths paths, ResourceGovernor governor)
    {
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton(governor);
        services.AddLogging(builder =>
        {
            // Serilog's level switch is the only filter, so that the Verbose logging toggle takes
            // effect without a restart. Left at its default, this pipeline drops everything below
            // Information before Serilog ever sees it, and the toggle silently does nothing.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new SerilogLoggerProvider(dispose: false));
        });

        // -- Storage ---------------------------------------------------------------------------
        services.AddSingleton<ISettingsStore>(provider => new JsonSettingsStore(
            paths.SettingsFile,
            provider.GetRequiredService<ILogger<JsonSettingsStore>>()));

        // The ledger is opened once and shared: several upload workers write to it concurrently.
        services.AddSingleton<IStateStore>(_ => new SqliteStateStore(paths.StateDatabase));

        // -- Security --------------------------------------------------------------------------
        services.AddSingleton<ICredentialStore>(provider => new WindowsCredentialStore(
            provider.GetRequiredService<ILogger<WindowsCredentialStore>>()));
        services.AddSingleton<ICredentialStoreAccessor, CredentialStoreAccessor>();

        // -- Updates ---------------------------------------------------------------------------
        services.AddSingleton(_ =>
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return client;
        });

        services.AddSingleton(provider => new VersionPolicyClient(
            provider.GetRequiredService<HttpClient>(),
            policyUrl: null,
            log: provider.GetRequiredService<ILogger<VersionPolicyClient>>()));

        services.AddSingleton<UpdateService>();

        // -- Transfers -------------------------------------------------------------------------
        services.AddSingleton<TransferService>();

        // -- Shell -----------------------------------------------------------------------------
        services.AddSingleton<TrayIcon>();

        // -- View models -----------------------------------------------------------------------
        //
        // Settings are read synchronously here on purpose: the shell cannot be built without
        // them, and doing it asynchronously would only move a few milliseconds of file I/O
        // behind a loading state nobody would see.
        services.AddSingleton(provider => new SettingsViewModel(
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<ISettingsStore>().LoadAsync().GetAwaiter().GetResult()));

        services.AddSingleton(provider => new TransferStatusViewModel(
            provider.GetRequiredService<TransferService>().Progress));

        services.AddSingleton<UploadsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
