using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PlayDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T10:00:00Z");
    private readonly Account account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Trading");

    [Fact]
    public void Draft_edits_create_no_revisions_and_planning_records_the_first()
    {
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        Assert.Null(play.ChangePlan(PlayDocuments.Serialize(TestPlays.Plan("101")), null, Now));
        Assert.Equal(0, play.PlanRevision);
        var (change, revision) = play.MarkPlanned(Now);
        Assert.Equal((PlayStatus.Draft, PlayStatus.Planned), (change.From, change.To));
        Assert.Equal((1, "Planned", PlayStatus.Planned), (revision.Number, revision.Reason, revision.Status));
        Assert.Contains("\"price\":\"101\"", revision.Plan);
        Assert.Equal(Now, play.PlannedAtUtc);
    }

    [Fact]
    public void Planned_plan_changes_need_a_reason_and_append_revisions()
    {
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        play.MarkPlanned(Now);
        var moved = PlayDocuments.Serialize(TestPlays.Plan(stop: "97"));
        Assert.Throws<DomainRuleException>(() => play.ChangePlan(moved, " ", Now));
        var revision = play.ChangePlan(moved, "  Tightened the stop under the new low ", Now)!;
        Assert.Equal((2, "Tightened the stop under the new low"), (revision.Number, revision.Reason));
        Assert.Null(play.ChangePlan(moved, null, Now));
        play.Annotate("Renamed", "{}", "", Now);
        Assert.Equal(2, play.PlanRevision);
    }

    [Fact]
    public void Account_and_instrument_are_fixed_after_planning()
    {
        var play = TestPlays.Create(account, null);
        Assert.Throws<DomainRuleException>(() => play.MarkPlanned(Now));
        play.MoveTo(account, new PerpetualInstrument("hyperliquid", "ETH"), InstrumentSource.Venue, Now);
        play.MarkPlanned(Now);
        Assert.Throws<DomainRuleException>(() => play.MoveTo(account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, Now));
        Assert.Throws<ArgumentException>(() => TestPlays.Create(account, new PerpetualInstrument("manual", "BTC")));
    }

    [Fact]
    public void Pause_resume_and_cancel_follow_the_lifecycle()
    {
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        Assert.Throws<DomainRuleException>(() => play.Pause(Now));
        play.MarkPlanned(Now);
        play.Pause(Now);
        Assert.Throws<DomainRuleException>(() => play.MarkPlanned(Now));
        Assert.Equal(PlayStatus.Planned, play.Resume(Now).To);
        var cancelled = play.Cancel(CancelReason.Missed, " Price ran away ", Now);
        Assert.Equal((PlayStatus.Cancelled, CancelReason.Missed, "Price ran away"), (play.Status, cancelled.Reason, cancelled.Note));
        Assert.Equal(Now, play.EndedAtUtc);
        Assert.Throws<DomainRuleException>(() => play.Resume(Now));
        Assert.Throws<DomainRuleException>(() => play.ChangePlan(PlayDocuments.Serialize(TestPlays.Plan("1")), "late", Now));
        Assert.Throws<DomainRuleException>(() => play.Annotate("New title", "{}", "", Now));
        play.Annotate(play.Title, play.Drawings, "Should have waited for the retest.", Now);
        Assert.Equal("Should have waited for the retest.", play.Review);
    }

    [Fact]
    public void Every_change_moves_the_version()
    {
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        var versions = new List<long> { play.Version };
        play.Annotate("Title", "{}", "", Now); versions.Add(play.Version);
        play.Annotate("Title", "{}", "", Now); versions.Add(play.Version);
        play.MarkPlanned(Now); versions.Add(play.Version);
        Assert.Equal([1, 2, 2, 3], versions);
    }
}

public sealed class PlayDocumentTests : PlayDocumentTestsValues
{
    [Fact]
    public void Plans_keep_exact_numbers_and_reject_malformed_input()
    {
        var plan = PlayDocuments.Normalize(TestPlays.Plan("0.000012340", "0.00001"));
        Assert.Equal("0.000012340", plan.Entries[0].Price);
        Assert.Equal(PlayDocuments.Serialize(plan), PlayDocuments.Serialize(PlayDocuments.Read(PlayDocuments.Serialize(plan))));
        Assert.Equal("", PlayDocuments.Normalize(TestPlays.Plan("")).Entries[0].Price);
        foreach (var invalid in new[]
        {
            TestPlays.Plan("12abc"), TestPlays.Plan() with { Leverage = "2.5" }, TestPlays.Plan() with { Leverage = "101" },
            TestPlays.Plan() with { Direction = "up" }, TestPlays.Plan() with { Entries = [] },
            TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0], TestPlays.Plan().Entries[0]] },
            TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "pips", "1", "100")] }] },
            TestPlays.Plan() with { Notes = new PlanNotes(new string('x', 20001), "", "", "") },
            TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with { Stops = Enumerable.Range(0, 11).Select(i => new PlanExit($"s{i}", "price", "90", "")).ToList() }] },
            // Stop and target IDs share one namespace with entries.
            TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("target-1", "price", "90", "100")] }] },
        })
            Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(invalid)).StatusCode);
    }

    [Fact]
    public void Price_stops_and_targets_must_sit_on_their_side_of_the_entry()
    {
        var reversed = TestPlays.Plan(stop: "105");
        Assert.Equal("Entry 1: a long stop goes below the entry price.",
            Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(reversed)).Message);
        Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(TestPlays.Plan(stop: "100")));
        // The same levels describe a short with the stop and target swapped.
        var entry = reversed.Entries[0];
        PlayDocuments.Normalize(reversed with { Direction = "short", Entries = [entry with { Targets = [new PlanExit("target-1", "price", "90", "100")] }] });
        // Percent levels and unpriced entries are not checked.
        PlayDocuments.Normalize(TestPlays.Plan("") with { Direction = "short" });
        PlayDocuments.Normalize(TestPlays.Plan() with { Direction = "short", Entries = [entry with {
            Stops = [new PlanExit("stop-1", "percent", "5", "100")], Targets = [new PlanExit("target-1", "percent", "10", "100")] }] });
    }

    [Fact]
    public void Planning_requires_a_priced_entry()
    {
        Assert.Equal(409, Assert.Throws<ProblemException>(() => PlayDocuments.RequirePlannable(TestPlays.Plan(""))).StatusCode);
        // Reads stay tolerant: a stored plan from before the plain-decimal rule still plans.
        PlayDocuments.RequirePlannable(TestPlays.Plan("1e-5"));
    }

    /// <summary>
    /// Review probe <c>verify-opus-dotnet/More4.cs</c> <c>Grammar</c>: the server saved these and the matcher read them
    /// (<c>1e2</c> as 100, <c>.5</c> as 0.5), while the browser treated them as missing. Both now refuse them on write.
    /// </summary>
    [Theory]
    [InlineData("1e2")]
    [InlineData("100.")]
    [InlineData(".5")]
    [InlineData("+100")]
    [InlineData("-1")]
    [InlineData("1\n")]
    public void Review_probe_grammar_text_is_refused_on_write(string text)
    {
        var plan = TestPlays.Plan(text, stop: "") with
        {
            Entries = [TestPlays.Plan(text, stop: "").Entries[0] with { Targets = [new PlanExit("target-1", "price", "", "100")] }]
        };
        var refused = Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(plan));
        Assert.Equal((400, $"Entry 1 price: {PlainDecimalRule}"), (refused.StatusCode, refused.Message));
    }

    [Fact]
    public void Negative_percentages_point_to_a_price_and_zero_percent_is_break_even_after_a_fill()
    {
        PlayPlanDocument Percent(string stop, string target) => TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with
            {
                Stops = [new PlanExit("stop-1", "percent", stop, "100")], Targets = [new PlanExit("target-1", "percent", target, "100")]
            }]
        };
        Assert.Equal("Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative.",
            Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Percent("-5", "10"))).Message);
        Assert.Equal("Entry 1 target 1: a percentage cannot be negative; use a price instead.",
            Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Percent("5", "-10"))).Message);
        // The grammar decides first, as in the browser: a minus sign before an exponent or zero gets the plain-decimal reason.
        foreach (var text in new[] { "-1e30", "-1e-30", "-1e1", "-0" })
            Assert.Equal($"Entry 1 stop 1: {PlainDecimalRule}", Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Percent(text, "10"))).Message);
        // 0 % is break-even, which needs a fill on the entry (October 20, 2026); it resolves from the planned entry.
        Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Percent("0", "10"))).StatusCode);
        var breakEven = PlayDocuments.Normalize(Percent("0", "10"), executedEntries: new HashSet<string> { "entry-1" });
        Assert.Equal("0", breakEven.Entries[0].Stops[0].Value);
        Assert.Equal(100m, ExecutionMatcher.Levels(breakEven).Single(l => l.Role == OrderLinkRole.Stop).Price);
    }

    /// <summary>
    /// The owner's decision of October 20, 2026: a 0 % stop is break-even, which needs a fill on the entry. Before any fill
    /// it is refused like a price stop at the entry, priced entry or not; after one it saves and resolves from the planned
    /// entry. A stop kept unchanged from the stored plan still saves. A blank stop is not set, so it is no stop and never
    /// refused; 0 % targets are outside this rule.
    /// </summary>
    [Theory]
    [InlineData("long", "0")]
    [InlineData("long", "0.00")]
    [InlineData("short", "0")]
    [InlineData("short", "00")]
    public void A_zero_percent_stop_needs_a_filled_entry(string direction, string zero)
    {
        PlayPlanDocument Plan(string stop, string price = "100", string target = "10") => TestPlays.Plan() with
        {
            Direction = direction,
            Entries = [TestPlays.Plan().Entries[0] with
            {
                Price = price, Stops = [new PlanExit("stop-1", "percent", stop, "100")], Targets = [new PlanExit("target-1", "percent", target, "100")]
            }]
        };
        var message = $"Entry 1: a {direction} stop goes {(direction == "long" ? "below" : "above")} the entry price; 0 % (break-even) needs a fill on the entry.";
        foreach (var price in new[] { "100", "" })
        {
            var refused = Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Plan(zero, price)));
            Assert.Equal((400, message), (refused.StatusCode, refused.Message));
        }
        Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Plan(zero), executedEntries: new HashSet<string> { "entry-2" }));
        // Any fill on the entry allows it, and it still resolves from the planned entry.
        var filled = PlayDocuments.Normalize(Plan(zero), executedEntries: new HashSet<string> { "entry-1" });
        Assert.Equal((zero, 100m), (filled.Entries[0].Stops[0].Value, ExecutionMatcher.Levels(filled).Single(l => l.Role == OrderLinkRole.Stop).Price));
        // Kept unchanged from the stored plan (its fill since unlinked), it saves; in the other direction it is checked again.
        Assert.Equal(zero, PlayDocuments.Normalize(Plan(zero), Plan(zero)).Entries[0].Stops[0].Value);
        Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Plan(zero) with { Direction = direction == "long" ? "short" : "long" }, Plan(zero)));
        // Not set is no stop, and a 0 % target is not this rule's business.
        Assert.Equal("", PlayDocuments.Normalize(Plan("")).Entries[0].Stops[0].Value);
        Assert.Equal("0", PlayDocuments.Normalize(Plan("5", target: "0")).Entries[0].Targets[0].Value);
    }

    /// <summary>
    /// The server reads stored % stop text with decimal, which keeps 28 places and rounds half to even on the exact digits:
    /// a stored <c>5e-29</c> is zero, so once its entry price changes (no longer kept) it is refused as a 0 % stop before a
    /// fill. Just above the midpoint it is not zero. The browser reads it the same way (<c>numbers.ts</c>).
    /// </summary>
    [Theory]
    [InlineData("5e-29", true)]
    [InlineData("0.00000000000000000000000000005", true)]
    [InlineData("4.9999999999999999999999e-29", true)]
    [InlineData("5.0000000000000000000001e-29", false)]
    [InlineData("6e-29", false)]
    public void A_stored_percent_stop_that_decimal_reads_as_zero_is_a_zero_percent_stop(string text, bool zero)
    {
        PlayPlanDocument Plan(string price) => TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with
            {
                Price = price, Stops = [new PlanExit("stop-1", "percent", text, "100")], Targets = [new PlanExit("target-1", "percent", "10", "100")]
            }]
        };
        var stored = Plan("100");
        // Unchanged, the stored stop is kept and saves.
        Assert.Equal(text, PlayDocuments.Normalize(Plan("100"), stored).Entries[0].Stops[0].Value);
        if (zero)
            Assert.Equal("Entry 1: a long stop goes below the entry price; 0 % (break-even) needs a fill on the entry.",
                Assert.Throws<ProblemException>(() => PlayDocuments.Normalize(Plan("101"), stored)).Message);
        else Assert.Equal(text, PlayDocuments.Normalize(Plan("101"), stored).Entries[0].Stops[0].Value);
    }

    [Fact]
    public void Drawings_are_bounded_objects_of_lists()
    {
        Assert.Equal("{}", PlayDocuments.NormalizeDrawings(null));
        Assert.Equal("""{"hyperliquid:BTC":[{"id":"a"}]}""",
            PlayDocuments.NormalizeDrawings(JsonDocument.Parse("""{ "hyperliquid:BTC": [ { "id": "a" } ] }""").RootElement));
        foreach (var invalid in new[] { "[]", """{"a":{}}""", """{"a":[1]}""", """{"":[]}""" })
            Assert.Throws<ProblemException>(() => PlayDocuments.NormalizeDrawings(JsonDocument.Parse(invalid).RootElement));
        var many = "{\"a\":[" + string.Join(',', Enumerable.Repeat("{}", 201)) + "]}";
        Assert.Throws<ProblemException>(() => PlayDocuments.NormalizeDrawings(JsonDocument.Parse(many).RootElement));
    }

    [Fact]
    public void Read_drawings_outlive_the_parsed_document()
    {
        const string stored = """{"hyperliquid:BTC":[{"id":"a","points":[{"time":1,"price":"0.000012340"}]}]}""";
        var drawings = PlayDocuments.ReadDrawings(stored);
        Assert.Equal(stored, JsonSerializer.Serialize(drawings));
        Assert.Equal("0.000012340", drawings.GetProperty("hyperliquid:BTC")[0].GetProperty("points")[0].GetProperty("price").GetString());
        Assert.Equal(JsonValueKind.Object, PlayDocuments.ReadDrawings("{}").ValueKind);
        Assert.ThrowsAny<JsonException>(() => PlayDocuments.ReadDrawings("{"));
    }
}
