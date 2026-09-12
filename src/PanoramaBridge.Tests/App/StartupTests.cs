using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.App;
using PanoramaBridge.App.Services;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The window can actually be built.
/// </summary>
/// <remarks>
/// <para>
/// Every registration in the container compiles whatever it resolves to, so a missing one, a
/// cycle between two view models, or a constructor that throws is a build with no errors and an
/// application that shows "PanoramaBridge could not start" on an instrument computer. Adding a
/// tab is exactly the change that invites it.
/// </para>
/// <para>
/// This builds the real container against a temporary data directory, so it touches the same
/// settings store and ledger the application does, and cleans both up afterwards.
/// </para>
/// </remarks>
public sealed class StartupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pb-startup-").FullName;

    private ServiceProvider NewContainer() => Program.BuildServiceProvider(
        new AppPaths(_root),
        new ResourceGovernor(NullLogger<ResourceGovernor>.Instance));

    [Fact]
    public void Everything_the_window_binds_to_can_be_resolved()
    {
        using var services = NewContainer();

        var shell = services.GetRequiredService<MainViewModel>();

        shell.Settings.ShouldNotBeNull();
        shell.Configurations.ShouldNotBeNull();
        shell.TransferStatus.ShouldNotBeNull();
        shell.Uploads.ShouldNotBeNull();
    }

    [Fact]
    public void The_configurations_tab_and_the_editor_tabs_are_looking_at_the_same_settings()
    {
        // Two objects each holding their own copy is the trap SettingsViewModel's own remarks
        // describe, and from the container it would look like everything working: the list would
        // simply stop agreeing with the tabs after the first edit.
        using var services = NewContainer();

        var shell = services.GetRequiredService<MainViewModel>();

        shell.Configurations.Rows.Count.ShouldBe(shell.Settings.Configurations.Count);

        services.GetRequiredService<SettingsViewModel>()
            .ShouldBeSameAs(shell.Settings);
    }

    [Fact]
    public void A_fresh_install_opens_on_a_configuration_rather_than_an_empty_list()
    {
        // Somebody who has never run this before should find a folder box to fill in, not an
        // empty grid and a guess about which button makes one.
        using var services = NewContainer();

        var shell = services.GetRequiredService<MainViewModel>();

        shell.Configurations.Rows.ShouldHaveSingleItem();
        shell.Configurations.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void The_transfer_service_resolves_without_touching_a_server()
    {
        using var services = NewContainer();

        var transfers = services.GetRequiredService<TransferService>();

        transfers.IsMonitoring.ShouldBeFalse();
        transfers.MonitoredConfigurations.ShouldBe(0);
    }

    public void Dispose()
    {
        // The container built a real ledger under this directory. A pooled connection handle that
        // has not been released yet makes the delete throw, and the test then fails for a reason
        // unrelated to anything it asserts -- which is how the suite's earlier temp-directory leak
        // stayed hidden. The same two lines LedgerRekeyMigrationTests already carries.
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory is not worth failing a run over.
        }
    }
}
