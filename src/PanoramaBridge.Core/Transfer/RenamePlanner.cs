using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Transfer;

/// <summary>One file and the name it would be sent under.</summary>
public readonly record struct ProposedRename(UploadRecord Record, string Name);

/// <summary>
/// The outcome of working out names for a batch of held files.
/// </summary>
/// <param name="Proposals">What would be sent, and under what name.</param>
/// <param name="Problem">
/// Why nothing can be proposed, written for somebody looking at a stalled transfer. Null when the
/// plan is usable.
/// </param>
public sealed record RenamePlan(IReadOnlyList<ProposedRename> Proposals, string? Problem)
{
    /// <summary>True when the plan can be carried out.</summary>
    public bool IsUsable => Problem is null;

    /// <summary>A plan that could not be made.</summary>
    public static RenamePlan Failed(string problem) => new([], problem);
}

/// <summary>
/// Works out what to call each file in a batch that is being sent alongside what is already there.
/// </summary>
/// <remarks>
/// <para>
/// In Core rather than in the view model that asks for it, because it is transfer logic: which
/// folder to look in, what is already occupying it, and what a free name looks like. It first
/// lived in the Uploads tab, where it duplicated the engine's own renaming, could not be tested
/// without a dispatcher, and quietly broke the rule that keeps this kind of decision out of the
/// WPF layer.
/// </para>
/// <para>
/// The names are worked out before anything is written so they can be shown and agreed to. They
/// are still re-checked by the engine immediately before the bytes move: this plan can be minutes
/// or a restart old by then, and probably-free is not good enough when the cost of being wrong is
/// somebody else's data.
/// </para>
/// </remarks>
public sealed class RenamePlanner
{
    private readonly IWebDavClient _client;

    public RenamePlanner(IWebDavClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Proposes a free name for each record.</summary>
    /// <remarks>
    /// One listing per destination folder covers the whole batch, and names handed out within it
    /// are remembered, so five hundred acquisitions landing in one folder do not all propose the
    /// same name.
    /// </remarks>
    public async Task<RenamePlan> PlanAsync(
        IEnumerable<UploadRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var taken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var proposals = new List<ProposedRename>();

        foreach (var record in records)
        {
            RemotePath path;

            try
            {
                path = RemotePath.Parse(record.RemotePath);
            }
            catch (ArgumentException)
            {
                // A row whose destination cannot be parsed is one nothing can be proposed for.
                // Skipped rather than failing the batch: the other files are still resolvable.
                continue;
            }

            var folder = path.Parent;
            var key = folder.ToEncodedString();

            if (!taken.TryGetValue(key, out var names))
            {
                try
                {
                    var listing = await _client
                        .ListAsync(folder, cancellationToken)
                        .ConfigureAwait(false);

                    names = listing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (WebDavException ex)
                {
                    return RenamePlan.Failed(ex.ToUserMessage());
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // The transport, not the protocol. This is what an unplugged network or a
                    // server that has gone away actually produces, and the client stays non-null
                    // through all of it, so it cannot be detected by asking whether we are
                    // connected.
                    return RenamePlan.Failed(
                        "The server could not be reached, so a free name could not be checked. "
                        + $"Try again once the connection is back. ({ex.Message})");
                }

                taken[key] = names;
            }

            var proposed = ConflictNames.NextFree(path.Name, names);

            // The suffix lengthens the name and the destination has limits. Refusing here keeps
            // the failure attached to the decision that caused it, rather than letting it become
            // a failed transfer minutes later carrying a message about character counts.
            var usable = PathSafety.ValidateSegment(proposed);

            if (!usable.IsValid)
            {
                return RenamePlan.Failed(
                    $"'{path.Name}' cannot be renamed automatically: {usable.Message} "
                    + "Rename it on disk, or keep the copy on the server.");
            }

            names.Add(proposed);
            proposals.Add(new ProposedRename(record, proposed));
        }

        return new RenamePlan(proposals, null);
    }
}
