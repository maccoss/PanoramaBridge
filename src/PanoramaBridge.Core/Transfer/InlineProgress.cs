namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler inline, on the reporting thread.
/// </summary>
/// <remarks>
/// <para>
/// The framework's <see cref="Progress{T}"/> deliberately does not do this: it captures a
/// synchronization context and <em>posts</em> each report, falling back to the thread pool when
/// there is none. That is the right behaviour for a UI callback, and the wrong behaviour here,
/// because it means a report can be delivered after the code that follows it has already run.
/// </para>
/// <para>
/// Transfer progress feeds an aggregator where the newest report for a file wins, so ordering is
/// load-bearing: a posted "uploading, 100%" arriving after "verified" would flip a finished row
/// back to in-progress and leave it there. Reporting inline makes the sequence the engine emits
/// the sequence the consumer sees.
/// </para>
/// <para>
/// The consumer is therefore responsible for being cheap and thread-safe, which the aggregator
/// is by design.
/// </para>
/// </remarks>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <inheritdoc />
    public void Report(T value) => _handler(value);
}
