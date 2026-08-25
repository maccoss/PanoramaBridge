using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// Working out what to call each file in a batch being sent alongside what is already there.
/// </summary>
/// <remarks>
/// This lived in the Uploads view model, where it duplicated the engine's renaming and could not
/// be tested without a dispatcher -- so none of it was. These are the cases that were shipping
/// unexercised.
/// </remarks>
public sealed class RenamePlannerTests
{
    private static readonly RemotePath Uploads = RemotePath.Parse("/_webdav/uploads/");

    private readonly FakeWebDavClient _server = new();

    private static UploadRecord Held(string localName, RemotePath destination) => new(
        LocalPath: @"C:\data\" + localName,
        RemotePath: destination.ToEncodedString(),
        Length: 4096,
        LastWriteUnixMs: 1_700_000_000_000,
        Md5: null,
        Sha256: null,
        State: TransferState.Conflict,
        VerifyMethod: VerifyMethod.None,
        VerifiedUtc: null,
        Attempts: 0,
        LastError: "Occupied.",
        IsDataset: false,
        ConflictKind: ConflictKind.DestinationOccupied);

    [Fact]
    public async Task Each_file_gets_the_first_free_name()
    {
        _server.Seed(Uploads.Append("run.raw"), "occupying"u8.ToArray());

        var plan = await new RenamePlanner(_server)
            .PlanAsync([Held("run.raw", Uploads.Append("run.raw"))]);

        plan.IsUsable.ShouldBeTrue();
        plan.Proposals.Single().Name.ShouldBe("run (2).raw");
    }

    [Fact]
    public async Task Two_files_wanting_the_same_name_do_not_both_get_it()
    {
        // The case a per-file lookup gets wrong: the listing is identical for both, so without
        // remembering what has already been handed out they would both propose run (2).raw and
        // the second would land on the first.
        _server.Seed(Uploads.Append("run.raw"), "occupying"u8.ToArray());

        var plan = await new RenamePlanner(_server).PlanAsync([
            Held("first.raw", Uploads.Append("run.raw")),
            Held("second.raw", Uploads.Append("run.raw")),
        ]);

        plan.Proposals.Select(p => p.Name).ShouldBe(["run (2).raw", "run (3).raw"]);
    }

    [Fact]
    public async Task One_listing_covers_a_whole_folder()
    {
        // Five hundred acquisitions in one folder should cost one request, not five hundred, on
        // a machine attached to a mass spectrometer.
        _server.Seed(Uploads.Append("a.raw"), "occupying"u8.ToArray());
        _server.Seed(Uploads.Append("b.raw"), "occupying"u8.ToArray());

        _server.Reset();

        await new RenamePlanner(_server).PlanAsync([
            Held("a.raw", Uploads.Append("a.raw")),
            Held("b.raw", Uploads.Append("b.raw")),
        ]);

        _server.ListCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_name_the_server_would_refuse_stops_the_plan()
    {
        // PathSafety refuses a semicolon, and the engine would refuse it later anyway -- as a
        // failed transfer, minutes after the decision, carrying a message about characters. Said
        // here instead, attached to the decision that caused it.
        var awkward = new string('x', 250) + ".raw";
        _server.Seed(Uploads.Append(awkward), "occupying"u8.ToArray());

        var plan = await new RenamePlanner(_server)
            .PlanAsync([Held("long.raw", Uploads.Append(awkward))]);

        plan.IsUsable.ShouldBeFalse();
        plan.Problem!.ShouldContain("cannot be renamed automatically");
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_produces_a_sentence_not_an_exception()
    {
        _server.Seed(Uploads.Append("run.raw"), "occupying"u8.ToArray());
        _server.FailListingsOnce = true;

        var plan = await new RenamePlanner(_server)
            .PlanAsync([Held("run.raw", Uploads.Append("run.raw"))]);

        plan.IsUsable.ShouldBeFalse();
        plan.Problem.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nothing_to_plan_is_a_usable_plan_with_nothing_in_it()
    {
        var plan = await new RenamePlanner(_server).PlanAsync([]);

        plan.IsUsable.ShouldBeTrue();
        plan.Proposals.ShouldBeEmpty();
    }
}
