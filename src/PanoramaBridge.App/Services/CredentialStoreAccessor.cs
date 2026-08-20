using Microsoft.Extensions.Logging;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Security;

namespace PanoramaBridge.App.Services;

/// <summary>
/// Adapts the credential store to the narrow surface the shell needs.
/// </summary>
/// <remarks>
/// Failures are logged rather than thrown. A machine with a broken or locked-down Credential
/// Manager should still be able to transfer files by entering the key each session; refusing to
/// work at all would be a worse outcome than not remembering.
/// </remarks>
public sealed class CredentialStoreAccessor : ICredentialStoreAccessor
{
    private readonly ICredentialStore _store;
    private readonly ILogger<CredentialStoreAccessor> _log;

    public CredentialStoreAccessor(ICredentialStore store, ILogger<CredentialStoreAccessor> log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public void Remember(string serverUrl, string userName, string secret)
    {
        try
        {
            _store.Write(serverUrl, new StoredCredential(userName, secret));

            // Note what was stored, never the secret itself.
            _log.LogInformation("Stored the credential for {Server} as {User}.", serverUrl, userName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not store the credential for {Server}.", serverUrl);
        }
    }

    /// <inheritdoc />
    public void Forget(string serverUrl)
    {
        try
        {
            _store.Delete(serverUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not remove the credential for {Server}.", serverUrl);
        }
    }
}
