namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// How many files may be moving at once, across everything that transfers.
/// </summary>
/// <remarks>
/// <para>
/// The limit describes the disk and the link, not any one folder-to-destination pairing. Before
/// configurations there was one transfer engine and the limit was structural -- it was simply how
/// many workers that engine started. With a set of them the same setting would have multiplied:
/// eight configurations each allowed three transfers is twenty-four concurrent reads from one
/// volume, and on a spinning disk that is slower than three, not faster. The application says so
/// itself beside the slider, in <c>ConcurrencyAdvice</c>.
/// </para>
/// <para>
/// Shared rather than divided, because dividing wastes the link: three configurations against a
/// limit of three would get one slot each, so a configuration with a hundred files waiting could
/// use a third of the budget while the other two sat idle. A permit taken as each file starts
/// gives the whole budget to whoever has work.
/// </para>
/// <para>
/// A worker waiting for a permit has already taken a file off its queue and holds it until it
/// gets one. That is not a deadlock and cannot become one: waiting on this blocks nothing that
/// another worker needs in order to finish and release its own permit.
/// </para>
/// </remarks>
public sealed class TransferBudget : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private bool _disposed;

    /// <param name="concurrentTransfers">
    /// Files moving at once. Clamped to at least one, because a budget of zero would not slow
    /// transfers down, it would stop them -- silently, and looking exactly like a hang.
    /// </param>
    public TransferBudget(int concurrentTransfers)
    {
        Capacity = Math.Max(1, concurrentTransfers);
        _slots = new SemaphoreSlim(Capacity, Capacity);
    }

    /// <summary>Files allowed to move at once.</summary>
    public int Capacity { get; }

    /// <summary>Permits not currently held, for diagnostics and for cost assertions in tests.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>
    /// Waits for a permit, and returns the thing that gives it back.
    /// </summary>
    /// <remarks>
    /// Disposed rather than returned by a paired call, so that a transfer which throws cannot
    /// leak a permit. Losing one permanently reduces the budget for the life of the process, and
    /// losing all of them stops transfers with no sign of why.
    /// </remarks>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Permit(_slots);
    }

    /// <summary>
    /// Releases the semaphore.
    /// </summary>
    /// <remarks>
    /// Only once every engine sharing it has been stopped and drained. Disposing while a worker
    /// is inside <see cref="AcquireAsync"/> makes that worker throw
    /// <see cref="ObjectDisposedException"/> instead of the cancellation it was expecting, which
    /// is then logged as a transfer failure -- a file reported as failed when nothing about it
    /// failed. A shutdown that cannot wait should simply drop the budget instead: it holds no
    /// handle, so there is nothing to leak.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _slots.Dispose();
    }

    private sealed class Permit : IDisposable
    {
        private SemaphoreSlim? _slots;

        public Permit(SemaphoreSlim slots) => _slots = slots;

        public void Dispose()
        {
            // Exchanged rather than null-checked, so a double dispose cannot hand back a permit
            // that was never taken and quietly raise the limit above what the user set.
            var slots = Interlocked.Exchange(ref _slots, null);

            try
            {
                slots?.Release();
            }
            catch (ObjectDisposedException)
            {
                // The budget outlives every permit in normal use, but shutdown races exist and
                // failing to release into a disposed semaphore changes nothing.
            }
        }
    }
}
