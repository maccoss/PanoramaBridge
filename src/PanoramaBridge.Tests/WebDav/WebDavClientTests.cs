using System.Net;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.WebDav;

public sealed class WebDavClientTests : IDisposable
{
    private static readonly RemotePath Files =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/");

    private readonly List<(string Method, string Url)> _seen = [];
    private readonly string _tempFile = Path.Combine(
        Path.GetTempPath(),
        "pb-upload-" + Guid.NewGuid().ToString("n")[..8] + ".raw");

    /// <summary>
    /// Builds a client whose handler answers from a scripted sequence of responses, recording
    /// every request so the exact call pattern can be asserted.
    /// </summary>
    private WebDavClient ClientFor(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            _seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(respond(request, callCount++));
        });

        var options = new WebDavClientOptions
        {
            BaseAddress = new Uri("https://panoramaweb.org"),
            Credential = PanoramaCredential.ApiKey("test-key"),
            // Keep retry backoff from making the suite slow.
            MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        };

        return new WebDavClient(handler.CreateClient(), options);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    // -- Authentication ---------------------------------------------------------------------

    [Fact]
    public void An_api_key_authenticates_as_the_apikey_user()
    {
        var header = PanoramaCredential.ApiKey("abc123").ToAuthenticationHeader();

        header.Scheme.ShouldBe("Basic");
        System.Text.Encoding.UTF8
            .GetString(Convert.FromBase64String(header.Parameter!))
            .ShouldBe("apikey:abc123");
    }

    [Theory]
    [InlineData("apikey|abc123", "apikey:abc123")]
    [InlineData("  abc123  ", "apikey:abc123")]
    public void A_pasted_key_is_cleaned_up(string pasted, string expectedPair)
    {
        // Users paste whatever Panorama gave them, sometimes with the historical prefix or
        // stray whitespace from the clipboard.
        var header = PanoramaCredential.ApiKey(pasted).ToAuthenticationHeader();

        System.Text.Encoding.UTF8
            .GetString(Convert.FromBase64String(header.Parameter!))
            .ShouldBe(expectedPair);
    }

    [Fact]
    public void A_credential_never_reveals_its_secret_when_described()
    {
        // This string is safe to log; the secret is not.
        PanoramaCredential.ApiKey("super-secret").ToString().ShouldBe("API key");
        PanoramaCredential.UserNameAndPassword("mriffle", "hunter2").ToString()
            .ShouldBe("user mriffle");
    }

    // -- Recursive MKCOL --------------------------------------------------------------------

    [Fact]
    public async Task A_nested_collection_is_created_one_level_at_a_time()
    {
        // The server's MKCOL is single-level: creating a/b/c in one call returns 409. The
        // client has to walk up, create the ancestors, then retry.
        var existing = new HashSet<string> { "/_webdav/MacCoss/maccoss/@files/" };

        var client = ClientFor((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method.Method != "MKCOL")
            {
                return Status(HttpStatusCode.OK);
            }

            var parent = path[..(path.TrimEnd('/').LastIndexOf('/') + 1)];
            if (!existing.Contains(parent))
            {
                return Status(HttpStatusCode.Conflict);
            }

            return existing.Add(path)
                ? Status(HttpStatusCode.Created)
                : Status(HttpStatusCode.MethodNotAllowed);
        });

        await client.EnsureCollectionAsync(Files.Append("a").Append("b").Append("c", true));

        var mkcols = _seen.Where(s => s.Method == "MKCOL").Select(s => s.Url).ToList();

        // First attempt at the deepest level 409s, then a/, a/b/, and finally a/b/c again.
        mkcols[0].ShouldEndWith("/a/b/c/");
        mkcols.ShouldContain(u => u.EndsWith("/a/"));
        mkcols.ShouldContain(u => u.EndsWith("/a/b/"));
        mkcols[^1].ShouldEndWith("/a/b/c/");
    }

    [Fact]
    public async Task An_existing_collection_reports_405_and_that_counts_as_success()
    {
        var client = ClientFor((_, _) => Status(HttpStatusCode.MethodNotAllowed));

        await Should.NotThrowAsync(() => client.EnsureCollectionAsync(Files.Append("existing", true)));
    }

    [Fact]
    public async Task A_collection_is_only_created_once_per_client()
    {
        // A batch of uploads into one folder must not re-issue MKCOL for every file.
        var client = ClientFor((_, _) => Status(HttpStatusCode.Created));
        var target = Files.Append("batch", true);

        await client.EnsureCollectionAsync(target);
        await client.EnsureCollectionAsync(target);
        await client.EnsureCollectionAsync(target);

        _seen.Count(s => s.Method == "MKCOL").ShouldBe(1);
    }

    [Fact]
    public async Task No_permission_to_create_a_folder_surfaces_as_a_typed_failure()
    {
        // The Python version returned a bare bool here, which left the folder browser reading
        // its own log file back off disk to work out what had gone wrong.
        var client = ClientFor((_, _) => Status(HttpStatusCode.Forbidden));

        var ex = await Should.ThrowAsync<WebDavException>(
            () => client.EnsureCollectionAsync(Files.Append("nope", true)));

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ex.Method.ShouldBe("MKCOL");
        ex.ToUserMessage().ShouldContain("administrator");
    }

    // -- Retry policy -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_transient_status_is_retried_until_it_succeeds(HttpStatusCode transient)
    {
        var client = ClientFor((_, call) => call < 2
            ? Status(transient)
            : Status(HttpStatusCode.OK, """{"files":[]}"""));

        await client.ListAsync(Files);

        _seen.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    public async Task A_permanent_failure_is_not_retried(HttpStatusCode permanent)
    {
        // Retrying a rejected credential or an oversized file just wastes the server's time.
        var client = ClientFor((_, _) => Status(permanent));

        await Should.ThrowAsync<WebDavException>(() => client.ListAsync(Files));

        _seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var client = ClientFor((_, _) => Status(HttpStatusCode.ServiceUnavailable));

        await Should.ThrowAsync<WebDavException>(() => client.ListAsync(Files));

        _seen.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Cancelling_stops_immediately_rather_than_retrying()
    {
        using var cts = new CancellationTokenSource();
        var client = ClientFor((_, _) =>
        {
            cts.Cancel();
            return Status(HttpStatusCode.ServiceUnavailable);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => client.ListAsync(Files, cts.Token));

        _seen.Count.ShouldBe(1);
    }

    // -- Listing and hashing ----------------------------------------------------------------

    [Fact]
    public async Task A_listing_uses_the_json_method_rather_than_propfind()
    {
        var client = ClientFor((_, _) => Status(HttpStatusCode.OK, """{"files":[]}"""));

        await client.ListAsync(Files);

        _seen[0].Method.ShouldBe("GET");
        _seen[0].Url.ShouldEndWith("?method=json");
        _seen[0].Url.ShouldContain("/@files/");
    }

    [Fact]
    public async Task A_collection_hash_request_asks_for_md5sum()
    {
        var client = ClientFor((_, _) => Status(
            HttpStatusCode.OK,
            "1b234f2ba0a6ac3f3a0603acb23a4b57 *run1.raw\n"));

        var hashes = await client.GetCollectionHashesAsync(Files);

        _seen[0].Url.ShouldEndWith("?method=md5sum");
        hashes["run1.raw"].ShouldBe("1b234f2ba0a6ac3f3a0603acb23a4b57");
    }

    [Fact]
    public async Task A_missing_file_has_no_hash_rather_than_raising()
    {
        var client = ClientFor((_, _) => Status(HttpStatusCode.NotFound));

        (await client.GetFileHashAsync(Files.Append("absent.raw"))).ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_something_already_gone_is_not_an_error()
    {
        // The desired end state is "not there", which it already is.
        var client = ClientFor((_, _) => Status(HttpStatusCode.NotFound));

        await Should.NotThrowAsync(() => client.DeleteAsync(Files.Append("gone.raw")));
    }

    // -- Upload -----------------------------------------------------------------------------

    [Fact]
    public async Task An_upload_streams_the_file_and_returns_its_hashes()
    {
        var payload = "PanoramaBridge upload test payload"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFile, payload);

        var client = ClientFor((_, _) => Status(HttpStatusCode.Created));

        var result = await client.UploadAsync(_tempFile, Files.Append("sample.raw"));

        result.BytesUploaded.ShouldBe(payload.Length);
        result.Hashes.Md5.ShouldBe(Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(payload)).ToLowerInvariant());
        result.Hashes.Sha256.ShouldBe(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant());
    }

    [Fact]
    public async Task An_upload_sends_a_content_length_rather_than_chunking()
    {
        // A real Content-Length is what lets the server reject an upload up front, and it is
        // what makes Expect: 100-continue meaningful.
        await File.WriteAllBytesAsync(_tempFile, new byte[4096]);

        long? observed = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            observed = request.Content?.Headers.ContentLength;
            return Task.FromResult(Status(HttpStatusCode.Created));
        });

        var client = new WebDavClient(
            handler.CreateClient(),
            new WebDavClientOptions
            {
                BaseAddress = new Uri("https://panoramaweb.org"),
                Credential = PanoramaCredential.ApiKey("k"),
            });

        await client.UploadAsync(_tempFile, Files.Append("sized.raw"));

        observed.ShouldBe(4096);
    }

    [Fact]
    public async Task A_retried_upload_re_reads_the_file_and_still_hashes_correctly()
    {
        // HttpContent is single-use and the stream has already been consumed, so a retry has
        // to rebuild both. Getting this wrong yields a truncated upload or a wrong hash.
        var payload = "retry me"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFile, payload);

        // Fail only the first PUT. Keyed on the method because an upload issues a MKCOL for
        // the destination folder first, and a call-index would make that request absorb the
        // failure instead.
        var puts = 0;
        var client = ClientFor((request, _) =>
            request.Method == HttpMethod.Put && puts++ == 0
                ? Status(HttpStatusCode.ServiceUnavailable)
                : Status(HttpStatusCode.Created));

        var result = await client.UploadAsync(_tempFile, Files.Append("sample.raw"));

        _seen.Count(s => s.Method == "PUT").ShouldBe(2);
        result.BytesUploaded.ShouldBe(payload.Length);
        result.Hashes.Md5.ShouldBe(Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(payload)).ToLowerInvariant());
    }

    [Fact]
    public async Task A_semicolon_in_the_destination_blocks_the_upload_before_any_request()
    {
        // Panorama would truncate the name and could silently overwrite another file, so this
        // must never reach the network.
        await File.WriteAllBytesAsync(_tempFile, new byte[16]);

        var client = ClientFor((_, _) => Status(HttpStatusCode.Created));

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => client.UploadAsync(_tempFile, Files.Append("run;rep1.raw")));

        ex.Message.ShouldContain("semicolon");
        _seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_upload_creates_the_destination_folder_first()
    {
        await File.WriteAllBytesAsync(_tempFile, new byte[16]);

        var client = ClientFor((_, _) => Status(HttpStatusCode.Created));

        await client.UploadAsync(_tempFile, Files.Append("newfolder", true).Append("s.raw"));

        _seen[0].Method.ShouldBe("MKCOL");
        _seen[^1].Method.ShouldBe("PUT");
    }

    // -- Capabilities -----------------------------------------------------------------------

    [Fact]
    public async Task Capabilities_report_whether_atomic_publish_is_possible()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            response.Headers.Add("DAV", "1,2");

            // Allow is a *content* header in .NET, which is why the client reads
            // Content.Headers.Allow before falling back to the response headers.
            foreach (var verb in new[]
                     {
                         "OPTIONS", "GET", "HEAD", "COPY", "DELETE", "MOVE",
                         "LOCK", "UNLOCK", "PROPFIND", "POST", "PUT", "MKCOL",
                     })
            {
                response.Content.Headers.Allow.Add(verb);
            }
            return Task.FromResult(response);
        });

        var client = new WebDavClient(
            handler.CreateClient(),
            new WebDavClientOptions
            {
                BaseAddress = new Uri("https://panoramaweb.org"),
                Credential = PanoramaCredential.ApiKey("k"),
            });

        var capabilities = await client.GetCapabilitiesAsync(Files);

        capabilities.DavCompliance.ShouldBe("1,2");
        capabilities.Allows("MOVE").ShouldBeTrue();
        capabilities.Allows("MKCOL").ShouldBeTrue();
        capabilities.SupportsAtomicPublish.ShouldBeTrue();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
