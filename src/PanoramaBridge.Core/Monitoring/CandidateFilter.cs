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
    /// <summary>
    /// Extensions the companion walk refuses to look past unless a user says otherwise.
    /// </summary>
    /// <remarks>
    /// These are files another program derives from an acquisition and leaves beside it. They
    /// have exactly the same shape as a genuine companion -- <c>run.raw.skyd</c> is built the
    /// same way as <c>run.wiff.scan</c> -- so no rule about the shape of a name can tell them
    /// apart, and this has to be knowledge rather than logic. It is a default rather than a
    /// constant because the next tool to write beside an acquisition should not need a release.
    /// <list type="bullet">
    /// <item>
    /// <c>.skyd</c> is Skyline's chromatogram cache. AutoQC commonly runs on the instrument
    /// computer and imports each acquisition as it appears, leaving <c>run.raw.skyd</c> next to
    /// <c>run.raw</c>. The walk reached <c>.raw</c> and took it, so a cache that can run to
    /// gigabytes -- and is rebuilt on every re-import -- was transferred as though it were an
    /// acquisition.
    /// </item>
    /// <item>
    /// <c>.tmp</c> because a <c>run.raw.tmp</c> is by definition still being written. Uploading
    /// one is the single thing this application must never do.
    /// </item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> DefaultExclusions { get; } = [".skyd", ".tmp"];

    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _exclusions;

    /// <param name="extensions">
    /// Extensions to accept, with leading dots. An empty list accepts every file, which is what
    /// the settings screen means by leaving the box empty.
    /// </param>
    /// <param name="exclusions">
    /// Extensions the companion walk must not look past, with leading dots. Null takes
    /// <see cref="DefaultExclusions"/>, so a caller that has never heard of the setting still
    /// gets the safe behaviour; an empty list means excluding nothing, which is how somebody who
    /// wants every companion asks for it.
    /// </param>
    public CandidateFilter(IEnumerable<string> extensions, IEnumerable<string>? exclusions = null)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        _extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _exclusions = (exclusions ?? DefaultExclusions).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A filter that accepts any file the working-file and exclusion rules do not reject.
    /// </summary>
    public static CandidateFilter Everything { get; } = new([]);

    /// <summary>Extensions accepted, for logging and for the status line.</summary>
    public IReadOnlyCollection<string> Extensions => _extensions;

    /// <summary>Extensions the walk will not look past, for logging and for diagnostics.</summary>
    public IReadOnlyCollection<string> Exclusions => _exclusions;

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

        // Companion files travel with the acquisition they belong to.
        //
        // Sciex writes run.wiff alongside run.wiff.scan, and the .wiff is metadata: the spectra
        // are in the .scan. Matching on Path.GetExtension alone sees ".scan" and leaves it
        // behind, so a user who asked for .wiff got 38 MB of a 13.7 GB acquisition -- recorded
        // as verified, because the one file that was sent did arrive intact. Nothing about that
        // is visible until somebody tries to open it in Skyline.
        //
        // So a name is accepted if removing trailing extensions one at a time reaches one that
        // was asked for. run.wiff.scan reaches run.wiff; run.wiff.dia.quant reaches it too.
        //
        // The walk stops at an excluded extension instead of looking past it, which is what
        // keeps it from reaching .raw through a Skyline cache named run.raw.skyd. Stopping
        // mid-walk rather than only inspecting the last extension is the part that matters:
        // run.raw.skyd.gz would otherwise carry straight on past .skyd and be accepted.
        //
        // A match is looked for before an exclusion, so the exclusion list can only narrow the
        // walk and can never veto something typed into the extensions box. Somebody who
        // deliberately asks for .skyd gets .skyd; the alternative is an empty queue and nothing
        // on screen to explain it.
        var candidate = name;

        while (true)
        {
            var extension = Path.GetExtension(candidate);

            if (extension.Length == 0)
            {
                // Out of extensions without reaching anything asked for. An empty extensions box
                // means "everything the working-file rules do not exclude", not literally every
                // file: our own .md5 sidecars, and the SQLite journals a vendor leaves beside a
                // run, are never data.
                return _extensions.Count == 0 && !IsWorkingFile(name);
            }

            if (_extensions.Contains(extension))
            {
                return !IsWorkingFile(name);
            }

            if (_exclusions.Contains(extension))
            {
                return false;
            }

            candidate = Path.GetFileNameWithoutExtension(candidate);
        }
    }

    /// <summary>
    /// Whether a name is something a program is using rather than something to transfer.
    /// </summary>
    /// <remarks>
    /// Checked only once a name has otherwise been accepted, so it costs nothing on the common
    /// path. Deliberately not folded into the user-editable exclusion list: these are the rules
    /// that stop the application tripping over its own output, so they are not a user's to
    /// remove.
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
