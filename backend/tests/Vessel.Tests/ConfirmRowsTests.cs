using Vessel.Application.Common;
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

    [PostgresFact]
    public async Task Not_this_play_turns_an_order_down_for_every_level_and_as_an_unplanned_exit_in_one_step()
    {
        // Sol 1.6d: one filled take-profit matching two targets of an Open long. Declining its rows one by one let the next
        // check link the other target, or the order as an unplanned exit, and close the Play.
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var plan = TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with {
            Targets = [new PlanExit("target-1", "price", "110", "50"), new PlanExit("target-2", "price", "110", "50")] }] };
        var created = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", "Two targets", plan), default);
        var play = await h.Plays.ChangeStatusAsync(created.Summary.Id, new(created.Summary.Version, "planned"), default);
        var id = play.Summary.Id;
        h.Orders.Orders.Add(Order("11", "buy", 100.01m, "filled"));
        AddFills(h, Fill("e1", "11", "Open Long", 100m, 1m));
        Assert.Equal("open", (await h.CheckAsync(id)).Status);
        h.Orders.Orders.Add(Order("31", "sell", 110m, "filled", reduceOnly: true));
        AddFills(h, Fill("x1", "31", "Close Long", 110m, 1m));
        var execution = await h.CheckAsync(id);
        Assert.Equal(2, execution.Suggestions.Count(s => s.Order!.OrderId == "31"));

        execution = await h.Execution.RejectOrderAsync(id, new("31"), default);
        Assert.DoesNotContain(execution.Suggestions, s => s.Order!.OrderId == "31");
        // Later checks never attribute it to this Play, at a level or as an unplanned exit.
        execution = await h.CheckAsync(id);
        Assert.DoesNotContain(execution.Links, l => l.Order?.OrderId == "31");
        Assert.DoesNotContain(execution.Suggestions, s => s.Order!.OrderId == "31");
        Assert.Equal("open", execution.Status);
        // Turning down an order linked to this Play is refused: unlink it instead; an unknown order is not found.
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RejectOrderAsync(id, new("99"), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => h.Execution.RejectOrderAsync(id, new("11"), default))).StatusCode);
    }
}
