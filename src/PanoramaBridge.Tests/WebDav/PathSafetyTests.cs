using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.WebDav;

public sealed class PathSafetyTests
{
    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    [Theory]
    [InlineData("2026-05-08_QE_sample_01.raw")]
    [InlineData("with space.raw")]
    [InlineData("hash#sign.raw")]
    [InlineData("plus+sign.raw")]
    [InlineData("amp&ersand.raw")]
    [InlineData("apostrophe's.raw")]
    [InlineData("paren(s).raw")]
    [InlineData("percent%.raw")]
    [InlineData("café.raw")]
    [InlineData("@files")]
    [InlineData("Run001.d")]
    public void Names_that_survive_a_round_trip_are_accepted(string name)
    {
        // Each of these was uploaded to panoramaweb.org and read back with its name intact.
        PathSafety.ValidateSegment(name).IsValid.ShouldBeTrue(name);
    }

    [Theory]
    [InlineData("run;rep1.raw", "run")]
    [InlineData("2026-05-08;rep2.raw", "2026-05-08")]
    [InlineData(";leading.raw", "")]
    [InlineData("dir;name", "dir")]
    public void A_semicolon_is_refused_because_the_server_truncates_the_name(
        string name,
        string whatTheServerWouldStore)
    {
        // Verified against panoramaweb.org: the servlet container strips path parameters after
        // percent-decoding, so the name is cut at the first semicolon. Uploading run;rep1.raw
        // and run;rep2.raw stores BOTH as "run" -- the second silently destroys the first, and
        // both requests return 201. No encoding avoids it, so the upload has to be refused.
        var result = PathSafety.ValidateSegment(name);

        result.IsValid.ShouldBeFalse();
        result.Reason.ShouldBe(PathRejectionReason.SemicolonTruncatesOnServer);
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain($"'{whatTheServerWouldStore}'");
        result.Message.ShouldContain("overwrite");
    }

    [Fact]
    public void The_semicolon_rejection_names_the_file_and_says_what_to_do()
    {
        // The message is read by a scientist looking at a failed upload, not by a developer.
        var message = PathSafety.ValidateSegment("run;rep1.raw").Message.ShouldNotBeNull();

        message.ShouldContain("run;rep1.raw");
        message.ShouldContain("Rename");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_names_are_refused(string? name)
    {
        PathSafety.ValidateSegment(name).Reason.ShouldBe(PathRejectionReason.Empty);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Relative_segments_are_refused(string name)
    {
        PathSafety.ValidateSegment(name).Reason.ShouldBe(PathRejectionReason.Traversal);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("colon:name")]
    [InlineData("star*name")]
    [InlineData("pipe|name")]
    [InlineData("quote\"name")]
    public void Characters_that_cannot_appear_in_a_segment_are_refused(string name)
    {
        PathSafety.ValidateSegment(name).Reason.ShouldBe(PathRejectionReason.IllegalCharacter);
    }

    [Fact]
    public void Control_characters_are_refused()
    {
        PathSafety.ValidateSegment("bell\u0007name").Reason.ShouldBe(PathRejectionReason.IllegalCharacter);
    }

    [Fact]
    public void Over_long_names_are_refused()
    {
        PathSafety.ValidateSegment(new string('x', PathSafety.MaxSegmentLength + 1))
            .Reason.ShouldBe(PathRejectionReason.TooLong);

        PathSafety.ValidateSegment(new string('x', PathSafety.MaxSegmentLength))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_relative_path_is_checked_segment_by_segment()
    {
        PathSafety.ValidateRelativePath("2026\\run 7\\data.raw").IsValid.ShouldBeTrue();
        PathSafety.ValidateRelativePath("2026\\bad;dir\\data.raw").Reason
            .ShouldBe(PathRejectionReason.SemicolonTruncatesOnServer);
    }

    [Fact]
    public void The_destination_mirrors_the_local_directory_structure()
    {
        var resolved = PathSafety.ResolveDestination(
            @"C:\Data",
            @"C:\Data\2026\run 7\sample.raw",
            Destination);

        resolved.ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/2026/run%207/sample.raw");
        resolved.IsUnder(Destination).ShouldBeTrue();
    }

    [Fact]
    public void A_file_directly_in_the_base_directory_lands_at_the_destination_root()
    {
        PathSafety.ResolveDestination(@"C:\Data", @"C:\Data\sample.raw", Destination)
            .ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/sample.raw");
    }

    [Theory]
    [InlineData(@"C:\Elsewhere\sample.raw")]
    [InlineData(@"C:\Data\..\Elsewhere\sample.raw")]
    public void A_file_outside_the_base_directory_is_refused(string localFile)
    {
        Should.Throw<ArgumentException>(
            () => PathSafety.ResolveDestination(@"C:\Data", localFile, Destination));
    }

    [Fact]
    public void A_semicolon_anywhere_in_the_local_path_blocks_the_upload()
    {
        var ex = Should.Throw<ArgumentException>(
            () => PathSafety.ResolveDestination(
                @"C:\Data",
                @"C:\Data\batch;2\sample.raw",
                Destination));

        ex.Message.ShouldContain("semicolon");
    }
}
