using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Application.Events;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

/// <summary>
/// Golden wire contracts produced by the real producers: mutation capture and the event store, from a Play's
/// planning, a suggested and then confirmed entry link, the entry fill that opens it and the exit that closes it.
/// IDs and clock times are normalized; everything else, including every explicit null, is compared exactly.
/// Types without a producer in this step (discrepancies, monitor, credentials) gain theirs in later steps.
/// </summary>
public sealed partial class VesselEventGoldenTests
{
    private const string Envelope = """
        {"id":"<id>","type":"{type}","version":1,"ownerId":"<owner>","sequence":"{sequence}","runId":"<id>",
         "dedupKey":"{key}","severity":"{severity}","occurredAt":"<time>","detectedAt":"<time>",
         "timeliness":"timely","relevance":"{relevance}","expiresAt":null,
         "subject":{"accountId":"<account>","venueId":"hyperliquid","contractId":"BTC","playId":"<play>",
                    "entryId":{entry},"levelId":null,"discrepancyId":null},
         "source":{source},"data":{data}}
        """;

    public static TheoryData<int, string, string, string, string, string, string, string> Golden => new()
    {
        { 1, "play.status-changed", "status-change:<id>", "info", "null", """{"trigger":"owner","orderIds":null,"fillIds":null}""",
            """{"previous":"draft","current":"planned","source":"owner","reason":null}""", "current" },
        { 2, "link.needs-confirmation", "link:<id>:suggested", "warning", "\"entry-1\"", """{"trigger":"monitor","orderIds":["entry"],"fillIds":null}""",
            """{"linkId":"<id>","previous":null,"current":"suggested","role":"entry","source":"automatic"}""", "current" },
        { 3, "execution.link-changed", "link-change:<id>", "info", "\"entry-1\"", """{"trigger":"owner","orderIds":["entry"],"fillIds":null}""",
            """{"linkId":"<id>","previous":"suggested","current":"linked","role":"entry","source":"owner"}""", "current" },
        { 4, "execution.filled", "order:<account>:<entry-order>:filled:0.5", "info", "\"entry-1\"", """{"trigger":"monitor","orderIds":["entry"],"fillIds":["e1"]}""",
            """{"role":"entry","side":"buy","filledQuantity":"0.5","cumulativeQuantity":"0.5","remainingQuantity":"0","averagePrice":"100","sourceFillCount":1,"sourceFillIdsComplete":true,"complete":true}""", "current" },
        { 5, "play.opened", "play:<play>:opened", "info", "null", """{"trigger":"monitor","orderIds":null,"fillIds":null}""",
            """{"previous":"planned","current":"open","source":"venue","reason":null}""", "current" },
        { 6, "execution.link-changed", "link-change:<id>", "info", "null", """{"trigger":"monitor","orderIds":["exit"],"fillIds":null}""",
            """{"linkId":"<id>","previous":null,"current":"linked","role":"exit","source":"automatic"}""", "current" },
        { 7, "execution.filled", "order:<account>:<exit-order>:filled:0.5", "info", "null", """{"trigger":"monitor","orderIds":["exit"],"fillIds":["x1"]}""",
            """{"role":"exit","side":"sell","filledQuantity":"0.5","cumulativeQuantity":"0.5","remainingQuantity":"0","averagePrice":"101","sourceFillCount":1,"sourceFillIdsComplete":true,"complete":true}""", "superseded" },
        { 8, "play.closed", "play:<play>:closed", "info", "null", """{"trigger":"monitor","orderIds":null,"fillIds":null}""",
            """{"enteredQuantity":"0.5","exitedQuantity":"0.5","closedPnlUsd":"0.5","closedPnlBasis":"gross","fees":[{"token":"USDC","amount":"0.01"}]}""", "current" }
    };

    [VesselEventPostgresTheory]
    [MemberData(nameof(Golden))]
    public async Task Real_producers_match_the_golden_wire_contract(int sequence, string type, string key, string severity,
        string entry, string source, string data, string relevance)
    {
        var produced = await Produced();
        var expected = Envelope.Replace("{type}", type).Replace("{sequence}", sequence.ToString()).Replace("{key}", key)
            .Replace("{severity}", severity).Replace("{entry}", entry).Replace("{source}", source).Replace("{data}", data).Replace("{relevance}", relevance);
        var actual = produced.Single(e => e["sequence"]!.GetValue<string>() == sequence.ToString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
    }

    // B10: one transaction's events are sequenced in causal order, and every type has a golden contract here.
    [VesselEventPostgresFact]
    public async Task Producers_sequence_facts_before_the_status_change_they_cause()
    {
        var produced = await Produced();
        Assert.Equal(Golden.Select(row => (string)row[1]), produced.Select(e => e["type"]!.GetValue<string>()));
    }

    private static List<JsonNode> cached = [];
    private static readonly SemaphoreSlim Once = new(1, 1);

    private static async Task<List<JsonNode>> Produced()
    {
        await Once.WaitAsync();
        try
        {
            if (cached.Count == 0) cached = await ProduceAsync();
            return cached;
        }
        finally { Once.Release(); }
    }

    private static async Task<List<JsonNode>> ProduceAsync()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var now = DateTimeOffset.UtcNow;
        // The execution.filled dedup key names the internal order ID: a venue ID alone collides across contracts.
        var (entryOrder, exitOrder) = (Order(owner, account.Id, "entry", "buy", now), Order(owner, account.Id, "exit", "sell", now));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        var capture = new VesselEventMutationCapture();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options, new CoreOwner(owner));
        var store = new VesselEventStore(db, capture, new NoOutbox(), Options.Create(new VesselEventSettings()),
            NullLogger<VesselEventStore>.Instance);
        var work = new UnitOfWork(db, [store]);
        await work.ExecuteAsync(async () =>
        {
            await store.EnsureMonitoringBaselineAsync(account.Id, default);
            var planned = (await db.Plays.SingleAsync()).MarkPlanned(now);
            db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            db.Orders.Add(entryOrder);
            db.OrderLinks.Add(new(await db.Plays.SingleAsync(), entryOrder, OrderLinkRole.Entry, "entry-1", null,
                OrderLinkState.Suggested, OrderLinkSource.Automatic, now));
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            (await db.OrderLinks.SingleAsync()).Confirm(now);
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            db.Fills.Add(Fill(owner, account.Id, "entry", "e1", "buy", 100, DateTimeOffset.UtcNow));
            db.StatusChanges.Add((await db.Plays.SingleAsync()).MarkOpen("Entry filled", now));
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            var stored = await db.Plays.SingleAsync();
            db.Orders.Add(exitOrder);
            db.OrderLinks.Add(new(stored, exitOrder, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Automatic, now));
            db.Fills.Add(Fill(owner, account.Id, "exit", "x1", "sell", 101, DateTimeOffset.UtcNow));
            db.StatusChanges.Add(stored.MarkClosed("Exit filled", now));
            return true;
        }, default);
        var page = await EventReads.ReadAsync(db, 0, 200, default);
        return page.Events.Select(e => Normalize(JsonSerializer.Serialize(e, VesselEventJson.Options), owner, account.Id, play.Id,
            entryOrder.Id, exitOrder.Id)).ToList();
    }

    private static JsonNode Normalize(string json, Guid owner, Guid account, Guid play, Guid entryOrder, Guid exitOrder)
    {
        json = json.Replace(owner.ToString(), "<owner>").Replace(account.ToString(), "<account>").Replace(play.ToString(), "<play>")
            .Replace(entryOrder.ToString(), "<entry-order>").Replace(exitOrder.ToString(), "<exit-order>");
        json = Guids().Replace(json, "<id>");
        return JsonNode.Parse(Times().Replace(json, "\"<time>\""))!;
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Guids();
    [GeneratedRegex("\"\\d{4}-\\d{2}-\\d{2}T[^\"]+\"")]
    private static partial Regex Times();

    private static ImportedOrder Order(Guid owner, Guid account, string id, string side, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", ExternalOrderId = id, Side = side,
        OrderType = "limit", OriginalSize = 0.5m, RemainingSize = 0, Status = "filled", VenueStatus = "filled",
        PlacedAtUtc = now.AddMinutes(-1), StatusAtUtc = now, ObservedAtUtc = now
    };

    private static ImportedFill Fill(Guid owner, Guid account, string order, string id, string side, decimal price, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", SourceFillId = id, Side = side,
        Direction = side == "buy" ? "Open Long" : "Close Long", PositionEffect = side == "buy" ? "open" : "close",
        Price = price, Quantity = 0.5m, Fee = 0.005m, FeeToken = "USDC", ClosedPnlUsd = side == "buy" ? 0 : 0.5m,
        OccurredAtUtc = now, ExternalOrderId = order, TransactionHash = "synthetic"
    };

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
