using PanoramaBridge.Core.Storage;

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
    private readonly HashSet<string> _exclusions;

    /// <param name="extensions">
    /// Extensions to accept, with leading dots. An empty list accepts every file, which is what
    /// the settings screen means by leaving the box empty.
    /// </param>
    /// <param name="exclusions">
    /// Extensions the companion walk must not look past, with leading dots. Null takes
    /// <see cref="MonitoringConfiguration.DefaultExcludedExtensions"/>, so a caller that has never
    /// heard of the setting still gets the safe behavior; an empty list means excluding nothing,
    /// which is how somebody who wants every companion asks for it.
    /// <para>
    /// This list only narrows the companion walk. It is not where a rule that protects the
    /// application belongs, because a user can empty it -- see <see cref="IsWorkingFile"/>.
    /// </para>
    /// </param>
    public CandidateFilter(IEnumerable<string> extensions, IEnumerable<string>? exclusions = null)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        _extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _exclusions = (exclusions ?? MonitoringConfiguration.DefaultExcludedExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        // An empty extensions box means "everything the working-file and exclusion rules do not
        // reject". Answered here rather than by the walk below, because with nothing to match
        // against, the walk runs to the end of the name and tests every segment on the way --
        // which rejected QC.tmp.mzML, a finished mzML whose stem merely reads like a temporary
        // file. Only the actual extension decides.
        if (_extensions.Count == 0)
        {
            return !_exclusions.Contains(Path.GetExtension(name)) && !IsWorkingFile(name);
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
                // Out of extensions without reaching anything that was asked for.
                return false;
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
    /// <item>
    /// A name ending <c>.tmp</c>. A <c>run.raw.tmp</c> is by definition still being written, and
    /// uploading one is the single thing this application must never do -- so it belongs here
    /// and not in the exclusion list, which a user can empty. Matched on the end of the whole
    /// name rather than as one segment of the walk, so <c>QC.tmp.mzML</c> -- a finished mzML
    /// whose stem merely reads like a temporary file -- is unaffected.
    /// </item>
    /// <item>
    /// A sequence file the Thermo data system named for itself -- see
    /// <see cref="IsGeneratedSequence"/>.
    /// </item>
    /// </list>
    /// </remarks>
    private static bool IsWorkingFile(string name) =>
        name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || IsGeneratedSequence(name);

    /// <summary>
    /// Whether a sequence file was named by the data system rather than by a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Thermo data system leaves sequence files whose whole name is a GUID, like
    /// <c>203b13ca-0743-4a98-bed4-5830bfc7d826.sld</c>. They are working copies of the sequence
    /// being edited or run, and they accumulate: a lab asking for <c>.sld</c> wants the sequences
    /// somebody named and saved, not one of these per session.
    /// </para>
    /// <para>
    /// Unlike <c>.skyd</c>, this one a rule can actually decide. A Skyline cache is shaped
    /// exactly like a genuine companion, so telling them apart needs knowledge and the answer
    /// lives in a list a user can edit. A name that is a bare GUID carries nothing a person
    /// chose, which is checkable -- so it is decided here instead of being one more box to know
    /// about.
    /// </para>
    /// <para>
    /// Deliberately only <c>.sld</c>, and deliberately not every GUID-named file. A sequence is a
    /// working document and regenerable; an acquisition is not. Some pipelines do name
    /// acquisitions by GUID to keep them unique, and a rule broad enough to catch a temporary
    /// <c>.raw</c> would silently drop those -- losing data to save clutter, which is the wrong
    /// way round. If the data system turns out to do this for another extension, that is worth
    /// adding on evidence rather than by guessing at it now.
    /// </para>
    /// <para>
    /// TryParseExact with "D" rather than Guid.TryParse: the latter also accepts the braced and
    /// unhyphenated forms, and a thirty-two character hex name is a plausible thing for somebody
    /// to have chosen on purpose. Only the exact 8-4-4-4-12 shape the data system writes matches.
    /// </para>
    /// </remarks>
    private static bool IsGeneratedSequence(string name) =>
        Path.GetExtension(name).Equals(".sld", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "D", out _);
}
