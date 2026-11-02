using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Application.Overview;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class OverviewSummaryPostgresTests
{
    [PostgresFact]
    public async Task Portfolio_account_set_limits_loaded_fills_snapshots_and_links_and_empty_set_issues_no_queries()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var foreignOwner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Selected");
        var otherPortfolio = new Portfolio(Guid.NewGuid(), owner, "Other");
        var selected = new Account(Guid.NewGuid(), owner, "manual", "Selected"); selected.Configure(portfolio.Id, null, null);
        var disabled = new Account(Guid.NewGuid(), owner, "manual", "Disabled"); disabled.UpdateSettings(disabled.Name, portfolio.Id, false);
        var other = new Account(Guid.NewGuid(), owner, "manual", "Other"); other.Configure(otherPortfolio.Id, null, null);
        var unassigned = new Account(Guid.NewGuid(), owner, "manual", "Unassigned");
        var foreign = new Account(Guid.NewGuid(), foreignOwner, "manual", "Foreign");
        var now = DateTimeOffset.UtcNow;
        async Task Seed(Guid actor, Account[] accounts, params Portfolio[] portfolios)
        {
            await using var db = database.Context(actor);
            db.Portfolios.AddRange(portfolios); db.Accounts.AddRange(accounts);
            foreach (var account in accounts)
            {
                db.Snapshots.Add(new AccountSnapshot { OwnerId = actor, AccountId = account.Id, ObservedAtUtc = now, ValueScope = "fixture" });
                db.FundingPayments.Add(new FundingPayment { Id = Guid.NewGuid(), OwnerId = actor, AccountId = account.Id,
                    SourceId = "funding", ContractId = "BTC", Amount = 1, Asset = "USDC", OccurredAtUtc = now });
                db.FundingStates.Add(new AccountFundingState { OwnerId = actor, AccountId = account.Id,
                    LastAttemptFromUtc = now.AddDays(-30), LastAttemptToUtc = now, Notice = "Synthetic funding coverage." });
                var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual, "{}", now);
                db.Plays.Add(play);
                var order = TestOrders.For(play, "order");
                db.Orders.Add(order);
                db.OrderLinks.Add(new PlayOrderLink(play, order, OrderLinkRole.Entry, "entry", null, OrderLinkState.Linked, OrderLinkSource.Owner, now));
                // More than the overview fill display limit, including for unrelated accounts.
                for (var i = 0; i < 120; i++)
                    db.Fills.Add(new ImportedFill { Id = Guid.NewGuid(), OwnerId = actor, AccountId = account.Id, ContractId = "BTC",
                        SourceFillId = i.ToString(), Side = "buy", PositionEffect = "open", Direction = "Fixture", Price = 100,
                        Quantity = 1, FeeToken = "USDC", OccurredAtUtc = now.AddMinutes(-i), ExternalOrderId = "order", TransactionHash = "" });
            }
            await db.SaveChangesAsync();
        }
        await Seed(owner, [selected, disabled, other, unassigned], portfolio, otherPortfolio);
        await Seed(foreignOwner, [foreign]);
        var commands = new List<string>();
        await using var context = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString)
            .LogTo(commands.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information).Options, new CoreOwner(owner));
        var ids = (await context.Accounts.Where(a => a.IsEnabled && a.PortfolioId == portfolio.Id).Select(a => a.Id).ToListAsync()).ToHashSet();
        commands.Clear();
        var store = new ActivityStore(context);
        var facts = await store.ReadAsync(null, default, ids);
        Assert.Equal(selected.Id, Assert.Single(facts.Accounts).Id);
        Assert.Equal(120, facts.Fills.Count); Assert.All(facts.Fills, f => Assert.Equal(selected.Id, f.AccountId));
        Assert.Equal(selected.Id, Assert.Single(facts.Snapshots).AccountId);
        Assert.Equal(120, facts.FillPlayIds.Count);
        Assert.All(facts.FillPlayIds.Keys, id => Assert.Contains(facts.Fills, f => f.Id == id));
        Assert.Equal(selected.Id, Assert.Single(facts.FundingPayments).AccountId);
        Assert.Equal(selected.Id, Assert.Single(facts.FundingStates).AccountId);
        // Scoped accounts, fills, snapshots, funding, funding state, leverage observations, omission markers, forced
        // events (Phase 2, item 7b), the facts revision in the same snapshot (item 9b) and linked orders.
        Assert.Equal(10, commands.Count);
        // Even caller-supplied disabled/foreign IDs cannot bypass the SQL account restrictions.
        var guarded = await store.ReadAsync(null, default, new HashSet<Guid> { selected.Id, disabled.Id, foreign.Id });
        Assert.Equal(selected.Id, Assert.Single(guarded.Accounts).Id);
        Assert.Equal(120, guarded.Fills.Count); Assert.Single(guarded.Snapshots); Assert.Equal(120, guarded.FillPlayIds.Count);
        commands.Clear();
        var empty = await store.ReadAsync(null, default, new HashSet<Guid>());
        Assert.Empty(empty.Accounts); Assert.Empty(empty.Fills); Assert.Empty(empty.Snapshots); Assert.Empty(empty.FillPlayIds);
        Assert.Empty(empty.FundingPayments); Assert.Empty(empty.FundingStates); Assert.Empty(empty.LeverageObservations);
        Assert.Empty(commands);
    }

    [PostgresFact]
    public async Task Endpoint_uses_owner_enabled_facts_exact_amounts_and_current_membership()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var foreignOwner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Swing", "wallet");
        var account = new Account(Guid.NewGuid(), owner, "manual", "Owned"); account.Configure(portfolio.Id, null, 10.000000000000000000000000001m);
        var unassigned = new Account(Guid.NewGuid(), owner, "manual", "Unassigned"); unassigned.Configure(null, null, 20);
        var disabled = new Account(Guid.NewGuid(), owner, "manual", "Disabled"); disabled.Configure(null, null, 999); disabled.UpdateSettings(disabled.Name, null, false);
        var foreign = new Account(Guid.NewGuid(), foreignOwner, "manual", "Foreign"); foreign.Configure(null, null, 1000);
        var foreignPortfolio = new Portfolio(Guid.NewGuid(), foreignOwner, "Foreign");
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        await using (var db = database.Context(owner))
        {
            db.Portfolios.Add(portfolio); db.Accounts.AddRange(account, unassigned, disabled);
            foreach (var a in new[] { account, disabled })
                foreach (var close in new[] { false, true })
                    db.Fills.Add(new ImportedFill { Id = Guid.NewGuid(), OwnerId = owner, AccountId = a.Id, ContractId = "BTC",
                        SourceFillId = close ? "close" : "open", Side = close ? "sell" : "buy", PositionEffect = close ? "close" : "open",
                        Direction = "Fixture", Price = 100, Quantity = 1, Fee = 0.0000000000000000000000000001m, FeeToken = "USDC",
                        ClosedPnlUsd = close ? 10 : 0, OccurredAtUtc = close ? now : now.AddHours(-1), ExternalOrderId = close ? "close" : "open", TransactionHash = "" });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(foreignOwner)) { db.Portfolios.Add(foreignPortfolio); db.Accounts.Add(foreign); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString); using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/overview/summary?groupBy=portfolio&period=30&scope=all");
        response.EnsureSuccessStatusCode(); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var result = (await response.Content.ReadFromJsonAsync<OverviewSummaryDto>())!;
        Assert.Equal(2, result.Totals.AccountCount); Assert.Equal("30.000000000000000000000000001", result.Totals.Balance.AmountUsd);
        Assert.Equal("9.9999999999999999999999999998", result.Totals.RealizedResult.AmountUsd);
        Assert.Equal(2, result.Totals.ImportedFillCount); Assert.Equal("unassigned", result.Rows[0].Id);
        foreach (var scope in new[] { $"account:{foreign.Id}", $"portfolio:{foreignPortfolio.Id}", $"account:{Guid.NewGuid()}" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/overview/summary?scope={scope}")).StatusCode);
        await using (var db = database.Context(owner))
        {
            var a = await db.Accounts.FindAsync(account.Id); a!.UpdateSettings(a.Name, null, false); await db.SaveChangesAsync();
        }
        var after = (await client.GetFromJsonAsync<OverviewSummaryDto>("/api/overview/summary"))!;
        Assert.Equal("20", after.Totals.Balance.AmountUsd); Assert.Single(after.Rows); Assert.Null(after.Totals.RealizedResult.AmountUsd);
    }
}
