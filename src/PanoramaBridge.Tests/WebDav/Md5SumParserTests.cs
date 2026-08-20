using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.WebDav;

/// <summary>
/// The md5sum response is the whole basis of upload verification, so the fixtures here are
/// bodies captured verbatim from panoramaweb.org rather than invented examples.
/// </summary>
public sealed class Md5SumParserTests
{
    /// <summary>Captured from <c>/_webdav/home/@files/ASMS_2019_PanoramaPublic.pdf?method=md5sum</c>.</summary>
    private const string SingleFileResponse =
        "558fb082796f8ff111b6e2f2f3c3356c *ASMS_2019_PanoramaPublic.pdf\n";

    /// <summary>Captured from <c>/_webdav/home/@files?method=md5sum</c>.</summary>
    private const string CollectionResponse =
        "d41d8cd98f00b204e9800998ecf8427e *.nocrawl\n"
        + "6a32a24e4ad2653574029ff64e084d66 *.upload.log\n"
        + "a935b04c6f6dfc81a1e1b343d25f9bf0 *2012-ASMS-Panorama_poster_small.pdf\n"
        + "558fb082796f8ff111b6e2f2f3c3356c *ASMS_2019_PanoramaPublic.pdf\n"
        + "89c1acd7fca191af78e4b1824c3fc3ed *Panorama (Webinar 2014-08-19).pdf\n"
        + "8d4878fbde9c128cb5779e7c92396e52 *PanoramaSharing.zip\n";

    [Fact]
    public void A_single_file_response_yields_its_hash()
    {
        Md5SumParser.ParseSingle(SingleFileResponse)
            .ShouldBe("558fb082796f8ff111b6e2f2f3c3356c");
    }

    [Fact]
    public void A_collection_response_yields_every_file()
    {
        var hashes = Md5SumParser.Parse(CollectionResponse);

        hashes.Count.ShouldBe(6);
        hashes["ASMS_2019_PanoramaPublic.pdf"].ShouldBe("558fb082796f8ff111b6e2f2f3c3356c");
        hashes[".nocrawl"].ShouldBe("d41d8cd98f00b204e9800998ecf8427e");
    }

    [Fact]
    public void Names_with_spaces_and_punctuation_survive_intact()
    {
        // Names are emitted verbatim, so everything after the separator is the name. Splitting
        // on whitespace would truncate this one after "Panorama".
        Md5SumParser.Parse(CollectionResponse)
            .Keys.ShouldContain("Panorama (Webinar 2014-08-19).pdf");
    }

    [Theory]
    [InlineData("with space.raw")]
    [InlineData("at@sign.raw")]
    [InlineData("hash#sign.raw")]
    [InlineData("paren(s).raw")]
    [InlineData("apostrophe's.raw")]
    [InlineData("café.raw")]
    [InlineData("plus+sign.raw")]
    [InlineData("amp&ersand.raw")]
    [InlineData("star*name.raw")]
    public void Every_name_shape_the_server_emits_is_read_back_unchanged(string name)
    {
        // Each of these was round-tripped through a real upload; the server reports them raw,
        // with no percent-encoding and no GNU-style backslash escaping.
        var hashes = Md5SumParser.Parse($"1b234f2ba0a6ac3f3a0603acb23a4b57 *{name}\n");

        hashes.Keys.ShouldHaveSingleItem().ShouldBe(name);
    }

    [Fact]
    public void The_hash_is_normalized_to_lower_case()
    {
        Md5SumParser.ParseSingle("558FB082796F8FF111B6E2F2F3C3356C *x.raw")
            .ShouldBe("558fb082796f8ff111b6e2f2f3c3356c");
    }

    [Theory]
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c *x.raw")]          // no trailing newline
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c *x.raw\n")]        // LF
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c *x.raw\r\n")]      // CRLF
    [InlineData("\n558fb082796f8ff111b6e2f2f3c3356c *x.raw\n\n")]    // blank lines around it
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c  x.raw")]          // text mode: two spaces
    public void Line_ending_and_separator_variations_are_tolerated(string body)
    {
        var hashes = Md5SumParser.Parse(body);

        hashes.Count.ShouldBe(1);
        hashes["x.raw"].ShouldBe("558fb082796f8ff111b6e2f2f3c3356c");
    }

    [Fact]
    public void An_empty_response_is_an_empty_result_not_an_error()
    {
        // An empty collection is a legitimate state, not a failure.
        Md5SumParser.Parse(string.Empty).ShouldBeEmpty();
        Md5SumParser.ParseSingle(string.Empty).ShouldBeNull();
    }

    [Theory]
    [InlineData("<html><body>Login required</body></html>")]
    [InlineData("not a hash *x.raw")]
    [InlineData("558fb082 *too-short-hash.raw")]
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c*missing-space.raw")]
    [InlineData("558fb082796f8ff111b6e2f2f3c3356c *")]
    public void A_body_that_is_not_an_md5sum_response_is_rejected(string body)
    {
        // Silently returning nothing would let verification pass on a login page.
        Should.Throw<FormatException>(() => Md5SumParser.Parse(body));
    }

    [Fact]
    public void Names_are_compared_case_sensitively()
    {
        // The server is case-sensitive even though the Windows file system the files came from
        // is not; folding case here would let two distinct remote files collide.
        var hashes = Md5SumParser.Parse(
            "1b234f2ba0a6ac3f3a0603acb23a4b57 *Sample.raw\n"
            + "28c80cc302a93fe85a75f97c675b4ea8 *sample.raw\n");

        hashes.Count.ShouldBe(2);
    }

    [Fact]
    public void Verification_compares_the_whole_name_set_not_just_hashes()
    {
        // This is the shape the verification step relies on: a missing name, an extra name, or
        // a changed hash must each be detectable. It is what would catch a file the server
        // stored under a truncated name.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run1.raw"] = "1b234f2ba0a6ac3f3a0603acb23a4b57",
            ["run2.raw"] = "28c80cc302a93fe85a75f97c675b4ea8",
        };

        var actual = Md5SumParser.Parse("1b234f2ba0a6ac3f3a0603acb23a4b57 *run1.raw\n");

        actual.Keys.ShouldNotBe(expected.Keys);
        expected.Keys.Except(actual.Keys).ShouldBe(["run2.raw"]);
    }
}
