using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

/// <summary>
/// Issue #108: process-wide state of the Activity read model: one refresh at a time per account in this process (the database
/// lock covers other processes), and versions already hashed for a scope's exact set of account builds.
/// </summary>
public sealed class ActivityReadModelState
{
    private const int VersionLimit = 4096;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshes = new();
    private readonly ConcurrentDictionary<string, string> versions = new(StringComparer.Ordinal);

    internal SemaphoreSlim Refresh(Guid accountId) => refreshes.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    internal bool TryVersion(string key, out string version) => versions.TryGetValue(key, out version!);

    internal void Remember(string key, string version)
    {
        // A version is a few bytes; a full table is simply started again.
        if (versions.Count >= VersionLimit) versions.Clear();
        versions[key] = version;
    }
}

/// <summary>Issue #108: what <c>activity-verify</c> found for one owner.</summary>
public sealed record ActivityVerifyResult(int Accounts, int Operations, int StaleAccounts, IReadOnlyList<string> Differences);

/// <summary>
/// Issue #108: the Activity read model. Each enabled real account has one stored part (<c>activity_projection_accounts</c>)
/// and its operations (<c>activity_operations</c>), built by <see cref="ActivityService.Aggregate"/> over that account's facts.
/// A part is current while the account's history revision (moved by commit triggers on the same facts as the execution
/// revision) and its observation key are unchanged and its snapshot time stays inside the window it was built in. A read
/// rebuilds the parts that are not current, then pages, filters and counts in SQL; a monitor check that only moves an
/// observation time therefore rewrites nothing.
/// </summary>
public sealed class ActivityReadModel(VesselDbContext db, IActivityStore store, ActivityReadModelState state, IVenueRegistry? venues = null)
    : IActivityPageReader
{
    // A read that keeps finding history changed under it falls back to the in-memory aggregate rather than wait.
    private const int Attempts = 3;

    private sealed record Part(ActivityProjectedAccountState State, long Revision, string ObservationKey, DateTimeOffset? SnapshotAtUtc,
        DateTimeOffset? SnapshotAfterUtc, DateTimeOffset? SnapshotBeforeUtc, bool SnapshotPinned);

    public async Task<ActivityProjectedPage?> PageAsync(IReadOnlySet<Guid> accountIds, ActivityFilter filter, int page, int pageSize,
        CancellationToken ct)
    {
        var scope = accountIds.ToHashSet();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            List<Guid> stale;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                var (observed, parts, notCurrent) = await CurrentAsync(scope, ct);
                stale = notCurrent;
                if (stale.Count == 0)
                {
                    var result = await ReadPageAsync(observed, parts, filter, page, pageSize, ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                await transaction.CommitAsync(ct);
            }
            foreach (var id in stale) await RefreshAsync(id, ct);
        }
        return null;
    }

    /// <summary>The scoped enabled real accounts' observations and parts, and the accounts whose part is not current.</summary>
    private async Task<(ActivityObservations Observed, Dictionary<Guid, Part> Parts, List<Guid> Stale)> CurrentAsync(IReadOnlySet<Guid>? scope,
        CancellationToken ct)
    {
        var observed = await ActivityStore.ObservationsAsync(db, null, TradingMode.Real, ct, scope);
        var ids = observed.Accounts.Select(a => a.Id).ToArray();
        var revisions = await RevisionsAsync(ids, ct);
        var parts = await PartsAsync(ids, ct);
        var stale = observed.Accounts.Where(a => !Current(parts.GetValueOrDefault(a.Id), revisions.GetValueOrDefault(a.Id), observed, a))
            .Select(a => a.Id).ToList();
        return (observed, parts, stale);
    }

    private static bool Current(Part? part, long revision, ActivityObservations observed, Account account)
    {
        if (part is null || part.Revision != revision) return false;
        var snapshot = observed.Snapshots.SingleOrDefault(s => s.AccountId == account.Id);
        var funding = observed.FundingStates.SingleOrDefault(s => s.AccountId == account.Id);
        return part.ObservationKey == ActivityProjection.ObservationKey(account, snapshot, funding) &&
            ActivityProjection.SnapshotHolds(part.SnapshotAtUtc, part.SnapshotAfterUtc, part.SnapshotBeforeUtc, part.SnapshotPinned,
                snapshot?.ObservedAtUtc);
    }

    private async Task<ActivityProjectedPage> ReadPageAsync(ActivityObservations observed, Dictionary<Guid, Part> parts,
        ActivityFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var accounts = observed.Accounts;
        var ids = accounts.Select(a => a.Id).ToArray();
        var states = accounts.ToDictionary(a => a.Id, a => parts[a.Id].State);
        var shown = accounts.Where(a => filter.MatchesAccount(a.Id, a.PortfolioId)).Select(a => a.Id).ToArray();
        var (where, parameters) = Filter(shown, filter);
        var total = (int)(long)(await ScalarAsync($"SELECT count(*) FROM activity_operations WHERE {where}", parameters, ct))!;
        page = ListPaging.Clamp(page, pageSize, total);
        var rows = new List<ActivityOperationDto>();
        await using (var command = Command($"""
            SELECT summary, contract_identity, contract_native, basis_notional, basis_quantity FROM activity_operations WHERE {where}
            ORDER BY last_occurred_at DESC, account_id, operation_id LIMIT @take OFFSET @skip
            """, parameters))
        {
            command.Parameters.AddWithValue("take", pageSize);
            command.Parameters.AddWithValue("skip", ListPaging.Skip(page, pageSize));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(ActivityProjection.Operation(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        var (forced, forcedCounts) = await ActivityStore.ForcedAsync(db, ids, ct);
        var coverage = ActivityProjection.Coverage(accounts, states, forced, forcedCounts, observed.FundingStates);
        var version = await VersionAsync(observed, states, coverage, ct);
        var facets = new ActivityFacetsDto(
            states.Values.Where(s => s.OperationCount > 0).Select(s => s.AccountId).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList(),
            states.Values.SelectMany(s => s.Contracts).Distinct().Order(StringComparer.Ordinal).ToList());
        return new(rows, page, total, states.Values.Sum(s => s.OperationCount), states.Values.Sum(s => s.FillCount), version, facets,
            coverage, accounts.SelectMany(a => states[a.Id].UnassignedFunding).ToList(),
            new(accounts, [], observed.Snapshots, new Dictionary<Guid, Guid>()));
    }

    private static (string Where, NpgsqlParameter[] Parameters) Filter(Guid[] accounts, ActivityFilter filter)
    {
        var where = new StringBuilder("account_id = ANY(@accounts)");
        var parameters = new List<NpgsqlParameter> { new("accounts", accounts) };
        void Add(string condition, string name, object value)
        {
            where.Append(" AND ").Append(condition);
            parameters.Add(new(name, value));
        }
        if (filter.Contract is { } contract) Add("contract_id = @contract", "contract", contract);
        if (filter.Direction is { } direction) Add("direction = @direction", "direction", direction);
        if (filter.Status == "partial") where.Append(" AND status = 'open' AND partial");
        else if (filter.Status is { } status) Add("status = @status", "status", status);
        if (filter.From is { } from) Add("last_occurred_at >= @from", "from", from.ToUniversalTime());
        if (filter.To is { } to) Add("last_occurred_at < @to", "to", to.ToUniversalTime());
        return (where.ToString(), parameters.ToArray());
    }

    /// <summary>
    /// The scope's version, hashed once per exact set of account builds and funding attempt details (which only the version
    /// reads), so a page or filter change, or a routine observation, reads no operation twice.
    /// </summary>
    private async Task<string> VersionAsync(ActivityObservations observed, Dictionary<Guid, ActivityProjectedAccountState> states,
        IReadOnlyList<ActivityCoverageDto> coverage, CancellationToken ct)
    {
        var key = new StringBuilder(db.CurrentOwnerId.ToString("N"));
        foreach (var part in states.Values.OrderBy(s => s.AccountId)) key.Append('|').Append(part.AccountId.ToString("N")).Append(':').Append(part.BuildId);
        foreach (var attempt in observed.FundingStates.Where(s => states.ContainsKey(s.AccountId)).OrderBy(s => s.AccountId))
            key.Append('|').Append(attempt.AccountId.ToString("N")).Append(':').Append(attempt.LastAttemptToUtc.UtcTicks)
                .Append(':').Append(attempt.LastReadComplete);
        var cacheKey = key.ToString();
        if (state.TryVersion(cacheKey, out var cached)) return cached;
        var ids = states.Keys.ToArray();
        var funding = await db.FundingPayments.Where(p => ids.Contains(p.AccountId)).AsNoTracking().ToListAsync(ct);
        var omissions = await ActivityStore.OmissionsAsync(db, ids, ct);
        var version = await ActivityProjection.VersionAsync(SummariesAsync(ids, ct), coverage, funding,
            observed.FundingStates.Where(s => states.ContainsKey(s.AccountId)), omissions, ct);
        state.Remember(cacheKey, version);
        return version;
    }

    private async IAsyncEnumerable<string> SummariesAsync(Guid[] ids, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = Command("""
            SELECT summary FROM activity_operations WHERE account_id = ANY(@accounts)
            ORDER BY last_occurred_at DESC, account_id, operation_id
            """, [new("accounts", ids)]);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct)) yield return reader.GetString(0);
    }

    /// <summary>
    /// Rebuilds one account's part when it is not current, from its facts and revision read in one snapshot. True when it
    /// wrote a part. A part built from an older revision than the stored one is never written.
    /// </summary>
    public async Task<bool> RefreshAsync(Guid accountId, CancellationToken ct)
    {
        var gate = state.Refresh(accountId);
        await gate.WaitAsync(ct);
        try
        {
            long revision;
            ActivityFacts facts;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                var (observed, _, stale) = await CurrentAsync(new HashSet<Guid> { accountId }, ct);
                if (stale.Count == 0 || observed.Accounts.Count == 0)
                {
                    await transaction.CommitAsync(ct);
                    return false;
                }
                revision = (await RevisionsAsync([accountId], ct)).GetValueOrDefault(accountId);
                facts = await store.ReadAsync(accountId, TradingMode.Real, ct);
                await transaction.CommitAsync(ct);
            }
            var part = ActivityProjection.Build(facts, accountId, venues);
            return part is not null && await WriteAsync(part, revision, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> WriteAsync(ActivityProjectedAccount part, long revision, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var id = new NpgsqlParameter("account", part.AccountId);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('vessel.activity-projection:' || @account::text, 0))", [id], ct);
        if (await ScalarAsync("SELECT revision FROM activity_projection_accounts WHERE account_id = @account", [Copy(id)], ct) is long stored &&
            stored > revision)
            return false;
        await ExecuteAsync("DELETE FROM activity_projection_accounts WHERE account_id = @account", [Copy(id)], ct);
        // Only an account that still exists for this owner gets a part; operations follow their part's foreign key.
        var inserted = await ExecuteAsync("""
            INSERT INTO activity_projection_accounts (account_id, owner_id, build_id, revision, observation_key, snapshot_at,
                snapshot_after, snapshot_before, snapshot_pinned, fill_count, operation_count, contracts, coverage, unassigned_funding, built_at)
            SELECT a."Id", a."OwnerId", nextval('activity_projection_builds'), @revision, @key, @at, @after, @before, @pinned, @fills,
                @operations, @contracts, @coverage, @unassigned, now()
            FROM accounts a WHERE a."Id" = @account AND a."OwnerId" = @owner
            """,
        [
            Copy(id), new("owner", db.CurrentOwnerId), new("revision", revision), new("key", part.ObservationKey),
            Time("at", part.SnapshotAtUtc), Time("after", part.SnapshotAfterUtc), Time("before", part.SnapshotBeforeUtc),
            new("pinned", part.SnapshotPinned), new("fills", part.FillCount), new("operations", part.Operations.Count),
            new("contracts", part.Contracts.ToArray()), new("coverage", JsonSerializer.Serialize(part.Coverage)),
            new("unassigned", JsonSerializer.Serialize(part.UnassignedFunding))
        ], ct);
        if (inserted == 0) return false;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY activity_operations (account_id, operation_id, owner_id, last_occurred_at, contract_id, direction, status, partial,
                contract_identity, contract_native, basis_notional, basis_quantity, summary) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var o in part.Operations)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(part.AccountId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(o.Id, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(db.CurrentOwnerId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(o.LastOccurredAtUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(o.ContractId, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(copy, o.Direction, ct);
                await copy.WriteAsync(o.Status, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(o.Partial, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(o.ContractIdentity, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(o.ContractNative, NpgsqlDbType.Boolean, ct);
                await WriteNullableAsync(copy, o.BasisNotional, ct);
                await WriteNullableAsync(copy, o.BasisQuantity, ct);
                await copy.WriteAsync(o.Summary, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter copy, string? value, CancellationToken ct)
    {
        if (value is null) await copy.WriteNullAsync(ct);
        else await copy.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    /// <summary>The owner's enabled real accounts whose part is not current (what a read or <c>activity-rebuild</c> rebuilds).</summary>
    public async Task<(int Accounts, IReadOnlyList<Guid> Stale)> StaleAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var (observed, _, stale) = await CurrentAsync(null, ct);
        await transaction.CommitAsync(ct);
        return (observed.Accounts.Count, stale);
    }

    /// <summary>
    /// <c>activity-verify</c>: recomputes the owner's whole real aggregate in memory, from the facts read in the same snapshot as
    /// the stored parts, and compares every current part with that aggregate's part for the account, operation by operation,
    /// then the owner-wide version and coverage. Parts that are not current are counted, never compared. Read-only.
    /// </summary>
    public async Task<ActivityVerifyResult> VerifyAsync(CancellationToken ct)
    {
        ActivityFacts facts;
        Dictionary<Guid, Part> parts;
        ActivityObservations observed;
        List<Guid> stale;
        Dictionary<Guid, Dictionary<string, ActivityProjectedOperation>> stored;
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            (observed, parts, stale) = await CurrentAsync(null, ct);
            facts = await store.ReadAsync(null, TradingMode.Real, ct);
            stored = await OperationsAsync(observed.Accounts.Select(a => a.Id).ToArray(), ct);
            await transaction.CommitAsync(ct);
        }
        var aggregate = ActivityService.Aggregate(facts, TradingMode.Real, null, null, venues);
        var expected = ActivityProjection.Split(aggregate).ToDictionary(p => p.AccountId);
        var differences = new List<string>();
        var operations = 0;
        void Differ(string text) { if (differences.Count < 1000) differences.Add(text); }
        if (!expected.Keys.ToHashSet().SetEquals(observed.Accounts.Select(a => a.Id)))
            Differ("The aggregate and the read model see different enabled real accounts.");
        foreach (var account in observed.Accounts)
        {
            if (stale.Contains(account.Id) || !expected.TryGetValue(account.Id, out var want)) continue;
            var have = parts[account.Id].State;
            var rows = stored.GetValueOrDefault(account.Id) ?? [];
            operations += rows.Count;
            if (have.FillCount != want.FillCount) Differ($"Account {account.Id}: retained fill count differs.");
            if (!have.Contracts.SequenceEqual(want.Contracts, StringComparer.Ordinal)) Differ($"Account {account.Id}: contracts differ.");
            if (JsonSerializer.Serialize(have.Coverage) != JsonSerializer.Serialize(want.Coverage)) Differ($"Account {account.Id}: coverage differs.");
            if (JsonSerializer.Serialize(have.UnassignedFunding) != JsonSerializer.Serialize(want.UnassignedFunding))
                Differ($"Account {account.Id}: unattributed funding differs.");
            if (have.OperationCount != rows.Count) Differ($"Account {account.Id}: stored operation count differs from its rows.");
            foreach (var o in want.Operations)
                if (!rows.TryGetValue(o.Id, out var row)) Differ($"Account {account.Id}: operation {o.Id} is missing.");
                else if (row != o) Differ($"Account {account.Id}: operation {o.Id} differs.");
            foreach (var id in rows.Keys.Except(want.Operations.Select(o => o.Id)))
                Differ($"Account {account.Id}: operation {id} is not in the aggregate.");
        }
        if (stale.Count == 0 && observed.Accounts.Count > 0)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var (now, nowParts, nowStale) = await CurrentAsync(null, ct);
            // Compare the owner-wide page only when nothing changed since the facts read.
            if (nowStale.Count == 0 && nowParts.Values.All(p => parts.TryGetValue(p.State.AccountId, out var before) && before.State.BuildId == p.State.BuildId))
            {
                var page = await ReadPageAsync(now, nowParts, new ActivityFilter(null, null, null, null, null, null, null), 1, 1, ct);
                if (page.Version != aggregate.Version) Differ("The owner-wide version differs.");
                if (JsonSerializer.Serialize(page.Coverage) != JsonSerializer.Serialize(aggregate.Coverage)) Differ("The owner-wide coverage differs.");
                if (page.ScopeCount != aggregate.Operations.Count) Differ("The owner-wide operation count differs.");
            }
            await transaction.CommitAsync(ct);
        }
        return new(observed.Accounts.Count, operations, stale.Count, differences);
    }

    private async Task<Dictionary<Guid, Dictionary<string, ActivityProjectedOperation>>> OperationsAsync(Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Dictionary<string, ActivityProjectedOperation>>();
        await using var command = Command("""
            SELECT account_id, operation_id, last_occurred_at, contract_id, direction, status, partial, contract_identity, contract_native,
                basis_notional, basis_quantity, summary
            FROM activity_operations WHERE account_id = ANY(@accounts)
            """, [new("accounts", ids)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetGuid(0);
            if (!result.TryGetValue(account, out var rows)) result[account] = rows = new(StringComparer.Ordinal);
            var row = new ActivityProjectedOperation(reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetString(7),
                reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11));
            rows[row.Id] = row;
        }
        return result;
    }

    private async Task<Dictionary<Guid, long>> RevisionsAsync(Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, long>();
        await using var command = Command("SELECT account_id, revision FROM activity_account_revisions WHERE account_id = ANY(@accounts)",
            [new("accounts", ids)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetGuid(0)] = reader.GetInt64(1);
        return result;
    }

    private async Task<Dictionary<Guid, Part>> PartsAsync(Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Part>();
        await using var command = Command("""
            SELECT account_id, build_id, revision, observation_key, snapshot_at, snapshot_after, snapshot_before, snapshot_pinned,
                fill_count, operation_count, contracts, coverage, unassigned_funding
            FROM activity_projection_accounts WHERE owner_id = @owner AND account_id = ANY(@accounts)
            """, [new("owner", db.CurrentOwnerId), new("accounts", ids)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            DateTimeOffset? At(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
            var account = reader.GetGuid(0);
            var parsed = new ActivityProjectedAccountState(account, reader.GetInt64(1), reader.GetInt32(8), reader.GetInt32(9),
                reader.GetFieldValue<string[]>(10),
                JsonSerializer.Deserialize<List<ActivityCoverageDto>>(reader.GetString(11)) ?? [],
                JsonSerializer.Deserialize<List<OperationFundingDto>>(reader.GetString(12)) ?? []);
            result[account] = new(parsed, reader.GetInt64(2), reader.GetString(3), At(4), At(5), At(6), reader.GetBoolean(7));
        }
        return result;
    }

    private NpgsqlCommand Command(string sql, IEnumerable<NpgsqlParameter> parameters)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var parameter in parameters) command.Parameters.Add(parameter.Clone());
        return command;
    }

    private async Task<object?> ScalarAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    private async Task<int> ExecuteAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter Copy(NpgsqlParameter parameter) => parameter.Clone();

    private static NpgsqlParameter Time(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value is { } at ? at.ToUniversalTime() : DBNull.Value };
}
