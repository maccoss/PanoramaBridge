using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// A settings file that cannot be read right now, as distinct from one whose contents are bad.
/// </summary>
/// <remarks>
/// <para>
/// Both used to be caught together, and the answer to a bad file is to move it aside and start
/// from defaults. Applied to a file that was merely locked for a moment -- by antivirus opening it
/// as it is written, or a backup agent walking the profile -- that renames the monitored folder,
/// the destination and the sign-in out of the way, on an instrument, for a lock that had already
/// gone.
/// </para>
/// <para>
/// What made it survivable by accident was that the rename fails too while the file is locked. The
/// outcome therefore depended on timing rather than on a decision, which is the part worth fixing
/// even more than the outcome.
/// </para>
/// </remarks>
public sealed class LockedSettingsFileTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("pb-locked-settings-").FullName;

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private const string Usable =
        """
        {
          "$version": 2,
          "MaxConcurrentTransfers": 5,
          "Configurations": [ { "Name": "Lumos", "LocalDirectory": "D:\\Data" } ]
        }
        """;

    [Fact]
    public async Task A_file_locked_for_a_moment_is_read_rather_than_given_up_on()
    {
        // The ordinary case: something holds the file exclusively and lets go while we are still
        // trying. Retrying is the whole difference between a session that monitors the right
        // folder and one that starts from defaults.
        await File.WriteAllTextAsync(SettingsPath, Usable);

        var held = new FileStream(
            SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        // Released after the first attempt has certainly failed and before the last one runs.
        var release = Task.Run(async () =>
        {
            await Task.Delay(250);
            await held.DisposeAsync();
        });

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();
        await release;

        settings.MaxConcurrentTransfers.ShouldBe(5, "the file was read, not given up on");
        settings.Configurations.ShouldHaveSingleItem().Name.ShouldBe("Lumos");
    }

    [Fact]
    public async Task A_file_held_throughout_is_left_exactly_where_it_is()
    {
        // The important one. Defaults for this session are acceptable -- refusing to start would
        // be worse -- but the file must still be there, unrenamed, for the next launch.
        await File.WriteAllTextAsync(SettingsPath, Usable);
        var before = await File.ReadAllTextAsync(SettingsPath);

        AppSettings settings;

        // Scoped, because FileShare.None shuts everybody out including this test. Whether the
        // file survived can only be asked once the lock has gone, which is also when it matters.
        await using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = await new JsonSettingsStore(SettingsPath).LoadAsync();
        }

        settings.MaxConcurrentTransfers.ShouldBe(3, "defaults, for this session only");

        File.Exists(SettingsPath + ".corrupt")
            .ShouldBeFalse("a locked file is not a corrupt one and must not be renamed");

        File.Exists(SettingsPath).ShouldBeTrue();
        (await File.ReadAllTextAsync(SettingsPath))
            .ShouldBe(before, "and its contents are untouched");
    }

    [Fact]
    public async Task A_file_whose_contents_are_bad_is_still_moved_aside()
    {
        // Unchanged behavior, asserted so that separating the two failures did not quietly drop
        // it. A truncated file is evidence and must not be silently discarded.
        await File.WriteAllTextAsync(SettingsPath, "{ this is not json");

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();

        settings.ShouldBe(new AppSettings());
        File.Exists(SettingsPath + ".corrupt").ShouldBeTrue();
    }

    [Fact]
    public async Task A_save_that_cannot_replace_the_file_leaves_no_temporary_behind()
    {
        // The move is what fails when the file is locked, and it fails after the temporary exists.
        // One accumulates for every save that ever lost that race, and the next person looking at
        // the folder has two files and no way to tell which is the real one.
        await File.WriteAllTextAsync(SettingsPath, Usable);

        await using var held = new FileStream(
            SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var store = new JsonSettingsStore(SettingsPath);

        // UnauthorizedAccessException rather than IOException, which is measured rather than
        // assumed: File.Move over a target anyone holds open fails that way at every FileShare
        // setting, and UnauthorizedAccessException does not derive from IOException. Both
        // production catch clauses name the two separately for the same reason.
        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => store.SaveAsync(new AppSettings()));

        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_that_succeeds_still_leaves_no_temporary_behind()
    {
        // The pre-existing guarantee, kept: adding a cleanup path must not change the happy one.
        var store = new JsonSettingsStore(SettingsPath);

        await store.SaveAsync(new AppSettings());

        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
        File.Exists(SettingsPath).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
