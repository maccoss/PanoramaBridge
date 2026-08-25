using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// Choosing the name an acquisition is sent under when something already occupies its own.
/// </summary>
/// <remarks>
/// These names are shown to a person before they agree to them, so the rules are worth pinning:
/// a rename that surprises somebody is worse than one that is merely ugly.
/// </remarks>
public sealed class ConflictNamesTests
{
    [Fact]
    public void A_free_name_is_left_alone()
    {
        // Nothing to resolve. Renaming anyway would be a gratuitous change to a name somebody
        // chose at the instrument.
        ConflictNames.NextFree("run.raw", []).ShouldBe("run.raw");
        ConflictNames.NextFree("run.raw", ["other.raw"]).ShouldBe("run.raw");
    }

    [Fact]
    public void The_suffix_goes_before_the_extension()
    {
        // "run.raw (2)" would no longer be a .raw to Skyline, to Panorama, or to whoever sorts
        // the folder next -- a strange outcome for a step meant to preserve data.
        ConflictNames.NextFree("run.raw", ["run.raw"]).ShouldBe("run (2).raw");
    }

    [Fact]
    public void Counting_starts_at_two_and_steps_over_what_is_there()
    {
        // The copy already on the server is the first one, in the only sense a reader cares
        // about, so the next is 2 rather than 1.
        string[] taken = ["run.raw", "run (2).raw", "run (3).raw"];

        ConflictNames.NextFree("run.raw", taken).ShouldBe("run (4).raw");
    }

    [Fact]
    public void A_gap_in_the_numbering_is_filled()
    {
        string[] taken = ["run.raw", "run (3).raw"];

        ConflictNames.NextFree("run.raw", taken).ShouldBe("run (2).raw");
    }

    [Fact]
    public void Case_does_not_make_a_name_free()
    {
        // The servers this talks to treat these as the same name. Returning "run.raw" as free
        // because the listing said "Run.raw" would turn a conflict into a failed upload, which
        // is a worse answer arrived at more slowly.
        ConflictNames.NextFree("run.raw", ["RUN.RAW"]).ShouldBe("run (2).raw");
        ConflictNames.NextFree("run.raw", ["run.raw", "RUN (2).raw"]).ShouldBe("run (3).raw");
    }

    [Fact]
    public void A_name_with_no_extension_still_works()
    {
        ConflictNames.NextFree("acquisition", ["acquisition"]).ShouldBe("acquisition (2)");
    }

    [Fact]
    public void A_companion_keeps_its_last_extension_where_readers_look_for_it()
    {
        // run.wiff.scan holds the spectra, and .scan is what identifies it. The rename moves the
        // stem and leaves the extension alone.
        ConflictNames.NextFree("run.wiff.scan", ["run.wiff.scan"]).ShouldBe("run.wiff (2).scan");
    }

    [Fact]
    public void A_packed_acquisition_keeps_the_zip_on_the_end()
    {
        ConflictNames.NextFree("250314_HeLa.d.zip", ["250314_HeLa.d.zip"])
            .ShouldBe("250314_HeLa.d (2).zip");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => ConflictNames.NextFree(name, []));
}
