using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.WebDav;

/// <summary>
/// Fixtures captured verbatim from panoramaweb.org's <c>?method=json</c>.
/// </summary>
public sealed class MethodJsonParserTests
{
    private static readonly RemotePath Parent = RemotePath.Parse("/_webdav/home/@files/");

    /// <summary>
    /// Captured from <c>/_webdav/home/@files?method=json</c>: one read-only collection and one
    /// read-only file, which is what an unauthenticated public folder looks like.
    /// </summary>
    private const string PublicListing = """
    {"files":[
      {"id":"/_webdav/home/%40files/Slides","href":"/_webdav/home/%40files/Slides/",
       "text":"Slides","iconHref":"/_icons/folder.gif",
       "options":"OPTIONS, GET, HEAD, COPY, LOCK, UNLOCK, PROPFIND",
       "canDelete":false,"canRename":false,"canEdit":false,"canUpload":false,"canRead":true,
       "creationdate":"2023-03-08T07:26:02-00:00","createdby":"Vagisha Sharma",
       "collection":true,"leaf":false},
      {"id":"/_webdav/home/%40files/2012-ASMS-Panorama_poster_small.pdf",
       "href":"/_webdav/home/%40files/2012-ASMS-Panorama_poster_small.pdf",
       "text":"2012-ASMS-Panorama_poster_small.pdf","iconHref":"/_icons/pdf.gif",
       "options":"OPTIONS, GET, HEAD, COPY, LOCK, UNLOCK, PROPFIND",
       "canDelete":false,"canRename":false,"canEdit":false,"canUpload":false,"canRead":true,
       "creationdate":"2013-02-01T05:07:39-00:00","createdby":"Vagisha Sharma",
       "collection":false,"lastmodified":"2013-02-01T05:07:39-00:00",
       "contentlength":8185763,"size":8185763,"contenttype":"application/pdf",
       "etag":"W/\"8185763-1359695259000\"","leaf":true}
    ]}
    """;

    /// <summary>
    /// Captured from the authenticated MacCoss folder: the full verb set and every permission
    /// granted, which is what a writable destination looks like.
    /// </summary>
    private const string WritableListing = """
    {"files":[
      {"text":"RawFiles",
       "options":"OPTIONS, GET, HEAD, COPY, DELETE, MOVE, LOCK, UNLOCK, PROPFIND, POST, PUT, MKCOL",
       "canDelete":true,"canRename":true,"canEdit":true,"canUpload":true,"canRead":true,
       "collection":true,"leaf":false}
    ]}
    """;

    [Fact]
    public void A_collection_and_a_file_are_distinguished()
    {
        var resources = MethodJsonParser.Parse(PublicListing, Parent);

        resources.Count.ShouldBe(2);

        var folder = resources[0];
        folder.Name.ShouldBe("Slides");
        folder.IsCollection.ShouldBeTrue();
        folder.Length.ShouldBe(0);

        var file = resources[1];
        file.Name.ShouldBe("2012-ASMS-Panorama_poster_small.pdf");
        file.IsCollection.ShouldBeFalse();
        file.Length.ShouldBe(8_185_763);
        file.ContentType.ShouldBe("application/pdf");
        file.ETag.ShouldBe("W/\"8185763-1359695259000\"");
        file.CreatedBy.ShouldBe("Vagisha Sharma");
    }

    [Fact]
    public void Paths_are_rebuilt_rather_than_taken_from_the_server_href()
    {
        // The server's href arrives percent-encoded (%40files). Re-deriving the path keeps
        // every path in the application flowing through one construction route, which is the
        // whole reason RemotePath exists.
        var resources = MethodJsonParser.Parse(PublicListing, Parent);

        resources[0].Path.ToEncodedString().ShouldBe("/_webdav/home/@files/Slides/");
        resources[1].Path.ToEncodedString()
            .ShouldBe("/_webdav/home/@files/2012-ASMS-Panorama_poster_small.pdf");
    }

    [Fact]
    public void A_read_only_folder_reports_that_it_cannot_be_uploaded_to()
    {
        // This is what lets the remote browser grey out a folder before the user picks it,
        // rather than surfacing a 403 at the end of a long transfer.
        var folder = MethodJsonParser.Parse(PublicListing, Parent)[0];

        folder.Permissions.CanRead.ShouldBeTrue();
        folder.Permissions.CanUpload.ShouldBeFalse();
        folder.Permissions.Allows("PUT").ShouldBeFalse();
        folder.Permissions.Allows("MKCOL").ShouldBeFalse();
        folder.Permissions.SupportsAtomicPublish.ShouldBeFalse();
    }

    [Fact]
    public void A_writable_folder_reports_the_full_verb_set()
    {
        var folder = MethodJsonParser.Parse(WritableListing, Parent)[0];

        folder.Permissions.CanUpload.ShouldBeTrue();
        folder.Permissions.CanRename.ShouldBeTrue();
        folder.Permissions.Allows("PUT").ShouldBeTrue();
        folder.Permissions.Allows("MKCOL").ShouldBeTrue();
        folder.Permissions.Allows("MOVE").ShouldBeTrue();

        // MOVE plus upload is what the temp-name-then-rename publish strategy needs.
        folder.Permissions.SupportsAtomicPublish.ShouldBeTrue();
    }

    [Fact]
    public void Allowed_methods_are_matched_without_regard_to_case()
    {
        var folder = MethodJsonParser.Parse(WritableListing, Parent)[0];

        folder.Permissions.Allows("move").ShouldBeTrue();
        folder.Permissions.Allows("Move").ShouldBeTrue();
    }

    [Fact]
    public void Timestamps_are_read_as_utc()
    {
        var file = MethodJsonParser.Parse(PublicListing, Parent)[1];

        file.LastModifiedUtc.ShouldNotBeNull();
        file.LastModifiedUtc!.Value.Year.ShouldBe(2013);
        file.LastModifiedUtc.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_collection_falls_back_to_its_creation_date_when_it_has_no_modified_time()
    {
        MethodJsonParser.Parse(PublicListing, Parent)[0]
            .LastModifiedUtc.ShouldNotBeNull()
            .Year.ShouldBe(2023);
    }

    [Fact]
    public void An_empty_listing_is_valid()
    {
        MethodJsonParser.Parse("""{"files":[]}""", Parent).ShouldBeEmpty();
    }

    [Fact]
    public void A_login_page_instead_of_json_is_reported_as_an_authentication_problem()
    {
        // A LabKey session timeout answers with HTML. Treating that as "empty folder" would
        // make the app conclude nothing had been uploaded yet.
        var ex = Should.Throw<FormatException>(() => MethodJsonParser.Parse(
            "<html><body>Sign In</body></html>", Parent));

        ex.Message.ShouldContain("authenticated");
    }

    [Fact]
    public void Json_without_a_files_array_is_rejected()
    {
        Should.Throw<FormatException>(() => MethodJsonParser.Parse("""{"rows":[]}""", Parent));
    }

    [Fact]
    public void Missing_optional_fields_do_not_throw()
    {
        // Not every deployment populates every field, and a listing must not fail because one
        // is absent.
        var resources = MethodJsonParser.Parse("""{"files":[{"text":"bare.raw","leaf":true}]}""", Parent);

        var only = resources.ShouldHaveSingleItem();
        only.Name.ShouldBe("bare.raw");
        only.IsCollection.ShouldBeFalse();
        only.Length.ShouldBe(0);
        only.ETag.ShouldBeNull();
        only.Permissions.CanUpload.ShouldBeFalse();
    }

    [Fact]
    public void Collection_is_inferred_from_leaf_when_it_is_absent()
    {
        MethodJsonParser.Parse("""{"files":[{"text":"folder","leaf":false}]}""", Parent)
            .ShouldHaveSingleItem()
            .IsCollection.ShouldBeTrue();
    }

    [Fact]
    public void Entries_without_a_name_are_skipped()
    {
        MethodJsonParser.Parse("""{"files":[{"text":""},{"text":"real.raw","leaf":true}]}""", Parent)
            .ShouldHaveSingleItem()
            .Name.ShouldBe("real.raw");
    }

    [Fact]
    public void The_options_string_is_split_into_verbs()
    {
        MethodJsonParser.SplitMethods("OPTIONS, GET, HEAD, PUT, MKCOL")
            .ShouldBe(["OPTIONS", "GET", "HEAD", "PUT", "MKCOL"]);

        MethodJsonParser.SplitMethods(null).ShouldBeEmpty();
        MethodJsonParser.SplitMethods("  ").ShouldBeEmpty();
    }
}
