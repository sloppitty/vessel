using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ActivityOperationsPostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static ImportedFill Fill(Account account, int n) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", Side = n % 2 == 0 ? "buy" : "sell",
        PositionEffect = n % 2 == 0 ? "open" : "close", Direction = "fixture", Quantity = 1, Price = 100,
        FeeToken = "USDC", Fee = 0.01m, ClosedPnlUsd = n % 2 == 0 ? 0 : 1, PnlBasis = "gross", FeeBasis = "reported",
        SourceFillId = n.ToString(), ExternalOrderId = n.ToString(), TransactionHash = "", OccurredAtUtc = At.AddMinutes(n)
    };

    [PostgresFact]
    public async Task Legacy_fill_and_snapshot_ids_resolve_after_database_readback_without_rewriting_facts()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "risex", "Legacy fixture");
        var legacy = Fill(account, 0); legacy.ContractId = "DOGE";
        var native = Fill(account, 2); native.ContractId = "DOGE"; native.VenueContractId = "14";
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account); db.Fills.AddRange(legacy, native);
            db.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = At.AddDays(1),
                ValueScope = "fixture", Positions = [new() { OwnerId = owner, AccountId = account.Id, ContractId = "DOGE",
                    SignedQuantity = 2, EntryPrice = 100 }] });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var result = await ActivityTestSupport.Activity(db).PageAsync(account.Id);
            var op = Assert.Single(result.Operations);
            Assert.Equal("long", op.Direction); Assert.Equal("open", op.Status);
            Assert.Equal("2", op.OpenQuantity); Assert.Equal("2", op.TotalEnteredQuantity);
            Assert.Equal(new[] { legacy.Id, native.Id }, (await ActivityTestSupport.Activity(db).FillsAsync(account.Id, op.Id, default)).Fills.Select(f => f.Fill.Id));
            Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
            Assert.Null((await db.Fills.SingleAsync(f => f.Id == legacy.Id)).VenueContractId);
            Assert.Equal("14", (await db.Fills.SingleAsync(f => f.Id == native.Id)).VenueContractId);
            Assert.Null((await db.Positions.SingleAsync()).VenueContractId);
        }
    }

    [PostgresFact]
    public async Task Retained_native_contract_ids_partition_activity_after_database_readback()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        var retiredOpen = Fill(account, 0); retiredOpen.VenueContractId = "13";
        var retiredClose = Fill(account, 1); retiredClose.VenueContractId = "13"; retiredClose.ClosedPnlUsd = 10;
        var activeClose = Fill(account, 3); activeClose.VenueContractId = "14"; activeClose.ClosedPnlUsd = 20;
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account); db.Fills.AddRange(retiredOpen, retiredClose, activeClose);
            db.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = At.AddDays(1),
                ValueScope = "fixture", Positions = [new() { OwnerId = owner, AccountId = account.Id, ContractId = "BTC",
                    VenueContractId = "14", SignedQuantity = 1, EntryPrice = 100 }] });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var result = await ActivityTestSupport.Activity(db).PageAsync(account.Id);
            Assert.Equal(2, result.TotalCount);
            Assert.Equal("10", result.Operations.Single(o => o.Status == "closed").ReportedClosedPnlUsd);
            var active = result.Operations.Single(o => o.Status == "incomplete");
            Assert.Equal("20", active.ReportedClosedPnlUsd); Assert.Equal("1", active.OpenQuantity);
            Assert.Empty(result.Coverage);
            Assert.Equal("14", (await db.Positions.SingleAsync()).VenueContractId);
            Assert.Equal(3, await db.Fills.CountAsync());
        }
    }

    [PostgresFact]
    public async Task Activity_reads_all_retained_facts_owner_scoped_enabled_only_and_actual_play_links()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "manual", "Enabled");
        var disabled = new Account(Guid.NewGuid(), owner, "manual", "Disabled"); disabled.UpdateSettings("Disabled", null, false);
        var otherOwner = Guid.NewGuid(); var foreign = new Account(Guid.NewGuid(), otherOwner, "manual", "Foreign");
        Guid playId;
        await using (var db = database.Context(owner))
        {
            db.Accounts.AddRange(account, disabled);
            db.Fills.AddRange(Enumerable.Range(0, 120).Select(n => Fill(account, n)));
            db.Fills.Add(Fill(disabled, 0));
            var play = new Play(Guid.NewGuid(), account, new Vessel.Domain.Markets.PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual, "{}", At);
            play.Annotate("BTC breakout", "{}", "", At);
            playId = play.Id;
            db.Plays.Add(play);
            var (entry, exit) = (TestOrders.For(play, "0"), TestOrders.For(play, "1", "sell"));
            db.Orders.AddRange(entry, exit);
            db.OrderLinks.AddRange(new PlayOrderLink(play, entry, OrderLinkRole.Entry, "e1", null, OrderLinkState.Linked, OrderLinkSource.Owner, At),
                new PlayOrderLink(play, exit, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, At));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(otherOwner)) { db.Accounts.Add(foreign); db.Fills.Add(Fill(foreign, 0)); await db.SaveChangesAsync(); }
        await using (var db = database.Context(owner))
        {
            var facts = await new ActivityStore(db).ReadAsync(null, default);
            Assert.Single(facts.Accounts); Assert.Equal(120, facts.Fills.Count); Assert.Equal(2, facts.FillPlayIds.Count);
            Assert.All(facts.FillPlayIds.Values, id => Assert.Equal(playId, id));
            Assert.Equal("BTC breakout", (await new ActivityStore(db).PlayTitlesAsync([playId, Guid.NewGuid()], default)).Single(t => t.Key == playId).Value);
        }
        // Another owner's context never reads this Play's title.
        await using (var db = database.Context(otherOwner)) Assert.Empty(await new ActivityStore(db).PlayTitlesAsync([playId], default));
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString); using var client = factory.AuthorizedClient();
        var document = (await client.GetFromJsonAsync<ActivityPageDto>("/api/activity?pageSize=100"))!;
        Assert.Equal(60, document.TotalCount); Assert.Equal(120, document.RetainedFillCount);
        var titled = document.Operations.Single(o => o.PlayAssociation == "linked");
        Assert.Equal((playId, "BTC breakout"), (titled.PlayId, titled.PlayTitle));
        Assert.All(document.Operations.Where(o => o.PlayAssociation != "linked"), o => Assert.Null(o.PlayTitle));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity?accountId={foreign.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ActivityPageDto>($"/api/activity?accountId={disabled.Id}"))!.Operations);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity?scope=account:{foreign.Id}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<ActivityPageDto>($"/api/activity?scope=account:{disabled.Id}"))!.ScopeCount);

        // Expansion is scoped exactly like the page: own enabled accounts only, and the operation must still exist.
        var linked = document.Operations.Single(o => o.PlayAssociation == "linked");
        var rows = (await client.GetFromJsonAsync<OperationFillsDto>($"/api/activity/fills?accountId={account.Id}&operationId={linked.Id}"))!;
        Assert.Equal((linked.Id, 2, 0), (rows.OperationId, rows.Fills.Count, rows.OmittedFillCount));
        Assert.All(rows.Fills, f => Assert.Equal(playId, f.Fill.PlayId));
        await using var foreignFactory = new CoreApiFactory(otherOwner, connection: database.ConnectionString);
        using var other = foreignFactory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/activity/fills?accountId={account.Id}&operationId={linked.Id}")).StatusCode);
        var foreignOperation = (await other.GetFromJsonAsync<ActivityPageDto>("/api/activity"))!.Operations.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity/fills?accountId={foreign.Id}&operationId={foreignOperation.Id}")).StatusCode);
        await using var check = database.Context(owner);
        var disabledOperation = $"{(await check.Fills.SingleAsync(f => f.AccountId == disabled.Id)).Id}:long";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity/fills?accountId={disabled.Id}&operationId={disabledOperation}")).StatusCode);
    }

    [PostgresFact]
    public async Task Retained_snapshot_anchors_missing_entries_without_mutation()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Fixture");
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account); db.Fills.Add(Fill(account, 0));
            db.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = At.AddDays(1), ValueScope = "fixture",
                Positions = [new() { OwnerId = owner, AccountId = account.Id, ContractId = "BTC", SignedQuantity = 2, EntryPrice = 100 }] });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var result = await ActivityTestSupport.Activity(db).PageAsync(account.Id);
            var op = Assert.Single(result.Operations); Assert.Equal("2", op.OpenQuantity); Assert.Equal("long", op.Direction); Assert.Equal("incomplete", op.Status);
            Assert.Null(op.AverageEntryPrice); Assert.Contains(op.Coverage, c => c.Contains("entries") && c.Contains("missing"));
            Assert.Equal(2, (await db.Positions.SingleAsync()).SignedQuantity); Assert.Single(await db.Fills.ToListAsync());
        }
    }
}
