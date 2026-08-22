namespace PanoramaBridge.ThermoRaw.Tests;

/// <summary>
/// What the checker says about files that are and are not short.
/// </summary>
/// <remarks>
/// The fixtures are synthetic; see <see cref="SyntheticRawFile"/> for what that does and does not
/// prove. What is being tested here is the decision, not the format: that running off the end of
/// a file is reported as truncation, that a layout nobody understands is reported as unknown, and
/// above all that unknown never becomes a reason to hold a file back.
/// </remarks>
public sealed class ThermoRawValidatorTests
{
    private static ThermoRawResult Check(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ThermoRawValidator.Validate(stream, bytes.Length, "synthetic.raw");
    }

    [Fact]
    public void A_well_formed_file_reports_no_truncation()
    {
        var result = Check(SyntheticRawFile.Valid());

        result.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);
        result.FormatVersion.ShouldBe(66);
        result.AcquisitionFinished.ShouldBe(true);
        result.IsProvenTruncated.ShouldBeFalse();
    }

    [Fact]
    public void The_verdict_never_claims_the_file_is_complete()
    {
        // Deliberate. Proving a RAW file whole needs terminal-record analysis this does not do,
        // and a verdict that sounded like proof would be read as proof -- the exact failure the
        // Verified/Uploaded distinction exists to avoid elsewhere in this codebase.
        var result = Check(SyntheticRawFile.Valid());

        result.Summary.ShouldBe("No truncation detected");
        Enum.GetNames<ThermoRawVerdict>().ShouldNotContain("Complete");
    }

    [Fact]
    public void A_file_cut_short_is_proven_truncated()
    {
        // The case the whole thing exists for: a copy that died part-way. Nothing holds the file
        // open and its size is perfectly stable, so the ordinary readiness signals see nothing
        // wrong with it.
        var whole = SyntheticRawFile.Valid();
        var cut = whole[..(whole.Length / 2)];

        var result = Check(cut);

        result.Verdict.ShouldBe(ThermoRawVerdict.Truncated);
        result.IsProvenTruncated.ShouldBeTrue();
        result.Evidence.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_scan_index_larger_than_the_file_is_proven_truncated()
    {
        // Structurally intact as far as it goes, but the index describes more scans than there is
        // room for. This is what a file missing its tail looks like when the header survived.
        var bytes = new SyntheticRawFile { ScanCount = 500_000, TrailingBytes = 1024 }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Truncated);
        result.RequiredBytes.ShouldNotBeNull();
        result.RequiredBytes!.Value.ShouldBeGreaterThan(result.FileSize);
        result.Summary.ShouldContain("Truncated");
    }

    [Fact]
    public void A_pointer_past_the_end_is_proven_truncated()
    {
        var bytes = new SyntheticRawFile { ScanIndexAddressOverride = 900_000_000 }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Truncated);
    }

    [Fact]
    public void A_nonsensical_pointer_is_not_called_truncation()
    {
        // Found reviewing this code against the reference it is ported from. Every structural
        // problem was being reported as Truncated, including ones that say nothing about missing
        // bytes. That matters more here than in a reporting tool: this one gates transfers, so a
        // malformed field mistaken for truncation holds back a file that is perfectly whole --
        // the exact failure the "unknown never blocks" rule exists to prevent, arrived at from
        // the other direction.
        var bytes = new SyntheticRawFile { ZeroScanIndexPointer = true }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Unknown);
        result.Reason.ShouldBe(ThermoRawUnknownReason.LayoutNotUnderstood);
        result.IsProvenTruncated.ShouldBeFalse("nothing here shows a single byte is missing");
    }

    [Fact]
    public void A_scan_range_that_describes_no_scans_is_not_called_truncation()
    {
        var bytes = new SyntheticRawFile { ReversedScanRange = true }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Unknown);
        result.Reason.ShouldBe(ThermoRawUnknownReason.LayoutNotUnderstood);
    }

    [Fact]
    public void A_waters_raw_directory_is_not_reported_as_a_missing_file()
    {
        // Waters writes .raw as a folder, and this lab has both vendors. "The file is not there"
        // would send somebody looking for something that was never missing.
        var directory = Directory.CreateTempSubdirectory("pb-waters-").FullName;
        var asRaw = Path.Combine(Path.GetDirectoryName(directory)!, $"{Guid.NewGuid():N}.raw");

        try
        {
            Directory.Move(directory, asRaw);

            var result = ThermoRawValidator.Validate(asRaw);

            result.Verdict.ShouldBe(ThermoRawVerdict.NotThermoRaw);
            result.Evidence.ShouldContain(e => e.Contains("directory"));
            result.IsProvenTruncated.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(asRaw))
            {
                Directory.Delete(asRaw, recursive: true);
            }
        }
    }

    [Fact]
    public void An_unfinished_acquisition_is_reported_separately_from_truncation()
    {
        // A run that was aborted can be perfectly well-formed for as far as it goes. Calling that
        // truncation would be wrong, and would send someone looking for missing bytes that are
        // not missing.
        var bytes = new SyntheticRawFile { Finalised = false }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.NotFinalised);
        result.AcquisitionFinished.ShouldBe(false);
        result.IsProvenTruncated.ShouldBeFalse("nothing about it is short");
    }

    [Fact]
    public void An_unrecognised_revision_is_unknown_and_says_so()
    {
        // The one that must never block. Thermo ships new revisions; if an unfamiliar one stopped
        // uploads, a firmware update would silently halt an instrument.
        var bytes = new SyntheticRawFile { FormatVersion = 70 }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Unknown);
        result.Reason.ShouldBe(ThermoRawUnknownReason.UnrecognisedFormatVersion);
        result.FormatVersion.ShouldBe(70);
        result.IsProvenTruncated.ShouldBeFalse();
        result.Summary.ShouldContain("70", customMessage: "the revision has to be recoverable from the record");
    }

    [Fact]
    public void A_recognised_but_unconfirmed_revision_is_also_unknown()
    {
        var bytes = new SyntheticRawFile { FormatVersion = 8 }.Build();

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.Unknown);
        result.Reason.ShouldBe(ThermoRawUnknownReason.UnconfirmedFormatVersion);
    }

    [Fact]
    public void Only_truncation_ever_holds_a_file_back()
    {
        // Stated once, over every verdict, because this is the property that keeps a validator
        // from becoming an outage.
        foreach (var verdict in Enum.GetValues<ThermoRawVerdict>())
        {
            var result = new ThermoRawResult(
                "x.raw", verdict, ThermoRawUnknownReason.None, 66, 100, null, true, []);

            result.IsProvenTruncated.ShouldBe(verdict == ThermoRawVerdict.Truncated);
        }
    }

    [Fact]
    public void Something_that_is_not_a_raw_file_is_said_so_plainly()
    {
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0x00;
        bytes[1] = 0x00;

        var result = Check(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.NotThermoRaw);
        result.IsProvenTruncated.ShouldBeFalse();
    }

    [Fact]
    public void A_file_too_small_to_hold_a_header_is_not_called_truncated()
    {
        // Tempting to call this truncation, and wrong: nothing establishes it was ever a RAW
        // file, so there is nothing to say is missing from it.
        var result = Check(new byte[100]);

        result.Verdict.ShouldBe(ThermoRawVerdict.NotThermoRaw);
    }

    [Fact]
    public void An_empty_file_is_handled()
    {
        Check([]).Verdict.ShouldBe(ThermoRawVerdict.NotThermoRaw);
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(ulong.MaxValue, false)]
    [InlineData((ulong)long.MaxValue + 1, false)]
    [InlineData(133_000_000_000_000_000UL, true)]
    public void A_nonsensical_filetime_is_no_timestamp_rather_than_a_wrong_one(
        ulong fileTime, bool expected)
    {
        // Above long.MaxValue the cast to long wraps to a negative number, which is not rejected
        // as garbage -- it becomes a plausible-looking wrong date, which is worse than none.
        (ThermoRawHeader.ToTimestamp(fileTime) is not null).ShouldBe(expected);
    }

    [Theory]
    [InlineData("run.raw", true)]
    [InlineData("run.RAW", true)]
    [InlineData("run.Raw", true)]
    [InlineData("run.mzML", false)]
    [InlineData("raw", false)]
    [InlineData("run.raw.md5", false)]
    public void Candidates_are_recognised_by_extension(string name, bool expected)
    {
        ThermoRawValidator.IsCandidate(name).ShouldBe(expected);
    }

    [Fact]
    public void A_missing_file_is_an_error_rather_than_a_verdict_about_its_contents()
    {
        var result = ThermoRawValidator.Validate(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.raw"));

        result.Verdict.ShouldBe(ThermoRawVerdict.Error);
        result.IsProvenTruncated.ShouldBeFalse();
    }

    [Fact]
    public void Checking_a_real_file_on_disk_works_end_to_end()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pb-raw-{Guid.NewGuid():N}.raw");

        try
        {
            File.WriteAllBytes(path, SyntheticRawFile.Valid());

            var result = ThermoRawValidator.Validate(path);

            result.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);
            result.FileSize.ShouldBe(new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
