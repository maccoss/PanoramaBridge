using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// The concurrency limit, once there is more than one engine to apply it to.
/// </summary>
/// <remarks>
/// <para>
/// Before configurations the limit was structural: one engine, and the limit was how many workers
/// it started. With one engine per configuration the same setting silently multiplies -- eight
/// configurations at three transfers each is twenty-four concurrent reads from one volume, and on
/// a spinning disk that is slower than three. The application says so itself beside the slider.
/// </para>
/// <para>
/// This is a cost assertion rather than a result assertion. That the transfers all finish proves
/// nothing; what matters is how many were in flight at once while they did, which is the number
/// the user set and the only reason the setting exists.
/// </para>
/// </remarks>
public sealed class TransferBudgetTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pb-budget-").FullName;
    private SqliteStateStore _store = null!;

    public Task InitializeAsync()
    {
        _store = SqliteStateStore.InMemory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_budget_of_nothing_is_still_a_budget_of_one()
    {
        // Zero would not slow transfers down, it would stop them -- silently, looking exactly
        // like a hang, on a machine whose settings file somebody had hand-edited.
        new TransferBudget(0).Capacity.ShouldBe(1);
        new TransferBudget(-3).Capacity.ShouldBe(1);
    }

    [Fact]
    public async Task A_permit_is_given_back_even_when_the_work_throws()
    {
        // Losing one permanently lowers the limit for the life of the process; losing all of them
        // stops transfers with no sign of why.
        using var budget = new TransferBudget(2);

        for (var i = 0; i < 10; i++)
        {
            try
            {
                using var permit = await budget.AcquireAsync();
                throw new InvalidOperationException("the transfer failed");
            }
            catch (InvalidOperationException)
            {
                // The point is what happens to the permit.
            }
        }

        budget.Available.ShouldBe(2);
    }

    [Fact]
    public async Task Handing_a_permit_back_twice_does_not_raise_the_limit()
    {
        using var budget = new TransferBudget(1);

        var permit = await budget.AcquireAsync();
        permit.Dispose();
        permit.Dispose();

        budget.Available.ShouldBe(1, "a second release would let two transfers run where one was asked for");
    }

    [Fact]
    public async Task Two_engines_sharing_a_budget_never_exceed_it()
    {
        // The reason the budget exists. Two configurations, each with an engine willing to run
        // three transfers, against a limit of two.
        using var budget = new TransferBudget(2);

        var server = new ConcurrencyWatchingClient();

        await using var first = NewEngine("alpha", server, budget);
        await using var second = NewEngine("beta", server, budget);

        var alpha = Populate("alpha", 12);
        var beta = Populate("beta", 12);

        var running = new[] { first.RunAsync(), second.RunAsync() };

        foreach (var path in alpha)
        {
            await first.EnqueueAsync(path);
        }

        foreach (var path in beta)
        {
            await second.EnqueueAsync(path);
        }

        first.CompleteAdding();
        second.CompleteAdding();

        var summaries = await Task.WhenAll(running);

        summaries.Sum(s => s.Uploaded).ShouldBe(24, "every file still has to be transferred");
        server.HighWaterMark.ShouldBe(2, "the limit is shared, not applied twice, and it is reached");
    }

    [Fact]
    public async Task An_engine_with_work_may_use_the_whole_budget()
    {
        // Dividing the limit between configurations would be the easy answer and the wrong one:
        // three configurations against a limit of three would get one slot each, so the one with
        // a hundred files waiting could use a third of the link while the other two sat idle.
        using var budget = new TransferBudget(3);

        var server = new ConcurrencyWatchingClient { HoldEachUpload = TimeSpan.FromMilliseconds(40) };

        await using var busy = NewEngine("busy", server, budget);
        await using var idle = NewEngine("idle", server, budget);

        var files = Populate("busy", 9);
        Directory.CreateDirectory(Path.Combine(_directory, "idle"));

        var running = new[] { busy.RunAsync(), idle.RunAsync() };

        foreach (var path in files)
        {
            await busy.EnqueueAsync(path);
        }

        busy.CompleteAdding();
        idle.CompleteAdding();

        await Task.WhenAll(running);

        server.HighWaterMark.ShouldBe(3, "the idle configuration's share is not reserved from it");
    }

    [Fact]
    public async Task Without_a_budget_an_engine_is_limited_by_its_own_workers()
    {
        // The one-off scan and every test written before configurations existed. No budget means
        // the worker count is the limit, exactly as it always was.
        var server = new ConcurrencyWatchingClient { HoldEachUpload = TimeSpan.FromMilliseconds(20) };

        await using var engine = NewEngine("solo", server, budget: null, workers: 2);

        var files = Populate("solo", 8);
        var running = engine.RunAsync();

        foreach (var path in files)
        {
            await engine.EnqueueAsync(path);
        }

        engine.CompleteAdding();
        await running;

        server.HighWaterMark.ShouldBeLessThanOrEqualTo(2);
    }

    private TransferCoordinator NewEngine(
        string folder,
        IWebDavClient client,
        TransferBudget? budget,
        int workers = 3)
    {
        var root = Path.Combine(_directory, folder);
        Directory.CreateDirectory(root);

        return new TransferCoordinator(
            client,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = root,
                DestinationRoot = RemotePath.Parse($"/_webdav/{folder}/@files/"),
                MaxConcurrentTransfers = workers,
                Budget = budget,

                // Off, so the run measures the limit rather than the fake's hashing.
                VerifyUploads = false,
                WriteChecksumSidecars = false,
            },
            log: NullLogger<TransferCoordinator>.Instance);
    }

    private string[] Populate(string folder, int count)
    {
        var root = Path.Combine(_directory, folder);
        Directory.CreateDirectory(root);

        var paths = new string[count];

        for (var i = 0; i < count; i++)
        {
            paths[i] = Path.Combine(root, $"run{i}.raw");
            File.WriteAllBytes(paths[i], new byte[256]);
        }

        return paths;
    }
}
