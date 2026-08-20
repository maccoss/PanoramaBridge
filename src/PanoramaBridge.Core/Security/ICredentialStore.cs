namespace PanoramaBridge.Core.Security;

/// <summary>A stored credential. The secret is only ever held in memory.</summary>
/// <param name="UserName">Account name, or the literal <c>apikey</c> for an API key.</param>
/// <param name="Secret">The password or API key.</param>
public readonly record struct StoredCredential(string UserName, string Secret)
{
    /// <summary>Never renders the secret. Present so a careless log call cannot leak it.</summary>
    public override string ToString() => $"{UserName}:[redacted]";
}

/// <summary>
/// Where credentials are kept between sessions.
/// </summary>
/// <remarks>
/// Deliberately not the settings file. The Python version depended on <c>keyring</c> together
/// with <c>keyrings.alt</c>, whose fallback backends can silently degrade to an obfuscated
/// plain-text file on disk -- so a lab machine could end up with the account password sitting in
/// the user profile without anyone being told.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>Whether a real credential store is available on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads the credential for a server, or null when none is stored.</summary>
    StoredCredential? Read(string serverUrl);

    /// <summary>Stores or replaces the credential for a server.</summary>
    void Write(string serverUrl, StoredCredential credential);

    /// <summary>Removes the credential for a server. Succeeds when there was none.</summary>
    void Delete(string serverUrl);
}
