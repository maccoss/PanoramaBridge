using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.Infrastructure;

/// <summary>
/// Turning a mapped drive letter into the network path it stands for.
/// </summary>
/// <remarks>
/// A drive mapping belongs to one Windows sign-in, so a monitored folder recorded as <c>Y:\</c>
/// is invisible to a service or a scheduled task -- and the folder picker returns exactly what
/// the user clicked, which for a network folder reached through This PC is the drive letter.
/// </remarks>
public sealed class NetworkPathsTests
{
    [Theory]
    [InlineData(@"C:\Users\someone\Documents")]
    [InlineData(@"\\fileserver\instruments\QE")]
    [InlineData(@"relative\path")]
    [InlineData("")]
    public void Anything_that_is_not_a_mapped_drive_is_returned_untouched(string path) =>
        NetworkPaths.ResolveMappedDrive(path).ShouldBe(path);

    [Fact]
    public void A_null_path_is_tolerated()
    {
        // Called from the settings screen, where the box starts empty.
        NetworkPaths.ResolveMappedDrive(null).ShouldBe(string.Empty);
        NetworkPaths.IsMappedDrive(null).ShouldBeFalse();
    }

    [Fact]
    public void A_local_disk_is_not_a_mapping() =>
        NetworkPaths.IsMappedDrive(@"C:\").ShouldBeFalse();

    [SkippableFact]
    public void A_mapped_drive_resolves_to_the_share_it_stands_for()
    {
        // Needs a real mapping, so it runs only where there is one. Everything above is path
        // arithmetic and runs everywhere.
        var mapped = DriveInfo.GetDrives()
            .Select(d => d.Name)
            .FirstOrDefault(NetworkPaths.IsMappedDrive);

        Skip.If(mapped is null, "No mapped network drive on this machine.");

        var resolved = NetworkPaths.ResolveMappedDrive(mapped!);

        resolved.ShouldStartWith(@"\\");
        resolved.ShouldNotStartWith(mapped![..2]);

        // The remainder has to survive the translation, not just the root.
        NetworkPaths.ResolveMappedDrive(Path.Combine(mapped, "instruments", "QE"))
            .ShouldBe(Path.Combine(resolved.TrimEnd('\\'), "instruments", "QE"));

        Console.WriteLine($"[unc] {mapped} resolves to {resolved}");
    }
}
