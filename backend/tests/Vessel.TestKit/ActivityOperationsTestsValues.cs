using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public partial class ActivityOperationsTestsValues
{
    internal static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    internal static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    internal static ImportedFill Fill(int n, string side = "buy", string effect = "open", decimal quantity = 1m,
        decimal price = 100m, decimal pnl = 0m, decimal fee = 0m, string basis = "gross", string token = "USDC", string? order = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = "BTC", Side = side,
        Direction = "Display text must not control aggregation", PositionEffect = effect, Quantity = quantity, Price = price,
        ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, PnlBasis = basis, FeeBasis = "reported",
        OccurredAtUtc = Start.AddMinutes(n), ExternalOrderId = order ?? n.ToString(), SourceFillId = n.ToString(), TransactionHash = ""
    };

    internal static AccountSnapshot Snapshot(decimal quantity = 0, int observedMinute = 100) => new()
    {
        AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(observedMinute),
        Positions = quantity == 0 ? [] : [new() { ContractId = "BTC", SignedQuantity = quantity }]
    };
}
