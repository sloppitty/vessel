using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// The golden journal (issue #88): one small dataset whose figures are worked out by hand in
/// <c>docs/architecture/golden-journal.md</c>. Every name here (a1, P2, SOL-L, …) is the one the document uses. A new
/// instance is built for each run, so the in-memory stores and a database can each own their entities.
/// </summary>
internal sealed class GoldenJournal
{
    /// <summary>The fixed clock. D(n) is <see cref="Now"/> minus n whole days.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    public static DateTimeOffset D(int days) => Now.AddDays(-days);

    public static readonly Guid Owner = Id(0x00);

    /// <summary>Marks the market-context reader supplies for Hyperliquid at <see cref="Now"/>.</summary>
    public const string AvaxMark = "130", EthMark = "52";

    public Portfolio Swing { get; }
    /// <summary>Gross: closed P&amp;L before fees, USDC fees (Hyperliquid). Net: closed P&amp;L net of fees (RISEx). Disabled holds a large win.</summary>
    public Account Gross { get; }
    public Account Net { get; }
    public Account Disabled { get; }
    public List<Account> Accounts { get; } = [];
    public List<Play> Plays { get; } = [];
    public List<PlayPlanRevision> Revisions { get; } = [];
    public List<PlayStatusChange> StatusChanges { get; } = [];
    public List<ImportedOrder> Orders { get; } = [];
    public List<ImportedFill> Fills { get; } = [];
    public List<PlayOrderLink> Links { get; } = [];
    public List<AccountSnapshot> Snapshots { get; } = [];
    public Dictionary<string, Play> Play { get; } = [];
    public Dictionary<string, ImportedFill> Fill { get; } = [];

    public GoldenJournal()
    {
        Swing = new Portfolio(Id(0x10), Owner, "Swing", "folder");
        Gross = new Account(Id(0x20), Owner, "hyperliquid", "Gross");
        Gross.Configure(Swing.Id, null, null);
        Net = new Account(Id(0x21), Owner, "risex", "Net");
        Disabled = new Account(Id(0x22), Owner, "hyperliquid", "Disabled");
        Disabled.Configure(Swing.Id, null, null);
        Disabled.UpdateSettings(Disabled.Name, Swing.Id, false);
        Accounts.AddRange([Gross, Net, Disabled]);

        var a = Gross;
        // P2: a short that loses 4 before 0.1 of fees.
        var p2 = Closed("P2", 0x32, a, "ETH", Plan("short", "margin", "100", "1", "50", Stop("s1", "price", "52.5", "100")), D(17));
        Trade(p2, OrderLinkRole.Entry, "a1", "sell", "open", 2, 50, 0, 0.05m, D(16));
        Trade(p2, OrderLinkRole.Exit, "a2", "buy", "close", 2, 52, -4, 0.05m, D(15));
        // P1, P3, P4: a win, an exact-zero scratch and a win, in that order.
        var p1 = Closed("P1", 0x31, a, "BTC", Plan("long", "margin", "100", "1", "100", Stop("s1", "price", "95", "100")), D(14));
        Trade(p1, OrderLinkRole.Entry, "a3", "buy", "open", 1, 100, 0, 0.1m, D(13));
        Trade(p1, OrderLinkRole.Exit, "a4", "sell", "close", 1, 110, 10, 0.1m, D(12));
        var p3 = Closed("P3", 0x33, a, "BTC", Plan("long", "margin", "100", "1", "100", Stop("s1", "price", "95", "100")), D(12));
        Trade(p3, OrderLinkRole.Entry, "a5", "buy", "open", 1, 100, 0, 0.1m, D(11).AddHours(-1));
        Trade(p3, OrderLinkRole.Exit, "a6", "sell", "close", 1, 100.2m, 0.2m, 0.1m, D(11));
        var p4 = Closed("P4", 0x34, a, "BTC", Plan("long", "margin", "200", "1", "100", Stop("s1", "price", "95", "100")), D(11));
        Trade(p4, OrderLinkRole.Entry, "a7", "buy", "open", 2, 100, 0, 0.2m, D(10).AddHours(-1));
        Trade(p4, OrderLinkRole.Exit, "a8", "sell", "close", 2, 103, 6, 0.2m, D(10));
        // P6: its entry order filled 2, but only 1 execution is retained.
        var p6 = Closed("P6", 0x36, a, "ETH", Plan("long", "margin", "120", "1", "60", Stop("s1", "price", "57", "100")), D(9));
        Trade(p6, OrderLinkRole.Entry, "a9", "buy", "open", 1, 60, 0, 0, D(8).AddHours(-1), orderSize: 2);
        Trade(p6, OrderLinkRole.Exit, "a10", "sell", "close", 1, 63, 3, 0, D(8));
        // X1: no Play; its exit fee is in HYPE, a token outside the stablecoin list.
        Unassigned("a11", a, "DOGE", "buy", "open", 100, 1, 0, 0.1m, D(9).AddHours(-1));
        Unassigned("a12", a, "DOGE", "sell", "close", 100, 1.02m, 2, 0.01m, D(9), feeToken: "HYPE");
        // P5: a loss made only of fees, closing at the same instant as P7.
        var p5 = Closed("P5", 0x35, a, "BTC", Plan("long", "margin", "100", "1", "100", Stop("s1", "price", "95", "100")), D(6));
        Trade(p5, OrderLinkRole.Entry, "a13", "buy", "open", 1, 100, 0, 0.1m, D(5).AddHours(-1));
        Trade(p5, OrderLinkRole.Exit, "a14", "sell", "close", 1, 100, 0, 0.1m, D(5));
        // AVAX: no Play; a long with an exit followed by an add, still open and marked at 130.
        Unassigned("a15", a, "AVAX", "buy", "open", 10, 100, 0, 0, D(3));
        Unassigned("a16", a, "AVAX", "sell", "close", 5, 110, 50, 0, D(2));
        Unassigned("a17", a, "AVAX", "buy", "open", 5, 120, 0, 0, D(1));
        // P8: an Open short, half filled; P9: a Planned long with a price stop and a percent stop.
        var p8 = Active("P8", 0x38, a, "ETH", Plan("short", "quantity", "4", "2", "50", Stop("s1", "price", "55", "100")), D(2), open: true);
        Trade(p8, OrderLinkRole.Entry, "a18", "sell", "open", 2, 51, 0, 0.05m, D(1));
        Active("P9", 0x39, a, "BTC", Plan("long", "quantity", "1", "3", "100", Stop("s1", "price", "90", "50"), Stop("s2", "percent", "15", "50")),
            D(1), open: false);

        var n = Net;
        // P7: a net-of-fee loss closing at the same instant as P5.
        var p7 = Closed("P7", 0x37, n, "ETH", Plan("long", "margin", "200", "1", "200", Stop("s1", "price", "190", "100")), D(7));
        Trade(p7, OrderLinkRole.Entry, "b1", "buy", "open", 1, 200, -0.1m, 0.1m, D(6));
        Trade(p7, OrderLinkRole.Exit, "b2", "sell", "close", 1, 190, -10.1m, 0.1m, D(5));
        // SOL: no Play; a long flipped to a short that closes exactly on the 7-day boundary.
        Unassigned("b3", n, "SOL", "buy", "open", 2, 10, -0.01m, 0.01m, D(9));
        Unassigned("b4", n, "SOL", "sell", "flip", 5, 12, 3.95m, 0.05m, D(8));
        Unassigned("b5", n, "SOL", "buy", "close", 3, 11, 2.97m, 0.03m, D(7));
        // XRP: no Play; an open short valued from the snapshot (this venue has no marks).
        Unassigned("b6", n, "XRP", "sell", "open", 4, 0.5m, -0.002m, 0.002m, D(1));

        // The disabled account's large win is never shown anywhere.
        Unassigned("c1", Disabled, "BTC", "buy", "open", 1, 1000, 0, 0, D(4));
        Unassigned("c2", Disabled, "BTC", "sell", "close", 1, 2000, 1000, 0, D(3));

        Snapshots.Add(Snapshot(Gross, 1000, 600, Position(Gross, "AVAX", 10, 110, 200), Position(Gross, "ETH", -2, 51, -2)));
        Snapshots.Add(Snapshot(Net, 500, 400, Position(Net, "XRP", -4, 0.5m, -0.2m)));
    }

    /// <summary>Hyperliquid and manual descriptors, with marks for AVAX and ETH; RISEx has no descriptor or marks here.</summary>
    public static VenueRegistry Venues() => TestVenues.With(new Marks());

    /// <summary>The operation ID Activity gives a round trip: its first fill and its direction.</summary>
    public string Operation(string firstFill, string direction) => $"{Fill[firstFill].Id}:{direction}";

    /// <summary>The fills of a retained order, by the shared <see cref="OrderKey"/> rule.</summary>
    public IEnumerable<ImportedFill> FillsOf(ImportedOrder order) => Fills.Where(f => OrderKey.Of(f) == OrderKey.Of(order));

    public bool IsEnabled(Guid accountId) => Accounts.Single(a => a.Id == accountId).IsEnabled;

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{0x88_0000_0000L + n:x12}");

    private static PlanExit Stop(string id, string unit, string value, string share) => new(id, unit, value, share);

    private static PlayPlanDocument Plan(string direction, string sizing, string size, string leverage, string entry, params PlanExit[] stops) =>
        new(direction, sizing, size, leverage, null, [new PlanEntry("e1", "Entry 1", "#b9c9e4", "100", entry, stops, [])],
            new PlanNotes("", "", "", ""));

    private Play New(string name, int id, Account account, string contract, PlayPlanDocument plan, DateTimeOffset planned)
    {
        var play = new Play(Id(id), account, new PerpetualInstrument(account.VenueId, contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), planned.AddMinutes(-5));
        var (change, revision) = play.MarkPlanned(planned);
        StatusChanges.Add(change);
        Revisions.Add(revision);
        Plays.Add(play);
        Play[name] = play;
        return play;
    }

    // Closed Plays open at their first entry and are detected flat one hour after their last exit, so every screen must use
    // the exit time, never the detection time. The Play's own status changes are recorded later, in Trade.
    private Play Closed(string name, int id, Account account, string contract, PlayPlanDocument plan, DateTimeOffset planned)
    {
        var play = New(name, id, account, contract, plan, planned);
        closing[play.Id] = true;
        return play;
    }

    private Play Active(string name, int id, Account account, string contract, PlayPlanDocument plan, DateTimeOffset planned, bool open)
    {
        var play = New(name, id, account, contract, plan, planned);
        closing[play.Id] = false;
        if (open) opening[play.Id] = true;
        return play;
    }

    private readonly Dictionary<Guid, bool> closing = [], opening = [];

    private void Trade(Play play, OrderLinkRole role, string name, string side, string effect, decimal quantity, decimal price,
        decimal pnl, decimal fee, DateTimeOffset at, decimal? orderSize = null)
    {
        var account = Accounts.Single(a => a.Id == play.AccountId);
        var fill = Add(name, account, play.ContractId!, side, effect, quantity, price, pnl, fee, at, "USDC");
        var order = TestOrders.For(account, play.ContractId!, fill.ExternalOrderId, side, "filled", orderSize ?? quantity);
        order.LimitPrice = price;
        Orders.Add(order);
        Links.Add(new PlayOrderLink(play, order, role, role == OrderLinkRole.Exit ? null : "e1", null, OrderLinkState.Linked,
            OrderLinkSource.Owner, at));
        if (role == OrderLinkRole.Entry && play.Status == PlayStatus.Planned && (closing[play.Id] || opening.ContainsKey(play.Id)))
            StatusChanges.Add(play.MarkOpen("Filled", at));
        if (role == OrderLinkRole.Exit && closing[play.Id]) StatusChanges.Add(play.MarkClosed("Flat", at.AddHours(1)));
    }

    private void Unassigned(string name, Account account, string contract, string side, string effect, decimal quantity, decimal price,
        decimal pnl, decimal fee, DateTimeOffset at, string feeToken = "USDC") =>
        Add(name, account, contract, side, effect, quantity, price, pnl, fee, at, feeToken);

    private ImportedFill Add(string name, Account account, string contract, string side, string effect, decimal quantity, decimal price,
        decimal pnl, decimal fee, DateTimeOffset at, string feeToken)
    {
        var fill = new ImportedFill
        {
            Id = Id(0x1000 + Fill.Count), OwnerId = Owner, AccountId = account.Id, ContractId = contract, SourceFillId = name,
            ExternalOrderId = "order-" + name, TransactionHash = "", Side = side, Direction = "Golden journal", PositionEffect = effect,
            Quantity = quantity, Price = price, ClosedPnlUsd = pnl, Fee = fee, FeeToken = feeToken, FeeBasis = ExecutionFacts.FeeReported,
            PnlBasis = account == Net ? ExecutionFacts.PnlNetOfFee : ExecutionFacts.PnlGross, OccurredAtUtc = at,
        };
        Fills.Add(fill);
        Fill[name] = fill;
        return fill;
    }

    private static AccountSnapshot Snapshot(Account account, decimal value, decimal available, params AccountPosition[] positions) => new()
    {
        OwnerId = Owner, AccountId = account.Id, ObservedAtUtc = Now, ValueScope = "golden-journal", AccountValueUsd = value,
        AvailableMarginUsd = available, Positions = positions.ToList(),
    };

    private static AccountPosition Position(Account account, string contract, decimal signed, decimal entry, decimal unrealized) => new()
    {
        OwnerId = Owner, AccountId = account.Id, ContractId = contract, SignedQuantity = signed, EntryPrice = entry,
        UnrealizedPnlUsd = unrealized,
    };

    private sealed class Marks : IMarketContextReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueMarketContext>>([
                new("AVAX", AvaxMark, AvaxMark, null, null, "0", "0", "0", null),
                new("ETH", EthMark, EthMark, null, null, "0", "0", "0", null)]);
    }
}

/// <summary>The golden journal's fixed clock.</summary>
internal sealed class GoldenClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => GoldenJournal.Now;
}
