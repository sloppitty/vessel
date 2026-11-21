using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Vessel.Api;
using Vessel.Application.Activity;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Issue #108: the Activity read model returns what the in-memory aggregate returns, field for field, over the golden journal
/// and a large synthetic journal; a routine monitor check rewrites no stored row; the version still flags changed data.
/// </summary>
public sealed class ActivityReadModelPostgresTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset End = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    /// <summary>Activity over one database twice: the in-memory aggregate (the reference) and the read model.</summary>
    private sealed class Paths : IAsyncDisposable
    {
        private readonly List<VesselDbContext> contexts = [];
        public ActivityService Memory { get; }
        public ActivityService Projected { get; }
        public ActivityReadModel Model { get; }

        public Paths(CoreDatabase database, Guid owner, TimeProvider clock, IVenueRegistry venues, ActivityReadModelState? state = null)
        {
            VesselDbContext Context() { var db = database.Context(owner); contexts.Add(db); return db; }
            var memory = Context();
            Memory = new ActivityService(new ActivityStore(memory), new WorkspaceStore(memory, TestUnitOfWork.For(memory)), clock, venues,
                marks: new MarketContextCache(clock));
            var model = Context();
            Model = new ActivityReadModel(model, new ActivityStore(model), state ?? new ActivityReadModelState(), venues);
            var projected = Context();
            Projected = new ActivityService(new ActivityStore(projected), new WorkspaceStore(projected, TestUnitOfWork.For(projected)), clock,
                venues, marks: new MarketContextCache(clock), pages: Model);
        }

        public async ValueTask DisposeAsync() { foreach (var db in contexts) await db.DisposeAsync(); }
    }

    private static async Task<string> SameAsync(Paths paths, ActivityQuery query)
    {
        var expected = JsonSerializer.Serialize(await paths.Memory.PageAsync(query, default), Web);
        var actual = JsonSerializer.Serialize(await paths.Projected.PageAsync(query, default), Web);
        Assert.Equal(expected, actual);
        return expected;
    }

    /// <summary>Every page at two sizes, past the end, and each filter, scope and account detail the facets offer.</summary>
    private static async Task<int> SameEverywhereAsync(Paths paths, IReadOnlyList<Guid> accounts, IReadOnlyList<Guid> portfolios)
    {
        var compared = 0;
        var first = await paths.Memory.PageAsync(new(PageSize: "100"), default);
        Assert.True(first.ScopeCount > 0);
        foreach (var size in new[] { 100, 7 })
            for (var page = 1; page <= first.ScopeCount / size + 2; page += size == 7 ? 13 : 1, compared++)
                await SameAsync(paths, new(Page: page.ToString(), PageSize: size.ToString()));
        await SameAsync(paths, new(Page: "99999", PageSize: "25"));
        var filters = new List<ActivityQuery>
        {
            new(Direction: "long"), new(Direction: "short"), new(Status: "open"), new(Status: "partial"), new(Status: "closed"),
            new(Status: "incomplete"), new(Period: "7"), new(Period: "30"), new(Period: "90"),
            new(Period: "custom", From: End.AddDays(-60).ToString("yyyy-MM-dd"), To: End.AddDays(-20).ToString("yyyy-MM-dd")),
            new(Period: "custom", To: End.AddDays(-100).ToString("yyyy-MM-dd")), new(Portfolio: "unassigned"), new(Scope: "unassigned"),
            new(Direction: "short", Status: "closed", Period: "90")
        };
        filters.AddRange(first.Facets.Contracts.Select(c => new ActivityQuery(Contract: c)));
        filters.Add(new(Contract: "NOT-TRADED"));
        foreach (var account in accounts)
        {
            filters.Add(new(Account: account.ToString("D")));
            filters.Add(new(Scope: $"account:{account:D}"));
            filters.Add(new(AccountId: account));
            filters.Add(new(AccountId: account, Status: "closed", Direction: "long"));
        }
        foreach (var portfolio in portfolios)
        {
            filters.Add(new(Portfolio: portfolio.ToString("D")));
            filters.Add(new(Scope: $"portfolio:{portfolio:D}", Status: "open"));
        }
        foreach (var filter in filters)
        {
            await SameAsync(paths, filter with { PageSize = "100" });
            await SameAsync(paths, filter with { PageSize = "5", Page = "2" });
            compared += 2;
        }
        return compared;
    }

    private static async Task<ActivityVerifyResult> VerifyAsync(CoreDatabase database, Guid owner, IVenueRegistry venues)
    {
        await using var db = database.Context(owner);
        return await new ActivityReadModel(db, new ActivityStore(db), new ActivityReadModelState(), venues).VerifyAsync(default);
    }

    private static void Verified(ActivityVerifyResult result)
    {
        Assert.True(result.Differences.Count == 0, string.Join("\n", result.Differences));
        Assert.Equal(0, result.StaleAccounts);
        Assert.True(result.Operations > 0);
    }

    // A deterministic journal with what the walk distinguishes: partial reductions, flips, shared timestamps, unknown and
    // contradictory effects, missing order IDs, other fee tokens, gross and net P&L, two native markets under one canonical
    // key, ID-less legacy fills, omission markers, funding (some on execution boundaries), leverage observations, more than
    // 50 forced events, and complete, disagreeing, partial and missing snapshots, on a disabled account too.
    private sealed record Synthetic(Guid Owner, Portfolio Portfolio, Account[] Accounts);

    private static async Task<Synthetic> SeedAsync(CoreDatabase database, int fills = 900)
    {
        var random = new Random(108);
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Swing", TradingMode.Real, "folder");
        var gross = new Account(Guid.NewGuid(), owner, "hyperliquid", "Gross", TradingMode.Real);
        gross.Configure(portfolio.Id, null, null);
        var net = new Account(Guid.NewGuid(), owner, "risex", "Net", TradingMode.Real);
        net.Configure(portfolio.Id, null, null);
        var manual = new Account(Guid.NewGuid(), owner, "manual", "Manual", TradingMode.Real);
        var disabled = new Account(Guid.NewGuid(), owner, "hyperliquid", "Disabled", TradingMode.Real);
        disabled.Configure(portfolio.Id, null, null);
        disabled.UpdateSettings(disabled.Name, portfolio.Id, false);
        await using var db = database.Context(owner);
        db.Portfolios.Add(portfolio);
        db.Accounts.AddRange(gross, net, manual, disabled);
        (string Contract, string? Native)[] markets = [("BTC", "BTC"), ("ETH", null), ("SOL", "SOL"), ("SOL", "xyz:SOL"), ("DOGE", null)];
        var start = End.AddDays(-200);
        var snapshotsAt = End.AddHours(1);
        foreach (var (account, count) in new[] { (gross, fills), (net, fills * 3 / 4), (manual, fills / 3), (disabled, fills / 4) })
        {
            var positions = markets.ToDictionary(m => m, _ => 0m);
            var at = start;
            for (var n = 0; n < count; n++)
            {
                if (random.NextDouble() > 0.07) at = at.AddMinutes(random.Next(1, 600));
                var market = markets[random.Next(markets.Length)];
                var position = positions[market];
                var roll = random.NextDouble();
                decimal quantity; string side, effect;
                if (position == 0 || roll < 0.35)
                {
                    side = position > 0 || position == 0 && random.Next(2) == 0 ? ExecutionFacts.Buy : ExecutionFacts.Sell;
                    quantity = random.Next(1, 6); effect = ExecutionFacts.Open;
                }
                else
                {
                    side = position > 0 ? ExecutionFacts.Sell : ExecutionFacts.Buy;
                    var held = Math.Abs(position);
                    (quantity, effect) = roll < 0.7 && held > 1 ? (random.Next(1, (int)held), ExecutionFacts.Close)
                        : roll < 0.85 ? (held, ExecutionFacts.Close) : (held + random.Next(1, 4), ExecutionFacts.Flip);
                }
                positions[market] = position + (side == ExecutionFacts.Buy ? quantity : -quantity);
                var noise = random.NextDouble();
                if (noise < 0.03) effect = ExecutionFacts.Unknown;
                else if (noise < 0.05) effect = effect == ExecutionFacts.Open ? ExecutionFacts.Close : ExecutionFacts.Open;
                db.Fills.Add(new ImportedFill
                {
                    Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = market.Contract, VenueContractId = market.Native,
                    SourceFillId = $"f{n}", Side = side, Direction = side == ExecutionFacts.Buy ? "Open Long" : "Close Long", PositionEffect = effect,
                    Price = 100 + random.Next(0, 10000) / 100m, Quantity = quantity, Fee = random.Next(1, 100) / 100m,
                    FeeToken = random.NextDouble() < 0.04 ? "HYPE" : "USDC",
                    PnlBasis = account == net || account == manual && n % 2 == 0 ? ExecutionFacts.PnlNetOfFee : ExecutionFacts.PnlGross,
                    ClosedPnlUsd = effect == ExecutionFacts.Open ? 0 : random.Next(-5000, 5000) / 100m, OccurredAtUtc = at,
                    ExternalOrderId = random.NextDouble() < 0.03 ? "" : $"o{n / 2}", TransactionHash = "",
                    Leverage = account == net && random.NextDouble() < 0.5 ? random.Next(1, 20) : null
                });
            }
            if (account == manual) db.Snapshots.Add(new() { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = snapshotsAt, ValueScope = "fixture", PositionsComplete = false });
            else if (account != disabled)
                db.Snapshots.Add(new()
                {
                    OwnerId = owner, AccountId = account.Id, ObservedAtUtc = snapshotsAt, ValueScope = "fixture",
                    // One position per canonical contract, as the table keys it.
                    Positions = positions.Where(p => p.Value != 0).GroupBy(p => p.Key.Contract).Select(g => g.First()).Select((p, i) => new AccountPosition
                    {
                        OwnerId = owner, AccountId = account.Id, ContractId = p.Key.Contract, VenueContractId = p.Key.Native,
                        // The RISEx account's first position disagrees with its fills.
                        SignedQuantity = account == net && i == 0 ? p.Value + 1 : p.Value, EntryPrice = 100, UnrealizedPnlUsd = 1.5m * i,
                        Leverage = i % 2 == 0 ? 5 : null
                    }).ToList()
                });
        }
        var grossFills = db.ChangeTracker.Entries<ImportedFill>().Select(e => e.Entity).Where(f => f.AccountId == gross.Id).ToList();
        for (var i = 0; i < 400; i++)
        {
            var native = i % 3 == 0 ? null : "BTC";
            // Some settlements land exactly on an execution's time, which attribution leaves unassigned.
            var at = i % 37 == 0 ? grossFills[random.Next(grossFills.Count)].OccurredAtUtc : start.AddHours(8 * i + random.Next(0, 7));
            db.FundingPayments.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = gross.Id, SourceId = $"p{i}", ContractId = i % 5 == 0 ? "ETH" : "BTC",
                VenueContractId = i % 5 == 0 ? null : native, Amount = random.Next(-300, 300) / 1000m, Asset = i == 11 ? "HYPE" : "USDC",
                OccurredAtUtc = at
            });
        }
        db.FundingStates.Add(new()
        {
            OwnerId = owner, AccountId = gross.Id, CoveredFromUtc = start.AddDays(-1), CoveredToUtc = End, LastAttemptFromUtc = start,
            LastAttemptToUtc = End, LastReadComplete = true, PnlBasis = FundingFacts.Excluded, Notice = "Funding read through the last refresh."
        });
        db.FundingStates.Add(new()
        {
            OwnerId = owner, AccountId = net.Id, LastAttemptFromUtc = start, LastAttemptToUtc = End, LastReadComplete = false,
            Notice = "Funding history is incomplete."
        });
        for (var i = 0; i < 60; i++)
            db.LeverageObservations.Add(new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = gross.Id, ContractId = i % 2 == 0 ? "BTC" : "SOL",
                VenueContractId = i % 2 == 0 ? "BTC" : i % 4 == 1 ? "SOL" : null, ObservedAtUtc = start.AddDays(i * 3).AddMinutes(7),
                SignedQuantity = i % 3 == 0 ? -2 : 2, EntryPrice = 100, Leverage = 3 + i % 7
            });
        db.ExecutionOmissions.AddRange(
            Omission(owner, gross.Id, "m1", "BTC", "BTC", start.AddDays(40), null, null),
            Omission(owner, gross.Id, "m2", "ETH", null, null, start.AddDays(90), start.AddDays(91)),
            Omission(owner, gross.Id, "m3", null, null, null, start.AddDays(150), null),
            Omission(owner, net.Id, "m4", "SOL", "xyz:SOL", null, null, start.AddDays(20)));
        db.ForcedEvents.AddRange(Enumerable.Range(0, ActivityService.MaxForcedEventRows + 5).Select(i => new ForcedEvent
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = i % 2 == 0 ? net.Id : manual.Id, SourceId = $"forced{i}", Kind = ForcedEvent.Liquidation,
            ContractId = "BTC", VenueContractId = "BTC", Side = "sell", Quantity = 1, OccurredAtUtc = start.AddHours(i * 5),
            Exclusion = "excluded", FirstSeenAtUtc = start, LastSeenAtUtc = start
        }));
        await db.SaveChangesAsync();
        return new(owner, portfolio, [gross, net, manual, disabled]);
    }

    private static ExecutionOmission Omission(Guid owner, Guid account, string source, string? contract, string? native, DateTimeOffset? at,
        DateTimeOffset? from, DateTimeOffset? to) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, Kind = ExecutionOmission.Fee, Reason = "fee-bearing or unverified fee",
        SourceId = source, ContractId = contract, VenueContractId = native, OccurredAtUtc = at, UncertainFromUtc = from, UncertainToUtc = to,
        FirstSeenAtUtc = End, LastSeenAtUtc = End
    };

    [PostgresFact]
    public async Task Pages_filters_counts_coverage_and_version_match_the_in_memory_aggregate_on_a_large_synthetic_journal()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var venues = GoldenJournal.Venues();
        var clock = new Clock(End.AddDays(2));
        await using var paths = new Paths(database, seed.Owner, clock, venues);
        var accounts = seed.Accounts.Select(a => a.Id).ToList();
        Assert.True(await SameEverywhereAsync(paths, accounts, [seed.Portfolio.Id]) > 50);
        Verified(await VerifyAsync(database, seed.Owner, venues));

        // New history on one account, a routine check on another and a changed position on a third; then read again.
        await using (var writer = database.Context(seed.Owner))
        {
            writer.Fills.Add(new ImportedFill
            {
                Id = Guid.NewGuid(), OwnerId = seed.Owner, AccountId = seed.Accounts[0].Id, ContractId = "BTC", VenueContractId = "BTC",
                SourceFillId = "late", Side = ExecutionFacts.Buy, Direction = "Open Long", PositionEffect = ExecutionFacts.Open, Price = 150,
                Quantity = 2, Fee = 0.1m, FeeToken = "USDC", OccurredAtUtc = End.AddMinutes(30), ExternalOrderId = "late", TransactionHash = ""
            });
            (await writer.Snapshots.SingleAsync(s => s.AccountId == seed.Accounts[1].Id)).ObservedAtUtc = End.AddHours(5);
            var manual = await writer.Snapshots.Include(s => s.Positions).SingleAsync(s => s.AccountId == seed.Accounts[2].Id);
            manual.PositionsComplete = true;
            await writer.SaveChangesAsync();
        }
        await SameEverywhereAsync(paths, accounts, [seed.Portfolio.Id]);
        Verified(await VerifyAsync(database, seed.Owner, venues));
    }

    [PostgresFact]
    public async Task The_golden_journal_reads_the_same_through_the_read_model()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var j = new GoldenJournal();
        await using (var db = database.Context(GoldenJournal.Owner))
        {
            db.Portfolios.Add(j.Swing);
            db.Accounts.AddRange(j.Accounts);
            db.Plays.AddRange(j.Plays);
            db.PlanRevisions.AddRange(j.Revisions);
            db.StatusChanges.AddRange(j.StatusChanges);
            db.Orders.AddRange(j.Orders);
            db.Fills.AddRange(j.Fills);
            db.OrderLinks.AddRange(j.Links);
            db.Snapshots.AddRange(j.Snapshots);
            await db.SaveChangesAsync();
        }
        var venues = GoldenJournal.Venues();
        await using var paths = new Paths(database, GoldenJournal.Owner, new GoldenClock(), venues);
        await SameEverywhereAsync(paths, j.Accounts.Select(a => a.Id).ToList(), [j.Swing.Id]);
        Verified(await VerifyAsync(database, GoldenJournal.Owner, venues));

        // The golden checks themselves, with Activity served by the read model.
        await using var db2 = database.Context(GoldenJournal.Owner);
        var unit = TestUnitOfWork.For(db2);
        var sizing = new SizingStore(db2, unit);
        var screens = GoldenJournalScreens.Create(new ActivityStore(db2), new WorkspaceStore(db2, unit), new ReviewStore(db2, sizing), sizing,
            new RiskStore(db2, unit), unit, paths.Model);
        await GoldenJournalChecks.ActivityAsync(j, screens);
        await GoldenJournalChecks.IdentitiesAsync(j, screens);
    }

    private static async Task<List<string>> StoredAsync(CoreDatabase database)
    {
        // Each row's physical version: a rewritten row gets a new xmin even with equal values.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT 'part ' || account_id || ' ' || build_id || ' ' || xmin FROM activity_projection_accounts
            UNION ALL SELECT 'operation ' || account_id || ' ' || operation_id || ' ' || xmin FROM activity_operations
            UNION ALL SELECT 'row ' || account_id || ' ' || operation_id || ' ' || xmin FROM activity_operation_rows ORDER BY 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<Dictionary<Guid, long>> BuildsAsync(CoreDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT account_id, build_id FROM activity_projection_accounts", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var builds = new Dictionary<Guid, long>();
        while (await reader.ReadAsync()) builds[reader.GetGuid(0)] = reader.GetInt64(1);
        return builds;
    }

    [PostgresFact]
    public async Task A_routine_monitor_check_rewrites_no_stored_row_and_a_fill_rebuilds_only_its_account()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database, fills: 120);
        var venues = GoldenJournal.Venues();
        await using var paths = new Paths(database, seed.Owner, new Clock(End.AddDays(2)), venues);
        var before = await SameAsync(paths, new(PageSize: "25"));
        var stored = await StoredAsync(database);
        Assert.Contains(stored, r => r.StartsWith("operation ", StringComparison.Ordinal));

        // A quiet check: newer observation times, the same positions and completeness, nothing passed.
        await using (var writer = database.Context(seed.Owner))
        {
            foreach (var snapshot in await writer.Snapshots.ToListAsync()) snapshot.ObservedAtUtc = snapshot.ObservedAtUtc.AddMinutes(10);
            await writer.SaveChangesAsync();
        }
        var after = await SameAsync(paths, new(PageSize: "25"));
        Assert.Equal(stored, await StoredAsync(database));
        Assert.Equal(JsonDocument.Parse(before).RootElement.GetProperty("version").GetString(),
            JsonDocument.Parse(after).RootElement.GetProperty("version").GetString());

        // A fill on one account rebuilds that account's part only, and the version moves.
        var builds = await BuildsAsync(database);
        await using (var writer = database.Context(seed.Owner))
        {
            (await writer.Fills.Where(f => f.AccountId == seed.Accounts[1].Id).OrderBy(f => f.OccurredAtUtc).FirstAsync()).Price += 1;
            await writer.SaveChangesAsync();
        }
        var changed = await SameAsync(paths, new(PageSize: "25"));
        var rebuilt = await BuildsAsync(database);
        Assert.NotEqual(builds[seed.Accounts[1].Id], rebuilt[seed.Accounts[1].Id]);
        Assert.Equal(builds.Where(b => b.Key != seed.Accounts[1].Id), rebuilt.Where(b => b.Key != seed.Accounts[1].Id));
        Assert.NotEqual(JsonDocument.Parse(after).RootElement.GetProperty("version").GetString(),
            JsonDocument.Parse(changed).RootElement.GetProperty("version").GetString());
    }

    [PostgresTheory]
    [InlineData("price")]
    [InlineData("result")]
    [InlineData("fee")]
    [InlineData("snapshot-coverage")]
    [InlineData("history-coverage")]
    [InlineData("funding-detail")]
    [InlineData("funding-attempt")]
    [InlineData("omission")]
    [InlineData("disable")]
    public async Task The_version_still_flags_changed_data(string change)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database, fills: 80);
        var venues = GoldenJournal.Venues();
        await using var paths = new Paths(database, seed.Owner, new Clock(End.AddDays(2)), venues);
        static string Version(string page) => JsonDocument.Parse(page).RootElement.GetProperty("version").GetString()!;
        var before = Version(await SameAsync(paths, new(PageSize: "10")));
        var gross = seed.Accounts[0].Id;
        await using (var writer = database.Context(seed.Owner))
        {
            var fill = await writer.Fills.Where(f => f.AccountId == gross && f.PositionEffect == ExecutionFacts.Close).OrderBy(f => f.OccurredAtUtc).FirstAsync();
            switch (change)
            {
                case "price": fill.Price += 1; break;
                case "result": fill.ClosedPnlUsd += 2; break;
                case "fee": fill.Fee += 0.02m; break;
                case "snapshot-coverage": (await writer.Snapshots.SingleAsync(s => s.AccountId == gross)).ObservedAtUtc = fill.OccurredAtUtc; break;
                case "history-coverage": (await writer.Accounts.SingleAsync(a => a.Id == gross)).RecordSync(End, "Updated retained history coverage."); break;
                case "funding-detail": (await writer.FundingPayments.SingleAsync(p => p.SourceId == "p3")).SourceId = "corrected"; break;
                case "funding-attempt": (await writer.FundingStates.SingleAsync(s => s.AccountId == gross)).LastAttemptToUtc = End.AddHours(3); break;
                case "omission": writer.ExecutionOmissions.Add(Omission(seed.Owner, gross, "new", "BTC", "BTC", fill.OccurredAtUtc.AddSeconds(-1), null, null)); break;
                case "disable":
                    var account = await writer.Accounts.SingleAsync(a => a.Id == gross);
                    account.UpdateSettings(account.Name, account.PortfolioId, false);
                    break;
            }
            await writer.SaveChangesAsync();
        }
        Assert.NotEqual(before, Version(await SameAsync(paths, new(PageSize: "10"))));
    }

    /// <summary>Runs a change once, right after the first transaction this context commits.</summary>
    private sealed class AfterFirstCommit(Func<Task> change) : DbTransactionInterceptor
    {
        public int Fired { get; private set; }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Fired++ == 0) await change();
        }
    }

    [PostgresTheory]
    [InlineData("funding-attempt")]
    [InlineData("disable")]
    public async Task Verify_reports_no_difference_when_an_account_or_funding_attempt_changes_while_it_runs(string change)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database, fills: 80);
        var venues = GoldenJournal.Venues();
        await using (var paths = new Paths(database, seed.Owner, new Clock(End.AddDays(2)), venues))
            await SameAsync(paths, new(PageSize: "10"));
        Verified(await VerifyAsync(database, seed.Owner, venues));
        var gross = seed.Accounts[0].Id;
        // The change keeps every build current (a funding attempt is not history; a disabled account simply leaves the scope)
        // yet moves the owner-wide version, so a comparison taken from a later snapshot would differ.
        var interceptor = new AfterFirstCommit(async () =>
        {
            await using var writer = database.Context(seed.Owner);
            if (change == "funding-attempt")
                (await writer.FundingStates.SingleAsync(s => s.AccountId == gross)).LastAttemptToUtc = End.AddHours(3);
            else
            {
                var account = await writer.Accounts.SingleAsync(a => a.Id == gross);
                account.UpdateSettings(account.Name, account.PortfolioId, false);
            }
            await writer.SaveChangesAsync();
        });
        await using (var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
                         .AddInterceptors(interceptor).Options, new CoreOwner(seed.Owner)))
            Verified(await new ActivityReadModel(db, new ActivityStore(db), new ActivityReadModelState(), venues).VerifyAsync(default));
        Assert.True(interceptor.Fired > 0);
        // The change did land: the next verify reads it.
        var after = await VerifyAsync(database, seed.Owner, venues);
        Assert.Empty(after.Differences);
        if (change == "disable") Assert.Equal(seed.Accounts.Length - 2, after.Accounts);
    }

    [PostgresFact]
    public async Task Rebuild_and_verify_commands_build_every_owner_and_report_a_difference()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database, fills: 60);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString
        }).Build();
        async Task<(int Code, string Output)> Run(string command, params string[] args)
        {
            using var output = new StringWriter();
            return (await ActivityReadModelCommand.RunAsync(command, args, configuration, output), output.ToString());
        }
        var dry = await Run(ActivityReadModelCommand.Rebuild);
        Assert.Equal(0, dry.Code);
        Assert.Contains("Dry run: 3 of 3 accounts", dry.Output);
        Assert.Empty(await BuildsAsync(database));
        Assert.Equal(1, (await Run(ActivityReadModelCommand.Verify)).Code);
        var apply = await Run(ActivityReadModelCommand.Rebuild, "--apply");
        Assert.Equal((0, true), (apply.Code, apply.Output.Contains("Rebuilt 3 of 3 accounts", StringComparison.Ordinal)));
        Assert.Equal(3, (await BuildsAsync(database)).Count);
        Assert.Contains("Dry run: 0 of 3 accounts", (await Run(ActivityReadModelCommand.Rebuild)).Output);
        var verified = await Run(ActivityReadModelCommand.Verify);
        Assert.True(verified.Code == 0, verified.Output);
        Assert.Contains("no difference", verified.Output);

        // A stored row that no longer matches the aggregate is reported, with its operation.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var tamper = new NpgsqlCommand(
                "UPDATE activity_operation_rows SET summary = replace(summary, '\"Status\":\"', '\"Status\":\"x') WHERE ctid = (SELECT min(ctid) FROM activity_operation_rows)",
                connection);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }
        var different = await Run(ActivityReadModelCommand.Verify);
        Assert.Equal(1, different.Code);
        Assert.Contains("differs.", different.Output);
        Assert.Equal(2, (await Run(ActivityReadModelCommand.Rebuild, "--force")).Code);
        Assert.Equal(4, seed.Accounts.Length);
    }

    [PostgresFact]
    public async Task Each_account_revision_moves_only_for_its_own_history_and_once_per_transaction()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var seed = await SeedAsync(database, fills: 20);
        async Task<Dictionary<Guid, long>> Revisions()
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT account_id, revision FROM activity_account_revisions", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new Dictionary<Guid, long>();
            while (await reader.ReadAsync()) result[reader.GetGuid(0)] = reader.GetInt64(1);
            return result;
        }
        var before = await Revisions();
        Assert.Equal(seed.Accounts.Length, before.Count);
        await using (var writer = database.Context(seed.Owner))
        {
            foreach (var fill in await writer.Fills.Where(f => f.AccountId == seed.Accounts[0].Id).ToListAsync()) fill.Fee += 1;
            (await writer.Snapshots.SingleAsync(s => s.AccountId == seed.Accounts[1].Id)).ObservedAtUtc = End.AddDays(1);
            await writer.SaveChangesAsync();
        }
        var after = await Revisions();
        Assert.Equal(before[seed.Accounts[0].Id] + 1, after[seed.Accounts[0].Id]);
        Assert.Equal(before.Where(r => r.Key != seed.Accounts[0].Id), after.Where(r => r.Key != seed.Accounts[0].Id));
    }
}
