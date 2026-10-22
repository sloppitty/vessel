using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>
/// One account's fill import coverage (Phase 2, item 6): what an import requested, what it covered and the gap it found,
/// by the same continuity rule as Phase 0's 0.4b. Consecutive continuous imports of one source extend a single record, so
/// the ledger grows with gaps and changes, not with every scheduled read.
/// </summary>
public sealed class FillCoverageRecord : IOwned
{
    public const string Refresh = "refresh", Check = "check", Backfill = "backfill";
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
    /// Phase 2, item 6b: what a finished backfill read proved. It asked for the gap's window and covers from the first
    /// time it can vouch for to the window's end; with nothing proven it records the request only.
    /// </summary>
    public static FillCoverageRecord ForBackfill(Guid ownerId, Guid accountId, DateTimeOffset readAt, DateTimeOffset requestedFrom,
        DateTimeOffset requestedTo, DateTimeOffset? coveredFrom, bool complete) => new()
    {
        Id = Guid.CreateVersion7(), OwnerId = ownerId, AccountId = accountId, Source = Backfill, FirstObservedAtUtc = readAt,
        LastObservedAtUtc = readAt, RequestedFromUtc = requestedFrom, RequestedToUtc = requestedTo, Truncated = false, Complete = complete,
        CoveredFromUtc = complete ? null : coveredFrom, CoveredToUtc = complete || coveredFrom is not null ? requestedTo : null
    };

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
    /// <summary>
    /// Phase 2, item 6b: a backfill showed the venue no longer serves this span (its executions are beyond the venue's
    /// reach). It stays open, since nothing proves it covered, and cannot be backfilled again.
    /// </summary>
    public bool BeyondVenueReach { get; set; }
    /// <summary>The backfill whose read left this span unproven, when it is what remains of a wider gap.</summary>
    public Guid? OpenedByBackfillId { get; set; }
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

/// <summary>
/// Phase 2, item 6b: an owner-started backfill of one history gap, read page by page behind protection checks. It creates
/// no Play, no thesis and no execution notification; the fills it finds go through the import's normal deduplication.
/// </summary>
public sealed class HistoryBackfill : IOwned
{
    public const string Queued = "queued", Completed = "completed", Narrowed = "narrowed", Failed = "failed";
    public const int MaxFailures = 3;
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public Guid GapId { get; set; }
    public string State { get; set; } = Queued;
    /// <summary>The window read: the gap's start (the epoch for a gap of unknown start) to its end, or to when it was started.</summary>
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    /// <summary>Whether the gap's start was known; a gap of unknown start closes only when the whole reach is read.</summary>
    public bool StartKnown { get; set; }
    /// <summary>Where the next page starts: the newest raw time read so far, inclusive.</summary>
    public DateTimeOffset CursorUtc { get; set; }
    public int Pages { get; set; }
    public int RawRows { get; set; }
    public int AddedFills { get; set; }
    public int OmittedExecutions { get; set; }
    /// <summary>The oldest raw row of the first page, which says whether the window's start is still served.</summary>
    public DateTimeOffset? FirstRawAtUtc { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool IsActive => State == Queued;

    public static HistoryBackfill For(AccountHistoryGap gap, DateTimeOffset now)
    {
        if (!gap.IsOpen || gap.BeyondVenueReach) throw new DomainRuleException("Only an open gap the venue may still serve can be backfilled.");
        var from = gap.FromUtc ?? DateTimeOffset.UnixEpoch;
        var to = gap.ToUtc ?? now;
        // Venue windows are whole milliseconds; round outward so the gap stays inside the window.
        from = new DateTimeOffset(from.Ticks - from.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        to = new DateTimeOffset(to.Ticks + (TimeSpan.TicksPerMillisecond - to.Ticks % TimeSpan.TicksPerMillisecond) % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        if (to > now) to = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        if (from > to) throw new DomainRuleException("The gap's window is in the future.");
        return new()
        {
            Id = Guid.CreateVersion7(), OwnerId = gap.OwnerId, AccountId = gap.AccountId, GapId = gap.Id, FromUtc = from, ToUtc = to,
            StartKnown = gap.FromUtc is not null, CursorUtc = from, CreatedAtUtc = now, UpdatedAtUtc = now
        };
    }

    /// <summary>
    /// Records one page read from <see cref="CursorUtc"/>. Returns whether the window is exhausted: a page that brings
    /// nothing newer than the cursor, with room left below the page limit. A full page at one time cannot be paged past,
    /// so the backfill fails; so does reading more rows than the venue's reach.
    /// </summary>
    public bool Read(int rawCount, DateTimeOffset? oldestRaw, DateTimeOffset? newestRaw, int added, int omitted, int pageLimit, int reach,
        DateTimeOffset now)
    {
        if (!IsActive) throw new DomainRuleException("The backfill has finished.");
        Pages = checked(Pages + 1);
        RawRows = checked(RawRows + rawCount);
        AddedFills = checked(AddedFills + added);
        OmittedExecutions = checked(OmittedExecutions + omitted);
        if (Pages == 1) FirstRawAtUtc = oldestRaw;
        UpdatedAtUtc = now;
        Failures = 0;
        Error = null;
        if (RawRows > reach + pageLimit) { Fail("The backfill read more executions than the venue serves.", now); return false; }
        if (newestRaw is not { } newest || newest <= CursorUtc)
        {
            if (rawCount < pageLimit) return true;
            Fail("The venue returned a full page at one time, which cannot be paged past.", now);
            return false;
        }
        CursorUtc = newest;
        return false;
    }

    /// <summary>
    /// What the exhausted window proves. A known start is proven when the venue still serves it: the first page's oldest
    /// row is the gap's own starting execution. Then the whole window is covered. Otherwise only from the oldest row read
    /// to the end is, and the span before it is beyond the venue's reach. An unknown start is proven only when the whole
    /// account history fits within the reach, so nothing older can have been dropped.
    /// </summary>
    public (bool Complete, DateTimeOffset? CoveredFrom, bool Narrowed) Conclude(int reach)
    {
        if (!StartKnown) return RawRows < reach ? (true, null, false) : (false, FirstRawAtUtc, true);
        if (FirstRawAtUtc == FromUtc) return (false, FromUtc, false);
        return (false, FirstRawAtUtc, true);
    }

    public void Finish(string state, DateTimeOffset now)
    {
        if (state is not (Completed or Narrowed)) throw new DomainValidationException("Unknown backfill outcome.");
        (State, FinishedAtUtc, UpdatedAtUtc) = (state, now, now);
    }

    /// <summary>A read that failed; after <see cref="MaxFailures"/> in a row the backfill stops with a safe message.</summary>
    public void Failure(string error, DateTimeOffset now)
    {
        Failures++;
        UpdatedAtUtc = now;
        Error = error;
        if (Failures >= MaxFailures) Fail(error, now);
    }

    /// <summary>Stops the backfill with a safe message; its gap stays as it was.</summary>
    public void Stop(string error, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainRuleException("The backfill has finished.");
        Fail(error, now);
    }

    private void Fail(string error, DateTimeOffset now) => (State, Error, FinishedAtUtc, UpdatedAtUtc) = (Failed, error, now, now);
}

