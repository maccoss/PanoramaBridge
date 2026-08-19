using System.Text.Json.Serialization;

namespace PanoramaBridge.Core.Updates;

/// <summary>
/// The wire shape of <c>version-policy.json</c>, published at a stable raw URL in the
/// repository.
/// </summary>
/// <remarks>
/// Because the floor lives in the repo rather than in a shipped build, a bad release can be
/// retired without shipping another one.
/// </remarks>
public sealed class VersionPolicyDocument
{
    /// <summary>
    /// Builds older than this refuse to start new uploads. Omit or leave null to impose no floor.
    /// </summary>
    [JsonPropertyName("minimumSupportedVersion")]
    public string? MinimumSupportedVersion { get; set; }

    /// <summary>Shown to the user when the running build is below the floor.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>Why uploads are or are not permitted by the version policy.</summary>
public enum VersionPolicyStatus
{
    /// <summary>The policy was read and this build satisfies it.</summary>
    Satisfied,

    /// <summary>This build is older than the published floor. New uploads must not start.</summary>
    BelowMinimum,

    /// <summary>
    /// The policy could not be read or understood. Treated as permissive -- an unreachable
    /// GitHub must never brick an instrument PC mid-run.
    /// </summary>
    Unavailable,
}

/// <summary>Outcome of evaluating the published policy against the running build.</summary>
/// <param name="Status">Whether the floor was met, missed, or could not be determined.</param>
/// <param name="MinimumSupportedVersion">The floor, when one was published.</param>
/// <param name="Message">Operator-supplied explanation to show the user.</param>
public sealed record VersionPolicyResult(
    VersionPolicyStatus Status,
    Version? MinimumSupportedVersion,
    string? Message)
{
    /// <summary>
    /// True only when the policy was successfully read and this build is too old. Anything
    /// else -- satisfied, no floor published, network failure, malformed document -- allows
    /// uploads to proceed.
    /// </summary>
    public bool UploadsBlocked => Status == VersionPolicyStatus.BelowMinimum;

    public static VersionPolicyResult Unavailable(string? reason = null) =>
        new(VersionPolicyStatus.Unavailable, null, reason);
}
