using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Export;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class ExportStore(VesselDbContext db) : IFactsExportStore
{
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);

    // Each stream passes the token to the database enumeration, so an abandoned download stops its query (Sol round 1 on #39).
    public IAsyncEnumerable<ImportedFill> FillsAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Stream(db.Fills.Where(f => f.AccountId == accountId && (from == null || f.OccurredAtUtc >= from) && (to == null || f.OccurredAtUtc < to))
            .OrderBy(f => f.OccurredAtUtc).ThenBy(f => f.ContractId).ThenBy(f => f.SourceFillId).AsNoTracking(), ct);

    public async IAsyncEnumerable<(ManualExecution Row, string? Instrument)> ManualExecutionsAsync(Guid accountId, DateTimeOffset? from,
        DateTimeOffset? to, [EnumeratorCancellation] CancellationToken ct)
    {
        var rows = db.ManualExecutions.Join(db.Plays, m => m.PlayId, p => p.Id, (m, p) => new { Row = m, p.AccountId, p.ContractId })
            .Where(x => x.AccountId == accountId && (from == null || x.Row.OccurredAtUtc >= from) && (to == null || x.Row.OccurredAtUtc < to))
            .OrderBy(x => x.Row.OccurredAtUtc).ThenBy(x => x.Row.Id);
        await foreach (var row in rows.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct)) yield return (row.Row, row.ContractId);
    }

    // RISEx settlements within one microsecond share OccurredAtUtc; their native nanoseconds keep them in order.
    public IAsyncEnumerable<FundingPayment> FundingAsync(Guid accountId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Stream(db.FundingPayments.Where(p => p.AccountId == accountId && (from == null || p.OccurredAtUtc >= from) && (to == null || p.OccurredAtUtc < to))
            .OrderBy(p => p.OccurredAtUtc).ThenBy(p => p.OccurredAtUnixNanoseconds).ThenBy(p => p.ContractId).ThenBy(p => p.SourceId).AsNoTracking(), ct);

    private static async IAsyncEnumerable<T> Stream<T>(IQueryable<T> query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in query.AsAsyncEnumerable().WithCancellation(ct)) yield return item;
    }
}
