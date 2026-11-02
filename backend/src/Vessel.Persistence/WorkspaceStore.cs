using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Application.Venues;
using Vessel.Application.Paper;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Paper;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed partial class WorkspaceStore(VesselDbContext db, UnitOfWork unitOfWork, TimeProvider? time = null) : IWorkspaceStore
{
    public Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct) => PortfoliosAsync(ct, TradingMode.Real);
    public Task<List<Portfolio>> PortfoliosAsync(CancellationToken ct, string tradingMode = TradingMode.Real) =>
        db.Portfolios.Where(x => x.TradingMode == TradingMode.Validate(tradingMode)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
    public Task<List<Account>> AccountsAsync(CancellationToken ct) => AccountsAsync(ct, TradingMode.Real);
    public Task<List<Account>> AccountsAsync(CancellationToken ct, string tradingMode = TradingMode.Real) =>
        db.Accounts.Where(x => x.TradingMode == TradingMode.Validate(tradingMode)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<MarketDataAccount?> MarketDataAccountAsync(Guid id, CancellationToken ct) => db.Accounts.AsNoTracking()
        .Where(x => x.Id == id).Select(x => new MarketDataAccount(x.IsEnabled, x.VenueId, x.TradingMode)).SingleOrDefaultAsync(ct);
    public Task<bool> SourceExistsAsync(string venueId, string sourceId, CancellationToken ct) =>
        db.Accounts.AnyAsync(a => a.TradingMode == TradingMode.Real && a.VenueId == venueId && a.SourceId == sourceId, ct);
    public Task<AccountSnapshot?> SnapshotAsync(Guid id, CancellationToken ct) => db.Snapshots
        .Where(s => s.AccountId == id && db.Accounts.Any(a => a.Id == s.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real))
        .Include(s => s.Positions).Include(s => s.Stablecoins).AsSplitQuery().AsNoTracking().SingleOrDefaultAsync(ct);
    public Task<List<AccountSnapshot>> SnapshotsAsync(CancellationToken ct) => db.Snapshots.Where(s => db.Accounts.Any(a => a.Id == s.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real)).Include(x => x.Positions).Include(x => x.Stablecoins).AsSplitQuery().AsNoTracking().ToListAsync(ct);
    public Task<List<ImportedFill>> FillsAsync(Guid? accountId, int limit, CancellationToken ct) => db.Fills
        .Where(x => (accountId == null || x.AccountId == accountId) && db.Accounts.Any(a => a.Id == x.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real)).OrderByDescending(x => x.OccurredAtUtc).ThenBy(x => x.Id)
        .Take(limit).AsNoTracking().ToListAsync(ct);
    public Task<int> FillCountAsync(CancellationToken ct) => db.Fills.Where(f => db.Accounts.Any(a => a.Id == f.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real)).CountAsync(ct);
    public async Task AddPortfolioAsync(Portfolio portfolio, CancellationToken ct) { unitOfWork.RequireActive(); db.Portfolios.Add(portfolio); await unitOfWork.SaveChangesAsync(ct); }
    public async Task AddAccountAsync(Account account, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        db.Accounts.Add(account);
        await unitOfWork.SaveChangesAsync(ct);
        if (account.TradingMode == TradingMode.Paper)
        {
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(account.OwnerId, account.Id, account.StartingCapitalUsd!.Value, DateTimeOffset.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
        }
    }

    public async Task SaveAsync(CancellationToken ct) => await unitOfWork.SaveChangesAsync(ct);

    public Task<T> WithManagementLockAsync<T>(Func<Task<T>> action, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        // Grouping mutations for an owner acquire this before any account row lock.
        // This avoids assignment/portfolio-delete lock-order inversions. It is cross-process,
        // transaction-scoped and contains no provider I/O; ordinary sync does not take it.
        var key = "vessel-management:" + db.CurrentOwnerId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        var result = await action();
        return result;
    }, ct);

    public Task DeletePortfolioAsync(Guid id, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        // Lock the parent as well as all accounts. PostgreSQL FK key-share locks serialize
        // concurrent assignments (even privileged SQL) against this parent deletion.
        var portfolios = await db.Portfolios.FromSqlInterpolated(
            $"SELECT * FROM portfolios WHERE \"Id\" = {id} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE").ToListAsync(ct);
        var portfolio = portfolios.SingleOrDefault() ?? throw new ProblemException(404, "Portfolio not found.");
        var accounts = await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM accounts WHERE \"PortfolioId\" = {id} AND \"OwnerId\" = {db.CurrentOwnerId} ORDER BY \"Id\" FOR UPDATE").ToListAsync(ct);
        if (portfolio.TradingMode == TradingMode.Paper && accounts.Count > 0)
            throw new ProblemException(409, "Move all accounts to another paper portfolio before deleting this portfolio.");
        foreach (var account in accounts)
        {
            await db.Entry(account).ReloadAsync(ct);
            account.UpdateSettings(account.Name, null, account.IsEnabled);
        }
        await unitOfWork.SaveChangesAsync(ct);
        db.Portfolios.Remove(portfolio);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }, ct);

    public Task DeleteAccountAsync(Account account, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        if (await db.Plays.AnyAsync(p => p.AccountId == account.Id, ct))
            throw new ProblemException(409, "This account has linked Plays and cannot be deleted.");
        if (account.TradingMode == TradingMode.Paper &&
            await db.PaperLedgerEntries.AnyAsync(e => e.AccountId == account.Id, ct))
            throw new ProblemException(409, "Paper capital history is retained. Disable this account instead of deleting it.");
        // Explicit owner-filtered deletes run under the locked account's transaction.
        // Positions cascade from the snapshot, never from trading intent.
        await db.FillImportConflicts.Where(c => c.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.ExecutionOmissions.Where(m => m.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.ForcedEvents.Where(e => e.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.HistoryBackfills.Where(b => b.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.HistoryGaps.Where(g => g.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.FillCoverage.Where(r => r.AccountId == account.Id).ExecuteDeleteAsync(ct);
        // No Play holds the account, so no allocation has lines; removed ones go before their fills (Phase 2, item 11).
        await db.FillAllocations.Where(a => a.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.Fills.Where(f => f.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.Orders.Where(o => o.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.FundingPayments.Where(p => p.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.FundingStates.Where(s => s.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.LeverageObservations.Where(o => o.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.RiskObservations.Where(o => o.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.Snapshots.Where(s => s.AccountId == account.Id).ExecuteDeleteAsync(ct);
        // Event identities whose subject is this account go with it; their receipts cascade. A removed
        // sequence is a permanent hole that subscribers count as settled.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM events WHERE owner_id={account.OwnerId} AND account_id={account.Id};
            DELETE FROM event_dedup WHERE owner_id={account.OwnerId} AND account_id={account.Id};
            DELETE FROM event_baselines WHERE owner_id={account.OwnerId} AND account_id={account.Id};
            DELETE FROM event_fills WHERE owner_id={account.OwnerId} AND account_id={account.Id};
            """, ct);
        // After the events: their receipts were waited for first, so the notification cursor is never held while a
        // dispatcher that needs it holds a receipt this deletion waits on.
        await Notifications.NotificationStore.AccountDeletedAsync(db, account.Id, ct);
        // Bulk deletes do not update EF's tracker. Detach only this account's deleted
        // facts so a context that previously refreshed it cannot sever required FKs.
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            var deleted = entry.Entity switch
            {
                ImportedFill f => f.OwnerId == account.OwnerId && f.AccountId == account.Id,
                ImportedOrder o => o.OwnerId == account.OwnerId && o.AccountId == account.Id,
                AccountSnapshot s => s.OwnerId == account.OwnerId && s.AccountId == account.Id,
                AccountPosition p => p.OwnerId == account.OwnerId && p.AccountId == account.Id,
                FundingPayment p => p.OwnerId == account.OwnerId && p.AccountId == account.Id,
                LeverageObservation o => o.OwnerId == account.OwnerId && o.AccountId == account.Id,
                RiskObservation o => o.OwnerId == account.OwnerId && o.AccountId == account.Id,
                FillImportConflict c => c.OwnerId == account.OwnerId && c.AccountId == account.Id,
                ExecutionOmission m => m.OwnerId == account.OwnerId && m.AccountId == account.Id,
                ForcedEvent e => e.OwnerId == account.OwnerId && e.AccountId == account.Id,
                Vessel.Domain.Monitoring.AccountHistoryGap g => g.OwnerId == account.OwnerId && g.AccountId == account.Id,
                Vessel.Domain.Monitoring.HistoryBackfill b => b.OwnerId == account.OwnerId && b.AccountId == account.Id,
                Vessel.Domain.Monitoring.FillCoverageRecord r => r.OwnerId == account.OwnerId && r.AccountId == account.Id,
                AccountFundingState s => s.OwnerId == account.OwnerId && s.AccountId == account.Id,
                Vessel.Domain.Credentials.AccountCredential c => c.OwnerId == account.OwnerId && c.AccountId == account.Id,
                _ => false
            };
            if (deleted) entry.State = EntityState.Detached;
        }
        db.Accounts.Remove(account);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }, ct);

    public Task AccountVisibilityChangedAsync(Guid accountId, bool enabled, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        if (await db.Accounts.AnyAsync(a => a.Id == accountId && a.TradingMode == TradingMode.Paper, ct))
        {
            await new PaperStore(db, unitOfWork).WithSettingsLockAsync(async () =>
            {
                var now = (time ?? TimeProvider.System).GetUtcNow();
                var settings = await db.PaperSettings.SingleOrDefaultAsync(ct);
                var simulations = await db.PaperSimulations.Where(s => s.AccountId == accountId &&
                    db.Plays.Any(p => p.Id == s.PlayId && (p.Status == Vessel.Domain.Plays.PlayStatus.Planned ||
                        p.Status == Vessel.Domain.Plays.PlayStatus.Open))).ToListAsync(ct);
                foreach (var simulation in simulations)
                    if (!enabled)
                        simulation.Suspend("The paper account was disabled; the skipped window is not simulated and this outcome remains unscored.", now);
                    else if (settings is { Enabled: true })
                        simulation.Resume(PaperTradingService.NextMinute(now), settings.CheckIntervalMinutes);
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }, ct);
        }
        await Notifications.NotificationStore.AccountVisibilityChangedAsync(db, accountId, enabled, DateTimeOffset.UtcNow, ct);
        return true;
    }, ct);

    public Task<T> WithAccountLockAsync<T>(Guid id, Func<Account, Task<T>> action, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        // PostgreSQL row lock serializes refreshes across API processes, not just this host.
        // Owner is explicit in raw SQL as well as the global query filter.
        var accounts = await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM accounts WHERE \"Id\" = {id} AND \"OwnerId\" = {db.CurrentOwnerId} FOR UPDATE").ToListAsync(ct);
        var account = accounts.SingleOrDefault() ?? throw new ProblemException(404, "Account not found.");
        // A scoped context could already track this account before waiting on the lock.
        await db.Entry(account).ReloadAsync(ct);
        var result = await action(account);
        return result;
    }, ct);

    public Task SaveRefreshAsync(Account account, PerpetualVenueReadResult result, CancellationToken ct) => unitOfWork.ParticipateAsync(async () =>
    {
        if (account.TradingMode != TradingMode.Real)
            throw new ProblemException(409, "Paper accounts cannot import real venue facts.");
        // Validate immutable settlement identities before any refresh facts are written.
        if (result.Funding is { } funding) await ApplyFundingAsync(account, funding, ct);
        var observation = result.Snapshot;
        var snapshot = await db.Snapshots.Include(x => x.Positions).Include(x => x.Stablecoins).AsSplitQuery().SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
        var replacePerps = snapshot is null || observation.ObservedAtUtc > snapshot.ObservedAtUtc;
        var wallet = observation.StablecoinWallet;
        var replaceWallet = wallet is not null &&
            (snapshot?.StablecoinsObservedAtUtc is null || wallet.ObservedAtUtc > snapshot.StablecoinsObservedAtUtc);
        var incomingSourceIds = result.Fills.Select(f => f.SourceFillId).Distinct().ToArray();
        var retained = await db.Fills.Where(f => f.AccountId == account.Id && incomingSourceIds.Contains(f.SourceFillId))
            .AsNoTracking().ToListAsync(ct);
        var existing = retained.Select(f => (f.ContractId, f.SourceFillId)).ToHashSet();
        var newFills = result.Fills.Where(f => existing.Add((f.ContractId, f.SourceFillId))).ToList();
        // Leverage would be lost with the replaced positions; keep an observation when a position appears or changes,
        // or when new executions show it may have closed and reopened identically since the last refresh.
        if (replacePerps) await RecordLeverageAsync(account, observation, snapshot?.Positions ?? [], newFills, ct);
        if (replacePerps) await RecordRiskAsync(account, observation, snapshot?.Positions ?? [], newFills, ct);
        if (snapshot is null)
        {
            snapshot = new AccountSnapshot { OwnerId = account.OwnerId, AccountId = account.Id };
            db.Snapshots.Add(snapshot);
        }
        else if (replacePerps || replaceWallet)
        {
            if (replacePerps)
            {
                db.Positions.RemoveRange(snapshot.Positions);
                snapshot.Positions.Clear();
            }
            if (replaceWallet)
            {
                db.Stablecoins.RemoveRange(snapshot.Stablecoins);
                snapshot.Stablecoins.Clear();
            }
            // Delete only the advancing ledger's composite keys before replacement.
            await unitOfWork.SaveChangesAsync(ct);
        }
        // Equal timestamps are no-ops for that ledger. Stale state never prevents
        // importing new fill identities, or updating an independently newer wallet.
        if (replacePerps)
        {
            snapshot.ObservedAtUtc = observation.ObservedAtUtc.ToUniversalTime();
            snapshot.ValueScope = observation.ValueScope;
            snapshot.AccountValueUsd = observation.AccountValueUsd;
            snapshot.WithdrawableUsd = observation.WithdrawableUsd;
            snapshot.AvailableMarginUsd = observation.AvailableMarginUsd;
            snapshot.MarginUsedUsd = observation.MarginUsedUsd;
            // The mode read with these perps decides how the ledgers combine, even when this read's wallet failed.
            if (observation.AccountMode is { } mode) snapshot.AccountMode = mode;
            snapshot.Positions = observation.Positions.Select(p => new AccountPosition
            {
                OwnerId = account.OwnerId,
                AccountId = account.Id,
                ContractId = p.ContractId,
                VenueContractId = p.VenueContractId,
                SignedQuantity = p.SignedQuantity,
                EntryPrice = p.EntryPrice,
                UnrealizedPnlUsd = p.UnrealizedPnlUsd,
                MarginUsedUsd = p.MarginUsedUsd,
                Leverage = p.Leverage,
                LiquidationPrice = p.LiquidationPrice,
                MarginMode = p.MarginMode
            }).ToList();
        }
        if (replaceWallet)
        {
            snapshot.StablecoinsObservedAtUtc = wallet!.ObservedAtUtc.ToUniversalTime();
            snapshot.AccountMode = wallet.AccountMode;
            snapshot.StablecoinScope = wallet.Scope;
            snapshot.Stablecoins = wallet.Balances.Select(balance => new AccountStablecoin
            {
                OwnerId = account.OwnerId,
                AccountId = account.Id,
                TokenIndex = balance.TokenIndex,
                TokenId = balance.TokenId,
                Symbol = balance.Symbol,
                Total = balance.Total,
                Held = balance.Held,
                Available = balance.Available
            }).ToList();
        }
        var stored = AddFills(account, newFills);
        // A changed report of a retained fill, or a changed later copy within this batch, keeps the first (Sol round 1 on 14c).
        await RecordFillConflictsAsync(account, retained, stored, result.Fills, observation.ObservedAtUtc, ct);
        await RecordOmissionsAsync(account, result.Omissions, result.ForcedEvents, result.OldestRawFillAtUtc, observation.ObservedAtUtc, ct);
        // RawJson intentionally never crosses into journal persistence or logs.
        account.RecordSync(snapshot.ObservedAtUtc, result.HistoryNotice);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }, ct);

    /// <summary>
    /// Phase 2, item 6b: fills a backfill read, through the same deduplication and conflict rules as any import, without
    /// touching the snapshot, sync state, leverage or risk observations. Returns the fills it added.
    /// </summary>
    public Task<IReadOnlyList<ImportedFill>> SaveHistoricalFillsAsync(Account account, IReadOnlyList<VenueFill> fills, DateTimeOffset observedAt,
        CancellationToken ct, IReadOnlyList<VenueOmission>? omissions = null) => unitOfWork.ParticipateAsync<IReadOnlyList<ImportedFill>>(async () =>
    {
        if (account.TradingMode != TradingMode.Real)
            throw new ProblemException(409, "Paper accounts cannot import real venue facts.");
        var ids = fills.Select(f => f.SourceFillId).Distinct().ToArray();
        var retained = await db.Fills.Where(f => f.AccountId == account.Id && ids.Contains(f.SourceFillId)).AsNoTracking().ToListAsync(ct);
        var existing = retained.Select(f => (f.ContractId, f.SourceFillId)).ToHashSet();
        var stored = AddFills(account, fills.Where(f => existing.Add((f.ContractId, f.SourceFillId))), backfilled: true);
        await RecordFillConflictsAsync(account, retained, stored, fills, observedAt, ct);
        // The page's own rows left out leave markers too; their time is the page's when unreported.
        await RecordOmissionsAsync(account, omissions ?? [], [], null, observedAt, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return stored.Values.ToList();
    }, ct);

    /// <summary>
    /// Phase 2, item 7: one durable marker per omitted row, deduplicated by kind and native trade ID, and one forced-event
    /// fact per venue-forced execution, each also a marker. A row seen again only moves its last-seen time and fills in a
    /// time first seen as unknown; a read without it erases nothing. A marker naming an order marks that retained order's
    /// executions as known to be missing. An unreported time is bounded by the read's window instead.
    /// </summary>
    internal const string OmissionConflict = "The venue reported an omitted execution of a known order on a different market. No changes were saved.";

    private async Task RecordOmissionsAsync(Account account, IReadOnlyList<VenueOmission> omissions, IReadOnlyList<VenueForcedEvent> forced,
        DateTimeOffset? windowFrom, DateTimeOffset observedAt, CancellationToken ct)
    {
        var markers = omissions.Concat(forced.Select(f => new VenueOmission(f.SourceId, ExecutionOmission.Forced,
                f.Kind == ForcedEvent.Settlement ? "market settlement" : f.Kind, f.ContractId, f.VenueContractId, f.OrderId, f.OccurredAtUtc)))
            .DistinctBy(m => (m.Kind, m.SourceId)).ToList();
        if (markers.Count == 0) return;
        // An unreported time lies before the read finished, which can be after its snapshot: Lighter reads its trades
        // later (Sol round 1). Recording happens after the read, so its time bounds it.
        var recordedAt = (time ?? TimeProvider.System).GetUtcNow();
        var readBy = recordedAt > observedAt ? recordedAt : observedAt;
        var ids = markers.Select(m => m.SourceId).Distinct().ToArray();
        var known = (await db.ExecutionOmissions.Where(m => m.AccountId == account.Id && ids.Contains(m.SourceId)).ToListAsync(ct))
            .ToDictionary(m => (m.Kind, m.SourceId));
        foreach (var marker in markers)
        {
            if (known.TryGetValue((marker.Kind, marker.SourceId), out var row))
            {
                if (observedAt > row.LastSeenAtUtc) row.LastSeenAtUtc = observedAt;
                if (row.OccurredAtUtc is null && marker.OccurredAtUtc is { } at)
                    (row.OccurredAtUtc, row.UncertainFromUtc, row.UncertainToUtc) = (at.ToUniversalTime(), null, null);
                row.ContractId ??= marker.ContractId;
                row.VenueContractId ??= marker.VenueContractId;
                row.OrderId ??= marker.OrderId;
                continue;
            }
            db.ExecutionOmissions.Add(new ExecutionOmission
            {
                Id = Guid.CreateVersion7(), OwnerId = account.OwnerId, AccountId = account.Id, Kind = marker.Kind, Reason = marker.Reason,
                SourceId = marker.SourceId, ContractId = marker.ContractId, VenueContractId = marker.VenueContractId, OrderId = marker.OrderId,
                OccurredAtUtc = marker.OccurredAtUtc?.ToUniversalTime(),
                UncertainFromUtc = marker.OccurredAtUtc is null ? windowFrom?.ToUniversalTime() : null,
                UncertainToUtc = marker.OccurredAtUtc is null ? readBy.ToUniversalTime() : null,
                FirstSeenAtUtc = observedAt, LastSeenAtUtc = observedAt
            });
        }
        var forcedIds = forced.Select(f => f.SourceId).Distinct().ToArray();
        var retainedForced = (await db.ForcedEvents.Where(e => e.AccountId == account.Id && forcedIds.Contains(e.SourceId)).ToListAsync(ct))
            .ToDictionary(e => e.SourceId);
        foreach (var e in forced.DistinctBy(f => f.SourceId))
        {
            // The first copy is kept, as with fills; a repeat only moves the last-seen time.
            if (retainedForced.TryGetValue(e.SourceId, out var row)) { if (observedAt > row.LastSeenAtUtc) row.LastSeenAtUtc = observedAt; continue; }
            db.ForcedEvents.Add(new ForcedEvent
            {
                Id = Guid.CreateVersion7(), OwnerId = account.OwnerId, AccountId = account.Id, SourceId = e.SourceId, Kind = e.Kind,
                ContractId = e.ContractId, VenueContractId = e.VenueContractId, Side = e.Side, Quantity = e.Quantity, Price = e.Price,
                OccurredAtUtc = e.OccurredAtUtc.ToUniversalTime(), OrderId = e.OrderId, Fee = e.Fee, RealizedPnl = e.RealizedPnl,
                Exclusion = $"Venue-forced {(e.Kind == ForcedEvent.Settlement ? "market settlement" : e.Kind)}: kept beside the fills, never linked or scored.",
                FirstSeenAtUtc = observedAt, LastSeenAtUtc = observedAt
            });
        }
        // An order named by a marker has known missing executions, whatever its status. A marker naming a known order on
        // another market fails closed, like the order itself would (Sol round 1).
        var named = markers.Where(m => m.OrderId is not null).ToList();
        if (named.Count == 0) return;
        var orderIds = named.Select(m => m.OrderId!).Distinct().ToArray();
        foreach (var order in await db.Orders.Where(o => o.AccountId == account.Id && orderIds.Contains(o.ExternalOrderId)).ToListAsync(ct))
            foreach (var marker in named)
                switch (ExecutionOmission.Names(marker.ContractId, marker.VenueContractId, marker.OrderId!, order))
                {
                    case null: throw new VenueReadException(OmissionConflict);
                    case true: order.ExecutionsOmitted = true; break;
                }
    }

    private Dictionary<(string, string), ImportedFill> AddFills(Account account, IEnumerable<VenueFill> newFills, bool backfilled = false)
    {
        var stored = new Dictionary<(string, string), ImportedFill>();
        foreach (var f in newFills)
        {
            db.Fills.Add(stored[(f.ContractId, f.SourceFillId)] = new ImportedFill
            {
                Id = Guid.NewGuid(),
                OwnerId = account.OwnerId,
                AccountId = account.Id,
                ContractId = f.ContractId,
                VenueContractId = f.VenueContractId,
                SourceFillId = f.SourceFillId,
                Side = f.Side,
                Direction = f.Direction,
                PositionEffect = f.PositionEffect,
                Price = f.Price,
                Quantity = f.Quantity,
                Fee = f.Fee,
                FeeToken = f.FeeToken,
                FeeBasis = f.FeeBasis,
                ClosedPnlUsd = f.ClosedPnlUsd,
                PnlBasis = f.PnlBasis,
                OccurredAtUtc = f.OccurredAtUtc.ToUniversalTime(),
                ExternalOrderId = f.OrderId,
                TransactionHash = f.TransactionHash,
                Leverage = f.Leverage,
                Backfilled = backfilled
            });
        }
        return stored;
    }

    /// <summary>
    /// Phase 2, item 14c (F-12, probe V10): a retained fill ID reported again with different venue facts keeps its first
    /// copy and is recorded as a conflict with the differing field names. The row is upserted per fill, never with either
    /// payload.
    /// </summary>
    private async Task RecordFillConflictsAsync(Account account, List<ImportedFill> retained,
        Dictionary<(string, string), ImportedFill> added, IReadOnlyList<VenueFill> incoming, DateTimeOffset observedAt,
        CancellationToken ct)
    {
        var byIdentity = retained.ToDictionary(f => (f.ContractId, f.SourceFillId));
        var firstCopies = new HashSet<(string, string)>();
        var differing = new Dictionary<Guid, (ImportedFill Fill, HashSet<string> Fields)>();
        foreach (var report in incoming)
        {
            var key = (report.ContractId, report.SourceFillId);
            // A fill new in this batch was stored from its first copy; only its later copies are compared with it.
            if (!byIdentity.TryGetValue(key, out var stored) && !(added.TryGetValue(key, out stored) && !firstCopies.Add(key))) continue;
            var fields = DifferingFields(stored!, report);
            if (fields.Count == 0) continue;
            if (differing.TryGetValue(stored.Id, out var known)) known.Fields.UnionWith(fields);
            else differing[stored.Id] = (stored, fields);
        }
        if (differing.Count == 0) return;
        var ids = differing.Keys.ToArray();
        var recorded = await db.FillImportConflicts.Where(c => c.AccountId == account.Id && ids.Contains(c.FillId)).ToDictionaryAsync(c => c.FillId, ct);
        var seen = observedAt.ToUniversalTime();
        foreach (var (fillId, (fill, fields)) in differing)
        {
            if (recorded.TryGetValue(fillId, out var conflict))
            {
                fields.UnionWith(conflict.Fields.Split(','));
                conflict.Fields = string.Join(",", FillConflictFields.All.Where(fields.Contains));
                conflict.LastSeenAtUtc = seen > conflict.LastSeenAtUtc ? seen : conflict.LastSeenAtUtc;
                conflict.Reports++;
                continue;
            }
            db.FillImportConflicts.Add(new FillImportConflict
            {
                Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, FillId = fillId, ContractId = fill.ContractId,
                SourceFillId = fill.SourceFillId, Fields = string.Join(",", FillConflictFields.All.Where(fields.Contains)),
                FirstSeenAtUtc = seen, LastSeenAtUtc = seen, Reports = 1,
            });
        }
    }

    /// <summary>The venue facts that differ between the retained copy and a new report (see <see cref="FillConflictFields"/>).</summary>
    internal static HashSet<string> DifferingFields(ImportedFill stored, VenueFill report)
    {
        // PostgreSQL keeps microseconds; compare the report at the precision it can be stored with.
        static DateTimeOffset Stored(DateTimeOffset value) => value.ToUniversalTime().AddTicks(-(value.UtcTicks % 10));
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (stored.Side != report.Side) fields.Add(FillConflictFields.Side);
        // The adapter's wording of the venue's own direction (Hyperliquid's dir, RISEx's position side): a venue fact, unlike
        // the position effect or bases Vessel derives (Sol round 1 on 14c).
        if (stored.Direction != report.Direction) fields.Add(FillConflictFields.Direction);
        if (stored.Price != report.Price) fields.Add(FillConflictFields.Price);
        if (stored.Quantity != report.Quantity) fields.Add(FillConflictFields.Quantity);
        if (stored.Fee != report.Fee) fields.Add(FillConflictFields.Fee);
        if (stored.FeeToken != report.FeeToken) fields.Add(FillConflictFields.FeeToken);
        if (stored.ClosedPnlUsd != report.ClosedPnlUsd) fields.Add(FillConflictFields.ClosedPnl);
        if (stored.OccurredAtUtc.ToUniversalTime() != Stored(report.OccurredAtUtc)) fields.Add(FillConflictFields.Time);
        if (stored.ExternalOrderId != report.OrderId) fields.Add(FillConflictFields.OrderId);
        if (stored.TransactionHash != report.TransactionHash) fields.Add(FillConflictFields.TransactionHash);
        if (stored.VenueContractId is not null && report.VenueContractId is not null && stored.VenueContractId != report.VenueContractId)
            fields.Add(FillConflictFields.VenueContract);
        if (stored.Leverage is not null && report.Leverage is not null && stored.Leverage != report.Leverage)
            fields.Add(FillConflictFields.Leverage);
        return fields;
    }

    public async Task<HistoryCoverageFacts> HistoryCoverageAsync(Guid accountId, int limit, CancellationToken ct)
    {
        var gaps = db.HistoryGaps.AsNoTracking().Where(g => g.AccountId == accountId);
        var open = gaps.Where(g => g.ClosedAtUtc == null);
        var records = db.FillCoverage.AsNoTracking().Where(r => r.AccountId == accountId);
        return new(await open.CountAsync(ct), await open.OrderByDescending(g => g.OpenedAtUtc).ThenBy(g => g.Id).Take(limit).ToListAsync(ct),
            await gaps.Where(g => g.ClosedAtUtc != null).OrderByDescending(g => g.ClosedAtUtc).ThenBy(g => g.Id).Take(limit).ToListAsync(ct),
            await records.CountAsync(ct), await records.OrderByDescending(r => r.LastObservedAtUtc).ThenByDescending(r => r.Id).Take(limit).ToListAsync(ct))
        {
            Backfills = await db.HistoryBackfills.AsNoTracking().Where(b => b.AccountId == accountId).OrderByDescending(b => b.CreatedAtUtc)
                .ThenByDescending(b => b.Id).Take(10).ToListAsync(ct)
        };
    }

    public async Task<(int Total, IReadOnlyList<FillImportConflict> Recent)> FillConflictsAsync(Guid accountId, int limit, CancellationToken ct)
    {
        var query = db.FillImportConflicts.AsNoTracking().Where(c => c.AccountId == accountId);
        var total = await query.CountAsync(ct);
        var recent = total == 0 ? [] : await query.OrderByDescending(c => c.LastSeenAtUtc).ThenBy(c => c.Id).Take(limit).ToListAsync(ct);
        return (total, recent);
    }

    /// <summary>
    /// Appends one observation per reported leverage whose position is new to the replaced snapshot, whose signed
    /// quantity, entry price or leverage differs from the latest retained observation of that account and native
    /// contract, or whose market received newly imported executions or has retained executions at or after its last
    /// observation. A position can close and reopen identically between refreshes, and history can arrive before the
    /// snapshot that observes the reopening. Preserve that later observation even when its executions are already
    /// retained. A fill without a native identity matches every native market of its canonical contract. After any
    /// equal-timestamp execution gets a later observation, idle refreshes add nothing.
    /// </summary>
    private async Task RecordLeverageAsync(Account account, VenueSnapshot observation, IReadOnlyCollection<AccountPosition> replaced,
        IReadOnlyCollection<VenueFill> newFills, CancellationToken ct)
    {
        var reported = observation.Positions.Where(p => p.Leverage is not null).ToList();
        if (reported.Count == 0) return;
        var contracts = reported.Select(p => p.ContractId).Distinct().ToArray();
        var latest = await db.LeverageObservations.Where(o => o.AccountId == account.Id && contracts.Contains(o.ContractId))
            .GroupBy(o => new { o.ContractId, o.VenueContractId })
            .Select(g => g.OrderByDescending(o => o.ObservedAtUtc).ThenByDescending(o => o.Id).First())
            .AsNoTracking().ToListAsync(ct);
        var observedAt = observation.ObservedAtUtc.ToUniversalTime();
        var previousByPartition = latest.ToDictionary(o => (o.ContractId, o.VenueContractId));
        var relevant = latest.Where(o => o.ObservedAtUtc < observedAt && reported.Any(p =>
            p.ContractId == o.ContractId && p.VenueContractId == o.VenueContractId)).ToList();
        // History is read separately from the snapshot and may already include a close/reopen that the replaced
        // snapshot predates. Keep only the latest execution time per partition, bounded to the observation interval;
        // no execution before the previous observation or after this snapshot can prove that it saw the reopening.
        var since = relevant.Count == 0 ? (DateTimeOffset?)null : relevant.Min(o => o.ObservedAtUtc);
        var retainedExecutions = since is { } from
            ? await db.Fills.Where(f => f.AccountId == account.Id && contracts.Contains(f.ContractId) &&
                f.OccurredAtUtc >= from && f.OccurredAtUtc <= observedAt)
                .GroupBy(f => new { f.ContractId, f.VenueContractId })
                .Select(g => new { g.Key.ContractId, g.Key.VenueContractId, LastOccurredAtUtc = g.Max(f => f.OccurredAtUtc) })
                .ToListAsync(ct)
            : [];
        foreach (var position in reported)
        {
            var previous = previousByPartition.GetValueOrDefault((position.ContractId, position.VenueContractId));
            var continued = replaced.Any(p => p.ContractId == position.ContractId && p.VenueContractId == position.VenueContractId &&
                p.SignedQuantity == position.SignedQuantity && p.EntryPrice == position.EntryPrice);
            var executed = newFills.Any(f => f.ContractId == position.ContractId &&
                (f.VenueContractId is null || position.VenueContractId is null || f.VenueContractId == position.VenueContractId)) ||
                previous is not null && retainedExecutions.Any(f => f.LastOccurredAtUtc >= previous.ObservedAtUtc &&
                    f.ContractId == position.ContractId &&
                    (f.VenueContractId is null || position.VenueContractId is null || f.VenueContractId == position.VenueContractId));
            if (continued && !executed && previous is not null && previous.Leverage == position.Leverage && previous.SignedQuantity == position.SignedQuantity &&
                previous.EntryPrice == position.EntryPrice || previous is not null && previous.ObservedAtUtc >= observedAt) continue;
            db.LeverageObservations.Add(new LeverageObservation
            {
                Id = Guid.NewGuid(),
                OwnerId = account.OwnerId,
                AccountId = account.Id,
                ContractId = position.ContractId,
                VenueContractId = position.VenueContractId,
                ObservedAtUtc = observedAt,
                SignedQuantity = position.SignedQuantity,
                EntryPrice = position.EntryPrice,
                Leverage = position.Leverage!.Value
            });
        }
    }

    /// <summary>
    /// Keeps the venue's reported liquidation price and margin mode (Phase 2, item 8a) when a position appears, when its
    /// quantity, entry price or margin mode changes, when an isolated position's liquidation price changes, or when its
    /// market has executions since the last observation, newly imported or already retained (history can arrive before the
    /// snapshot that observes a reopening, as for leverage). A cross-margin liquidation price that only drifts with the
    /// account is not kept again; the snapshot holds the latest. Positions whose venue reports neither are skipped.
    /// </summary>
    private async Task RecordRiskAsync(Account account, VenueSnapshot observation, IReadOnlyCollection<AccountPosition> replaced,
        IReadOnlyCollection<VenueFill> newFills, CancellationToken ct)
    {
        var reported = observation.Positions.Where(p => p.LiquidationPrice is not null || p.MarginMode is not null).ToList();
        if (reported.Count == 0) return;
        var contracts = reported.Select(p => p.ContractId).Distinct().ToArray();
        var latest = (await db.RiskObservations.Where(o => o.AccountId == account.Id && contracts.Contains(o.ContractId))
            .GroupBy(o => new { o.ContractId, o.VenueContractId })
            .Select(g => g.OrderByDescending(o => o.ObservedAtUtc).ThenByDescending(o => o.Id).First())
            .AsNoTracking().ToListAsync(ct)).ToDictionary(o => (o.ContractId, o.VenueContractId));
        var observedAt = observation.ObservedAtUtc.ToUniversalTime();
        // Sol round 1 on #41: executions already retained since the last observation count too, bounded like leverage.
        var relevant = latest.Values.Where(o => o.ObservedAtUtc < observedAt).ToList();
        var since = relevant.Count == 0 ? (DateTimeOffset?)null : relevant.Min(o => o.ObservedAtUtc);
        var retainedExecutions = since is { } from
            ? await db.Fills.Where(f => f.AccountId == account.Id && contracts.Contains(f.ContractId) &&
                f.OccurredAtUtc >= from && f.OccurredAtUtc <= observedAt)
                .GroupBy(f => new { f.ContractId, f.VenueContractId })
                .Select(g => new { g.Key.ContractId, g.Key.VenueContractId, LastOccurredAtUtc = g.Max(f => f.OccurredAtUtc) })
                .ToListAsync(ct)
            : [];
        foreach (var position in reported)
        {
            var previous = latest.GetValueOrDefault((position.ContractId, position.VenueContractId));
            if (previous is not null && previous.ObservedAtUtc >= observedAt) continue;
            var continued = replaced.Any(p => p.ContractId == position.ContractId && p.VenueContractId == position.VenueContractId &&
                p.SignedQuantity == position.SignedQuantity && p.EntryPrice == position.EntryPrice);
            bool Market(string contractId, string? native) => contractId == position.ContractId &&
                (native is null || position.VenueContractId is null || native == position.VenueContractId);
            var executed = newFills.Any(f => Market(f.ContractId, f.VenueContractId)) || previous is not null &&
                retainedExecutions.Any(f => f.LastOccurredAtUtc >= previous.ObservedAtUtc && Market(f.ContractId, f.VenueContractId));
            // An isolated position's liquidation price is its own; only a cross price drifts with the whole account.
            var sameLiquidation = position.MarginMode == MarginModes.Cross || previous?.LiquidationPrice == position.LiquidationPrice;
            if (continued && !executed && previous is not null && previous.SignedQuantity == position.SignedQuantity &&
                previous.EntryPrice == position.EntryPrice && previous.MarginMode == position.MarginMode && sameLiquidation) continue;
            db.RiskObservations.Add(new RiskObservation
            {
                Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = position.ContractId,
                VenueContractId = position.VenueContractId, ObservedAtUtc = observedAt, SignedQuantity = position.SignedQuantity,
                EntryPrice = position.EntryPrice, LiquidationPrice = position.LiquidationPrice, MarginMode = position.MarginMode,
            });
        }
    }
}
