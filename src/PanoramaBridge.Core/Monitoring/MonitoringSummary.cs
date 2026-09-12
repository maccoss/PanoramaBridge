namespace PanoramaBridge.Core.Monitoring;

/// <summary>What the status line says, and whether it is reporting a problem.</summary>
/// <param name="Line">One sentence, written for the person watching the window.</param>
/// <param name="Failed">
/// True when at least one folder could not be checked, so the window can show it as a failure
/// rather than as news.
/// </param>
public readonly record struct MonitoringLine(string Line, bool Failed);

/// <summary>
/// Turns what each configuration's last sweep found into one line.
/// </summary>
/// <remarks>
/// <para>
/// In <c>Core</c> rather than in the view model because it is the only part of the status line
/// that is a decision, and a decision about what to tell somebody when several folders disagree
/// should be testable without a dispatcher.
/// </para>
/// <para>
/// The decision that matters is that a failure is never averaged away. Configurations sweep on
/// their own schedules, so a line set by whichever reported last would announce a folder that
/// cannot be read and then clear it a moment later when a different folder reports it is fine.
/// A broken share on one instrument would flicker past and be gone, which is the opposite of what
/// a status line is for.
/// </para>
/// </remarks>
public static class MonitoringSummary
{
    /// <summary>
    /// Describes the most recent sweep from each configuration, keyed by its name.
    /// </summary>
    public static MonitoringLine Describe(IReadOnlyDictionary<string, SweepResult> sweeps)
    {
        ArgumentNullException.ThrowIfNull(sweeps);

        if (sweeps.Count == 0)
        {
            return new MonitoringLine("Monitoring.", false);
        }

        var broken = sweeps
            .Where(entry => entry.Value.Failed)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        if (broken.Length > 0)
        {
            // Whitespace as well as null. SweepResult.Failed is Problem being non-null, so an
            // empty one is still a failure -- and coalescing on null alone would put an empty
            // status line on an instrument computer, which reads as the application having lost
            // its place rather than as a folder it cannot read.
            var problem = string.IsNullOrWhiteSpace(broken[0].Value.Problem)
                ? "the folder could not be checked"
                : broken[0].Value.Problem!;

            // Unnamed when it is the only configuration, so the single-configuration case reads
            // exactly as it always has rather than growing a prefix nobody needs.
            var line = sweeps.Count == 1
                ? problem
                : broken.Length == 1
                    ? $"{broken[0].Key}: {problem}"
                    : $"{broken[0].Key}: {problem} ({broken.Length} configurations cannot be checked.)";

            return new MonitoringLine(line, true);
        }

        var offered = 0;
        var examined = 0;

        foreach (var result in sweeps.Values)
        {
            offered += result.Offered;
            examined += result.Examined;
        }

        // Said out loud once there is more than one, because "12 files checked" reads as the whole
        // machine and is only ever one folder's answer otherwise.
        var scope = sweeps.Count > 1 ? $" across {sweeps.Count} configurations" : string.Empty;

        return new MonitoringLine(
            offered > 0
                ? $"Monitoring - {offered} file(s) to transfer{scope}."
                : $"Monitoring - {examined} file(s) checked{scope}, all up to date.",
            false);
    }
}
