using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.WebDav;

/// <summary>
/// Every URL the application builds goes through RemotePath, and URL construction is where the
/// Python implementation's worst bug lived: two code paths joined URLs differently, so upload
/// checksums were written to one location and read from another and verification silently
/// degraded forever. These tests are the guard rail for that.
/// </summary>
public sealed class RemotePathTests
{
    private static readonly Uri PanoramaBase = new("https://panoramaweb.org");

    [Fact]
    public void A_typical_panorama_path_round_trips()
    {
        var path = RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/");

        path.IsCollection.ShouldBeTrue();
        path.Name.ShouldBe("@files");
        path.ToEncodedString().ShouldBe("/_webdav/MacCoss/maccoss/@files/");
    }

    [Fact]
    public void The_at_sign_is_left_unencoded()
    {
        // LabKey names its file roots @files and @pipeline and emits them unencoded in its own
        // hrefs. Uri.EscapeDataString would turn this into %40, which is why encoding is done
        // by hand. Both forms resolve on the server; matching the server keeps logs readable.
        RemotePath.EncodeSegment("@files").ShouldBe("@files");
    }

    [Theory]
    [InlineData("plain.raw", "plain.raw")]
    [InlineData("with space.raw", "with%20space.raw")]
    [InlineData("hash#sign.raw", "hash%23sign.raw")]
    [InlineData("question?mark.raw", "question%3Fmark.raw")]
    [InlineData("percent%25.raw", "percent%2525.raw")]
    [InlineData("plus+sign.raw", "plus%2Bsign.raw")]
    [InlineData("amp&ersand.raw", "amp%26ersand.raw")]
    [InlineData("semi;colon.raw", "semi%3Bcolon.raw")]
    [InlineData("apostrophe's.raw", "apostrophe%27s.raw")]
    [InlineData("café.raw", "caf%C3%A9.raw")]
    [InlineData("dash-dot.under_tilde~.raw", "dash-dot.under_tilde~.raw")]
    public void Segments_encode_conservatively(string decoded, string encoded)
    {
        RemotePath.EncodeSegment(decoded).ShouldBe(encoded);
    }

    [Fact]
    public void Encoding_is_never_applied_twice()
    {
        // Parsing accepts either form, so a value that has already been through the wire does
        // not come back double-escaped.
        var fromDecoded = RemotePath.Parse("/a/with space.raw");
        var fromEncoded = RemotePath.Parse("/a/with%20space.raw");

        fromEncoded.ShouldBe(fromDecoded);
        fromEncoded.ToEncodedString().ShouldBe("/a/with%20space.raw");
        fromEncoded.Name.ShouldBe("with space.raw");
    }

    [Theory]
    [InlineData("with space.raw")]
    [InlineData("hash#sign.raw")]
    [InlineData("percent%.raw")]
    [InlineData("plus+sign.raw")]
    [InlineData("café.raw")]
    [InlineData("@files")]
    public void The_framework_does_not_renormalize_what_we_built(string name)
    {
        // Uri can silently rewrite a path it considers non-canonical. If that ever happens the
        // request would go somewhere other than where the code believes, so it is asserted
        // rather than assumed.
        var built = RemotePath.Parse("/_webdav/MacCoss").Append(name).ToUri(PanoramaBase);

        built.AbsoluteUri.ShouldBe(
            "https://panoramaweb.org/_webdav/MacCoss/" + RemotePath.EncodeSegment(name));
    }

    [Fact]
    public void A_base_url_with_its_own_path_is_preserved()
    {
        // new Uri(baseUri, "/absolute") DISCARDS the base path. That overload is the trap the
        // Python version fell into, and it is why ToUri concatenates explicitly.
        var deployedUnderPrefix = new Uri("https://host.example.org/labkey");

        RemotePath.Parse("/_webdav/Project/@files/x.raw")
            .ToUri(deployedUnderPrefix)
            .AbsoluteUri
            .ShouldBe("https://host.example.org/labkey/_webdav/Project/@files/x.raw");
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        RemotePath.Parse("/_webdav/x.raw")
            .ToUri(new Uri("https://panoramaweb.org/"))
            .AbsoluteUri
            .ShouldBe("https://panoramaweb.org/_webdav/x.raw");
    }

    [Fact]
    public void Collections_keep_a_trailing_slash_and_files_do_not()
    {
        RemotePath.Parse("/a/b/").ToEncodedString().ShouldBe("/a/b/");
        RemotePath.Parse("/a/b").ToEncodedString().ShouldBe("/a/b");
        RemotePath.Parse("/a/b").AsCollection().ToEncodedString().ShouldBe("/a/b/");
    }

    [Fact]
    public void A_collection_and_a_file_with_the_same_segments_are_not_equal()
    {
        RemotePath.Parse("/a/b/").ShouldNotBe(RemotePath.Parse("/a/b"));
    }

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("/../etc/passwd")]
    [InlineData("a/b/..")]
    public void Parent_traversal_is_rejected_rather_than_resolved(string path)
    {
        // Collapsing '..' silently would let a crafted relative path escape the configured
        // upload folder. Refusing it is the only safe reading.
        Should.Throw<ArgumentException>(() => RemotePath.Parse(path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Append_rejects_anything_that_is_not_a_single_segment(string segment)
    {
        Should.Throw<ArgumentException>(() => RemotePath.Parse("/base").Append(segment));
    }

    [Fact]
    public void Backslashes_are_normalized_so_windows_relative_paths_work()
    {
        // Local relative paths arrive as "sub\dir\file.raw" on Windows.
        RemotePath.Parse("sub\\dir\\file.raw").ToEncodedString().ShouldBe("/sub/dir/file.raw");
    }

    [Fact]
    public void Empty_and_dot_segments_are_dropped()
    {
        RemotePath.Parse("/a//b/./c").ToEncodedString().ShouldBe("/a/b/c");
    }

    [Fact]
    public void The_root_is_its_own_parent_so_walking_up_terminates()
    {
        var path = RemotePath.Parse("/a/b/c");

        path.Parent.ToEncodedString().ShouldBe("/a/b/");
        path.Parent.Parent.ToEncodedString().ShouldBe("/a/");
        path.Parent.Parent.Parent.ShouldBe(RemotePath.Root);
        RemotePath.Root.Parent.ShouldBe(RemotePath.Root);
        RemotePath.Root.ToEncodedString().ShouldBe("/");
    }

    [Fact]
    public void Containment_compares_segments_not_string_prefixes()
    {
        var destination = RemotePath.Parse("/_webdav/MacCoss/data/run1/");

        // The naive string-prefix check would wrongly accept this one.
        RemotePath.Parse("/_webdav/MacCoss/data/run10/x.raw")
            .IsUnder(destination)
            .ShouldBeFalse();

        RemotePath.Parse("/_webdav/MacCoss/data/run1/x.raw")
            .IsUnder(destination)
            .ShouldBeTrue();
    }

    [Fact]
    public void A_joined_relative_path_always_stays_under_its_base()
    {
        var destination = RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

        var joined = destination.Append(RemotePath.Parse("2026/run 7/data.raw"));

        joined.IsUnder(destination).ShouldBeTrue();
        joined.ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/2026/run%207/data.raw");
    }

    [Fact]
    public void Segments_from_a_local_relative_path_are_validated()
    {
        RemotePath.FromSegments(["2026", "run 7", "data.raw"])
            .ToEncodedString()
            .ShouldBe("/2026/run%207/data.raw");

        Should.Throw<ArgumentException>(() => RemotePath.FromSegments(["ok", ".."]));
    }

    [Fact]
    public void Equal_paths_hash_equally_so_they_work_as_dictionary_keys()
    {
        // The created-collections cache is keyed on RemotePath.
        var a = RemotePath.Parse("/_webdav/a/b/");
        var b = RemotePath.Parse("/_webdav/a/b/");

        a.GetHashCode().ShouldBe(b.GetHashCode());
        new HashSet<RemotePath> { a, b }.Count.ShouldBe(1);
    }
}
