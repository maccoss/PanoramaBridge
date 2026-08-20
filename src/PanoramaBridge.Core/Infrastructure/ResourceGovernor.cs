using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Keeps the application out of the way of the software that actually runs the instrument.
/// </summary>
/// <remarks>
/// <para>
/// This runs on the computer attached to a mass spectrometer, alongside the vendor's acquisition
/// software. Losing an acquisition because a file-transfer utility was competing for the
/// processor or the disk would be a far worse outcome than a transfer finishing later, so the
/// application is deliberately configured to yield.
/// </para>
/// <para>
/// Three separate settings matter, and priority alone is not enough:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Process priority</b> below normal, so any thread of the acquisition software preempts ours
/// whenever both are runnable.
/// </item>
/// <item>
/// <b>Working set</b>, trimmed when the application goes quiet. A monitor that sits idle for days
/// holding on to a hundred megabytes is taking memory the acquisition software may want.
/// </item>
/// </list>
/// <para>
/// Windows also offers a process-wide background mode
/// (<c>PROCESS_MODE_BACKGROUND_BEGIN</c>) that lowers disk <em>and</em> memory priority. It is
/// deliberately not used, because measurement showed it is actively harmful for a long-running
/// windowed application: idle processor use went from 0.3% of a core to 41%. Lowest memory
/// priority means Windows trims the working set aggressively -- it fell from 135 MB to 32 MB --
/// and the process then spends its life faulting those pages back in. That mode is designed for
/// short-lived background work such as an indexer, not for a process that must sit quietly for
/// weeks. Processor priority plus modest transfer concurrency achieves the goal without it.
/// </para>
/// </remarks>
public sealed class ResourceGovernor
{
    private readonly ILogger<ResourceGovernor> _log;

    public ResourceGovernor(ILogger<ResourceGovernor>? log = null) =>
        _log = log ?? NullLogger<ResourceGovernor>.Instance;

    /// <summary>
    /// Applies the polite defaults. Called once at startup.
    /// </summary>
    /// <param name="yieldToOtherSoftware">
    /// When true, lowers processor priority so acquisition software always preempts this process.
    /// </param>
    public void ApplyPoliteDefaults(bool yieldToOtherSoftware = true)
    {
        if (!yieldToOtherSoftware)
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();

            // Below normal rather than Idle: Idle can starve the process entirely on a busy
            // machine, which would leave transfers permanently stalled rather than merely slow.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

            // Windows schedules long stretches of below-normal work better when it knows the
            // process is not the one the user is interacting with.
            process.PriorityBoostEnabled = false;

            _log.LogInformation(
                "Running at {Priority} priority so instrument software always takes precedence.",
                process.PriorityClass);
        }
        catch (Exception ex)
        {
            // Politeness is a preference, not a prerequisite.
            _log.LogWarning(ex, "Could not lower the process priority.");
        }
    }

    /// <summary>
    /// Hands unused memory back to the operating system.
    /// </summary>
    /// <remarks>
    /// Called when the application goes quiet after a transfer. The pages are not lost -- they
    /// are paged back in if needed -- but an idle monitor should not be holding a working set it
    /// only needed while hashing gigabytes. This is a real effect, not cosmetic: the memory
    /// becomes available to the acquisition software.
    /// </remarks>
    public void ReleaseIdleMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetCurrentProcess();
                EmptyWorkingSet(process.Handle);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not trim the working set.");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
