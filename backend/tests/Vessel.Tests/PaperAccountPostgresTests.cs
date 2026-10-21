using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperAccountPostgresTests
{
    private static WorkspaceService Service(VesselDbContext db, Guid owner) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner),
            TestVenues.With(new FixtureReader()), TestUnitOfWork.For(db));

    private static async Task<AccountDto> PaperAccount(VesselDbContext db, Guid owner, string? capital = null)
    {
        var service = Service(db, owner);
        var portfolio = await service.CreatePortfolioAsync(new("Paper", TradingMode: "paper"), default);
        return await service.CreateAccountAsync(new(portfolio.Id, "Paper", "hyperliquid",
            TradingMode: "paper", StartingCapitalUsd: capital), default);
    }

    [PostgresFact]
    public async Task Migration_marks_retained_accounts_portfolios_and_plays_real()
    {
        await using var database = await CoreDatabase.CreateAsync("20261019132931_MonitorCoverage");
        var owner = Guid.NewGuid(); var portfolio = Guid.NewGuid(); var account = Guid.NewGuid(); var play = Guid.NewGuid();
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO portfolios (\"Id\",\"OwnerId\",\"Name\") VALUES ({portfolio},{owner},'Legacy')");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO accounts (\"Id\",\"OwnerId\",\"VenueId\",\"Name\",\"PortfolioId\",\"SyncStatus\") VALUES ({account},{owner},'manual','Legacy',{portfolio},'manual')");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO plays (\"Id\",\"OwnerId\",\"AccountId\",\"VenueId\",\"ContractId\",\"Status\",\"Direction\") VALUES ({play},{owner},{account},'manual','BTC','Draft','long')");
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.Equal("real", (await db.Accounts.SingleAsync()).TradingMode);
        Assert.Null((await db.Accounts.SingleAsync()).StartingCapitalUsd);
        Assert.Equal("real", (await db.Portfolios.SingleAsync()).TradingMode);
        Assert.Equal("real", (await db.Plays.SingleAsync()).TradingMode);
        Assert.Empty(await db.PaperLedgerEntries.ToListAsync());
    }

    [PostgresFact]
    public async Task Creation_atomically_seeds_exact_capital_and_defaults_reads_to_real()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var service = Service(db, owner);
        var real = await service.CreateAccountAsync(new(null, "Real", "manual", ManualAccountValueUsd: "123.456"), default);
        var paper = await PaperAccount(db, owner);
        Assert.Equal("4000", paper.StartingCapitalUsd);
        Assert.Equal("4000", paper.BalanceUsd);
        Assert.Equal("4000", paper.AccountValueUsd);
        Assert.Equal("4000", paper.AvailableBalanceUsd);
        Assert.Equal("paper", paper.AvailableBalanceSource);
        Assert.Null(paper.Address); Assert.Null(paper.SourceId); Assert.Null(paper.AccountMode);
        Assert.Null(paper.TotalStablecoinNominalUsd);
        var seed = Assert.Single(await db.PaperLedgerEntries.ToListAsync());
        Assert.Equal(paper.Id, seed.AccountId); Assert.Equal(4000m, seed.AmountUsd); Assert.Equal("seed", seed.Kind);
        Assert.Equal(real.Id, Assert.Single(await service.AccountsAsync(default)).Id);
        Assert.Equal(paper.Id, Assert.Single(await service.AccountsAsync(default, "paper")).Id);
        Assert.Null(await service.SnapshotAsync(paper.Id, default, "paper"));
        Assert.Equal(0, (await service.OverviewAsync(default, "paper")).Totals.ImportedFillCount);
        Assert.Equal("123.456", (await service.OverviewAsync(default)).Totals.TotalBalanceUsd);
        Assert.Equal("4000", (await service.OverviewAsync(default, "paper")).Totals.TotalBalanceUsd);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.DeleteAccountAsync(paper.Id, default, "paper"))).StatusCode);
    }

    [PostgresFact]
    public async Task Zero_capital_remains_known_zero()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await PaperAccount(db, owner, "0.000");
        Assert.Equal("0", account.StartingCapitalUsd);
        Assert.Equal("0", account.AvailableBalanceUsd);
        Assert.Equal("0", account.AccountValueUsd);
        Assert.Equal("complete", Assert.Single(await Service(db, owner).PortfoliosAsync(default, "paper")).BalanceCoverage);
    }

    [PostgresFact]
    public async Task PostgreSQL_enforces_immutable_capital_and_mode_and_append_only_seed()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await PaperAccount(db, owner);
        var seed = await db.PaperLedgerEntries.SingleAsync();
        foreach (var command in new[]
        {
            $"UPDATE accounts SET \"StartingCapitalUsd\"=5000 WHERE \"Id\"='{account.Id}'",
            $"UPDATE accounts SET \"TradingMode\"='real' WHERE \"Id\"='{account.Id}'",
            $"UPDATE portfolios SET \"TradingMode\"='real' WHERE \"Id\"='{account.PortfolioId}'",
            $"UPDATE paper_ledger_entries SET \"AmountUsd\"=5000 WHERE \"Id\"='{seed.Id}'",
            $"DELETE FROM paper_ledger_entries WHERE \"Id\"='{seed.Id}'",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(command));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        Assert.Equal(4000m, (await db.PaperLedgerEntries.AsNoTracking().SingleAsync()).AmountUsd);
    }

    [PostgresFact]
    public async Task Composite_owner_mode_constraints_reject_cross_mode_membership_and_cross_account_paper_facts()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var service = Service(db, owner);
        var paper = await PaperAccount(db, owner);
        var other = await PaperAccount(db, owner);
        var real = await service.CreatePortfolioAsync(new("Real"), default);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE accounts SET \"PortfolioId\"={real.Id} WHERE \"Id\"={paper.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        var account = await db.Accounts.SingleAsync(a => a.Id == paper.Id);
        var play = new Play(Guid.NewGuid(), account, null, InstrumentSource.Manual, "{}", DateTimeOffset.UtcNow);
        db.Plays.Add(play); await db.SaveChangesAsync();
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE plays SET \"TradingMode\"='real' WHERE \"Id\"={play.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO paper_reservations (\"PlayId\",\"OwnerId\",\"AccountId\",\"TradingMode\",\"AmountUsd\") VALUES ({play.Id},{owner},{other.Id},'paper',10)"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        var foreignOwner = Guid.NewGuid();
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO accounts (\"Id\",\"OwnerId\",\"VenueId\",\"Name\",\"PortfolioId\",\"SyncStatus\",\"TradingMode\",\"StartingCapitalUsd\") VALUES ({Guid.NewGuid()},{foreignOwner},'hyperliquid','Foreign',{paper.PortfolioId},'paper','paper',4000)"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [PostgresFact]
    public async Task Actual_fact_inserts_cannot_target_paper_accounts_or_plays()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var dto = await PaperAccount(db, owner);
        var account = await db.Accounts.SingleAsync();
        var play = new Play(Guid.NewGuid(), account, null, InstrumentSource.Manual, "{}", DateTimeOffset.UtcNow);
        db.Plays.Add(play); await db.SaveChangesAsync();
#pragma warning disable EF1002 // Identifiers are the fixed table lists below; values are generated GUIDs, never request text.
        foreach (var table in new[]
        {
            "imported_fills", "imported_orders", "account_snapshots", "account_positions", "account_stablecoins",
            "account_leverage_observations", "funding_payments", "account_funding_states", "account_credentials"
        })
        {
            // Deliberately bypass every use case: the BEFORE trigger must reject before other required fields.
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {table} (\"OwnerId\",\"AccountId\") VALUES ('{owner}','{dto.Id}')"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var table in new[] { "manual_executions", "manual_execution_history", "play_order_links" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {table} (\"OwnerId\",\"PlayId\") VALUES ('{owner}','{play.Id}')"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
#pragma warning restore EF1002
    }

    [PostgresFact]
    public async Task Virtual_balances_distinguish_reservation_margin_unknown_marks_and_disable_freeze()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var dto = await PaperAccount(db, owner);
        var account = await db.Accounts.SingleAsync();
        var now = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var play = new Play(Guid.NewGuid(), account, null, InstrumentSource.Manual, "{}", now);
        db.Plays.Add(play); await db.SaveChangesAsync();
        db.PaperReservations.Add(new() { OwnerId = owner, AccountId = account.Id, PlayId = play.Id, AmountUsd = 100 });
        db.PaperExecutions.Add(new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id, EntryId = "entry",
            Role = "entry", Price = 100, Quantity = 10, MarginUsd = 200, PlanVersion = 1,
            OccurredAtUtc = now.AddMinutes(1), CandleOpenAtUtc = now, CandleCloseAtUtc = now.AddMinutes(1)
        });
        db.PaperSimulations.Add(new() { OwnerId = owner, AccountId = account.Id, PlayId = play.Id, State = "waiting", NextCheckAtUtc = now.AddHours(1) });
        // The account visibility hook freezes active simulations, even when disable and enable occur between scans.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plays SET \"Status\"='Planned' WHERE \"Id\"={play.Id}");
        await db.SaveChangesAsync();
        var service = Service(db, owner);
        var before = await service.AccountAsync(dto.Id, default, "paper");
        Assert.Equal("4000", before.BalanceUsd); Assert.Null(before.AccountValueUsd); Assert.Equal("3700", before.AvailableBalanceUsd);
        await service.UpdateAccountAsync(dto.Id, new("Disabled", dto.PortfolioId, false, 1), default, "paper");
        await service.UpdateAccountAsync(dto.Id, new("Enabled", dto.PortfolioId, true, 2), default, "paper");
        var after = await service.AccountAsync(dto.Id, default, "paper");
        Assert.Null(after.AvailableBalanceUsd);
        var state = await db.PaperSimulations.SingleAsync();
        Assert.Equal("suspended", state.State); Assert.Null(state.NextCheckAtUtc);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<string[]>(state.IssuesJson)!);
        Assert.Contains("disabled", Assert.Single(state.Suspensions()).Reason);
        Assert.Equal(100m, (await db.PaperReservations.SingleAsync()).AmountUsd);
        Assert.Equal(200m, (await db.PaperExecutions.SingleAsync()).MarginUsd);
    }

    [PostgresFact]
    public async Task Unresolved_read_issues_or_ambiguous_touches_withhold_available_and_equity_without_recorded_entries()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var dto = await PaperAccount(db, owner);
        var account = await db.Accounts.SingleAsync();
        var play = new Play(Guid.NewGuid(), account, null, InstrumentSource.Manual, "{}", DateTimeOffset.UtcNow);
        db.Plays.Add(play); await db.SaveChangesAsync();
        var simulation = new PaperSimulation { OwnerId = owner, AccountId = account.Id, PlayId = play.Id };
        db.PaperSimulations.Add(simulation); await db.SaveChangesAsync();
        var service = Service(db, owner);
        foreach (var (state, issues) in new[] { ("waiting", "[\"Temporary candle read failed.\"]"), ("ambiguous", "[]"), ("gap", "[]") })
        {
            simulation.State = state; simulation.IssuesJson = issues;
            await db.SaveChangesAsync();
            var held = await service.AccountAsync(dto.Id, default, "paper");
            Assert.Equal("4000", held.BalanceUsd);
            Assert.Null(held.AvailableBalanceUsd);
            Assert.Null(held.AccountValueUsd);
            Assert.Equal(0, held.PositionCount);
        }
        simulation.State = "waiting"; simulation.IssuesJson = "[]";
        await db.SaveChangesAsync();
        var resolved = await service.AccountAsync(dto.Id, default, "paper");
        Assert.Equal("4000", resolved.AccountValueUsd);
        Assert.Equal("4000", resolved.AvailableBalanceUsd);
    }

    [PostgresFact]
    public async Task Balance_uses_one_snapshot_when_entry_atomically_replaces_its_reservation()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        Guid accountId, playId;
        await using (var setup = database.Context(owner))
        {
            accountId = (await PaperAccount(setup, owner)).Id;
            var account = await setup.Accounts.SingleAsync();
            var play = new Play(Guid.NewGuid(), account, null, InstrumentSource.Manual, "{}", now);
            playId = play.Id;
            setup.Plays.Add(play); await setup.SaveChangesAsync();
            setup.PaperReservations.Add(new() { OwnerId = owner, AccountId = accountId, PlayId = playId, AmountUsd = 200 });
            await setup.SaveChangesAsync();
        }
        var interleave = new EntryCommitInterceptor(async () =>
        {
            await using var writer = database.Context(owner);
            await using var transaction = await writer.Database.BeginTransactionAsync();
            writer.PaperExecutions.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = accountId, PlayId = playId, EntryId = "entry",
                Role = "entry", Price = 100, Quantity = 10, MarginUsd = 200, PlanVersion = 1,
                OccurredAtUtc = now.AddMinutes(1), CandleOpenAtUtc = now, CandleCloseAtUtc = now.AddMinutes(1)
            });
            await writer.PaperReservations.Where(r => r.PlayId == playId).ExecuteDeleteAsync();
            await writer.SaveChangesAsync(); await transaction.CommitAsync();
        });
        await using var reader = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interleave).Options, new CoreOwner(owner));
        // Existing read-committed transactions must be safe too: no new RR transaction can be substituted.
        var balances = await TestUnitOfWork.For(reader).ExecuteAsync(() =>
            new WorkspaceStore(reader, TestUnitOfWork.For(reader)).PaperBalancesAsync([accountId], default), default);
        Assert.True(interleave.Committed);
        Assert.Equal("3800", balances[accountId].AvailableMarginUsd);
        Assert.Equal("4000", balances[accountId].CashUsd);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.PaperReservations.ToListAsync());
        Assert.Equal(200m, (await verify.PaperExecutions.SingleAsync()).MarginUsd);
        Assert.Equal("3800", (await new WorkspaceStore(verify, TestUnitOfWork.For(verify))
            .PaperBalancesAsync([accountId], default))[accountId].AvailableMarginUsd);
    }

    private sealed class EntryCommitInterceptor(Func<Task> commit) : DbCommandInterceptor
    {
        public bool Committed { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Committed && command.CommandText.Contains("paper_executions", StringComparison.Ordinal))
            {
                Committed = true;
                // The old multi-read implementation reached here with open margin already read as zero.
                // Releasing its reservation before that later read then incorrectly advertised all 4000.
                await commit();
            }
            return result;
        }
    }
}
