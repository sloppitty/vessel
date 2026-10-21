using Microsoft.EntityFrameworkCore;
using Vessel.Application.Export;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class ExportStore(VesselDbContext db) : IFactsExportStore
{
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);

    public IAsyncEnumerable<ImportedFill> FillsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        db.Fills.Where(f => f.AccountId == accountId && (from == null || f.OccurredAtUtc >= from) && (to == null || f.OccurredAtUtc < to))
            .OrderBy(f => f.OccurredAtUtc).ThenBy(f => f.ContractId).ThenBy(f => f.SourceFillId).AsNoTracking().AsAsyncEnumerable();

    public IAsyncEnumerable<ManualExecution> ManualExecutionsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        db.ManualExecutions.Where(m => db.Plays.Any(p => p.Id == m.PlayId && p.AccountId == accountId) &&
                (from == null || m.OccurredAtUtc >= from) && (to == null || m.OccurredAtUtc < to))
            .OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.Id).AsNoTracking().AsAsyncEnumerable();

    public IAsyncEnumerable<FundingPayment> FundingAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        db.FundingPayments.Where(p => p.AccountId == accountId && (from == null || p.OccurredAtUtc >= from) && (to == null || p.OccurredAtUtc < to))
            .OrderBy(p => p.OccurredAtUtc).ThenBy(p => p.ContractId).ThenBy(p => p.SourceId).AsNoTracking().AsAsyncEnumerable();
}
