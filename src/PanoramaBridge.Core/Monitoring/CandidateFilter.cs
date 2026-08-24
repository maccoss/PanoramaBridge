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

        if (_extensions.Count == 0)
        {
            return true;
        }

        // Companion files travel with the acquisition they belong to.
        //
        // Sciex writes run.wiff alongside run.wiff.scan, and the .wiff is metadata: the spectra
        // are in the .scan. Matching on Path.GetExtension alone sees ".scan" and leaves it
        // behind, so a user who asked for .wiff got 38 MB of a 13.7 GB acquisition -- recorded
        // as verified, because the one file that was sent did arrive intact. Nothing about that
        // is visible until somebody tries to open it in Skyline.
        //
        // So a name is accepted if removing trailing extensions one at a time reaches one that
        // was asked for. run.wiff.scan reaches run.wiff; run.wiff.dia.quant reaches it too. The
        // rule is deliberately about the shape of the name rather than a list of vendor
        // suffixes, because the vendor that adds a new one will not tell us.
        var candidate = name;

        while (true)
        {
            var extension = Path.GetExtension(candidate);

            if (extension.Length == 0)
            {
                return false;
            }

            if (_extensions.Contains(extension))
            {
                return !IsWorkingFile(name);
            }

            candidate = Path.GetFileNameWithoutExtension(candidate);
        }
    }

    /// <summary>
    /// Whether a name is something a program is using rather than something to transfer.
    /// </summary>
    /// <remarks>
    /// Checked only once a name has otherwise been accepted, so it costs nothing on the common
    /// path.
    /// <list type="bullet">
    /// <item>
    /// SQLite's journal, write-ahead log and shared-memory files sit beside a database while it
    /// is open. Sciex leaves a <c>.wiff2-journal</c> next to every acquisition, and the extension
    /// walk above would otherwise reach <c>.wiff2</c> and accept it.
    /// </item>
    /// <item>
    /// The <c>.md5</c> sidecar this application writes itself. Without this, asking for
    /// <c>.raw</c> would reach <c>run.raw</c> from <c>run.raw.md5</c> and upload our own
    /// bookkeeping as though it were data.
    /// </item>
    /// </list>
    /// </remarks>
    private static bool IsWorkingFile(string name) =>
        name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase);
}
