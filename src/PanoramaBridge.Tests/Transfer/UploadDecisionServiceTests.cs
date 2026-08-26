using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// The decision ladder is the answer to upload tracking having been unreliable, so these tests
/// assert not only the right answer but what it cost to reach -- the point of the design is that
/// the common case is free.
/// </summary>
public sealed class UploadDecisionServiceTests : IAsyncDisposable
{
    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly FakeWebDavClient _server = new();
    private readonly CountingFileHasher _hasher = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("pb-decide-").FullName;

    private UploadDecisionService NewService() =>
        new(_store, new RemoteSnapshotCache(_server), _hasher);

    private async Task<LocalFileStamp> WriteLocalAsync(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, content);
        return LocalFileStamp.FromFile(path);
    }

    // -- Tier 0: the ledger -------------------------------------------------------------------

    [Fact]
    public async Task A_file_already_verified_is_skipped_with_no_hashing_and_no_network()
    {
        // This is the assertion the whole design exists for. The Python version answered this
        // same question with a full SHA-256 of the file plus two or three round trips.
        var stamp = await WriteLocalAsync("done.raw", "already uploaded");
        var destination = Destination.Append("done.raw");

        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, destination.ToEncodedString()) with
        {
            Md5 = "whatever",
            State = TransferState.Verified,
            VerifyMethod = VerifyMethod.ServerMd5,
        });

        _server.Reset();

        var decision = await NewService().DecideAsync(stamp, destination, ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Skip);
        decision.Tier.ShouldBe(DecisionTier.Ledger);

        _hasher.Calls.ShouldBe(0, "the fast path must not hash");
        _server.TotalCalls.ShouldBe(0, "the fast path must not touch the network");
    }

    [Fact]
    public async Task A_ledger_row_whose_file_has_changed_does_not_short_circuit()
    {
        var stamp = await WriteLocalAsync("grown.raw", "small");
        var destination = Destination.Append("grown.raw");

        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, destination.ToEncodedString()) with
        {
            State = TransferState.Verified,
            VerifyMethod = VerifyMethod.ServerMd5,
        });

        // The instrument appended to it after the row was written.
        var grown = await WriteLocalAsync("grown.raw", "small plus a great deal more");

        var decision = await NewService().DecideAsync(grown, destination, ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Upload);
        decision.Tier.ShouldNotBe(DecisionTier.Ledger);
    }

    [Fact]
    public async Task A_row_verified_only_by_size_is_not_treated_as_settled()
    {
        // Size alone cannot distinguish a good copy from one the server mis-stored, so a row
        // that never got a hash comparison has to be re-examined rather than trusted.
        var stamp = await WriteLocalAsync("weak.raw", "size checked only");
        var destination = Destination.Append("weak.raw");

        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, destination.ToEncodedString()) with
        {
            State = TransferState.Uploaded,
            VerifyMethod = VerifyMethod.SizeOnly,
        });

        var decision = await NewService().DecideAsync(stamp, destination, ConflictPolicy.Ask);

        decision.Tier.ShouldNotBe(DecisionTier.Ledger);
    }

    [Fact]
    public async Task A_row_pointing_at_a_different_destination_does_not_short_circuit()
    {
        // The same file being verified into one folder says nothing about another.
        var stamp = await WriteLocalAsync("moved.raw", "content");

        await _store.SaveAsync(
            UploadRecord.ForNewFile(stamp, "/_webdav/MacCoss/maccoss/@files/somewhere-else/moved.raw") with
            {
                State = TransferState.Verified,
                VerifyMethod = VerifyMethod.ServerMd5,
            });

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("moved.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Upload);
    }

    // -- Tier 1: the remote snapshot ----------------------------------------------------------

    [Fact]
    public async Task A_file_absent_from_the_server_is_uploaded_without_being_hashed_first()
    {
        // The hash comes free from the upload's own pass over the file, so computing it here
        // would mean reading a multi-gigabyte acquisition twice.
        var stamp = await WriteLocalAsync("new.raw", "brand new");

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("new.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Upload);
        decision.Tier.ShouldBe(DecisionTier.RemoteSnapshot);
        decision.Hashes.ShouldBeNull();
        _hasher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_whole_folder_is_answered_by_two_requests_however_many_files_it_holds()
    {
        // The heart of the speed claim: per-folder cost, not per-file.
        var service = NewService();
        var stamps = new List<LocalFileStamp>();

        for (var i = 0; i < 25; i++)
        {
            stamps.Add(await WriteLocalAsync($"batch{i}.raw", $"content {i}"));
        }

        _server.Reset();

        foreach (var stamp in stamps)
        {
            await service.DecideAsync(
                stamp,
                Destination.Append(Path.GetFileName(stamp.Path)),
                ConflictPolicy.Ask);
        }

        // The folder does not exist yet, so the listing 404s and no hash call is needed.
        _server.ListCalls.ShouldBe(1);
        _server.CollectionHashCalls.ShouldBe(0);
        _server.TotalCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_populated_folder_costs_one_listing_and_one_hash_request()
    {
        var service = NewService();

        for (var i = 0; i < 10; i++)
        {
            var content = $"content {i}";
            _server.Seed(Destination.Append($"batch{i}.raw"), System.Text.Encoding.UTF8.GetBytes(content));
            await WriteLocalAsync($"batch{i}.raw", content);
        }

        _server.Reset();

        for (var i = 0; i < 10; i++)
        {
            var stamp = LocalFileStamp.FromFile(Path.Combine(_directory, $"batch{i}.raw"));
            var decision = await service.DecideAsync(
                stamp,
                Destination.Append($"batch{i}.raw"),
                ConflictPolicy.Ask);

            decision.Action.ShouldBe(UploadAction.Skip);
        }

        _server.ListCalls.ShouldBe(1);
        _server.CollectionHashCalls.ShouldBe(1);
    }

    /// <summary>
    /// A new file going into a populated destination must not make the server hash the folder.
    /// </summary>
    /// <remarks>
    /// The case a lab actually has: a destination holding hundreds of gigabytes of previous
    /// acquisitions, and one new file to put in it. Panorama computes a collection hash on
    /// demand over every byte in the folder, at roughly 600 MB/s, so a 300 GB destination is
    /// minutes of server time -- spent to discover that the file is not there and must be
    /// uploaded, which the listing already said. The hash is only ever read when a name matches.
    /// </remarks>
    [Fact]
    public async Task A_file_not_on_the_server_is_decided_without_hashing_the_folder()
    {
        var service = NewService();

        // A destination that already holds other work.
        for (var i = 0; i < 25; i++)
        {
            _server.Seed(Destination.Append($"earlier{i}.raw"), System.Text.Encoding.UTF8.GetBytes($"acquisition {i}"));
        }

        var stamp = await WriteLocalAsync("brand-new.raw", "today's acquisition");
        _server.Reset();

        var decision = await service.DecideAsync(
            stamp, Destination.Append("brand-new.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Upload);

        _server.ListCalls.ShouldBe(1, "the listing is what answers this");
        _server.CollectionHashCalls.ShouldBe(
            0, "nothing read a hash, so nothing should have asked the server to compute one");
    }

    [Fact]
    public async Task A_failed_folder_hash_is_not_remembered()
    {
        // The one that matters most. Monitoring runs for days on one cache, so a cached failure
        // is not one bad decision but every later decision about that folder: each file whose
        // name is already there would fail, exhaust its attempts, and be abandoned. One 503 must
        // cost one retry, not a session.
        var service = NewService();
        var stamp = await WriteLocalAsync("run.raw", "acquisition data");
        _server.Seed(Destination.Append("run.raw"), "something else entirely"u8.ToArray());

        _server.FailNextCollectionHash = true;

        await Should.ThrowAsync<WebDavException>(() =>
            service.DecideAsync(stamp, Destination.Append("run.raw"), ConflictPolicy.Ask));

        // The very next attempt has to reach the server again rather than replay the failure.
        var decision = await service.DecideAsync(
            stamp, Destination.Append("run.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Conflict, "and reach a real answer this time");
    }

    [Fact]
    public async Task A_folder_hash_does_not_outlive_the_listing_it_belongs_to()
    {
        // Without this the hashes have no expiry at all. The listing is refetched after its
        // lifetime precisely so a change another client made shows up; answering from an
        // hours-old hash would hide exactly that, and -- worse -- a same-size replacement would
        // match the local file and be recorded "Verified (server MD5)" against content the
        // server does not hold.
        var now = DateTimeOffset.UtcNow;
        var snapshots = new RemoteSnapshotCache(
            _server, lifetime: TimeSpan.FromMinutes(5), clock: () => now);
        var service = new UploadDecisionService(_store, snapshots, _hasher);

        var stamp = await WriteLocalAsync("shared.raw", "our content...");
        _server.Seed(Destination.Append("shared.raw"), "our content..."u8.ToArray());

        await service.DecideAsync(stamp, Destination.Append("shared.raw"), ConflictPolicy.Ask);
        _server.CollectionHashCalls.ShouldBe(1);

        // A colleague replaces it on Panorama, same length, different content.
        _server.Seed(Destination.Append("shared.raw"), "their content.."u8.ToArray());
        now = now.AddMinutes(10);

        var decision = await service.DecideAsync(
            stamp, Destination.Append("shared.raw"), ConflictPolicy.Ask);

        _server.CollectionHashCalls.ShouldBe(
            2, "the listing expired, so the hashes that came with it must be gone too");

        decision.Action.ShouldBe(
            UploadAction.Conflict, "the copy on the server is no longer the one we know about");
    }

    [Fact]
    public async Task Workers_arriving_together_do_not_each_hash_the_folder()
    {
        // A duplicate listing is a wasted cheap request; a duplicate collection hash is minutes
        // of server-side reading. GetOrAdd's factory is not atomic, which is why this is a Lazy.
        var service = NewService();

        for (var i = 0; i < 4; i++)
        {
            var content = $"acquisition {i}";
            await WriteLocalAsync($"same{i}.raw", content);
            _server.Seed(
                Destination.Append($"same{i}.raw"),
                System.Text.Encoding.UTF8.GetBytes(content));
        }

        using var gate = new SemaphoreSlim(0);
        _server.HoldCollectionHash = gate;
        _server.Reset();

        var decisions = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            var stamp = LocalFileStamp.FromFile(Path.Combine(_directory, $"same{i}.raw"));
            return service.DecideAsync(stamp, Destination.Append($"same{i}.raw"), ConflictPolicy.Ask);
        })).ToArray();

        // Let them all pile up on the hash before any of them completes.
        await Task.Delay(150);
        gate.Release(8);

        await Task.WhenAll(decisions);

        _server.CollectionHashCalls.ShouldBe(
            1, "four workers wanting the same folder's hashes is one request, not four");
    }

    [Fact]
    public async Task A_different_size_on_the_server_is_a_conflict_without_hashing_either_side()
    {
        var stamp = await WriteLocalAsync("clash.raw", "the local version, which is longer");
        _server.Seed(Destination.Append("clash.raw"), "short"u8.ToArray());

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("clash.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Conflict);
        decision.Tier.ShouldBe(DecisionTier.RemoteSnapshot);
        _hasher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_folder_occupying_the_destination_name_is_a_conflict()
    {
        var stamp = await WriteLocalAsync("collide", "file, but the server has a folder here");
        _server.Seed(Destination.Append("collide", isCollection: true).Append("inner.raw"), [1, 2, 3]);

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("collide"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Conflict);
        decision.Reason.ShouldContain("folder");
    }

    [Fact]
    public async Task Same_size_but_no_reported_hash_is_a_conflict_rather_than_an_assumption()
    {
        // Calling it identical on size alone is exactly the kind of unfounded "verified" the
        // Python version reported.
        var content = "same length here";
        var stamp = await WriteLocalAsync("nohash.raw", content);
        _server.Seed(Destination.Append("nohash.raw"), System.Text.Encoding.UTF8.GetBytes(content));
        _server.WithholdHashes = true;

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("nohash.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Conflict);
        decision.Reason.ShouldContain("hash");
    }

    // -- Tier 2: hashing ----------------------------------------------------------------------

    [Fact]
    public async Task An_identical_copy_on_the_server_is_skipped_and_counts_as_hash_verified()
    {
        var content = "identical on both sides";
        var stamp = await WriteLocalAsync("same.raw", content);
        _server.Seed(Destination.Append("same.raw"), System.Text.Encoding.UTF8.GetBytes(content));

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("same.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Skip);
        decision.Tier.ShouldBe(DecisionTier.LocalHash);
        decision.ImpliedVerification.ShouldBe(VerifyMethod.ServerMd5);
        _hasher.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Same_name_and_size_but_different_content_is_a_conflict()
    {
        var stamp = await WriteLocalAsync("differs.raw", "aaaaaaaaaa");
        _server.Seed(Destination.Append("differs.raw"), "bbbbbbbbbb"u8.ToArray());

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("differs.raw"), ConflictPolicy.Ask);

        decision.Action.ShouldBe(UploadAction.Conflict);
        decision.Tier.ShouldBe(DecisionTier.LocalHash);
        decision.Hashes.ShouldNotBeNull();
        decision.RemoteHash.ShouldNotBeNull();
        decision.Hashes!.Value.Md5.ShouldNotBe(decision.RemoteHash);
    }

    [Fact]
    public async Task A_hash_is_computed_once_and_then_cached()
    {
        // The Python version built its cache key two different ways in two places, so it never
        // hit its own cache and re-hashed multi-gigabyte files constantly.
        var content = "cache me";
        var stamp = await WriteLocalAsync("cached.raw", content);
        _server.Seed(Destination.Append("cached.raw"), System.Text.Encoding.UTF8.GetBytes(content));

        var service = NewService();

        await service.GetHashesAsync(stamp);
        await service.GetHashesAsync(stamp);
        await service.GetHashesAsync(stamp);

        _hasher.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Modifying_a_file_invalidates_its_cached_hash()
    {
        var service = NewService();

        var before = await WriteLocalAsync("changing.raw", "first");
        var first = await service.GetHashesAsync(before);

        var after = await WriteLocalAsync("changing.raw", "second, quite different");
        var second = await service.GetHashesAsync(after);

        second.Md5.ShouldNotBe(first.Md5);
        _hasher.Calls.ShouldBe(2);
    }

    // -- Conflict policy ----------------------------------------------------------------------

    [Theory]
    [InlineData(ConflictPolicy.Ask, UploadAction.Conflict)]
    [InlineData(ConflictPolicy.Skip, UploadAction.Skip)]
    [InlineData(ConflictPolicy.Overwrite, UploadAction.Upload)]
    public async Task The_policy_decides_what_happens_to_a_genuine_conflict(
        ConflictPolicy policy,
        UploadAction expected)
    {
        var stamp = await WriteLocalAsync("policy.raw", "local content xx");
        _server.Seed(Destination.Append("policy.raw"), "server content!!"u8.ToArray());

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("policy.raw"), policy);

        decision.Action.ShouldBe(expected);
    }

    [Fact]
    public async Task An_identical_file_is_skipped_whatever_the_policy_says()
    {
        // Overwrite is about resolving a clash, not about re-sending gigabytes needlessly.
        var content = "identical";
        var stamp = await WriteLocalAsync("same.raw", content);
        _server.Seed(Destination.Append("same.raw"), System.Text.Encoding.UTF8.GetBytes(content));

        foreach (var policy in Enum.GetValues<ConflictPolicy>())
        {
            var decision = await NewService()
                .DecideAsync(stamp, Destination.Append("same.raw"), policy);

            decision.Action.ShouldBe(UploadAction.Skip, $"policy {policy}");
        }
    }

    [Fact]
    public async Task An_overwrite_does_not_claim_the_new_copy_is_already_verified()
    {
        var stamp = await WriteLocalAsync("over.raw", "local content xx");
        _server.Seed(Destination.Append("over.raw"), "server content!!"u8.ToArray());

        var decision = await NewService()
            .DecideAsync(stamp, Destination.Append("over.raw"), ConflictPolicy.Overwrite);

        decision.Action.ShouldBe(UploadAction.Upload);
        decision.ImpliedVerification.ShouldBe(VerifyMethod.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
