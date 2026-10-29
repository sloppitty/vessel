using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class ReviewPositionRegressionTestsSupport
{

    internal static ImportedFill Fill(Account account, int minute, string side = "buy", string effect = "open",
        decimal pnl = 0, string contract = "BTC", decimal fee = 0, string token = "USDC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract,
        Side = side, PositionEffect = effect, Direction = "", Price = 100, Quantity = 1,
        ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, FeeBasis = "reported", PnlBasis = "gross",
        OccurredAtUtc = ReviewClock.Now.AddDays(-2).AddMinutes(minute),
        ExternalOrderId = Guid.NewGuid().ToString(), SourceFillId = Guid.NewGuid().ToString(), TransactionHash = ""
    };
}
