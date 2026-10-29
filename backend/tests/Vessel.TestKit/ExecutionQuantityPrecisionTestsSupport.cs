using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class ExecutionQuantityPrecisionTestsSupport
{
    internal const decimal Huge = 10000000000000000000000000000m;
    internal static readonly DateTimeOffset At = ReviewClock.Now.AddHours(-1);

    internal sealed class Fixture
    {
        public Account Account { get; }
        public MemoryPlayStore Plays { get; }
        public MemoryExecutionStore Store { get; }
        public Play Play { get; }
        public List<LinkedFill> Fills { get; } = [];
        public ClosedPlayFacts Facts => new(Play, Plays.Revisions, Fills);
        public PlayExecutionService Service => new(Plays, Store, TestVenues.With(), new ReviewClock(), TestUnitOfWork.None);

        public Fixture(string direction = "long")
        {
            Account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Precision");
            Plays = new(Account.OwnerId);
            Plays.Accounts.Add(Account);
            Play = new(Guid.NewGuid(), Account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                PlayDocuments.Serialize(TestPlays.Plan("1", direction == "long" ? "0.95" : "1.05") with { Direction = direction }),
                At.AddMinutes(-1));
            Plays.Add(Play);
            Plays.Add(Play.MarkPlanned(At.AddMinutes(-1)).Revision);
            Store = new(Plays);
        }

        public ImportedFill Add(OrderLinkRole role, decimal quantity, int minute, decimal price = 0.00000001m, decimal pnl = 0,
            string? orderId = null)
        {
            orderId ??= "order-" + Fills.Count;
            var entry = role == OrderLinkRole.Entry;
            var side = ExecutionIntegrity.EntrySide(PlayDocuments.Read(Play.Plan));
            if (!entry) side = side == ExecutionFacts.Buy ? ExecutionFacts.Sell : ExecutionFacts.Buy;
            var fill = new ImportedFill
            {
                Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ContractId = "BTC",
                SourceFillId = "fill-" + Fills.Count, ExternalOrderId = orderId, Side = side, Direction = "",
                PositionEffect = entry ? ExecutionFacts.Open : ExecutionFacts.Close, Price = price, Quantity = quantity,
                Fee = 0, FeeToken = "USDC", ClosedPnlUsd = pnl, OccurredAtUtc = At.AddMinutes(minute), TransactionHash = "",
            };
            Fills.Add(new(role, fill));
            Store.Fills.Add(fill);
            if (Store.Orders.All(o => o.ExternalOrderId != orderId))
                Store.Orders.Add(new()
                {
                    Id = Guid.NewGuid(), OwnerId = Account.OwnerId, AccountId = Account.Id, ExternalOrderId = orderId,
                    ContractId = "BTC", Side = side, OrderType = "Limit", LimitPrice = price, OriginalSize = quantity,
                    PlacedAtUtc = At, Status = "filled", VenueStatus = "filled", StatusAtUtc = At, ObservedAtUtc = At,
                });
            return fill;
        }

        public async Task<PlayExecutionDto> LinkAll()
        {
            PlayExecutionDto? result = null;
            foreach (var fill in Fills.DistinctBy(f => f.Fill.ExternalOrderId))
                result = await Service.LinkAsync(Play.Id, new(fill.Fill.ExternalOrderId,
                    fill.Role == OrderLinkRole.Entry ? "entry" : "exit", fill.Role == OrderLinkRole.Entry ? "entry-1" : null), default);
            return result!;
        }
    }
}
