using Vessel.Application.Activity;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// October 18, 2026: with no decided closed Play the sizing record starts from closed Activity operations. Sources are
/// never mixed, R is unknown and nothing becomes a Play or a link.
/// </summary>
public sealed class SizingActivityFallbackTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Guid owner = Guid.NewGuid(), accountId = Guid.NewGuid();
    private readonly MemorySizingStore store = new();
    private readonly List<ImportedFill> fills = [];
    private readonly List<Account> accounts = [];

    public SizingActivityFallbackTests() => accounts.Add(new Account(accountId, owner, "risex", "Venue"));

    private ImportedFill Fill(int minute, string side, string effect, decimal price, decimal pnl = 0, decimal fee = 0, string basis = "gross",
        string token = "USDC", decimal quantity = 1, Guid? account = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account ?? accountId, ContractId = "BTC", Side = side, PositionEffect = effect,
        Direction = "fixture", Price = price, Quantity = quantity, ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, PnlBasis = basis,
        FeeBasis = "reported", OccurredAtUtc = Start.AddMinutes(minute), ExternalOrderId = minute.ToString(), SourceFillId = Guid.NewGuid().ToString(),
        TransactionHash = ""
    };

    /// <summary>One long round trip of one unit opened at 100, closed at 10 minutes after <paramref name="minute"/>.</summary>
    private void RoundTrip(int minute, decimal pnl, decimal fee = 0, string basis = "gross", string token = "USDC", Guid? account = null)
    {
        fills.Add(Fill(minute, "buy", "open", 100, basis == "net-of-fee" ? -fee : 0, fee, basis, token, account: account));
        fills.Add(Fill(minute + 10, "sell", "close", 100 + pnl, pnl, fee, basis, token, account: account));
    }

    private SizingService Service()
    {
        var facts = new ActivityFacts(accounts, fills, [], new Dictionary<Guid, Guid>());
        var workspace = new MemoryWorkspaceStore(owner);
        workspace.Accounts.AddRange(accounts);
        var activity = new ActivityService(new Store(facts), workspace, TimeProvider.System);
        return new SizingService(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None, activity);
    }

    [Fact]
    public async Task Without_closed_plays_the_record_uses_closed_operations_with_unlevered_returns_and_no_r()
    {
        RoundTrip(0, 10, fee: 0.5m);   // win: 10 − 1 fee = 9 on 100 notional
        RoundTrip(100, -4, fee: 0.5m); // loss: −4 − 1 = −5
        fills.Add(Fill(300, "buy", "open", 100)); // open: never scored
        var result = await Service().GetAsync(default);

        Assert.Equal(SizingService.SourceActivity, result.Record.Source);
        Assert.Equal((2, 2, 1, 1), (result.Record.ClosedPlays, result.Record.DecidedPlays, result.Record.Wins, result.Record.Losses));
        Assert.Equal(("0.5", "9", "5"), (result.Record.BattingAverage, result.Record.AverageGainPercent, result.Record.AverageLossPercent));
        Assert.Null(result.Record.AverageWinR); Assert.Null(result.Record.AverageLossR);
        var latest = result.Record.Recent[0];
        Assert.Equal(("loss", "-5", "-5"), (latest.Outcome, latest.NetResultUsd, latest.ReturnPercent));
        Assert.Null(latest.PlayId); Assert.Null(latest.RiskUsd); Assert.Null(latest.RMultiple);
        Assert.Equal(accountId, latest.AccountId); Assert.EndsWith(":long", latest.OperationId);
        Assert.Equal(("BTC Long", "BTC"), (latest.Title, latest.Instrument));
        Assert.Contains("closed positions in Activity", result.Notice);
        Assert.Contains("not advice", result.Notice);
        Assert.Contains("Activity", result.Exposure.Reason);
    }

    [Fact]
    public async Task Net_of_fee_results_are_not_charged_their_fee_twice()
    {
        RoundTrip(0, 10, fee: 0.5m, basis: "net-of-fee"); // entry −0.5, exit 10 already net
        var result = await Service().GetAsync(default);
        Assert.Equal("9.5", Assert.Single(result.Record.Recent).NetResultUsd);
    }

    [Fact]
    public async Task Unknown_net_results_incomplete_and_ambiguous_operations_are_left_out()
    {
        RoundTrip(0, 10, fee: 0.5m, token: "HYPE"); // gross fee in an unlisted token: net unknown
        fills.Add(Fill(100, "sell", "close", 110, 5)); // a close without its opening: incomplete
        var result = await Service().GetAsync(default);
        Assert.Equal(SizingService.SourceNone, result.Record.Source);
        Assert.Equal(0, result.Record.ClosedPlays);
        Assert.Empty(result.Record.Recent);
        Assert.DoesNotContain("Activity", result.Notice);
    }

    [Fact]
    public async Task Operations_with_known_missing_executions_are_left_out()
    {
        // Balanced retained fills look closed, but both orders filled size 2 and only one execution of each is retained.
        RoundTrip(0, 10);
        store.Orders.AddRange([Order("0", "buy", 2), Order("10", "sell", 2)]);
        var result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceNone, 0), (result.Record.Source, result.Record.ClosedPlays));

        // A fully retained order, an order on another contract with the same venue ID and an unfilled one prove nothing missing.
        store.Orders.Clear();
        store.Orders.AddRange([Order("0", "buy", 1), Order("10", "sell", 2, contract: "ETH"), Order("10", "sell", 2, status: "canceled")]);
        result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceActivity, 1, 1), (result.Record.Source, result.Record.ClosedPlays, result.Record.Wins));
    }

    [Fact]
    public async Task Executions_of_the_order_outside_the_operation_count_toward_its_size()
    {
        // Order 10 (size 2) closes the long and later opens the short; both executions are retained, so nothing is missing.
        fills.Add(Fill(0, "buy", "open", 100));
        fills.Add(Fill(10, "sell", "close", 110, 10));
        var reopen = Fill(20, "sell", "open", 110);
        reopen.ExternalOrderId = "10";
        fills.Add(reopen);
        fills.Add(Fill(30, "buy", "close", 100, 10));
        store.Orders.Add(Order("10", "sell", 2));
        var result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceActivity, 2), (result.Record.Source, result.Record.Wins));
    }

    [Fact]
    public async Task A_return_outside_decimal_range_is_left_out_without_failing_the_record()
    {
        // Net 1e18 over a 1e-16 notional fits decimal, the return percent does not.
        fills.Add(Fill(0, "buy", "open", 0.00000001m, quantity: 0.00000001m));
        fills.Add(Fill(10, "sell", "close", 0.00000002m, 1_000_000_000_000_000_000m, quantity: 0.00000001m));
        RoundTrip(100, 5);
        var result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceActivity, 1, 1), (result.Record.Source, result.Record.ClosedPlays, result.Record.Wins));
        Assert.Equal("5", Assert.Single(result.Record.Recent).ReturnPercent);

        fills.RemoveRange(2, 2);
        result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceNone, 0), (result.Record.Source, result.Record.ClosedPlays));
    }

    [Fact]
    public async Task An_entry_notional_beyond_28_places_is_left_out_rather_than_rounded()
    {
        // 1e-14 × 1.5e-14 = 1.5e-28 exactly; rounded to 28 places it would be 2e-28 and score 50% instead of 66.67%.
        fills.Add(Fill(0, "buy", "open", 0.000000000000015m, quantity: 0.00000000000001m));
        fills.Add(Fill(10, "sell", "close", 0.00000000000002m, 0.0000000000000000000000000001m, quantity: 0.00000000000001m));
        var result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceNone, 0), (result.Record.Source, result.Record.ClosedPlays));

        // The same tiny scale is scored when the exact product is representable: 1e-28 over 2e-28 is 50%.
        fills.Clear();
        fills.Add(Fill(0, "buy", "open", 0.00000000000002m, quantity: 0.00000000000001m));
        fills.Add(Fill(10, "sell", "close", 0.00000000000003m, 0.0000000000000000000000000001m, quantity: 0.00000000000001m));
        result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceActivity, 1), (result.Record.Source, result.Record.Wins));
        Assert.Equal("50", Assert.Single(result.Record.Recent).ReturnPercent);
    }

    private ImportedOrder Order(string id, string side, decimal size, string contract = "BTC", string status = "filled") => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = accountId, ContractId = contract, ExternalOrderId = id, Side = side, OrderType = "limit",
        LimitPrice = 100, OriginalSize = size, RemainingSize = 0, PlacedAtUtc = Start, Status = status, VenueStatus = status,
        StatusAtUtc = Start, ObservedAtUtc = Start
    };

    [Fact]
    public async Task Disabled_accounts_do_not_contribute()
    {
        var disabled = new Account(Guid.NewGuid(), owner, "risex", "Disabled");
        disabled.UpdateSettings(disabled.Name, null, false);
        accounts.Add(disabled);
        RoundTrip(0, 10, account: disabled.Id);
        var result = await Service().GetAsync(default);
        Assert.Equal((SizingService.SourceNone, 0), (result.Record.Source, result.Record.ClosedPlays));
    }

    [Fact]
    public async Task The_window_keeps_the_latest_twenty_and_the_ladder_walks_every_fallback_outcome()
    {
        for (var i = 0; i < 22; i++) RoundTrip(i * 100, 5);
        for (var i = 22; i < 25; i++) RoundTrip(i * 100, -2);
        var result = await Service().GetAsync(default);
        Assert.Equal((25, 20, 17, 3), (result.Record.ClosedPlays, result.Record.Recent.Count, result.Record.Wins, result.Record.Losses));
        Assert.Equal(("half", "0.5"), (result.Exposure.Level, result.Exposure.Multiplier));
        Assert.Equal("0.625", result.Limits.EffectiveRiskPercent);
        Assert.StartsWith("Half size after 3 losses in a row.", result.Exposure.Reason);
    }

    [Fact]
    public async Task One_decided_closed_play_replaces_the_fallback_without_mixing_sources()
    {
        RoundTrip(0, -4); RoundTrip(100, -4); RoundTrip(200, -4);
        store.Closed.Add(ClosedPlay(win: true));
        var result = await Service().GetAsync(default);
        Assert.Equal(SizingService.SourcePlays, result.Record.Source);
        Assert.Equal((1, 1, 0), (result.Record.ClosedPlays, result.Record.Wins, result.Record.Losses));
        Assert.Equal("full", result.Exposure.Level);
        Assert.NotNull(Assert.Single(result.Record.Recent).PlayId);
        Assert.DoesNotContain("Activity", result.Exposure.Reason);
    }

    [Fact]
    public async Task Retrospective_plays_alone_do_not_stop_the_fallback()
    {
        RoundTrip(0, 3);
        store.Closed.Add(ClosedPlay(win: true, retrospective: true));
        var result = await Service().GetAsync(default);
        Assert.Equal(SizingService.SourceActivity, result.Record.Source);
        Assert.Equal(1, result.Record.Wins);
    }

    [Fact]
    public async Task Without_activity_the_existing_play_record_is_unchanged()
    {
        var result = await new SizingService(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None).GetAsync(default);
        Assert.Equal((SizingService.SourcePlays, 0), (result.Record.Source, result.Record.ClosedPlays));
    }

    private ClosedPlayFacts ClosedPlay(bool win, bool retrospective = false)
    {
        var account = accounts[0];
        var plan = new PlayPlanDocument("long", "margin", "250", "3", null,
            [new PlanEntry("e", "e", "#b9c9e4", "100", "100", [new PlanExit("s", "price", "95", "100")], [new PlanExit("t", "price", "120", "100")])],
            new PlanNotes("", "", "", ""));
        var instrument = new PerpetualInstrument("risex", "BTC");
        Play play;
        List<PlayPlanRevision> revisions = [];
        if (retrospective)
            play = Play.Retrospective(Guid.NewGuid(), account, instrument, PlayDocuments.Serialize(plan), Start.AddDays(1), Start.AddDays(1), "Retro");
        else
        {
            play = new Play(Guid.NewGuid(), account, instrument, InstrumentSource.Venue, PlayDocuments.Serialize(plan), Start);
            revisions.Add(play.MarkPlanned(Start).Revision);
            play.MarkOpen("Filled.", Start.AddHours(1));
            play.MarkClosed("Exited.", Start.AddDays(1));
        }
        ImportedFill Linked(string side, string effect, decimal price, decimal pnl, int minutes) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC", Side = side, PositionEffect = effect, Direction = "",
            Price = price, Quantity = 1, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", PnlBasis = "gross", FeeBasis = "reported",
            OccurredAtUtc = Start.AddMinutes(30 + minutes), ExternalOrderId = "o" + minutes, SourceFillId = "f" + minutes, TransactionHash = ""
        };
        return new ClosedPlayFacts(play, revisions, [new(OrderLinkRole.Entry, Linked("buy", "open", 100, 0, 0)),
            new(OrderLinkRole.Target, Linked("sell", "close", win ? 110 : 90, win ? 10 : -10, 60))]);
    }

    private sealed class Store(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
    }
}
