using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// For tests whose subject is not worker liveness: the store reports a heartbeat fresh at the test clock, so an owner
/// Check is admitted as if a worker were running. Every other call goes to the real store. Liveness itself is tested
/// against recorded heartbeats in <c>MonitorOfflineAdmissionTests</c>.
/// </summary>
internal sealed class MonitorWorkerOnline(IMonitorStore inner, TimeProvider clock) : IMonitorStore
{
    public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) =>
        Task.FromResult<ServerMonitorHeartbeat?>(new("monitor", clock.GetUtcNow()));

    public Task<MonitorCapture?> CaptureAsync(Guid accountId, CancellationToken ct) => inner.CaptureAsync(accountId, ct);
    public Task<IReadOnlyList<(MonitorAccount Row, string VenueId)>> SummaryAsync(IReadOnlyCollection<string> venueIds,
        DateTimeOffset now, CancellationToken ct) => inner.SummaryAsync(venueIds, now, ct);
    public Task<MonitorAccount?> FindAsync(Guid accountId, CancellationToken ct) => inner.FindAsync(accountId, ct);
    public Task<FundingCursor?> FundingCursorAsync(Guid accountId, CancellationToken ct) => inner.FundingCursorAsync(accountId, ct);
    public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => inner.VenueRetryAtAsync(venueId, ct);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => inner.VenueRetriesAsync(ct);
    public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) =>
        inner.DeferVenueAsync(venueId, retryAtUtc, now, ct);
    public Task<IReadOnlyList<MonitorCandidate>> DiscoveryCandidatesAsync(IReadOnlyCollection<string> venueIds, DateTimeOffset now,
        Guid? after, int limit, CancellationToken ct) => inner.DiscoveryCandidatesAsync(venueIds, now, after, limit, ct);
    public Task<bool> EligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct) => inner.EligibleAsync(accountId, now, ct);
    public Task<bool> ProtectionEligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct) =>
        inner.ProtectionEligibleAsync(accountId, now, ct);
    public Task RecordCoverageAsync(FillCoverageRecord record, CancellationToken ct) => inner.RecordCoverageAsync(record, ct);
    public Task<HistoryBackfill?> ActiveBackfillAsync(Guid accountId, CancellationToken ct) => inner.ActiveBackfillAsync(accountId, ct);

    public Task<MonitorLease?> ClaimBackfillAsync(Guid accountId, string workerId, DateTimeOffset now, CancellationToken ct) =>
        inner.ClaimBackfillAsync(accountId, workerId, now, ct);
    public Task ReleaseBackfillAsync(MonitorLease lease, DateTimeOffset now, CancellationToken ct) => inner.ReleaseBackfillAsync(lease, now, ct);
    public Task<MonitorLease?> ClaimFundingAsync(Guid accountId, string workerId, DateTimeOffset now, CancellationToken ct) =>
        inner.ClaimFundingAsync(accountId, workerId, now, ct);
    public Task ReleaseFundingAsync(MonitorLease lease, DateTimeOffset now, DateTimeOffset? nextAtUtc, CancellationToken ct) =>
        inner.ReleaseFundingAsync(lease, now, nextAtUtc, ct);
    public Task<(DateTimeOffset At, IReadOnlyList<string> Ids)?> FillWitnessAsync(Guid accountId, DateTimeOffset atOrBefore, CancellationToken ct) =>
        inner.FillWitnessAsync(accountId, atOrBefore, ct);
    public Task<IReadOnlyList<AccountHistoryGap>> OpenGapsAsync(Guid accountId, CancellationToken ct) => inner.OpenGapsAsync(accountId, ct);
    public Task<DateTimeOffset> GapEndBoundAsync(AccountHistoryGap gap, CancellationToken ct) => inner.GapEndBoundAsync(gap, ct);
    public Task<HistoryBackfill?> BackfillAsync(Guid accountId, Guid backfillId, CancellationToken ct) => inner.BackfillAsync(accountId, backfillId, ct);
    public Task<AccountHistoryGap?> GapAsync(Guid accountId, Guid gapId, CancellationToken ct) => inner.GapAsync(accountId, gapId, ct);
    public Task AddAsync(HistoryBackfill backfill, CancellationToken ct) => inner.AddAsync(backfill, ct);
    public Task AddAsync(AccountHistoryGap gap, CancellationToken ct) => inner.AddAsync(gap, ct);
    public Task<bool> HasOpenGapAsync(Guid accountId, CancellationToken ct) => inner.HasOpenGapAsync(accountId, ct);
    public Task<T> LockedAsync<T>(Guid accountId, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct) =>
        inner.LockedAsync(accountId, action, ct);
    public Task<IReadOnlyList<MonitorLease>> ClaimDueAsync(string workerId, DateTimeOffset now, int limit, CancellationToken ct) =>
        inner.ClaimDueAsync(workerId, now, limit, ct);
    public Task<MonitorLease?> ClaimAsync(Guid accountId, Guid? requestId, string workerId, DateTimeOffset now, CancellationToken ct,
        bool protectionOnly = false) => inner.ClaimAsync(accountId, requestId, workerId, now, ct, protectionOnly);
    public Task<bool> RenewAsync(MonitorLease lease, DateTimeOffset now, CancellationToken ct) => inner.RenewAsync(lease, now, ct);
    public Task UpdateWatchesAsync(Guid accountId, VenueOrderReadResult? orders, DateTimeOffset now, CancellationToken ct) =>
        inner.UpdateWatchesAsync(accountId, orders, now, ct);
    public Task<IReadOnlyList<MonitorWatchDto>> WatchesAsync(Guid playId, DateTimeOffset now, CancellationToken ct) =>
        inner.WatchesAsync(playId, now, ct);
    public Task SaveCoverageAsync(Guid accountId, IReadOnlyList<MonitorCoverage> rows, CancellationToken ct) =>
        inner.SaveCoverageAsync(accountId, rows, ct);
    public Task<IReadOnlyList<MonitorCoverage>> CoverageAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct) =>
        inner.CoverageAsync(accountIds, ct);
    public Task<IReadOnlyDictionary<Guid, int>> UncheckedPlaysAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct) =>
        inner.UncheckedPlaysAsync(accountIds, ct);
    public Task<bool> VenueAtCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct) => inner.VenueAtCapacityAsync(venueId, now, ct);
    public Task<bool> OwnerAtCapacityAsync(DateTimeOffset now, CancellationToken ct) => inner.OwnerAtCapacityAsync(now, ct);
    public Task<bool> ReadCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct, int headroom = 0) =>
        inner.ReadCapacityAsync(venueId, now, ct, headroom);
}
