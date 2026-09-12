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

    /// <summary>Reads the credential for a server and account, or null when none is stored.</summary>
    /// <param name="account">
    /// Which credential for this server is meant, when a configuration keeps one of its own.
    /// Empty means the credential held for the server as a whole.
    /// </param>
    /// <remarks>
    /// <para>
    /// Keyed by server <em>and</em> account, because two configurations may sign in to one server
    /// as different people. Deliberately not keyed by user name, which looks like the obvious
    /// discriminator and does not work: with an API key -- the recommended mode -- there is no
    /// user name at all, only the key, so two API-key configurations on one server would still
    /// overwrite each other.
    /// </para>
    /// <para>
    /// An empty account resolves to exactly the target used before accounts existed, which is
    /// what makes this need no migration. The credential already stored on every installed copy
    /// is found unchanged, and a build without accounts still finds it after a rollback because
    /// nothing moved.
    /// </para>
    /// </remarks>
    StoredCredential? Read(string serverUrl, string account = "");

    /// <summary>Stores or replaces the credential for a server and account.</summary>
    void Write(string serverUrl, StoredCredential credential, string account = "");

    /// <summary>Removes one. Succeeds when there was none.</summary>
    void Delete(string serverUrl, string account = "");
}
