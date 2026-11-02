using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Orders have an internal ID and an external identity (account, contract, venue order ID); links reference the internal ID.
/// See docs/architecture/order-identity.md.
/// </summary>
public sealed class OrderIdentityUseCasesTests : OrderIdentityTestsSupport
{

    [Fact]
    public void A_link_refuses_an_order_of_another_contract_or_account()
    {
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Main");
        var other = new Account(Guid.NewGuid(), owner, "hyperliquid", "Other");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        PlayOrderLink Link(Vessel.Domain.Workspace.ImportedOrder order) =>
            new(play, order, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, At);

        var linked = Link(TestOrders.For(play, "7"));
        Assert.Equal(play.AccountId, linked.AccountId);
        Assert.Throws<DomainRuleException>(() => Link(TestOrders.For(account, "ETH", "7")));
        Assert.Throws<DomainRuleException>(() => Link(TestOrders.For(other, "BTC", "7")));
        Assert.Throws<DomainRuleException>(() => Link(TestOrders.For(Guid.NewGuid(), account.Id, "BTC", "7")));
    }
}
