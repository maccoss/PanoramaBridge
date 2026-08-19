using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.Infrastructure;

/// <summary>
/// The Python version wrote its log to a relative path and split state between two files in
/// the user profile. One resolver, tested, keeps that from happening again.
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pb-tests-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public void Every_path_is_absolute_and_under_the_root()
    {
        var paths = new AppPaths(_root);

        foreach (var path in new[]
                 {
                     paths.LogDirectory, paths.LogFileTemplate, paths.SettingsFile, paths.StateDatabase,
                 })
        {
            Path.IsPathFullyQualified(path).ShouldBeTrue($"{path} should be absolute");
            path.ShouldStartWith(paths.Root);
        }
    }

    [Fact]
    public void The_default_root_is_local_appdata_not_roaming()
    {
        // A SQLite state database must never be synced across a roaming domain profile.
        var paths = new AppPaths();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        paths.Root.ShouldBe(Path.Combine(local, AppPaths.AppFolderName));
    }

    [Fact]
    public void EnsureCreated_is_idempotent()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Directory.Exists(paths.Root).ShouldBeTrue();
        Directory.Exists(paths.LogDirectory).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class AppInfoTests
{
    [Fact]
    public void The_version_comes_from_the_assembly_not_a_hardcoded_string()
    {
        // The Python About dialog said "Version 1.0" while the package said 0.1.9rc4.
        AppInfo.InformationalVersion.ShouldNotBeNullOrWhiteSpace();
        AppInfo.InformationalVersion.Contains('+').ShouldBeFalse("build metadata should be trimmed");
        AppInfo.Version.ShouldBeGreaterThan(new Version(0, 0, 0));
    }

    [Fact]
    public void The_user_agent_identifies_the_product_version_and_platform()
    {
        // Panorama administrators read this out of the server access log to see which
        // versions the lab is actually running.
        AppInfo.UserAgent.ShouldStartWith("PanoramaBridge/");
        AppInfo.UserAgent.ShouldContain(AppInfo.InformationalVersion);
        AppInfo.UserAgent.ShouldContain(AppInfo.RuntimeIdentifier);
    }
}
