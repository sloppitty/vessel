using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Monitoring;

/// <param name="Outcome"><c>unverifiable</c> when the subject stayed unobservable beyond the monitor lifetime; the
/// record is neither resolved nor still checked. Null otherwise.</param>
/// <param name="LastObservedAtUtc">Source time of the last decisive (fresh present or absent) observation; null when none
/// is recorded.</param>
public sealed record MonitorDiscrepancyDto(Guid Id, Guid AccountId, Guid? PlayId, string ContractId, string Kind,
    string Severity, string State, DateTimeOffset OpenedAtUtc, DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? ResolvedAtUtc, DateTimeOffset? DismissedAtUtc, string? Note, JsonElement Details,
    string? Outcome = null, DateTimeOffset? UnverifiableAtUtc = null, DateTimeOffset? LastObservedAtUtc = null);
/// <summary>
/// Evidence for one planned stop level: <c>resting</c> (a linked closing stop rested in the current book at
/// <paramref name="ObservedAtUtc"/>, at the planned price within the matcher's step), <c>resting-elsewhere</c> (the same,
/// at another price), <c>not-seen</c> (a complete, fresh book had none), <c>unknown</c> or <c>not-read</c> (the venue's
/// order reader does not return the current book). <paramref name="OrderId"/> is the venue order ID of the linked order
/// concerned, when there is one. <paramref name="Price"/> is that resting order's acting price and
/// <paramref name="PlannedPrice"/> the saved level price the check compared it with, both exact; null when unknown.
/// </summary>
public sealed record MonitorStopEvidenceDto(string EntryId, string LevelId, string Evidence, string? OrderId, DateTimeOffset? ObservedAtUtc,
    string? Price = null, string? PlannedPrice = null);

/// <summary>
/// The last stop-coverage outcome recorded for an Open Play. <paramref name="State"/> is projected at read time:
/// <c>stale</c> when <paramref name="CheckedAtUtc"/> is older than twice the monitor interval, otherwise
/// <paramref name="RecordedState"/> (<c>known</c>, <c>shared</c>, <c>unknown</c> or <c>unsupported</c>).
/// <paramref name="Covered"/> is set only for a recorded <c>known</c> state.
/// </summary>
public sealed record MonitorCoverageDto(Guid PlayId, string ContractId, string State, string RecordedState, bool? Covered,
    string Reason, DateTimeOffset CheckedAtUtc, IReadOnlyList<MonitorStopEvidenceDto> Stops);

/// <summary>One Open Play's coverage as a check evaluated it, before it is stored.</summary>
public sealed record MonitorCoverageOutcome(Guid PlayId, string ContractId, string State, bool? Covered, string Reason,
    IReadOnlyList<MonitorStopEvidenceDto> Stops);

/// <param name="DiscrepanciesTruncated">More visible records exist than <paramref name="Discrepancies"/> lists (at most
/// <see cref="MonitorDiscrepancyList.MaxRecords"/>). Outstanding incidents are listed first, so only older records are left
/// out unless the incidents alone exceed the cap. Optional: an older producer omits it.</param>
/// <param name="Coverage">Stored stop coverage of the account's Open Plays, stale projected at read time. Optional: an
/// older producer may omit it.</param>
/// <param name="UncheckedPlays">Open, tracked Plays on the account with no recorded coverage yet (after the migration, or
/// once reconciliation opens one, until the next check). Never "Monitored" while above zero. Optional, like
/// <paramref name="Coverage"/>.</param>
public sealed record AccountMonitorDto(Guid AccountId, string State, DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? NextRunAtUtc, string ManualState, string? RunTrigger,
    DateTimeOffset? NextManualAllowedAtUtc, string? Error, bool WorkerOnline, IReadOnlyList<MonitorDiscrepancyDto> Discrepancies,
    bool DiscrepanciesTruncated = false, IReadOnlyList<MonitorCoverageDto>? Coverage = null, int UncheckedPlays = 0)
{
    /// <summary>The configured check cadence (<c>Vessel:Monitor:Interval</c>) in whole minutes, so the panel can state it (I-08).</summary>
    public int? IntervalMinutes { get; init; }
}
/// <summary>
/// Visible records, outstanding incidents (open, or unverifiable and not dismissed since) first, then the rest by
/// confirmation time, newest first; at most <see cref="MaxRecords"/>. <paramref name="Truncated"/> says the cap left some out.
/// </summary>
public sealed record MonitorDiscrepancyList(IReadOnlyList<MonitorDiscrepancyDto> Records, bool Truncated)
{
    public const int MaxRecords = 500;
}
public sealed record MonitorSummaryDto(bool WorkerOnline, DateTimeOffset? HeartbeatAtUtc, IReadOnlyList<AccountMonitorDto> Accounts);
public sealed record MonitorRunAccepted(Guid RequestId, AccountMonitorDto Monitor);

public sealed class MonitorAdmissionException(int retryAfter) : Exception("This account check is running or cooling down.")
{
    public int RetryAfter { get; } = retryAfter;
}

// Credential revision is a one-way digest of encrypted storage, never credential plaintext.
public sealed record MonitorCapture(Account Account, string? CredentialRevision, DateTimeOffset? CredentialExpiresAtUtc,
    DateTimeOffset? NewestRetainedFillAtUtc = null);
public sealed record MonitorLease(Guid OwnerId, Guid AccountId, Guid RequestId, long Generation, string WorkerId, string Trigger);
public sealed record MonitorRead(PerpetualVenueReadResult Sync, VenueOrderReadResult? Orders,
    string? OrderError = null, bool CredentialError = false);
/// <param name="CurrentOrderBook">The venue capability: its order reader returns the current open book. Without it the
/// book-dependent checks do not run and protection coverage is unsupported. Required, so no caller assumes a book.</param>
public sealed record MonitorEvaluation(Guid RunId, Account Account, MonitorRead Read, DateTimeOffset? PreviousSuccessAtUtc,
    DateTimeOffset? NewestFillBeforeReadUtc, bool HistoryGap, string Trigger, bool CurrentOrderBook);
/// <param name="Coverage">Stop coverage of each evaluated Open Play; null when the facts were too incomplete to name them,
/// which leaves the stored outcomes to go stale.</param>
public sealed record MonitorEvaluationResult(DateTimeOffset? NextConfirmationAtUtc, bool Incomplete = false, string? SafeNotice = null,
    IReadOnlyList<MonitorCoverageOutcome>? Coverage = null);

/// <summary>Called inside the fenced account unit of work, after imports and execution reconciliation.</summary>
public interface IMonitorEvaluator
{
    /// <returns>A confirmation deadline, if another fresh account-wide observation is needed.</returns>
    Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct);
    /// <summary>The records of <see cref="ReadListAsync"/>, without its truncation flag.</summary>
    Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid accountId, Guid? playId, CancellationToken ct);
    /// <summary>Visible records of the account (or of a Play), outstanding incidents first, and whether the cap left any out.</summary>
    Task<MonitorDiscrepancyList> ReadListAsync(Guid accountId, Guid? playId, CancellationToken ct);
    /// <summary>
    /// Discrepancy housekeeping for a lease-owned run that made no discrepancy decisions, under the same account fence.
    /// <paramref name="interrupted"/> (failed or discarded checks) breaks pending confirmation sequences; every run
    /// ages outstanding work toward its unverifiable outcome.
    /// </summary>
    Task SettleAsync(Account account, Guid runId, string trigger, bool interrupted, CancellationToken ct);
}

public interface IMonitorImporter
{
    Task RetainAsync(Account account, VenueOrderReadResult orders, CancellationToken ct);
    Task ReconcileAsync(Account account, VenueOrderReadResult orders, IReadOnlyList<VenueInstrument> instruments, CancellationToken ct);
}

/// <summary>One account's discovery inputs, read set-based; only rows whose scheduling changes are then locked.</summary>
public sealed record MonitorCandidate(Guid AccountId, string VenueId, bool Monitorable, bool Eligible, bool HasExpiredWatches,
    string? CredentialRevision, MonitorAccount? Row);

/// <summary>
/// A linked order (exit or entry) watched after its Play ended. <paramref name="Role"/>: entry, stop, target or exit;
/// <paramref name="State"/>: watching, resolved or unverifiable.
/// </summary>
public sealed record MonitorWatchDto(Guid LinkId, Guid PlayId, string OrderId, string Role, string State, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

/// <summary>
/// Records why a run failed: the run, account and the exception's type name only. Never messages, URLs, bodies or
/// credentials, which venue and HTTP errors can carry.
/// </summary>
public interface IMonitorRunLog
{
    void Failed(MonitorLease lease, string errorType);
}

public interface IMonitorStore
{
    Task<MonitorCapture?> CaptureAsync(Guid accountId, CancellationToken ct);
    /// <summary>
    /// Monitor rows of enabled accounts on the given venues that are currently eligible, or still hold an outstanding
    /// announcement (an open record, or an unverifiable one not dismissed since), bounded, in one read.
    /// </summary>
    Task<IReadOnlyList<(MonitorAccount Row, string VenueId)>> SummaryAsync(IReadOnlyCollection<string> venueIds,
        DateTimeOffset now, CancellationToken ct);
    Task<MonitorAccount?> FindAsync(Guid accountId, CancellationToken ct);
    /// <summary>The account's stored funding coverage and last attempt, read before a Refresh's funding windows; null when none.</summary>
    Task<FundingCursor?> FundingCursorAsync(Guid accountId, CancellationToken ct);
    Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct);
    Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct);
    /// <summary>
    /// Persists the maximum Retry-After deadline, independently of any account lease. The deadline is clamped to at
    /// most <see cref="VenueRateLimitException.MaxDelay"/> after <paramref name="now"/> before it is stored.
    /// </summary>
    Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct);
    Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct);
    /// <summary>
    /// Accounts that may need scheduling changes (monitorable ones, and any with a non-idle monitor row), ordered by
    /// account ID after <paramref name="after"/>, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<MonitorCandidate>> DiscoveryCandidatesAsync(IReadOnlyCollection<string> venueIds, DateTimeOffset now,
        Guid? after, int limit, CancellationToken ct);
    /// <summary>Whether the account has anything to check: protection work, or kept execution history (Phase 2, item 6).</summary>
    Task<bool> EligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Protection work only: an eligible Play, a live terminal-link watch or outstanding discrepancy work. An account
    /// eligible only because it keeps its history runs behind it, with headroom left for protection checks.
    /// </summary>
    Task<bool> ProtectionEligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Inside the account lock and the import's unit of work (Phase 2, item 6): records one import's coverage, extending
    /// the latest record when it continues it, opens the gap it found and closes every open gap on complete history.
    /// </summary>
    Task RecordCoverageAsync(FillCoverageRecord record, CancellationToken ct);
    /// <summary>Phase 2, item 6b: the account's active backfill, read without tracking; null when none.</summary>
    Task<HistoryBackfill?> ActiveBackfillAsync(Guid accountId, CancellationToken ct);
    /// <summary>Inside the account lock: a backfill, tracked for update; null when not the account's.</summary>
    Task<HistoryBackfill?> BackfillAsync(Guid accountId, Guid backfillId, CancellationToken ct);
    /// <summary>Inside the account lock: a gap of the account, tracked for update; null when not the account's.</summary>
    Task<AccountHistoryGap?> GapAsync(Guid accountId, Guid gapId, CancellationToken ct);
    /// <summary>Inside the account lock: a new backfill (at most one active per account) or a new gap.</summary>
    Task AddAsync(HistoryBackfill backfill, CancellationToken ct);
    Task AddAsync(AccountHistoryGap gap, CancellationToken ct);
    /// <summary>Whether any gap of the account is still open, unsaved changes in this unit included.</summary>
    Task<bool> HasOpenGapAsync(Guid accountId, CancellationToken ct);
    Task<T> LockedAsync<T>(Guid accountId, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct);
    Task<IReadOnlyList<MonitorLease>> ClaimDueAsync(string workerId, DateTimeOffset now, int limit, CancellationToken ct);
    /// <param name="protectionOnly">Phase 2, item 6: claim only protection work, leaving a scheduled read for kept history alone.</param>
    Task<MonitorLease?> ClaimAsync(Guid accountId, Guid? requestId, string workerId, DateTimeOffset now, CancellationToken ct,
        bool protectionOnly = false);
    Task<bool> RenewAsync(MonitorLease lease, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Inside the account lock. With <paramref name="orders"/>, a reported terminal status ends a watch whatever the
    /// read's completeness, and absence ends it only on a complete open-order read. Watches past their lifetime end
    /// <c>unverifiable</c>, with or without a read.
    /// </summary>
    Task UpdateWatchesAsync(Guid accountId, VenueOrderReadResult? orders, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<MonitorWatchDto>> WatchesAsync(Guid playId, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Inside the account lock and unit of work: replaces the account's stored coverage with one evaluated check's rows,
    /// upserting by Play and removing Plays the check no longer evaluated.
    /// </summary>
    Task SaveCoverageAsync(Guid accountId, IReadOnlyList<MonitorCoverage> rows, CancellationToken ct);
    /// <summary>Stored coverage of the accounts' currently Open Plays, at most 500 per account, by account and Play.</summary>
    Task<IReadOnlyList<MonitorCoverage>> CoverageAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct);
    /// <summary>Per account, its Open tracked Plays (planned, on a venue instrument) without a coverage row; absent when none.</summary>
    Task<IReadOnlyDictionary<Guid, int>> UncheckedPlaysAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct);
    /// <summary>
    /// Inside account admission transaction. Locks the owner budget, then this venue budget, and checks both capacities.
    /// <paramref name="headroom"/> venue slots, and one of the owner's, stay free for protection checks (Phase 2, item 6).
    /// </summary>
    Task<bool> ReadCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct, int headroom = 0);
    /// <summary>Lock-free preflight only; admission rechecks under the venue budget lock.</summary>
    Task<bool> VenueAtCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct);
    Task<bool> OwnerAtCapacityAsync(DateTimeOffset now, CancellationToken ct);
}
