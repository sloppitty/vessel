using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Minimal retained orders for tests that only need a link target: links reference an order's internal ID.</summary>
internal static class TestOrders
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The last migration before the order identity change. Migration tests that seed historical "OrderId" columns stop
    /// here: the change has no backfill and refuses a database that holds orders, fills or links.
    /// </summary>
    public const string BeforeOrderIdentity = "20261016174509_FundingPayments";

    public static ImportedOrder For(Guid owner, Guid account, string contract, string externalOrderId, string side = "buy",
        string status = "filled", decimal size = 1) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = contract, ExternalOrderId = externalOrderId,
        Side = side, OrderType = "Limit", LimitPrice = 100, OriginalSize = size, RemainingSize = status == "open" ? size : 0,
        PlacedAtUtc = At, Status = status, VenueStatus = status, StatusAtUtc = At, ObservedAtUtc = At
    };

    public static ImportedOrder For(Account account, string contract, string externalOrderId, string side = "buy", string status = "filled",
        decimal size = 1) => For(account.OwnerId, account.Id, contract, externalOrderId, side, status, size);

    /// <summary>An order on the Play's own account and contract.</summary>
    public static ImportedOrder For(Play play, string externalOrderId, string side = "buy", string status = "filled", decimal size = 1) =>
        For(play.OwnerId, play.AccountId, play.ContractId!, externalOrderId, side, status, size);
}
