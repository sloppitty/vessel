using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Risk;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 1 against PostgreSQL: the owner's Planned and Open real Plays summed by direction, never netted; Paused
/// and Unresolved listed as not included; unknown Plays counted, not summed; the soft limit is the owner's and blank by default.
/// </summary>
public sealed class OpenRiskApiTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

    private static PlayPlanDocument Plan(string direction, string size, string entry, string stop) => new(direction, "quantity", size, "1", null,
        [new PlanEntry("e1", "Entry", "#b9c9e4", "100", entry, stop.Length == 0 ? [new PlanExit("s1", "price", "", "100")] : [new PlanExit("s1", "price", stop, "100")], [])],
        new PlanNotes("", "", "", ""));

    private static Play Add(Vessel.Persistence.VesselDbContext db, Account account, string contract, PlayPlanDocument plan, PlayStatus status, int minute)
    {
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), T0.AddMinutes(minute));
        db.Plays.Add(play);
        if (status == PlayStatus.Draft) return play;
        var (change, revision) = play.MarkPlanned(T0.AddMinutes(minute));
        db.Add(change); db.Add(revision);
        if (status == PlayStatus.Paused) db.Add(play.Pause(T0.AddMinutes(minute + 1)));
        if (status is PlayStatus.Open or PlayStatus.Unresolved) db.Add(play.MarkOpen("Entry fill.", T0.AddMinutes(minute + 1)));
        if (status == PlayStatus.Unresolved) db.Add(play.DeclareUnresolved(UnresolvedReason.Other, "Stuck.", "{}", T0.AddMinutes(minute + 2)));
        return play;
    }

    private static async Task<(CoreDatabase Database, Guid Owner, Account Account, Account Other, Guid Open)> Seed()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Trading");
        var other = new Account(Guid.NewGuid(), owner, "hyperliquid", "Other");
        var disabled = new Account(Guid.NewGuid(), owner, "hyperliquid", "Disabled");
        disabled.UpdateSettings("Disabled", null, false);
        await using var db = database.Context(owner);
        db.Accounts.AddRange(account, other, disabled);
        db.Snapshots.Add(new AccountSnapshot { OwnerId = owner, AccountId = account.Id, ObservedAtUtc = T0, ValueScope = "fixture", AccountValueUsd = 1000 });
        await db.SaveChangesAsync();

        // G-06: three 1.25% longs on BTC, ETH and SOL are one stressed book.
        foreach (var (contract, minute) in new[] { ("BTC", 0), ("ETH", 1), ("SOL", 2) })
            Add(db, account, contract, Plan("long", "2.5", "100", "95"), PlayStatus.Planned, minute);
        // A short of 10 is shown beside the longs, never netted against them.
        Add(db, other, "DOGE", Plan("short", "2", "100", "105"), PlayStatus.Planned, 3);
        // An Open long filled at 100 with its stop managed to 105: no risk, and no linked resting stop.
        var open = Add(db, account, "BTC", Plan("long", "1", "100", "105"), PlayStatus.Open, 4);
        var order = new ImportedOrder
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ExternalOrderId = "entry", ContractId = "BTC", Side = "buy", OrderType = "Limit",
            LimitPrice = 100, OriginalSize = 1, RemainingSize = 0, PlacedAtUtc = T0.AddMinutes(4), Status = "filled", VenueStatus = "filled",
            StatusAtUtc = T0.AddMinutes(5), ObservedAtUtc = T0.AddMinutes(5),
        };
        db.Orders.Add(order);
        db.Fills.Add(new ImportedFill
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC", SourceFillId = "fill", ExternalOrderId = "entry", Side = "buy",
            Direction = "", PositionEffect = "open", Price = 100, Quantity = 1, Fee = 0, FeeToken = "USDC", ClosedPnlUsd = 0, OccurredAtUtc = T0.AddMinutes(5),
            TransactionHash = "",
        });
        db.OrderLinks.Add(new PlayOrderLink(open, order, OrderLinkRole.Entry, "e1", null, OrderLinkState.Linked, OrderLinkSource.Automatic, T0.AddMinutes(5)));
        // D-04: a stop without a price is unknown, never a partial sum.
        Add(db, account, "ARB", Plan("long", "1", "100", ""), PlayStatus.Planned, 6);
        Add(db, account, "OP", Plan("long", "1", "100", "95"), PlayStatus.Paused, 7);
        Add(db, account, "SUI", Plan("long", "1", "100", "95"), PlayStatus.Unresolved, 8);
        Add(db, account, "APT", Plan("long", "1", "100", "95"), PlayStatus.Draft, 9);
        Add(db, disabled, "BTC", Plan("long", "100", "100", "50"), PlayStatus.Planned, 10);
        await db.SaveChangesAsync();
        return (database, owner, account, other, open.Id);
    }

    [PostgresFact]
    public async Task Open_risk_sums_each_direction_and_counts_what_it_cannot_measure()
    {
        var (database, owner, account, other, open) = await Seed();
        await using var _ = database;
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();

        var all = (await client.GetFromJsonAsync<OpenRiskDto>("/api/risk/open"))!;
        Assert.Equal(("37.5", 4, 1), (all.Long.KnownUsd, all.Long.KnownCount, all.Long.UnknownCount));
        Assert.Equal(("10", 1, 0), (all.Short.KnownUsd, all.Short.KnownCount, all.Short.UnknownCount));
        Assert.Equal([("paused", 1), ("unresolved", 1)], all.NotIncluded.Select(x => (x.Reason, x.Count)));
        Assert.Equal(1, all.UnprotectedCount);
        Assert.Null(all.LimitPercent);
        var managed = all.Plays.Single(p => p.PlayId == open);
        Assert.Equal(("0", null, true, "open"), (managed.RiskUsd, managed.Reason, managed.Unprotected, managed.Status));
        Assert.Equal(OpenRiskCalculator.UnpricedStop, all.Plays.Single(p => p.RiskUsd is null).Reason);
        // The disabled account's Play and the Draft are not in the population at all.
        Assert.Equal(6, all.Plays.Count);

        // An account scope reads only its own Plays and balance.
        var scoped = (await client.GetFromJsonAsync<OpenRiskDto>($"/api/risk/open?scope=account:{other.Id}"))!;
        Assert.Equal(("0", "10"), (scoped.Long.KnownUsd, scoped.Short.KnownUsd));
        Assert.Empty(scoped.NotIncluded);
        var mine = (await client.GetFromJsonAsync<OpenRiskDto>($"/api/risk/open?scope=account:{account.Id}"))!;
        Assert.Equal(("37.5", "0", 1), (mine.Long.KnownUsd, mine.Short.KnownUsd, mine.Balance.ContributingAccountCount));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/risk/open?scope=account:{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/risk/open?scope=everything")).StatusCode);
        // Venue facts only: a paper request is refused before any read.
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/risk/open?tradingMode=paper")).StatusCode);
    }

    [PostgresFact]
    public async Task The_preview_measures_the_edited_plan_against_the_saved_executions()
    {
        var (database, owner, account, _, open) = await Seed();
        await using var _ = database;
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        RiskPlanDto Edited(string stop) => new("long", "quantity", "1", "1", [new("e1", "100", "100", [new("s1", "price", stop, "100")])]);

        // Moving the managed stop back below the fill shows its risk at once, from the saved fill at 100.
        var saved = await Preview(client, new(open, account.Id, Edited("90")));
        Assert.Equal(("10", (string?)null, true), (saved.RiskUsd, saved.Reason, saved.Unprotected));
        // A new Draft has no executions: its planned quantity at its planned price.
        var draft = await Preview(client, new(null, account.Id, Edited("97.5")));
        Assert.Equal(("2.5", false), (draft.RiskUsd, draft.Unprotected));
        // A blank stop in a Draft is unknown, never an error.
        Assert.Equal(OpenRiskCalculator.UnpricedStop, (await Preview(client, new(null, account.Id, Edited("")))).Reason);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/risk/open/preview",
            new OpenRiskPreviewRequest(null, Guid.NewGuid(), Edited("90")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/risk/open/preview",
            new OpenRiskPreviewRequest(null, account.Id, Edited("90") with { Entries = [] }))).StatusCode);
    }

    private static async Task<OpenRiskPreviewDto> Preview(HttpClient client, OpenRiskPreviewRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/risk/open/preview", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OpenRiskPreviewDto>())!;
    }

    [PostgresFact]
    public async Task The_soft_limit_is_blank_until_the_owner_sets_it_and_can_be_cleared()
    {
        var (database, owner, _, _, _) = await Seed();
        await using var _ = database;
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();

        Assert.Null((await client.GetFromJsonAsync<RiskSettingsDto>("/api/risk/settings"))!.OpenRiskLimitPercent);
        var set = await client.PutAsJsonAsync("/api/risk/settings", new UpdateRiskSettingsRequest("3.5"));
        set.EnsureSuccessStatusCode();
        Assert.Equal("3.5", (await set.Content.ReadFromJsonAsync<RiskSettingsDto>())!.OpenRiskLimitPercent);
        Assert.Equal("3.5", (await client.GetFromJsonAsync<OpenRiskDto>("/api/risk/open"))!.LimitPercent);
        foreach (var invalid in new[] { "0", "100.5", "-1", "1e1", "1.23456", "abc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/risk/settings", new UpdateRiskSettingsRequest(invalid))).StatusCode);
        (await client.PutAsJsonAsync("/api/risk/settings", new UpdateRiskSettingsRequest(null))).EnsureSuccessStatusCode();
        Assert.Null((await client.GetFromJsonAsync<RiskSettingsDto>("/api/risk/settings"))!.OpenRiskLimitPercent);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/risk/settings?tradingMode=paper", new UpdateRiskSettingsRequest("2"))).StatusCode);
    }
}
