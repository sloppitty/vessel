using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

internal sealed class ReviewFixture : IReviewStore
{
    public Guid Owner { get; } = Guid.NewGuid();
    public List<Account> Accounts { get; } = [];
    public List<Portfolio> Portfolios { get; } = [];
    public List<ClosedPlayFacts> Closed { get; } = [];
    public List<EndedPlayFacts> Cancelled { get; } = [];
    public List<UnresolvedPlayFacts> Unresolved { get; } = [];
    /// <summary>With the Hyperliquid descriptor, so Play-local results know the venue's quote asset.</summary>
    public ReviewService Service => new(this, new ReviewClock(), TestVenues.With());
    public ReviewQuery? LastQuery { get; private set; }
    public Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
    {
        LastQuery = query;
        return Task.FromResult(new ReviewFacts(Portfolios.ToList(), Accounts.ToList(), Closed.ToList(), 0, 0, null)
            { CancelledPlays = Cancelled.ToList(), UnresolvedPlays = Unresolved.ToList() }); // A snapshot, like the store
    }
    public Account Account(string name = "Main", Guid? portfolio = null)
    {
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", name);
        account.Configure(portfolio, null, null);
        Accounts.Add(account);
        return account;
    }
    public ClosedPlayFacts Add(decimal net, DateTimeOffset? exit = null, Account? account = null,
        string instrument = "BTC", decimal entryPrice = 100, bool risk = true, PlayPlanDocument? customPlan = null)
    {
        account ??= Accounts.FirstOrDefault() ?? Account();
        var at = exit ?? ReviewClock.Now.AddHours(-1);
        var plan = customPlan ?? new PlayPlanDocument("long", "margin", "100", "1", null,
            [new PlanEntry("e", "Entry", "#ffffff", "100", "100",
                risk ? [new PlanExit("s", "price", "95", "100")] : [], [])], new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, instrument),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), at.AddDays(-2));
        var revision = play.MarkPlanned(at.AddDays(-2)).Revision;
        play.MarkOpen("filled", at.AddDays(-1));
        play.MarkClosed("synced later", ReviewClock.Now);
        ImportedFill Fill(bool opening) => new()
        {
            Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, ContractId = instrument,
            SourceFillId = Guid.NewGuid().ToString(), ExternalOrderId = Guid.NewGuid().ToString(), TransactionHash = "",
            Side = opening ? ExecutionFacts.Buy : ExecutionFacts.Sell, Direction = "",
            PositionEffect = opening ? ExecutionFacts.Open : ExecutionFacts.Close,
            Quantity = 1, Price = opening ? entryPrice : entryPrice + net,
            ClosedPnlUsd = opening ? 0 : net, Fee = 0, FeeToken = "USDC",
            OccurredAtUtc = opening ? at.AddDays(-1) : at,
        };
        var facts = new ClosedPlayFacts(play, [revision],
            [new LinkedFill(OrderLinkRole.Entry, Fill(true)), new LinkedFill(OrderLinkRole.Exit, Fill(false))]);
        Closed.Add(facts);
        return facts;
    }
}
