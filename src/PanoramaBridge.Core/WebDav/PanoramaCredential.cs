using System.Net.Http.Headers;
using System.Text;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// How the client authenticates to Panorama.
/// </summary>
/// <remarks>
/// Basic only. The server advertises <c>WWW-Authenticate: Basic realm=""</c> and never offers
/// Digest, so the Digest option the Python version exposed was dead configuration.
/// </remarks>
public abstract class PanoramaCredential
{
    private protected PanoramaCredential()
    {
    }

    /// <summary>The user name half of the Basic pair.</summary>
    public abstract string UserName { get; }

    /// <summary>The secret half. Never logged, never persisted outside the credential store.</summary>
    public abstract string Secret { get; }

    /// <summary>A LabKey API key, which is the recommended way to authenticate.</summary>
    /// <remarks>
    /// Keys are generated from the Panorama user menu under External Tool Access. They are
    /// revocable without changing the account password, can be restricted to a role, and expire
    /// server-side -- all of which make them safer to put on a shared instrument PC than the
    /// account password.
    /// </remarks>
    public static PanoramaCredential ApiKey(string key) => new ApiKeyCredential(key);

    /// <summary>A Panorama account user name and password.</summary>
    public static PanoramaCredential UserNameAndPassword(string userName, string password) =>
        new BasicCredential(userName, password);

    /// <summary>
    /// Builds the header value. Set once on the client rather than supplied in response to a
    /// challenge: .NET's built-in credential handling waits to be challenged, which would mean
    /// discovering a bad key only after streaming a multi-gigabyte body.
    /// </summary>
    public AuthenticationHeaderValue ToAuthenticationHeader()
    {
        var pair = Encoding.UTF8.GetBytes($"{UserName}:{Secret}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
    }

    /// <summary>Describes the credential without revealing it. Safe to log.</summary>
    public abstract override string ToString();

    private sealed class ApiKeyCredential : PanoramaCredential
    {
        private const string ApiKeyUserName = "apikey";

        /// <summary>
        /// Historical LabKey keys were handed out with this prefix. Users paste whatever they
        /// were given, so it is stripped rather than rejected.
        /// </summary>
        private const string LegacyPrefix = "apikey|";

        public ApiKeyCredential(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            var trimmed = key.Trim();
            if (trimmed.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[LegacyPrefix.Length..];
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("The API key is empty.", nameof(key));
            }

            Secret = trimmed;
        }

        public override string UserName => ApiKeyUserName;

        public override string Secret { get; }

        public override string ToString() => "API key";
    }

    private sealed class BasicCredential : PanoramaCredential
    {
        public BasicCredential(string userName, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            ArgumentNullException.ThrowIfNull(password);

            UserName = userName.Trim();
            Secret = password;
        }

        public override string UserName { get; }

        public override string Secret { get; }

        public override string ToString() => $"user {UserName}";
    }
}
