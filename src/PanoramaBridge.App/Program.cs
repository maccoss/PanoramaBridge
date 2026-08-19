using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Updates;
using Serilog;
using Serilog.Extensions.Logging;
using Velopack;

namespace PanoramaBridge.App;

/// <summary>
/// Explicit entry point.
/// </summary>
/// <remarks>
/// WPF normally generates its own Main, but Velopack requires
/// <see cref="VelopackApp"/> to run before anything else in the process -- it is what handles
/// the install, update and uninstall hooks that the launcher invokes. So the generated entry
/// point is suppressed with the DISABLE_XAML_GENERATED_MAIN constant and this takes its place.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be first. On an update or uninstall hook this call never returns.
        VelopackApp.Build().Run();

        var paths = new AppPaths();
        paths.EnsureCreated();

        Serilog.Log.Logger = LoggingSetup.Create(paths);

        try
        {
            Serilog.Log.Information(
                "Starting {Product} {Version} ({Rid}); data directory {Root}",
                AppInfo.ProductName,
                AppInfo.InformationalVersion,
                AppInfo.RuntimeIdentifier,
                paths.Root);

            using var services = BuildServiceProvider(paths);

            var app = new App(services);
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

    private static ServiceProvider BuildServiceProvider(AppPaths paths)
    {
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(dispose: false)));

        // One HttpClient for the process. Update checks are low volume, but the same
        // discipline that the transfer layer needs starts here.
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
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
