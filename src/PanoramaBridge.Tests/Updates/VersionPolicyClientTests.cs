using System.Net;
using PanoramaBridge.Core.Updates;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Updates;

/// <summary>
/// The version floor is the mechanism that keeps stale builds from uploading, so its decision
/// table is worth pinning down precisely -- especially the fail-open paths. Blocking a lab's
/// instrument PC because GitHub was briefly unreachable would be far worse than letting it run
/// a slightly old build.
/// </summary>
public sealed class VersionPolicyClientTests
{
    private static VersionPolicyClient ClientWith(HttpMessageHandler handler) =>
        new(new HttpClient(handler, disposeHandler: false));

    private static VersionPolicyClient OfflineClient() =>
        ClientWith(StubHttpMessageHandler.Throwing(new HttpRequestException("no network")));

    [Theory]
    [InlineData("1.1.0", "1.2.0")]
    [InlineData("0.9.0", "1.0.0")]
    [InlineData("1.2.0", "1.2.1")]
    public void Build_below_the_floor_blocks_uploads(string current, string minimum)
    {
        var result = OfflineClient().Evaluate(
            $$"""{"minimumSupportedVersion": "{{minimum}}"}""",
            Version.Parse(current));

        result.Status.ShouldBe(VersionPolicyStatus.BelowMinimum);
        result.UploadsBlocked.ShouldBeTrue();
        result.MinimumSupportedVersion.ShouldBe(Version.Parse(minimum));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.3.0", "1.2.0")]
    [InlineData("2.0.0", "1.9.9")]
    public void Build_at_or_above_the_floor_is_allowed(string current, string minimum)
    {
        var result = OfflineClient().Evaluate(
            $$"""{"minimumSupportedVersion": "{{minimum}}"}""",
            Version.Parse(current));

        result.Status.ShouldBe(VersionPolicyStatus.Satisfied);
        result.UploadsBlocked.ShouldBeFalse();
    }

    [Fact]
    public void A_policy_with_no_floor_is_the_healthy_default()
    {
        var result = OfflineClient().Evaluate(
            """{"minimumSupportedVersion": null, "message": null}""",
            new Version(1, 0, 0));

        result.Status.ShouldBe(VersionPolicyStatus.Satisfied);
        result.UploadsBlocked.ShouldBeFalse();
        result.MinimumSupportedVersion.ShouldBeNull();
    }

    [Fact]
    public void The_operator_message_is_carried_through_to_the_user()
    {
        const string Message = "1.2.0 fixes a verification bug. Please update before uploading.";

        var result = OfflineClient().Evaluate(
            $$"""{"minimumSupportedVersion": "1.2.0", "message": "{{Message}}"}""",
            new Version(1, 1, 0));

        result.UploadsBlocked.ShouldBeTrue();
        result.Message.ShouldBe(Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("<html><body>404: Not Found</body></html>")]
    [InlineData("""{"minimumSupportedVersion": }""")]
    public void A_malformed_policy_fails_open(string document)
    {
        var result = OfflineClient().Evaluate(document, new Version(1, 0, 0));

        result.Status.ShouldBe(VersionPolicyStatus.Unavailable);
        result.UploadsBlocked.ShouldBeFalse();
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.2.0")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unparseable_minimum_version_fails_open(string minimum)
    {
        var result = OfflineClient().Evaluate(
            $$"""{"minimumSupportedVersion": "{{minimum}}"}""",
            new Version(1, 0, 0));

        result.UploadsBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreachable_policy_url_fails_open()
    {
        var result = await OfflineClient().EvaluateAsync(new Version(0, 0, 1));

        result.Status.ShouldBe(VersionPolicyStatus.Unavailable);
        result.UploadsBlocked.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_failed_http_response_fails_open(HttpStatusCode status)
    {
        var client = ClientWith(StubHttpMessageHandler.Returning(status));

        var result = await client.EvaluateAsync(new Version(0, 0, 1));

        result.Status.ShouldBe(VersionPolicyStatus.Unavailable);
        result.UploadsBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reachable_policy_is_applied()
    {
        var client = ClientWith(StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            """{"minimumSupportedVersion": "9.9.9", "message": "Update required."}"""));

        var result = await client.EvaluateAsync(new Version(1, 0, 0));

        result.Status.ShouldBe(VersionPolicyStatus.BelowMinimum);
        result.UploadsBlocked.ShouldBeTrue();
        result.Message.ShouldBe("Update required.");
    }

    [Fact]
    public void The_shipped_policy_file_is_valid_and_imposes_no_floor()
    {
        // Guards against a typo in version-policy.json silently disabling the whole mechanism,
        // and against accidentally committing a floor that would block every build.
        var repoRoot = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(repoRoot, "version-policy.json"));

        var result = OfflineClient().Evaluate(document, new Version(0, 1, 0));

        result.Status.ShouldBe(VersionPolicyStatus.Satisfied);
        result.UploadsBlocked.ShouldBeFalse();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return directory!.FullName;
    }
}
