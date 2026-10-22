using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>
/// One account's fill import coverage (Phase 2, item 6): what an import requested, what it covered and the gap it found,
/// by the same continuity rule as Phase 0's 0.4b. Consecutive continuous imports of one source extend a single record, so
/// the ledger grows with gaps and changes, not with every scheduled read.
/// </summary>
public sealed class FillCoverageRecord : IOwned
{
    public const string Refresh = "refresh", Check = "check";
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public string Source { get; set; } = Check;
    public DateTimeOffset FirstObservedAtUtc { get; set; }
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public int ImportCount { get; set; } = 1;
    /// <summary>The start the import asked for; null for the venue's own recent window, which is what Refresh and checks read.</summary>
    public DateTimeOffset? RequestedFromUtc { get; set; }
    public DateTimeOffset RequestedToUtc { get; set; }
    /// <summary>Whether the venue's cap was reached; null when unknown, which records no coverage and no gap.</summary>
    public bool? Truncated { get; set; }
    /// <summary>Verified complete history within the venue's scope; it covers everything up to <see cref="CoveredToUtc"/>.</summary>
    public bool Complete { get; set; }
    /// <summary>The span the import proves continuous; null when it proves none.</summary>
    public DateTimeOffset? CoveredFromUtc { get; set; }
    public DateTimeOffset? CoveredToUtc { get; set; }
    /// <summary>
    /// The span the import may have skipped, in execution time: from the newest fill known before it to its oldest raw fill.
    /// The end is null when the read reported no fill time.
    /// </summary>
    public DateTimeOffset? GapFromUtc { get; set; }
    public DateTimeOffset? GapToUtc { get; set; }

    /// <summary>
    /// The record for one import by the 0.4b rule. <paramref name="boundary"/> is the newest fill known before it; with no
    /// boundary (a first import) nothing can be skipped. A capped read whose oldest raw fill is newer than the boundary, or
    /// unknown, leaves a gap. Otherwise it is continuous from the earlier of the boundary and its oldest raw fill: the
    /// read joins what was known before, and returned everything from its oldest row on.
    /// </summary>
    public static FillCoverageRecord For(Guid ownerId, Guid accountId, string source, DateTimeOffset readAt, bool? truncated,
        bool complete, DateTimeOffset? oldestRawFill, DateTimeOffset? boundary)
    {
        if (source is not (Refresh or Check)) throw new DomainValidationException("Unknown coverage source.");
        var record = new FillCoverageRecord
        {
            Id = Guid.CreateVersion7(), OwnerId = ownerId, AccountId = accountId, Source = source, FirstObservedAtUtc = readAt,
            LastObservedAtUtc = readAt, RequestedToUtc = readAt, Truncated = truncated, Complete = complete && truncated is not null
        };
        if (truncated is null) return record;
        if (record.Complete) { record.CoveredToUtc = readAt; return record; }
        if (truncated == true && boundary is { } known && (oldestRawFill is null || oldestRawFill > known))
        {
            // Both ends are execution times, so venue clock skew against the read time can never invert the span (Sol round 1).
            (record.GapFromUtc, record.GapToUtc) = (known, oldestRawFill);
            if (oldestRawFill is { } oldestRead && oldestRead <= readAt) (record.CoveredFromUtc, record.CoveredToUtc) = (oldestRead, readAt);
            return record;
        }
        var start = boundary is null ? oldestRawFill : oldestRawFill is null || boundary < oldestRawFill ? boundary : oldestRawFill;
        if (start is { } from && from <= readAt) (record.CoveredFromUtc, record.CoveredToUtc) = (from, readAt);
        return record;
    }

    /// <summary>
    /// Whether <paramref name="next"/> continues this record: both continuous imports of the same source over the venue's
    /// recent window, with the next one starting at or before this one's end.
    /// </summary>
    public bool Continues(FillCoverageRecord next) => Source == next.Source && RequestedFromUtc is null && next.RequestedFromUtc is null &&
        GapFromUtc is null && next.GapFromUtc is null && !Complete && !next.Complete && Truncated is not null && next.Truncated is not null &&
        CoveredToUtc is { } end && next.CoveredFromUtc is { } start && start <= end && next.LastObservedAtUtc >= LastObservedAtUtc;

    public void Extend(FillCoverageRecord next)
    {
        if (!Continues(next)) throw new DomainRuleException("Only a continuous import of the same source extends a coverage record.");
        LastObservedAtUtc = next.LastObservedAtUtc;
        RequestedToUtc = next.RequestedToUtc;
        // A later read can reach further back than this record did (Sol round 1).
        if (next.CoveredFromUtc < CoveredFromUtc) CoveredFromUtc = next.CoveredFromUtc;
        CoveredToUtc = next.CoveredToUtc;
        Truncated = Truncated == true || next.Truncated == true;
        ImportCount = checked(ImportCount + 1);
    }
}

/// <summary>
/// A span of an account's execution history that may be missing (Phase 2, item 6). It closes only when a read proves it
/// covered, never on a successful call alone. A gap recorded before the ledger existed has no known range.
/// </summary>
public sealed class AccountHistoryGap : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>Null when the start is unknown (a gap recorded before the ledger).</summary>
    public DateTimeOffset? FromUtc { get; set; }
    /// <summary>Null when the end is unknown (a gap recorded before the ledger).</summary>
    public DateTimeOffset? ToUtc { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; }
    public Guid? OpenedByCoverageId { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public Guid? ClosedByCoverageId { get; set; }
    public bool IsOpen => ClosedAtUtc is null;

    /// <summary>
    /// Another import found the same possible gap again, from the same execution (Sol round 1): it widens this one rather
    /// than opening a copy. An unknown end stays unknown.
    /// </summary>
    public void Widen(DateTimeOffset? to)
    {
        if (!IsOpen) throw new DomainRuleException("A closed gap is not widened.");
        ToUtc = ToUtc is null || to is null ? null : to > ToUtc ? to : ToUtc;
    }

    /// <summary>Closes the gap on proof: a read that covered it. Closing is final.</summary>
    public void Close(Guid coverageId, DateTimeOffset now)
    {
        if (!IsOpen) return;
        (ClosedAtUtc, ClosedByCoverageId) = (now, coverageId);
    }
}
