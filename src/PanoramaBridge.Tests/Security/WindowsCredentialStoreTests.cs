using PanoramaBridge.Core.Security;

namespace PanoramaBridge.Tests.Security;

/// <summary>
/// Exercises the real Windows Credential Manager. Uses a throwaway server name so it can never
/// touch a credential the user actually relies on, and removes what it creates.
/// </summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly WindowsCredentialStore _store = new();
    private readonly string _server = $"https://pb-test-{Guid.NewGuid():n}.invalid";

    [Fact]
    public void A_stored_credential_comes_back_intact()
    {
        _store.Write(_server, new StoredCredential("apikey", "096e8e12047ef0d881bddf5a25accf8d6"));

        var read = _store.Read(_server).ShouldNotBeNull();

        read.UserName.ShouldBe("apikey");
        read.Secret.ShouldBe("096e8e12047ef0d881bddf5a25accf8d6");
    }

    [Fact]
    public void An_unknown_server_reads_back_as_nothing_rather_than_throwing()
    {
        // A first run has nothing stored, and that is not an error.
        _store.Read($"https://pb-never-stored-{Guid.NewGuid():n}.invalid").ShouldBeNull();
    }

    [Fact]
    public void Writing_again_replaces_the_previous_secret()
    {
        _store.Write(_server, new StoredCredential("apikey", "first-key"));
        _store.Write(_server, new StoredCredential("apikey", "second-key"));

        _store.Read(_server)!.Value.Secret.ShouldBe("second-key");
    }

    [Fact]
    public void A_deleted_credential_is_gone()
    {
        _store.Write(_server, new StoredCredential("apikey", "to-be-removed"));
        _store.Delete(_server);

        _store.Read(_server).ShouldBeNull();
    }

    [Fact]
    public void Deleting_something_that_is_not_there_is_not_an_error()
    {
        Should.NotThrow(() => _store.Delete($"https://pb-absent-{Guid.NewGuid():n}.invalid"));
    }

    [Theory]
    [InlineData("pa$$w0rd with spaces")]
    [InlineData("unicode-café-ß-日本語")]
    [InlineData("64-char-hex-096e8e12047ef0d881bddf5a25accf8d6122242dabd5a2dedc520f32")]
    public void Awkward_secrets_survive_the_round_trip(string secret)
    {
        // The blob is length-prefixed rather than null-terminated, so anything that is valid
        // UTF-16 has to come back byte for byte.
        _store.Write(_server, new StoredCredential("someone@uw.edu", secret));

        _store.Read(_server)!.Value.Secret.ShouldBe(secret);
    }

    [Fact]
    public void The_target_name_is_keyed_on_the_host_not_the_folder()
    {
        // Otherwise every destination folder on the same server would need its own login.
        WindowsCredentialStore.TargetFor("https://panoramaweb.org/_webdav/MacCoss/maccoss/@files/")
            .ShouldBe("PanoramaBridge:https://panoramaweb.org");

        WindowsCredentialStore.TargetFor("https://panoramaweb.org")
            .ShouldBe(WindowsCredentialStore.TargetFor("https://panoramaweb.org/anything/else"));
    }

    [Fact]
    public void The_stored_credential_never_renders_its_secret()
    {
        // Guards against a careless interpolation into a log message.
        new StoredCredential("apikey", "super-secret").ToString().ShouldBe("apikey:[redacted]");
        new StoredCredential("apikey", "super-secret").ToString().ShouldNotContain("super-secret");
    }

    [Fact]
    public void An_implausibly_long_secret_is_refused_rather_than_silently_truncated()
    {
        Should.Throw<ArgumentException>(
            () => _store.Write(_server, new StoredCredential("apikey", new string('x', 400))));
    }

    public void Dispose() => _store.Delete(_server);
}
