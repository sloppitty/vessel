using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6: one import's coverage by Phase 0's 0.4b continuity rule. The boundary is the newest fill known before
/// the import; a capped read whose oldest raw fill is newer than it, or unknown, may have skipped executions.
/// </summary>
public sealed class HistoryCoverageTests
{
    private static readonly DateTimeOffset Read = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid(), Account = Guid.NewGuid();
    private static FillCoverageRecord For(bool? truncated, DateTimeOffset? oldest, DateTimeOffset? boundary, bool complete = false,
        string source = FillCoverageRecord.Check, DateTimeOffset? at = null) =>
        FillCoverageRecord.For(Owner, Account, source, at ?? Read, truncated, complete, oldest, boundary);

    [Fact]
    public void An_import_covers_from_what_was_known_before_it_or_records_the_gap_it_may_have_skipped()
    {
        var boundary = Read.AddDays(-2);
        // Unknown truncation records the request only, like 0.4b, which records nothing.
        var unknown = For(null, Read.AddDays(-1), boundary);
        Assert.Equal(((DateTimeOffset?)null, (DateTimeOffset?)null, (DateTimeOffset?)null, Read), (unknown.CoveredFromUtc, unknown.CoveredToUtc,
            unknown.GapFromUtc, unknown.RequestedToUtc));
        // Uncapped: continuous from the boundary, or from the oldest fill when it is earlier.
        Assert.Equal((boundary, Read), Range(For(false, Read.AddDays(-1), boundary)));
        Assert.Equal((Read.AddDays(-3), Read), Range(For(false, Read.AddDays(-3), boundary)));
        // Capped but reaching back past the boundary: still continuous.
        Assert.Null(For(true, Read.AddDays(-3), boundary).GapFromUtc);
        // Capped, its oldest fill newer than the boundary: a gap from the boundary to it; covered from it.
        var gap = For(true, Read.AddDays(-1), boundary);
        Assert.Equal((boundary, Read.AddDays(-1)), (gap.GapFromUtc!.Value, gap.GapToUtc!.Value));
        Assert.Equal((Read.AddDays(-1), Read), Range(gap));
        // Capped with no fill time: the whole span since the boundary may be missing, and nothing is proven.
        var blind = For(true, null, boundary);
        Assert.Equal((boundary, Read, (DateTimeOffset?)null), (blind.GapFromUtc!.Value, blind.GapToUtc!.Value, blind.CoveredFromUtc));
        // A first import has nothing before it to skip: covered from its oldest fill, and an empty one proves nothing.
        Assert.Equal((Read.AddDays(-5), Read), Range(For(true, Read.AddDays(-5), null)));
        Assert.Null(For(false, null, null).CoveredFromUtc);
        // Verified complete history covers everything up to the read.
        var complete = For(false, Read.AddDays(-1), boundary, complete: true);
        Assert.Equal((true, (DateTimeOffset?)null, Read), (complete.Complete, complete.CoveredFromUtc, complete.CoveredToUtc!.Value));
    }

    [Fact]
    public void Consecutive_continuous_imports_of_one_source_extend_one_record_and_a_gap_starts_another()
    {
        var first = For(false, Read.AddDays(-1), Read.AddDays(-2));
        var next = For(false, Read, Read.AddMinutes(-30), at: Read.AddMinutes(10));
        Assert.True(first.Continues(next));
        first.Extend(next);
        Assert.Equal((2, Read.AddDays(-2), Read.AddMinutes(10), Read.AddMinutes(10)), (first.ImportCount, first.CoveredFromUtc!.Value,
            first.CoveredToUtc!.Value, first.LastObservedAtUtc));
        // A gap, another source, unknown truncation or a start after the record's end never extends it.
        Assert.False(first.Continues(For(true, Read.AddMinutes(15), Read.AddMinutes(11), at: Read.AddMinutes(20))));
        Assert.False(first.Continues(For(false, Read, Read.AddMinutes(-30), source: FillCoverageRecord.Refresh, at: Read.AddMinutes(20))));
        Assert.False(first.Continues(For(null, Read, Read, at: Read.AddMinutes(20))));
        Assert.False(first.Continues(For(false, Read.AddMinutes(15), Read.AddMinutes(12), at: Read.AddMinutes(20))));
        Assert.Throws<Vessel.Domain.Common.DomainRuleException>(() => first.Extend(For(true, null, Read, at: Read.AddMinutes(20))));
    }

    private static (DateTimeOffset, DateTimeOffset) Range(FillCoverageRecord record) => (record.CoveredFromUtc!.Value, record.CoveredToUtc!.Value);
}
