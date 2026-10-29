using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Third review round for PR #46: durable re-confirmation, stop mechanics and bounded unobservable lifetime.</summary>
public sealed class MonitorDiscrepancyRound3UnitTests
{

    [Fact] // Finding 2: one classification for matching, link validation and monitoring.
    public void Order_mechanics_are_classified_once_and_refuse_take_profit_stops()
    {
        Assert.Equal(OrderMechanism.Stop, OrderMechanics.Classify("Stop Market", true));
        Assert.Equal(OrderMechanism.Stop, OrderMechanics.Classify("stop-loss-limit", true));
        Assert.Equal(OrderMechanism.TakeProfit, OrderMechanics.Classify("Take Profit Limit", true));
        Assert.Equal(OrderMechanism.TakeProfit, OrderMechanics.Classify("take-profit", true));
        Assert.Equal(OrderMechanism.UnknownTrigger, OrderMechanics.Classify("Trigger", true));
        Assert.Equal(OrderMechanism.Plain, OrderMechanics.Classify("Limit", false));
        var plan = new PlayPlanDocument("long", "quantity", "1", "1", null, [], new("", "", "", ""));
        ImportedOrder Order(string type) => new() { ExternalOrderId = "o", ContractId = "BTC", Side = "sell", OrderType = type, TriggerPrice = 120,
            LimitPrice = 120, ReduceOnly = true, OriginalSize = 1, RemainingSize = 1, Status = "open", VenueStatus = "open" };
        Assert.NotNull(ExecutionIntegrity.RoleConflict(OrderLinkRole.Stop, plan, Order("take-profit"), []));
        Assert.NotNull(ExecutionIntegrity.RoleConflict(OrderLinkRole.Stop, plan, Order("Take Profit Market"), []));
        Assert.Null(ExecutionIntegrity.RoleConflict(OrderLinkRole.Stop, plan, Order("Stop Market"), []));
        Assert.Null(ExecutionIntegrity.RoleConflict(OrderLinkRole.Target, plan, Order("take-profit"), []));
    }

    [Fact] // Finding 3: the lifetime is configurable.
    public void Unobservable_lifetime_defaults_to_seven_days_and_is_validated()
    {
        Assert.Equal(TimeSpan.FromDays(7), new MonitorOptions().UnobservableLifetime);
        Assert.Throws<InvalidOperationException>(() => new MonitorOptions { UnobservableLifetime = TimeSpan.FromMinutes(5) }.Validate());
    }
}
