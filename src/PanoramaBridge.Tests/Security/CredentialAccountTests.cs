using PanoramaBridge.Core.Security;

namespace PanoramaBridge.Tests.Security;

/// <summary>
/// Credential tests share one machine-wide resource, so they must not run beside each other.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel by default, and Windows Credential Manager is per-user
/// rather than per-test: two classes writing and deleting entries at once produced a failure
/// roughly one full run in three, in a test that passed every time on its own. Random target
/// names are not enough, which is the same lesson ProgramTests records about an environment
/// variable.
/// </remarks>
public sealed class CredentialManagerCollection;

[CollectionDefinition(nameof(CredentialManagerCollection), DisableParallelization = true)]
public sealed class CredentialManagerCollectionDefinition;

/// <summary>
/// Keeping one server's credentials apart when several configurations sign in to it.
/// </summary>
/// <remarks>
/// <para>
/// Credentials were keyed by server alone, so two configurations on one server overwrote each
/// other's stored secret and whichever signed in last decided who both of them were.
/// </para>
/// <para>
/// The property that matters most here is the dull one: an empty account has to resolve to
/// exactly the name used before accounts existed. Every installed copy has its credential under
/// that name, and moving it would sign people out on update -- on an instrument, silently, until
/// somebody noticed transfers had stopped.
/// </para>
/// </remarks>
[Collection(nameof(CredentialManagerCollection))]
public sealed class CredentialAccountTests
{
    private const string Server = "https://panoramaweb.org";

    [Fact]
    public void An_empty_account_is_exactly_the_name_used_before_accounts_existed()
    {
        // The whole migration strategy, asserted. If this ever stops being true, every installed
        // copy loses its stored credential on update -- and a rolled-back build loses it again.
        WindowsCredentialStore.TargetFor(Server, string.Empty)
            .ShouldBe(WindowsCredentialStore.TargetFor(Server));
    }

    [Fact]
    public void Two_accounts_on_one_server_do_not_share_a_slot()
    {
        // The reason any of this changed.
        var first = WindowsCredentialStore.TargetFor(Server, "config-a");
        var second = WindowsCredentialStore.TargetFor(Server, "config-b");

        first.ShouldNotBe(second);
        first.ShouldNotBe(WindowsCredentialStore.TargetFor(Server));
    }

    [Fact]
    public void An_account_is_still_keyed_on_the_host_rather_than_the_folder()
    {
        // The pre-existing rule, which the account must not quietly undo: one server means one
        // slot per account however deep the remote path in the settings box happens to be.
        WindowsCredentialStore.TargetFor(Server + "/_webdav/MacCoss/maccoss/@files/", "config-a")
            .ShouldBe(WindowsCredentialStore.TargetFor(Server + "/anything/else", "config-a"));
    }

    [Fact]
    public void Different_servers_stay_apart_whatever_the_account()
    {
        WindowsCredentialStore.TargetFor("https://panoramaweb.org", "config-a")
            .ShouldNotBe(WindowsCredentialStore.TargetFor("https://labkey.partner.edu", "config-a"));
    }

    [SkippableFact]
    public void A_credential_written_for_one_account_is_invisible_to_another()
    {
        // Against the real Credential Manager, because the separation being tested is Windows's
        // rather than ours: two target names, two entries, no overlap.
        var store = new WindowsCredentialStore();
        Skip.IfNot(store.IsAvailable, "Windows Credential Manager is not available here.");

        const string A = "pb-test-config-a";
        const string B = "pb-test-config-b";
        var server = "https://pb-test-" + Guid.NewGuid().ToString("n")[..8] + ".invalid";

        try
        {
            store.Write(server, new StoredCredential("apikey", "secret-for-a"), A);
            store.Write(server, new StoredCredential("apikey", "secret-for-b"), B);

            store.Read(server, A)!.Value.Secret.ShouldBe("secret-for-a");
            store.Read(server, B)!.Value.Secret.ShouldBe("secret-for-b");

            // And neither is the server-wide one, which is what an older build reads.
            store.Read(server).ShouldBeNull();
        }
        finally
        {
            store.Delete(server, A);
            store.Delete(server, B);
            store.Delete(server);
        }
    }

    [SkippableFact]
    public void Deleting_one_account_leaves_the_other_signed_in()
    {
        // Removing a configuration must not sign out the ones beside it.
        var store = new WindowsCredentialStore();
        Skip.IfNot(store.IsAvailable, "Windows Credential Manager is not available here.");

        var server = "https://pb-test-" + Guid.NewGuid().ToString("n")[..8] + ".invalid";

        try
        {
            store.Write(server, new StoredCredential("apikey", "keep-me"), "keeper");
            store.Write(server, new StoredCredential("apikey", "remove-me"), "goner");

            store.Delete(server, "goner");

            store.Read(server, "goner").ShouldBeNull();
            store.Read(server, "keeper")!.Value.Secret.ShouldBe("keep-me");
        }
        finally
        {
            store.Delete(server, "keeper");
            store.Delete(server, "goner");
        }
    }
}
