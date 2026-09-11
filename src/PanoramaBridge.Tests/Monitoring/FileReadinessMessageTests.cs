using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// The sentences shown beside a file that is not ready yet.
/// </summary>
/// <remarks>
/// These are read by somebody standing at an instrument wondering where their data is, so they
/// are worth asserting on directly rather than only through the gate that produces them.
/// </remarks>
public sealed class FileReadinessMessageTests
{
    [Fact]
    public void A_growing_file_reports_its_size_in_units_a_person_reads()
    {
        // 7.8 GB, not 8,415,481,856. Nobody counts digits to find out how big an acquisition
        // is. Binary units, so this is the number Explorer shows for the same file -- 8,415,481,856
        // bytes is 7.8 GiB, and would be 8.4 only if these were decimal gigabytes.
        var readiness = FileReadiness.Growing(8_412_336_128, 8_415_481_856);

        readiness.Detail.ShouldContain("7.8 GB");
        readiness.Detail.ShouldNotContain("8,415,481,856");
        readiness.Reason.ShouldBe(ReadinessReason.Growing);
        readiness.Length.ShouldBe(8_415_481_856);
    }

    [Fact]
    public void The_change_is_stated_outright_because_rounding_would_hide_it()
    {
        // The reason this is not the old sentence in bigger units. Both ends of a few megabytes
        // of growth on an 8 GB file round to the same string, so a message built from the two
        // sizes would read "8.4 GB to 8.4 GB" -- a sentence saying the file is still being
        // written, beside two identical numbers saying it is not.
        var readiness = FileReadiness.Growing(8_412_336_128, 8_415_481_856);

        readiness.Detail.ShouldContain("up 3.0 MB");
    }

    [Fact]
    public void A_file_that_shrank_is_not_described_as_growing()
    {
        // The tracker calls this whenever the size CHANGED, and a file being replaced or
        // rewritten can shrink. Saying "up" about that would be plainly wrong on screen.
        var readiness = FileReadiness.Growing(4096, 1024);

        readiness.Detail.ShouldContain("down 3.0 KB");
        readiness.Detail.ShouldNotContain("up ");
    }

    [Fact]
    public void A_small_file_still_reads_in_bytes()
    {
        // Not everything is an acquisition. A few hundred bytes should say so rather than
        // rounding to 0.0 KB.
        FileReadiness.Growing(100, 400).Detail.ShouldContain("400 B");
    }
}
