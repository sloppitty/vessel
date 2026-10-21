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
    public Task<(DateTimeOffset From, DateTimeOffset To)?> FundingCoverageAsync(Guid accountId, CancellationToken ct) =>
        inner.FundingCoverageAsync(accountId, ct);
    public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => inner.VenueRetryAtAsync(venueId, ct);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => inner.VenueRetriesAsync(ct);
    public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) =>
        inner.DeferVenueAsync(venueId, retryAtUtc, now, ct);
    public Task<IReadOnlyList<MonitorCandidate>> DiscoveryCandidatesAsync(IReadOnlyCollection<string> venueIds, DateTimeOffset now,
        Guid? after, int limit, CancellationToken ct) => inner.DiscoveryCandidatesAsync(venueIds, now, after, limit, ct);
    public Task<bool> EligibleAsync(Guid accountId, DateTimeOffset now, CancellationToken ct) => inner.EligibleAsync(accountId, now, ct);
    public Task<T> LockedAsync<T>(Guid accountId, Func<Account, MonitorAccount, Task<T>> action, CancellationToken ct) =>
        inner.LockedAsync(accountId, action, ct);
    public Task<IReadOnlyList<MonitorLease>> ClaimDueAsync(string workerId, DateTimeOffset now, int limit, CancellationToken ct) =>
        inner.ClaimDueAsync(workerId, now, limit, ct);
    public Task<MonitorLease?> ClaimAsync(Guid accountId, Guid? requestId, string workerId, DateTimeOffset now, CancellationToken ct) =>
        inner.ClaimAsync(accountId, requestId, workerId, now, ct);
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
    public Task<bool> ReadCapacityAsync(string venueId, DateTimeOffset now, CancellationToken ct) => inner.ReadCapacityAsync(venueId, now, ct);
}
