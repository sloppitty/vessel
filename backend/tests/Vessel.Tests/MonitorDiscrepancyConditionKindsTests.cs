using System.Text.Json;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, D-19 follow-up (owner, October 23): a changing size is normal while a trade is built, so a missing stop, an
/// oversized stop or target, and a venue position smaller or larger than expected confirm on their condition, as the
/// undersized stop does. Their fingerprints and details keep the quantities, so an open record shows the latest and a
/// dismissed one reopens only on changed evidence. Interruptions, a fresh absence and the dismissed evidence returning
/// still restart the sequence.
/// </summary>
public sealed class MonitorDiscrepancyConditionKindsTests
{
    public static TheoryData<string> Kinds => new() { "protection.missing", "protection.excess", "position.short", "position.excess" };

    [Theory, MemberData(nameof(Kinds))]
    public async Task Changing_quantities_confirm_once_after_the_delay_with_the_latest_evidence(string kind)
    {
        var f = Arrange(kind);
        var start = f.Clock.Now;
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        // Every check sees other quantities, with the condition still present.
        f.Advance(30); Evidence(f, kind, 1);
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.DoesNotContain(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
        f.Advance(30); Evidence(f, kind, 2);
        await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
        Assert.Equal(("open", Severity(kind), start, Latest(kind, 2)), (row.State, row.Severity, row.OpenedAtUtc, Shown(kind, row.Details)));
        var opened = Assert.Single(f.Events.Items, x => x.Subject.DiscrepancyId == row.Id);
        Assert.Equal(("discrepancy.opened", Severity(kind)), (opened.Type, opened.Severity));
        Assert.Equal(Latest(kind, 2), Shown(kind, opened.Data.GetProperty("details")));
        // An open record keeps following its evidence silently.
        f.Advance(60); Evidence(f, kind, 3);
        await f.Evaluate();
        row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
        Assert.Equal(("open", start, Latest(kind, 3)), (row.State, row.OpenedAtUtc, Shown(kind, row.Details)));
        Assert.Single(f.Events.Items, x => x.Subject.DiscrepancyId == row.Id);
    }

    [Fact] // The owner's case: an entry that keeps filling after the grace still confirms 60 s after the grace.
    public async Task A_missing_stop_on_a_position_that_keeps_growing_after_the_grace_confirms_on_its_condition()
    {
        var f = Arrange("protection.missing");
        var graceEnds = f.Clock.Now.AddSeconds(30);
        f.Store.Fills[0].OccurredAtUtc = graceEnds - new MonitorOptions().ProtectionGrace;
        // Within the grace the stop is not missing yet; the end of the grace is the next check.
        Assert.Equal(graceEnds, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.Empty(f.Store.Rows);
        f.Advance(30); Evidence(f, "protection.missing", 1);
        Assert.Equal(graceEnds + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        var row = f.Store.Rows.Single(x => x.Kind == "protection.missing");
        f.Advance(30); Evidence(f, "protection.missing", 2);
        await f.Evaluate();
        Assert.Equal(("candidate", (DateTimeOffset?)graceEnds), (row.State, row.PendingSinceUtc));
        f.Advance(30); Evidence(f, "protection.missing", 3);
        await f.Evaluate();
        var dto = Assert.Single(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default), x => x.Kind == "protection.missing");
        Assert.Equal(("open", "critical", graceEnds, "1.6"), (dto.State, dto.Severity, dto.OpenedAtUtc, Shown("protection.missing", dto.Details)));
        Assert.Equal(("discrepancy.opened", "critical"), (Assert.Single(f.Events.Items).Type, f.Events.Items[0].Severity));
    }

    [Theory]
    [InlineData("protection.missing", "incomplete-read")]
    [InlineData("protection.missing", "history-gap")]
    [InlineData("protection.missing", "interrupted-check")]
    [InlineData("protection.missing", "absent")]
    [InlineData("protection.excess", "incomplete-read")]
    [InlineData("protection.excess", "history-gap")]
    [InlineData("protection.excess", "interrupted-check")]
    [InlineData("protection.excess", "absent")]
    [InlineData("position.short", "incomplete-read")]
    [InlineData("position.short", "history-gap")]
    [InlineData("position.short", "interrupted-check")]
    [InlineData("position.short", "absent")]
    [InlineData("position.excess", "incomplete-read")]
    [InlineData("position.excess", "history-gap")]
    [InlineData("position.excess", "interrupted-check")]
    [InlineData("position.excess", "absent")]
    public async Task An_unknown_or_interrupted_check_or_a_fresh_absence_restarts_the_sequence(string kind, string interruption)
    {
        var f = Arrange(kind);
        var start = f.Clock.Now;
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == kind);
        // Changed evidence continues the sequence.
        f.Advance(30); Evidence(f, kind, 1);
        await f.Evaluate();
        Assert.Equal(start, row.PendingSinceUtc);
        f.Advance(15);
        if (interruption == "interrupted-check") await f.Service.SettleAsync(f.Account, Guid.NewGuid(), "scheduled", true, default);
        else if (interruption == "absent") { Clear(f, kind); await f.Evaluate(); }
        else
        {
            Unknown(f, kind, interruption, true);
            Assert.True((await f.Evaluate()).Incomplete);
            Unknown(f, kind, interruption, false);
        }
        var state = interruption == "absent" ? "resolved" : "candidate";
        Assert.Equal((state, (DateTimeOffset?)null), (row.State, row.PendingSinceUtc));
        // 60 s after the start, this observation would confirm an uninterrupted sequence. It starts a new one instead.
        f.Advance(15); Evidence(f, kind, 2);
        var restart = f.Clock.Now;
        Assert.Equal(restart + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.Equal((state, (DateTimeOffset?)restart), (row.State, row.PendingSinceUtc));
        Assert.DoesNotContain(f.Events.Items, x => x.Subject.DiscrepancyId == row.Id);
        f.Advance(60); await f.Evaluate();
        Assert.Equal(("open", restart), (row.State, row.OpenedAtUtc));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_dismissed_record_reopens_only_on_changed_evidence_after_the_delay(string kind)
    {
        var f = Arrange(kind);
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == kind);
        Assert.Equal("open", row.State);
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, "building the position", default);
        var dismissed = row.Fingerprint;
        // The dismissed evidence returning stays dismissed and keeps no work.
        f.Advance(60); await f.Evaluate();
        Assert.Equal(("dismissed", false), (row.State, row.HasWork));
        // Changed evidence is pending work, not a reopening.
        f.Advance(60); Evidence(f, kind, 1);
        await f.Evaluate();
        Assert.Equal(("dismissed", dismissed, true), (row.State, row.Fingerprint, row.HasWork));
        // The dismissed evidence returning ends that work, although the condition has held throughout.
        f.Advance(30); Evidence(f, kind, 0);
        await f.Evaluate();
        Assert.Equal(("dismissed", (string?)null, false), (row.State, row.PendingFingerprint, row.HasWork));
        // Changed evidence that keeps changing reopens it 60 s later, with its latest evidence.
        f.Advance(30); Evidence(f, kind, 1);
        var since = f.Clock.Now;
        await f.Evaluate();
        f.Advance(30); Evidence(f, kind, 2);
        await f.Evaluate();
        Assert.Equal(("dismissed", (DateTimeOffset?)since), (row.State, row.PendingSinceUtc));
        f.Advance(30); await f.Evaluate();
        var dto = Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
        Assert.Equal(("open", since, Latest(kind, 2)), (dto.State, dto.OpenedAtUtc, Shown(kind, dto.Details)));
        var events = f.Events.Items.Where(x => x.Subject.DiscrepancyId == row.Id).ToArray();
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved", "discrepancy.opened"], events.Select(x => x.Type).ToArray());
        Assert.Equal("dismissed", events[1].Data.GetProperty("resolution").GetString());
    }

    [Theory] // One comparison, two kinds: a switch is another record, which never continues the other's sequence.
    [InlineData("position.short", "position.excess")]
    [InlineData("position.excess", "position.short")]
    public async Task A_switch_between_a_short_and_an_excess_position_starts_its_own_sequence(string from, string to)
    {
        var f = Arrange(from);
        var start = f.Clock.Now;
        await f.Evaluate();
        f.Advance(30); Evidence(f, from, 1);
        await f.Evaluate();
        var first = f.Store.Rows.Single(x => x.Kind == from);
        Assert.Equal(start, first.PendingSinceUtc);
        // 60 s after the first observation the position differs the other way.
        f.Advance(30); Evidence(f, to, 1);
        var switched = f.Clock.Now;
        Assert.Equal(switched + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        var second = f.Store.Rows.Single(x => x.Kind == to);
        Assert.Equal(("candidate", (DateTimeOffset?)switched), (second.State, second.PendingSinceUtc));
        Assert.Equal(("resolved", false, (string?)null), (first.State, first.Visible, first.PendingFingerprint));
        f.Advance(30); Evidence(f, to, 2);
        await f.Evaluate();
        Assert.Equal(("candidate", (DateTimeOffset?)switched), (second.State, second.PendingSinceUtc));
        f.Advance(30); await f.Evaluate();
        Assert.Equal(("open", switched), (second.State, second.OpenedAtUtc));
        var opened = Assert.Single(f.Events.Items);
        Assert.Equal(("discrepancy.opened", second.Id), (opened.Type, opened.Subject.DiscrepancyId));
        Assert.False(first.Visible);
    }

    [Theory] // Item 6c's rule: an observation resting on the snapshot and the book confirms only when both are the delay later.
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_excess_resting_on_the_position_confirms_only_when_its_snapshot_is_also_the_delay_later(bool wholePosition)
    {
        var f = new DiscrepancyFixture();
        if (wholePosition)
        {
            // A whole-position stop counts the open quantity only when the snapshot agrees: the stop beside it is the excess.
            f.Stop(o => o with { IsPositionTpsl = true, OriginalSize = 0, RemainingSize = 0 });
            f.AddOrder("stop-2", OrderLinkRole.Stop, .5m, 90);
            var index = f.VenueOrders.FindIndex(x => x.OrderId == "stop-2");
            f.VenueOrders[index] = f.VenueOrders[index] with { ReduceOnly = false };
        }
        else f.Stop(o => o with { RemainingSize = 2, ReduceOnly = false }); // The book alone shows it.
        var start = f.Clock.Now;
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.excess");
        Assert.Equal(start, row.PendingSinceUtc);
        // The next snapshot is read 41 s later, and its book 60 s later.
        f.Advance(41);
        var snapshot = f.Observation();
        f.Advance(19);
        await f.Service.EvaluateAsync(snapshot with { Read = snapshot.Read with { Orders = f.Observation().Read.Orders } }, default);
        Assert.Equal(wholePosition ? "candidate" : "open", row.State);
        f.Advance(19); await f.Evaluate();
        Assert.Equal(("open", start), (row.State, row.OpenedAtUtc));
    }

    /// <summary>The fixture with the kind's condition present, at evidence version 0.</summary>
    internal static DiscrepancyFixture Arrange(string kind)
    {
        var f = new DiscrepancyFixture();
        Evidence(f, kind, 0);
        return f;
    }

    /// <summary>
    /// Sets the kind's condition with evidence version <paramref name="version"/>; each version has other quantities, so
    /// another fingerprint. For the protection kinds the Play's open quantity (and the same venue position) is the
    /// version's <see cref="Value"/>, with no resting stop or with a stop of 2; for the position kinds the venue position is.
    /// </summary>
    internal static void Evidence(DiscrepancyFixture f, string kind, int version)
    {
        switch (kind)
        {
            case "protection.missing":
                f.VenueOrders.RemoveAll(x => x.OrderId == "stop");
                Quantity(f, Value(kind, version));
                break;
            case "protection.excess":
                Quantity(f, Value(kind, version));
                f.Stop(o => o with { RemainingSize = 2, ReduceOnly = false });
                break;
            default: f.Position = Value(kind, version); break;
        }
    }

    /// <summary>Clears the kind's condition, so the next fresh observation proves it absent.</summary>
    internal static void Clear(DiscrepancyFixture f, string kind)
    {
        var quantity = 1 + f.Store.Fills.Where(Scaled).Sum(x => x.Quantity);
        switch (kind)
        {
            case "protection.missing":
                f.VenueOrders.Add(new("stop", "BTC", "sell", "Stop Market", 90, 90, true, false, quantity, quantity, f.Clock.Now.AddMinutes(-15),
                    VenueOrderStatus.Open, "open", f.Clock.Now));
                break;
            case "protection.excess": f.Stop(o => o with { RemainingSize = quantity, ReduceOnly = true }); break;
            default: f.Position = 1; break;
        }
    }

    /// <summary>The quantity shown for evidence version <paramref name="version"/>.</summary>
    internal static string Latest(string kind, int version) => ExactAmount.From(Value(kind, version)).ToString();
    internal static string? Shown(string kind, JsonElement details) =>
        details.GetProperty(kind.StartsWith("position.", StringComparison.Ordinal) ? "venueQuantity" : "quantity").GetString();
    private static string Severity(string kind) => kind == "protection.missing" ? "critical" : "warning";

    private static decimal Value(string kind, int version) => kind switch
    {
        "position.short" => .5m + .1m * version,
        "position.excess" => 2 + .5m * version,
        _ => 1 + .2m * version
    };

    /// <summary>
    /// The Play's open quantity, and the same venue position, through entry fills on its linked entry order. A larger
    /// quantity only adds a fill, so persisted fixtures can follow it.
    /// </summary>
    private static void Quantity(DiscrepancyFixture f, decimal quantity)
    {
        var current = 1 + f.Store.Fills.Where(Scaled).Sum(x => x.Quantity);
        if (quantity < current) { f.Store.Fills.RemoveAll(Scaled); current = 1; }
        if (quantity > current) f.Fill("scale-" + f.Store.Fills.Count, "entry", quantity - current, f.Clock.Now.AddSeconds(-1));
        f.Position = quantity;
    }

    private static bool Scaled(Vessel.Domain.Workspace.ImportedFill fill) => fill.SourceFillId.StartsWith("scale-", StringComparison.Ordinal);

    /// <summary>
    /// Evidence the kind cannot decide: a history gap for every kind, or an incomplete read of what it rests on. An
    /// incomplete book still shows an oversized resting order (positive evidence), so the excess uses incomplete facts.
    /// </summary>
    private static void Unknown(DiscrepancyFixture f, string kind, string interruption, bool on)
    {
        if (interruption == "history-gap") { f.HistoryGap = on; return; }
        switch (kind)
        {
            case "protection.missing": f.OrdersComplete = !on; break;
            case "protection.excess": f.Store.Complete = !on; break;
            default: f.PositionsComplete = !on; break;
        }
    }
}

public sealed class MonitorDiscrepancyConditionKindsPostgresTests
{
    [PostgresTheory] // Through persisted records: confirms on the condition, opens once, shows the latest quantities.
    [InlineData("protection.missing")]
    [InlineData("protection.excess")]
    [InlineData("position.short")]
    [InlineData("position.excess")]
    public async Task Changing_quantities_confirm_once_with_the_latest_evidence(string kind)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = MonitorDiscrepancyConditionKindsTests.Arrange(kind);
        await database.Seed(f);
        var start = f.Clock.Now;
        async Task<MonitorEvaluationResult> Check() { await using var scope = database.Scope(f); return await scope.Evaluate(f.Observation()); }
        async Task Change(int version)
        {
            var count = f.Store.Fills.Count;
            MonitorDiscrepancyConditionKindsTests.Evidence(f, kind, version);
            Assert.True(f.Store.Fills.Count >= count);
            await using var scope = database.Scope(f);
            scope.Db.AddRange(f.Store.Fills.Skip(count));
            await scope.Db.SaveChangesAsync();
        }
        async Task<(MonitorDiscrepancyDto Row, string[] Events)> Read()
        {
            await using var read = database.Scope(f);
            var row = Assert.Single(await read.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
            var events = (await EventReads.ReadAsync(read.Db, 0, 100, default)).Events.Where(x => x.Subject.DiscrepancyId == row.Id).ToArray();
            if (events.Length > 0)
                Assert.Equal(MonitorDiscrepancyConditionKindsTests.Latest(kind, 2),
                    MonitorDiscrepancyConditionKindsTests.Shown(kind, events[0].Data.GetProperty("details")));
            return (row, events.Select(x => x.Type).ToArray());
        }

        await Check();
        f.Advance(30); await Change(1);
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await Check()).NextConfirmationAtUtc);
        f.Advance(30); await Change(2);
        Assert.False((await Check()).Incomplete);
        var (row, events) = await Read();
        Assert.Equal(("open", start, MonitorDiscrepancyConditionKindsTests.Latest(kind, 2)),
            (row.State, row.OpenedAtUtc, MonitorDiscrepancyConditionKindsTests.Shown(kind, row.Details)));
        Assert.Equal(["discrepancy.opened"], events);

        // Open: the details follow the evidence in place and nothing more is announced.
        f.Advance(60); await Change(3);
        await Check();
        (row, events) = await Read();
        Assert.Equal(("open", start, MonitorDiscrepancyConditionKindsTests.Latest(kind, 3)),
            (row.State, row.OpenedAtUtc, MonitorDiscrepancyConditionKindsTests.Shown(kind, row.Details)));
        Assert.Equal(["discrepancy.opened"], events);

        // The condition clears: resolved and announced, as before.
        f.Advance(60); MonitorDiscrepancyConditionKindsTests.Clear(f, kind);
        await Check();
        (row, events) = await Read();
        Assert.Equal("resolved", row.State);
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved"], events);
    }
}
