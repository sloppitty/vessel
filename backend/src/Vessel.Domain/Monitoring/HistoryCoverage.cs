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
    /// Phase 2, item 6b: what a finished backfill read proved. It asked for the gap's window and covers what its witnesses
    /// vouch for; with nothing proven it records the request only.
    /// </summary>
    public static FillCoverageRecord ForBackfill(Guid ownerId, Guid accountId, DateTimeOffset readAt, DateTimeOffset requestedFrom,
        DateTimeOffset requestedTo, DateTimeOffset? coveredFrom, DateTimeOffset coveredTo, bool complete) => new()
    {
        Id = Guid.CreateVersion7(), OwnerId = ownerId, AccountId = accountId, Source = Backfill, FirstObservedAtUtc = readAt,
        LastObservedAtUtc = readAt, RequestedFromUtc = requestedFrom, RequestedToUtc = requestedTo, Truncated = false, Complete = complete,
        CoveredFromUtc = complete ? null : coveredFrom is { } from && from <= coveredTo ? from : null,
        CoveredToUtc = complete || coveredFrom is { } start && start <= coveredTo ? coveredTo : null
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
/// Phase 2, item 6b: an owner-started backfill of one history gap, read in its own runs behind protection checks. It
/// creates no Play, no thesis and no notification; the fills it finds go through the import's normal deduplication and
/// are marked as backfilled.
/// </summary>
/// <remarks>
/// The venue serves only an account's most recent executions, so the reach can move while a backfill reads. Continuity is
/// proven by witnesses: every page must return again each raw row the previous page ended on (the first page: each
/// execution Vessel retained at the gap's start). A missing witness means the reach moved past it, and only what was
/// proven before it is covered. This relies on the venue serving rows in time order and cutting the oldest first.
/// </remarks>
public sealed class HistoryBackfill : IOwned
{
    public const string Queued = "queued", Completed = "completed", Narrowed = "narrowed", Failed = "failed";
    public const int MaxFailures = 3;
    /// <summary>How long a backfill waits between runs, so it stays behind protection checks and the venue's budget.</summary>
    public static readonly TimeSpan Pacing = TimeSpan.FromMinutes(1);
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public Guid GapId { get; set; }
    public string State { get; set; } = Queued;
    /// <summary>
    /// The window read: from the gap's start (the epoch for a gap of unknown start) to the latest page's time. Every page
    /// reads to now: only that proves the whole history fits the reach, and it covers a gap that widened meanwhile.
    /// </summary>
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    /// <summary>Whether the gap's start was known; a gap of unknown start closes only when the whole reach is read.</summary>
    public bool StartKnown { get; set; }
    /// <summary>Where the next page starts: the newest raw time read so far, inclusive.</summary>
    public DateTimeOffset CursorUtc { get; set; }
    /// <summary>The raw row IDs at <see cref="CursorUtc"/> the next page must return again, as a JSON array.</summary>
    public string Witness { get; set; } = "[]";
    public int Pages { get; set; }
    /// <summary>Distinct raw rows read: a witness returned again is not counted twice.</summary>
    public int RawRows { get; set; }
    public int AddedFills { get; set; }
    public int OmittedExecutions { get; set; }
    /// <summary>The oldest raw row of the first page.</summary>
    public DateTimeOffset? FirstRawAtUtc { get; set; }
    /// <summary>Whether the first page returned every execution retained at the gap's start; null before it, or without a known start.</summary>
    public bool? StartServed { get; set; }
    /// <summary>Set when a later page no longer returned the previous page's witnesses: nothing after the cursor is proven.</summary>
    public bool ContinuityLost { get; set; }
    public bool Exhausted { get; set; }
    /// <summary>When the scheduler may read its next page; it reads behind protection checks.</summary>
    public DateTimeOffset NextPageAtUtc { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool IsActive => State == Queued;

    public static HistoryBackfill For(AccountHistoryGap gap, DateTimeOffset now)
    {
        if (!gap.IsOpen || gap.BeyondVenueReach) throw new DomainRuleException("Only an open gap the venue may still serve can be backfilled.");
        var from = Floor(gap.FromUtc ?? DateTimeOffset.UnixEpoch);
        // Every page reads to now, so the window ends where its latest page did.
        var to = Floor(now);
        if (from > to) throw new DomainRuleException("The gap's window is in the future.");
        return new()
        {
            Id = Guid.CreateVersion7(), OwnerId = gap.OwnerId, AccountId = gap.AccountId, GapId = gap.Id, FromUtc = from, ToUtc = to,
            StartKnown = gap.FromUtc is not null, CursorUtc = from, NextPageAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        };
    }

    public static DateTimeOffset Floor(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>The raw row IDs the next page must return again.</summary>
    public IReadOnlyList<string> Witnesses() => System.Text.Json.JsonSerializer.Deserialize<string[]>(Witness) ?? [];

    /// <summary>
    /// Records one page read from <see cref="CursorUtc"/> to <paramref name="windowTo"/>. <paramref name="expected"/> are the
    /// witnesses it must return: the previous page's last rows, or for the first page the executions retained at the gap's
    /// start. Returns whether the window is finished: exhausted (a page with nothing newer than the cursor, below the page
    /// limit) or broken (a later page without its witnesses). A full page at one time, or more rows than the venue serves,
    /// fails the backfill.
    /// </summary>
    public bool Read(IReadOnlyList<(string Id, DateTimeOffset At)> raw, DateTimeOffset windowFrom, int added, int omitted,
        IReadOnlyCollection<string> expected, DateTimeOffset windowTo, int pageLimit, int reach, DateTimeOffset now)
    {
        if (!IsActive) throw new DomainRuleException("The backfill has finished.");
        // The first page may start at the witness before the gap's start; every later one at the cursor.
        if (Pages == 0 ? windowFrom > FromUtc : windowFrom != CursorUtc) throw new DomainRuleException("The page does not continue the backfill.");
        if (raw.Any(r => r.At < windowFrom || r.At > windowTo)) throw new DomainRuleException("The page returned rows outside its window.");
        CursorUtc = windowFrom;
        NextPageAtUtc = now + Pacing;
        var ids = raw.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var witnessed = expected.Count > 0 && expected.All(ids.Contains);
        var first = Pages == 0;
        Pages = checked(Pages + 1);
        RawRows = checked(RawRows + raw.Count - (first ? 0 : expected.Count(ids.Contains)));
        AddedFills = checked(AddedFills + added);
        OmittedExecutions = checked(OmittedExecutions + omitted);
        if (windowTo > ToUtc) ToUtc = windowTo;
        UpdatedAtUtc = now;
        Failures = 0;
        Error = null;
        if (first)
        {
            FirstRawAtUtc = raw.Count == 0 ? null : raw.Min(r => r.At);
            // Without a known start there is nothing to witness: the read starts at the epoch.
            if (StartKnown) StartServed = witnessed;
        }
        else if (!witnessed)
        {
            // The reach moved past the previous page's last rows: what followed them may be gone.
            ContinuityLost = true;
            return true;
        }
        if (RawRows > reach + pageLimit) { Fail("The backfill read more executions than the venue serves.", now); return false; }
        var newest = raw.Count == 0 ? (DateTimeOffset?)null : raw.Max(r => r.At);
        if (newest is not { } latest || latest <= CursorUtc)
        {
            if (raw.Count < pageLimit) { Exhausted = true; return true; }
            Fail("The venue returned a full page at one time, which cannot be paged past.", now);
            return false;
        }
        CursorUtc = latest;
        Witness = System.Text.Json.JsonSerializer.Serialize(raw.Where(r => r.At == latest).Select(r => r.Id).Distinct().ToArray());
        return false;
    }

    /// <summary>What the finished window proves; see <see cref="Conclude"/>.</summary>
    /// <param name="Complete">The whole history up to <paramref name="CoveredTo"/> is retained.</param>
    /// <param name="CoveredFrom">Where the proven span starts; null when nothing is proven, or with complete history.</param>
    /// <param name="StartBeyondReach">The venue no longer serves what lies before the proven span.</param>
    public readonly record struct Proof(bool Complete, DateTimeOffset? CoveredFrom, DateTimeOffset CoveredTo, bool StartBeyondReach);

    /// <summary>
    /// What the finished window proves. A known start the venue still served, with continuity kept, proves the whole window;
    /// otherwise only from the first page's oldest row. A gap of unknown start, read to now with continuity kept, proves the
    /// whole history only when it fits within the reach: had anything older been dropped when the first page was read, the
    /// reach would have been full from that page on, and the backfill would have read at least the reach. Lost continuity
    /// proves nothing after the cursor, which stays open as a remainder.
    /// </summary>
    public Proof Conclude(int reach)
    {
        var end = ContinuityLost ? CursorUtc : ToUtc;
        if (!StartKnown)
            return !ContinuityLost && Exhausted && RawRows < reach ? new(true, null, end, false) : new(false, FirstRawAtUtc, end, true);
        return StartServed == true ? new(false, FromUtc, end, false) : new(false, FirstRawAtUtc, end, true);
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
        NextPageAtUtc = now + Pacing;
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
