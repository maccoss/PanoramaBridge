using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.WebDav;

/// <summary>Transport facts checked against a real LabKey server before a release.</summary>
public sealed class LiveWebDavContractTests
{
    private const string UrlVariable = "PANORAMABRIDGE_IT_URL";
    private const string KeyVariable = "PANORAMABRIDGE_IT_APIKEY";
    private const string PathVariable = "PANORAMABRIDGE_IT_PATH";

    [SkippableFact]
    public async Task A_recursive_upload_keeps_its_server_hash_and_acquisition_time()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        var configuredPath = Environment.GetEnvironmentVariable(PathVariable);

        Skip.If(
            string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(configuredPath),
            $"Set {UrlVariable}, {KeyVariable}, and {PathVariable} to run live WebDAV contracts.");

        var options = new WebDavClientOptions
        {
            BaseAddress = new Uri(url!, UriKind.Absolute),
            Credential = PanoramaCredential.ApiKey(key!),
        };

        using var http = options.CreateHttpClient();
        var client = new WebDavClient(http, options);
        var local = Directory.CreateTempSubdirectory("pb-webdav-contract-");

        var run = RemotePath
            .Parse(configuredPath!)
            .AsCollection()
            .Append("pb-contract-" + Guid.NewGuid().ToString("n"), isCollection: true);
        var nested = run.Append("nested", isCollection: true);
        var destination = nested.Append("acquired.raw");
        var source = Path.Combine(local.FullName, "acquired.raw");
        var acquired = DateTimeOffset.UtcNow.AddHours(-1);

        await File.WriteAllTextAsync(source, "PanoramaBridge live WebDAV contract.");

        try
        {
            // LabKey's MKCOL is single-level. Ensuring this nested collection proves the client
            // creates missing ancestors rather than relying on a server-specific recursive call.
            await client.EnsureCollectionAsync(nested);

            var result = await client.UploadAsync(source, destination, lastModified: acquired);
            var serverHash = await client.GetFileHashAsync(destination);
            var stored = await client.GetResourceAsync(destination);

            serverHash.ShouldBe(result.Hashes.Md5, "the server must hash the bytes it stored");
            stored.ShouldNotBeNull();
            stored!.Length.ShouldBe(result.BytesUploaded);
            stored.LastModifiedUtc.ShouldNotBeNull();
            (stored.LastModifiedUtc!.Value - acquired).Duration()
                .ShouldBeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                await client.DeleteAsync(destination);
                await client.DeleteAsync(nested);
                await client.DeleteAsync(run);
            }
            finally
            {
                local.Delete(recursive: true);
            }
        }
    }
}