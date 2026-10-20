using Vessel.Application.Plays;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.6d (F-03): a suggestion says "another play" only when another active Play is offered the order.</summary>
public sealed partial class ExecutionPostgresTests
{
    [PostgresFact]
    public async Task Suggestions_say_another_play_only_when_another_active_Play_is_offered_the_order()
    {
        // U7: two take-profit orders at the price of two targets are two orders, each matching both levels of this Play.
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var plan = TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with {
            Targets = [new PlanExit("target-1", "price", "110", "50"), new PlanExit("target-2", "price", "110", "50")] }] };
        var created = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Two targets", plan), default);
        var play = await h.Plays.ChangeStatusAsync(created.Summary.Id, new(created.Summary.Version, "planned"), default);
        h.Orders.Orders.AddRange([Order("31", "sell", 110m, reduceOnly: true, size: 0.5m), Order("32", "sell", 110m, reduceOnly: true, size: 0.5m)]);
        var execution = await h.CheckAsync(play.Summary.Id);
        Assert.Equal(4, execution.Suggestions.Count);
        Assert.Equal(["31", "32"], execution.Suggestions.Select(s => s.Order!.OrderId).Distinct().Order());
        Assert.All(execution.Suggestions, s => Assert.False(s.OtherPlay));
        Assert.All(execution.Links, l => Assert.Null(l.OtherPlay));

        // An entry order that two Plays both match says so on each.
        var other = await PlannedPlay(h, account, "Other");
        h.Orders.Orders.Add(Order("41", "buy", 100m));
        execution = await h.CheckAsync(play.Summary.Id);
        Assert.True(Assert.Single(execution.Suggestions, s => s.Order!.OrderId == "41").OtherPlay);
        Assert.True(Assert.Single((await h.Execution.GetAsync(other.Summary.Id, default)).Suggestions, s => s.Order!.OrderId == "41").OtherPlay);

        // A cancelled Play is no longer another play the order could belong to.
        var current = await h.Plays.GetAsync(other.Summary.Id, default);
        await h.Plays.ChangeStatusAsync(other.Summary.Id, new(current.Summary.Version, "cancelled", "missed"), default);
        Assert.False(Assert.Single((await h.Execution.GetAsync(play.Summary.Id, default)).Suggestions, s => s.Order!.OrderId == "41").OtherPlay);
    }
}
