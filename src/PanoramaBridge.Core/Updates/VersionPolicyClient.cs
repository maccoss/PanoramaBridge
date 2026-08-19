using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanoramaBridge.Core.Updates;

/// <summary>
/// Fetches the published minimum-supported-version floor and evaluates the running build
/// against it.
/// </summary>
/// <remarks>
/// Deliberately fail-open: every failure path -- offline, DNS, 404, HTML error page,
/// malformed JSON, unparseable version -- yields <see cref="VersionPolicyStatus.Unavailable"/>,
/// which permits uploads. Blocking a lab's instrument PC because GitHub was briefly
/// unreachable would be far worse than letting it run a slightly old build.
/// </remarks>
public sealed class VersionPolicyClient
{
    /// <summary>Where the floor is published. Kept on the default branch so it can be edited alone.</summary>
    public const string DefaultPolicyUrl =
        "https://raw.githubusercontent.com/maccoss/PanoramaBridge/main/version-policy.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<VersionPolicyClient> _log;
    private readonly Uri _policyUrl;

    public VersionPolicyClient(
        HttpClient http,
        string? policyUrl = null,
        ILogger<VersionPolicyClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? NullLogger<VersionPolicyClient>.Instance;
        _policyUrl = new Uri(policyUrl ?? DefaultPolicyUrl, UriKind.Absolute);
    }

    /// <summary>
    /// Reads the policy and compares it to <paramref name="currentVersion"/>.
    /// Never throws.
    /// </summary>
    public async Task<VersionPolicyResult> EvaluateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        string body;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _http
                .GetAsync(_policyUrl, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "Version policy at {Url} returned {Status}; treating as no policy.",
                    _policyUrl,
                    (int)response.StatusCode);
                return VersionPolicyResult.Unavailable($"HTTP {(int)response.StatusCode}");
            }

            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, TLS problem, timeout. Fail open.
            _log.LogDebug(ex, "Could not fetch the version policy from {Url}.", _policyUrl);
            return VersionPolicyResult.Unavailable(ex.Message);
        }

        return Evaluate(body, currentVersion);
    }

    /// <summary>
    /// Parses a policy document and compares it to <paramref name="currentVersion"/>.
    /// Exposed separately so the decision logic is testable without any HTTP.
    /// </summary>
    public VersionPolicyResult Evaluate(string documentJson, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        VersionPolicyDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<VersionPolicyDocument>(documentJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Version policy document was not valid JSON; ignoring it.");
            return VersionPolicyResult.Unavailable("malformed policy document");
        }

        if (document is null || string.IsNullOrWhiteSpace(document.MinimumSupportedVersion))
        {
            // A well-formed policy that publishes no floor is the normal, healthy state.
            return new VersionPolicyResult(VersionPolicyStatus.Satisfied, null, document?.Message);
        }

        if (!Version.TryParse(document.MinimumSupportedVersion.Trim(), out var minimum))
        {
            _log.LogWarning(
                "Version policy declared an unparseable minimum version {Value}; ignoring it.",
                document.MinimumSupportedVersion);
            return VersionPolicyResult.Unavailable("unparseable minimum version");
        }

        // Compare on major.minor.build only. Revision is never part of a published version.
        var current = Normalize(currentVersion);
        var floor = Normalize(minimum);

        return current < floor
            ? new VersionPolicyResult(VersionPolicyStatus.BelowMinimum, floor, document.Message)
            : new VersionPolicyResult(VersionPolicyStatus.Satisfied, floor, document.Message);
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
