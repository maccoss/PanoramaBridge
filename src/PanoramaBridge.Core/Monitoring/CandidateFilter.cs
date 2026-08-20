namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// Decides which files in a monitored tree are acquisition data worth transferring.
/// </summary>
/// <remarks>
/// <para>
/// One filter, shared by the periodic sweep and by the change watcher. They have to agree: a
/// rule applied in only one of them means a file arrives or does not depending on whether the
/// operating system happened to deliver a notification, which is the kind of difference nobody
/// can reproduce when it is reported.
/// </para>
/// <para>
/// Folder acquisitions -- Bruker <c>.d</c>, Waters <c>.raw</c> directories -- are not handled
/// here yet. They become atomic transfer items of their own rather than a filter rule, because
/// the decision is about when the whole folder is complete, not whether one file inside it
/// matches.
/// </para>
/// </remarks>
public sealed class CandidateFilter
{
    private readonly HashSet<string> _extensions;

    /// <param name="extensions">
    /// Extensions to accept, with leading dots. An empty list accepts every file, which is what
    /// the settings screen means by leaving the box empty.
    /// </param>
    public CandidateFilter(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        _extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A filter that accepts any file the working-file rules do not exclude.</summary>
    public static CandidateFilter Everything { get; } = new([]);

    /// <summary>Extensions accepted, for logging and for the status line.</summary>
    public IReadOnlyCollection<string> Extensions => _extensions;

    /// <summary>
    /// True when the file is one this application should try to transfer.
    /// </summary>
    /// <remarks>
    /// Extension matching goes through <see cref="Path.GetExtension(string)"/> rather than a
    /// suffix comparison, so a filter of <c>.raw</c> does not also match <c>archive.notraw</c>.
    /// </remarks>
    public bool Accepts(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);

        if (name.Length == 0)
        {
            return false;
        }

        // Instrument software and Windows both leave dot- and tilde-prefixed working files
        // behind, and a copy in progress is frequently one of them. They are never data.
        if (name.StartsWith('.') || name.StartsWith('~'))
        {
            return false;
        }

        return _extensions.Count == 0 || _extensions.Contains(Path.GetExtension(name));
    }
}
