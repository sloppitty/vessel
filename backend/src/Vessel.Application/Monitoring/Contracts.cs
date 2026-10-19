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
/// <param name="DiscrepanciesTruncated">More visible records exist than <paramref name="Discrepancies"/> lists (at most
/// <see cref="MonitorDiscrepancyList.MaxRecords"/>). Outstanding incidents are listed first, so only older records are left
/// out unless the incidents alone exceed the cap. Optional: an older producer omits it.</param>
public sealed record AccountMonitorDto(Guid AccountId, string State, DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? NextRunAtUtc, string ManualState, string? RunTrigger,
    DateTimeOffset? NextManualAllowedAtUtc, string? Error, bool WorkerOnline, IReadOnlyList<MonitorDiscrepancyDto> Discrepancies,
    bool DiscrepanciesTruncated = false);
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
public sealed record MonitorEvaluation(Guid RunId, Account Account, MonitorRead Read, DateTimeOffset? PreviousSuccessAtUtc,
    DateTimeOffset? NewestFillBeforeReadUtc, bool HistoryGap, string Trigger);
public sealed record MonitorEvaluationResult(DateTimeOffset? NextConfirmationAtUtc, bool Incomplete = false, string? SafeNotice = null);

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
    Task<bool> EligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct);
    Task<T> LockedAsync<T>(Guid accountId, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct);
    Task<IReadOnlyList<MonitorLease>> ClaimDueAsync(string workerId, DateTimeOffset now, int limit, CancellationToken ct);
    Task<MonitorLease?> ClaimAsync(Guid accountId, Guid? requestId, string workerId, DateTimeOffset now, CancellationToken ct);
    Task<bool> RenewAsync(MonitorLease lease, DateTimeOffset now, CancellationToken ct);
    /// <summary>
    /// Inside the account lock. With <paramref name="orders"/>, a reported terminal status ends a watch whatever the
    /// read's completeness, and absence ends it only on a complete open-order read. Watches past their lifetime end
    /// <c>unverifiable</c>, with or without a read.
    /// </summary>
    Task UpdateWatchesAsync(Guid accountId, VenueOrderReadResult? orders, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<MonitorWatchDto>> WatchesAsync(Guid playId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Inside account admission transaction. Locks the owner budget, then this venue budget, and checks both capacities.</summary>
    Task<bool> ReadCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Lock-free preflight only; admission rechecks under the venue budget lock.</summary>
    Task<bool> VenueAtCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct);
    Task<bool> OwnerAtCapacityAsync(DateTimeOffset now, CancellationToken ct);
}
