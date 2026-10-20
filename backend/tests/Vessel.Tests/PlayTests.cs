using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Evidence;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

internal static class TestPlays
{
    public static PlayPlanDocument Plan(string price = "100", string stop = "95") => new("long", "margin", "250", "3", null,
        [new PlanEntry("entry-1", "Entry 1", "#b9c9e4", "100", price, [new PlanExit("stop-1", "price", stop, "100")],
            [new PlanExit("target-1", "price", "110", "100")])],
        new PlanNotes("Breakout retest", "Close below 94", "", ""));

    public static Play Create(Account account, PerpetualInstrument? instrument) => new(Guid.NewGuid(), account, instrument,
        account.VenueId == "manual" ? InstrumentSource.Manual : InstrumentSource.Venue, PlayDocuments.Serialize(Plan()), DateTimeOffset.UnixEpoch);

    /// <summary>
    /// A plan an earlier server stored before plan numbers had to be plain decimals: an exponent entry price, a
    /// leading-dot size and a negative percentage stop (which then resolved to no stop).
    /// </summary>
    public static PlayPlanDocument LegacyPlan() => Plan("1e2") with
    {
        Size = ".5",
        Entries = [Plan("1e2").Entries[0] with { Stops = [new PlanExit("stop-1", "percent", "-5", "100")] }]
    };
}

/// <summary>The plan number list shared with the browser: <c>frontend/src/test/contracts/plan-numbers.json</c>.</summary>
internal static class PlanNumberFixture
{
    private static readonly JsonElement Root = Load();
    public static IReadOnlyList<string> Accepted => List("accepted");
    public static IReadOnlyList<string> Rejected => List("rejected");
    /// <summary>Refused texts that a stop or target in % refuses with the negative-percentage reason.</summary>
    public static IReadOnlyList<string> NegativePercent => List("negativePercent");

    private static JsonElement Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ContractFixtures.Path("plan-numbers.json")));
        return document.RootElement.Clone();
    }

    private static List<string> List(string name) => Root.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToList();
}

internal sealed class MemoryPlayStore(Guid owner) : IPlayStore
{
    public List<Account> Accounts { get; } = [];
    public List<Play> Plays { get; } = [];
    public List<PlayPlanRevision> Revisions { get; } = [];
    public List<PlayStatusChange> Changes { get; } = [];
    public List<PlayReviewRevision> Reviews { get; } = [];
    public List<PlayOrderLink> Links { get; } = [];
    public List<string> EvidenceKeys { get; } = [];
    public int Saves { get; private set; }
    // A plain test double for service tests; SQL view, search and scope semantics are covered against PostgreSQL.
    public Task<bool> ScopeExistsAsync(ListScope scope, CancellationToken ct) => Task.FromResult(scope.Kind switch
    {
        "account" => Accounts.Any(a => a.Id == scope.Id && a.OwnerId == owner),
        "portfolio" => Accounts.Any(a => a.PortfolioId == scope.Id && a.OwnerId == owner),
        _ => true
    });
    private IEnumerable<Play> Listed(PlayListView view, PlayListFilter filter) => Plays.Where(p => p.OwnerId == owner &&
        (filter.Scope is not { } scope || Accounts.Any(a => a.Id == p.AccountId && scope.Includes(a.Id, a.PortfolioId, a.IsEnabled))) &&
        (filter.Search is null || p.Title.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
            p.ContractId?.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) == true) && view switch
        {
            PlayListView.Drafts => p.Status == PlayStatus.Draft,
            PlayListView.Progress => p.Status is PlayStatus.Planned or PlayStatus.Paused or PlayStatus.Open,
            PlayListView.Review => p.Status is PlayStatus.Closed or PlayStatus.Cancelled && !(p.HasReview || p.ReviewedAtUtc != null),
            PlayListView.Reviewed => p.Status is PlayStatus.Closed or PlayStatus.Cancelled && (p.HasReview || p.ReviewedAtUtc != null),
            PlayListView.Closed => p.Status == PlayStatus.Closed,
            PlayListView.Cancelled => p.Status == PlayStatus.Cancelled,
            _ => true
        });
    public Task<PlayListPage> PageAsync(PlayListView view, PlayListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var total = Listed(view, filter).Count();
        page = ListPaging.Clamp(page, pageSize, total);
        return Task.FromResult(new PlayListPage(total, page, Rows(view, filter, ListPaging.Skip(page, pageSize), pageSize)));
    }
    private List<PlayListRow> Rows(PlayListView view, PlayListFilter filter, int skip, int take) =>
        Listed(view, filter).OrderByDescending(p => p.UpdatedAtUtc).ThenBy(p => p.Id).Skip(skip).Take(take).Select(p => new PlayListRow
        {
            Id = p.Id, Title = p.Title, Status = p.Status, AccountId = p.AccountId, VenueId = p.VenueId, ContractId = p.ContractId,
            InstrumentSource = p.InstrumentSource, Direction = p.Direction, PlanRevision = p.PlanRevision, Version = p.Version,
            CancelReason = p.CancelReason, CreatedAtUtc = p.CreatedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc, PlannedAtUtc = p.PlannedAtUtc,
            EndedAtUtc = p.EndedAtUtc, HasReview = p.HasReview, Origin = p.Origin, ReviewedAtUtc = p.ReviewedAtUtc
        }).ToList();
    public Task<List<PlayCountRow>> CountsAsync(PlayListFilter filter, CancellationToken ct) => Task.FromResult(Listed(PlayListView.All, filter)
        .GroupBy(p => (p.Status, Reviewed: p.HasReview || p.ReviewedAtUtc != null)).Select(g => new PlayCountRow(g.Key.Status, g.Key.Reviewed, g.Count())).ToList());
    public Task<Play?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Plays.SingleOrDefault(p => p.Id == id && p.OwnerId == owner));
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Task.FromResult(Accounts.SingleOrDefault(a => a.Id == id && a.OwnerId == owner));
    public Task<List<PlayPlanRevision>> RevisionsAsync(Guid playId, CancellationToken ct) => Task.FromResult(Revisions.Where(r => r.PlayId == playId).ToList());
    public Task<List<PlayStatusChange>> StatusChangesAsync(Guid playId, CancellationToken ct) => Task.FromResult(Changes.Where(c => c.PlayId == playId).ToList());
    public Task<List<PlayReviewRevision>> ReviewsAsync(Guid playId, CancellationToken ct) => Task.FromResult(Reviews.Where(c => c.PlayId == playId).ToList());
    /// <summary>Venue order IDs of linked orders, by internal ID; a link without one shows its internal ID.</summary>
    public Dictionary<Guid, string> ExternalOrderIds { get; } = [];
    public Task<List<LinkedPlanReference>> LinkedPlanReferencesAsync(Guid playId, CancellationToken ct) => Task.FromResult(Links
        .Where(l => l.PlayId == playId && l.State == OrderLinkState.Linked)
        .Select(l => new LinkedPlanReference(l, ExternalOrderIds.GetValueOrDefault(l.ImportedOrderId) ?? l.ImportedOrderId.ToString())).ToList());
    public void Add(Play play) => Plays.Add(play);
    public void Add(PlayPlanRevision revision) => Revisions.Add(revision);
    public void Add(PlayStatusChange change) => Changes.Add(change);
    public void Add(PlayReviewRevision revision) => Reviews.Add(revision);
    public HashSet<string> FilledEntries { get; } = [];
    public Task<IReadOnlySet<string>> FilledEntryIdsAsync(Guid playId, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(FilledEntries);
    /// <summary>Entry fills and manual entry executions, for every Play of this store.</summary>
    public List<EntryExecution> EntryExecutions { get; } = [];
    public Task<List<EntryExecution>> EntryExecutionsAsync(Guid playId, CancellationToken ct) => Task.FromResult(EntryExecutions.ToList());
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
    public Task<T> WithPlayLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct, Guid? destinationAccountId = null) => action();
    public Task<IReadOnlyList<string>> DeleteAsync(Play play, CancellationToken ct)
    {
        Plays.Remove(play);
        return Task.FromResult<IReadOnlyList<string>>(EvidenceKeys);
    }
}

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

public sealed class PlayDocumentTests
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
    /// Plan text on write (phase 0, 0.5a), from the list the browser checks too
    /// (<c>frontend/src/test/contracts/plan-numbers.json</c>, read by <c>numbers.test.ts</c>). Every number field
    /// keeps accepted text exactly and refuses the rest with 400.
    /// </summary>
    [Fact]
    public void Every_plan_number_field_follows_the_shared_fixture_list()
    {
        var failures = new List<string>();
        // A filled entry keeps the stop side check, and its 0 % rule, out of this grammar test.
        var filled = new HashSet<string> { "entry-1" };
        foreach (var text in PlanNumberFixture.Accepted)
            foreach (var (field, plan, read, _) in PlanNumberFields(text))
            {
                try
                {
                    if (read(PlayDocuments.Normalize(plan, executedEntries: filled)) != text) failures.Add($"{field} changed accepted {Show(text)}");
                }
                catch (ProblemException e) { failures.Add($"{field} refused accepted {Show(text)}: {e.Message}"); }
            }
        // A refusal names the field and gives the plain-decimal reason; only a negative plain decimal in a % field gets the sign reason.
        foreach (var text in PlanNumberFixture.Rejected)
            foreach (var (field, plan, _, refusal) in PlanNumberFields(text))
            {
                try { PlayDocuments.Normalize(plan, executedEntries: filled); failures.Add($"{field} accepted rejected {Show(text)}"); }
                catch (ProblemException e) when (e.StatusCode == 400 && e.Message == refusal) { }
                catch (ProblemException e) { failures.Add($"{field} refused {Show(text)} with {e.StatusCode} {Show(e.Message)}, not {Show(refusal)}"); }
            }
        Assert.Empty(failures);
        Assert.All(PlanNumberFixture.NegativePercent, text => Assert.Contains(text, PlanNumberFixture.Rejected));
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

    internal const string PlainDecimalRule =
        "use a plain decimal such as 100 or 0.5, with no sign, exponent or spaces, at most 64 characters and within decimal precision (28 decimal places).";

    private static string Show(string text) => JsonSerializer.Serialize(text);

    /// <summary>One plan per number field, holding <paramref name="text"/> there; unpriced entries and percent levels keep the side check out of the way.</summary>
    /// <summary>
    /// One plan per number field, holding <paramref name="text"/> there, with the message a refusal of it must carry.
    /// Unpriced entries and percent levels keep the side check out of the way.
    /// </summary>
    private static IEnumerable<(string Field, PlayPlanDocument Plan, Func<PlayPlanDocument, string?> Read, string Refusal)> PlanNumberFields(string text)
    {
        var plan = TestPlays.Plan();
        var entry = plan.Entries[0];
        PlayPlanDocument With(PlanEntry changed) => plan with { Entries = [changed] };
        PlanExit Exit(string id, string unit, string value, string share) => new(id, unit, value, share);
        string Plain(string field) => $"{field}: {PlainDecimalRule}";
        string Percent(string field, string kind) => !PlanNumberFixture.NegativePercent.Contains(text) ? Plain(field) : kind == "stop"
            ? $"{field}: use a price for a stop beyond the entry; a percentage cannot be negative."
            : $"{field}: a percentage cannot be negative; use a price instead.";
        yield return ("size", plan with { Size = text }, p => p.Size, Plain("Size"));
        yield return ("budget", plan with { BudgetOverride = text }, p => p.BudgetOverride, Plain("Budget"));
        yield return ("entry share", With(entry with { Share = text }), p => p.Entries[0].Share, Plain("Entry 1 share"));
        yield return ("entry price", With(entry with { Price = text, Stops = [Exit("stop-1", "percent", "5", "100")],
            Targets = [Exit("target-1", "percent", "10", "100")] }), p => p.Entries[0].Price, Plain("Entry 1 price"));
        yield return ("stop price", With(entry with { Price = "", Stops = [Exit("stop-1", "price", text, "100")] }),
            p => p.Entries[0].Stops[0].Value, Plain("Entry 1 stop 1"));
        yield return ("stop percent", With(entry with { Stops = [Exit("stop-1", "percent", text, "100")] }),
            p => p.Entries[0].Stops[0].Value, Percent("Entry 1 stop 1", "stop"));
        yield return ("stop share", With(entry with { Stops = [Exit("stop-1", "price", "95", text)] }),
            p => p.Entries[0].Stops[0].Share, Plain("Entry 1 stop 1 share"));
        yield return ("target price", With(entry with { Price = "", Targets = [Exit("target-1", "price", text, "100")] }),
            p => p.Entries[0].Targets[0].Value, Plain("Entry 1 target 1"));
        yield return ("target percent", With(entry with { Targets = [Exit("target-1", "percent", text, "100")] }),
            p => p.Entries[0].Targets[0].Value, Percent("Entry 1 target 1", "target"));
        yield return ("target share", With(entry with { Targets = [Exit("target-1", "price", "110", text)] }),
            p => p.Entries[0].Targets[0].Share, Plain("Entry 1 target 1 share"));
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

public sealed class PlayServiceTests
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemoryPlayStore store;
    private readonly MemoryObjectStore objects = new();
    private readonly PlayService service;
    private readonly Account venue;
    private readonly Account manual;

    public PlayServiceTests()
    {
        store = new MemoryPlayStore(owner);
        venue = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        manual = new Account(Guid.NewGuid(), owner, "manual", "Manual");
        store.Accounts.AddRange(venue, manual);
        service = new PlayService(store, objects, TimeProvider.System, TestUnitOfWork.Saving(store.SaveAsync));
    }

    private Task<PlayDto> Create(Account? account = null, string? instrument = "BTC", string source = "venue", PlayPlanDocument? plan = null) =>
        service.CreateAsync(new CreatePlayRequest((account ?? venue).Id, instrument, source, " Breakout ", plan ?? TestPlays.Plan()), default);

    private async Task<IReadOnlyList<PlaySummaryDto>> List() => (await service.ListAsync(null, null, null, null, null, default)).Plays;

    private UpdatePlayRequest Update(PlayDto play, PlayPlanDocument? plan = null, string? reason = null, string? instrument = null) =>
        new(play.Summary.Version, play.Summary.AccountId, instrument ?? play.Summary.Instrument, play.Summary.InstrumentSource,
            play.Summary.Title, plan ?? play.Plan, play.Drawings, play.Review, reason);

    [Theory]
    [InlineData("open", null, null, null, null)]
    [InlineData("ALL", null, null, null, null)]
    [InlineData(null, null, null, "0", null)]
    [InlineData(null, null, null, null, "101")]
    [InlineData(null, null, null, null, "1e2")]
    [InlineData(null, null, "portfolio:x", null, null)]
    [InlineData(null, null, "account", null, null)]
    public async Task Invalid_list_queries_are_400(string? view, string? q, string? scope, string? page, string? size) =>
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.ListAsync(view, q, scope, page, size, default))).StatusCode);

    [Fact]
    public async Task List_validates_search_and_scope_and_caps_unpaged_views()
    {
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.CountsAsync(new string('q', 101), null, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.CountsAsync(null, $"account:{Guid.NewGuid()}", default))).StatusCode);
        for (var i = 0; i < 201; i++) store.Plays.Add(TestPlays.Create(venue, new PerpetualInstrument("hyperliquid", "BTC")));
        var drafts = await service.ListAsync("drafts", "  ", null, "4", null, default);
        Assert.Equal(("drafts", 1, 200, 201, 200), (drafts.View, drafts.Page, drafts.PageSize, drafts.TotalCount, drafts.Plays.Count));
        var sized = await service.ListAsync("drafts", null, null, "4", "10", default);
        Assert.Equal((1, 10, 10), (sized.Page, sized.PageSize, sized.Plays.Count));
        var all = await service.ListAsync(null, null, null, "99", "100", default);
        Assert.Equal(("all", 3, 1), (all.View, all.Page, all.Plays.Count));
        Assert.Equal(201, (await service.CountsAsync(" btc ", $"account:{venue.Id}", default)).Drafts);
        venue.UpdateSettings(venue.Name, null, false);
        Assert.Equal(0, (await service.CountsAsync(null, "all", default)).All);
        Assert.Equal(201, (await service.CountsAsync(null, null, default)).All);
    }

    [Fact]
    public async Task Creates_a_draft_with_exact_plan_text()
    {
        var play = await Create(plan: TestPlays.Plan("0.00001230", "0.00001"));
        Assert.Equal(("draft", "Breakout", "BTC", "venue", "long", 0), (play.Summary.Status, play.Summary.Title,
            play.Summary.Instrument, play.Summary.InstrumentSource, play.Summary.Direction, play.Summary.PlanRevision));
        Assert.Equal("0.00001230", play.Plan.Entries[0].Price);
        Assert.Equal(JsonValueKind.Object, play.Drawings.ValueKind);
        Assert.Single(await List());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" \n\t ", false)]
    [InlineData("Waited for confirmation.", true)]
    public async Task Summaries_report_saved_review_presence_independently_of_status(string review, bool hasReview)
    {
        var play = await Create();
        play = await service.UpdateAsync(play.Summary.Id, Update(play) with { Review = review }, default);
        Assert.Equal(hasReview, play.Summary.HasReview);
        Assert.Equal("draft", play.Summary.Status);
        play = await service.ChangeStatusAsync(play.Summary.Id,
            new ChangePlayStatusRequest(play.Summary.Version, "cancelled", "missed"), default);
        Assert.Equal(hasReview, Assert.Single(await List()).HasReview);
        Assert.Equal(hasReview, (await service.GetAsync(play.Summary.Id, default)).Summary.HasReview);
        play = await service.UpdateAsync(play.Summary.Id, Update(play) with { Review = "" }, default);
        Assert.False(play.Summary.HasReview);
        Assert.Equal("cancelled", play.Summary.Status);
    }

    [Fact]
    public async Task Validates_accounts_and_instruments()
    {
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => Create(new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Foreign")))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Create(manual, "BTC", "venue"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Create(instrument: "dex:BTC"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Create(manual, " BTC", "manual"))).StatusCode);
        Assert.Equal("BTC-PERP", (await Create(manual, "BTC-PERP", "manual")).Summary.Instrument);
        Assert.Equal("manual", (await Create(venue, "Some label", "manual")).Summary.InstrumentSource);
        Assert.Null((await Create(instrument: null)).Summary.Instrument);
        venue.UpdateSettings(venue.Name, null, false);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Create())).StatusCode);
    }

    [Fact]
    public async Task Planning_then_revising_keeps_the_history()
    {
        var play = await Create(plan: TestPlays.Plan(""));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default))).StatusCode);
        play = await service.UpdateAsync(play.Summary.Id, Update(play, TestPlays.Plan("100")), default);
        Assert.Empty(store.Revisions);
        play = await service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);
        Assert.Equal(("planned", 1), (play.Summary.Status, play.Summary.PlanRevision));

        var moved = TestPlays.Plan(stop: "97");
        await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(play.Summary.Id, Update(play, moved), default));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Summary.Id, Update(play, instrument: "ETH"), default))).StatusCode);
        play = await service.UpdateAsync(play.Summary.Id, Update(play, moved, "Stop under the new swing low"), default);
        Assert.Equal(2, play.Summary.PlanRevision);

        var history = await service.HistoryAsync(play.Summary.Id, default);
        Assert.Equal(["Planned", "Stop under the new swing low"], history.Revisions.Select(r => r.Reason));
        Assert.Equal(["95", "97"], history.Revisions.Select(r => r.Plan.Entries[0].Stops[0].Value));
        Assert.Equal(("draft", "planned"), (history.StatusChanges.Single().From, history.StatusChanges.Single().To));
    }

    [Fact]
    public async Task Stale_versions_and_invalid_transitions_conflict()
    {
        var play = await Create();
        var stale = play.Summary.Version;
        play = await service.UpdateAsync(play.Summary.Id, Update(play) with { Title = "Renamed" }, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.UpdateAsync(play.Summary.Id, Update(play) with { ExpectedVersion = stale }, default))).StatusCode);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "paused"), default));
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "open"), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "cancelled", "bored"), default))).StatusCode);
        play = await service.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "cancelled", "changed-mind", "Not my setup"), default);
        Assert.Equal(("cancelled", "changed-mind"), (play.Summary.Status, play.Summary.CancelReason));
        var history = await service.HistoryAsync(play.Summary.Id, default);
        Assert.Equal(("changed-mind", "Not my setup"), (history.StatusChanges.Single().Reason, history.StatusChanges.Single().Note));
    }

    /// <summary>
    /// Strict on write, tolerant on read: a stored field whose text is unchanged still saves, so no earlier plan becomes
    /// unsaveable and nothing stored is rewritten. New or changed text must be a plain decimal.
    /// </summary>
    [Fact]
    public async Task Stored_plan_text_saves_unchanged_while_new_text_must_be_a_plain_decimal()
    {
        var legacy = TestPlays.LegacyPlan();
        var stored = PlayDocuments.Serialize(legacy);
        var play = new Play(Guid.NewGuid(), venue, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, stored, DateTimeOffset.UnixEpoch);
        store.Plays.Add(play);
        var dto = await service.GetAsync(play.Id, default);
        Assert.Equal(("1e2", ".5", "-5"), (dto.Plan.Entries[0].Price, dto.Plan.Size, dto.Plan.Entries[0].Stops[0].Value));

        dto = await service.UpdateAsync(play.Id, Update(dto) with { Title = "Renamed" }, default);
        Assert.Equal(("Renamed", stored), (dto.Summary.Title, play.Plan));

        // Other fields change around the stored text.
        var retargeted = legacy with { Entries = [legacy.Entries[0] with { Targets = [new PlanExit("target-1", "price", "120", "100")] }] };
        dto = await service.UpdateAsync(play.Id, Update(dto, retargeted), default);
        Assert.Equal(PlayDocuments.Serialize(retargeted), play.Plan);

        PlayPlanDocument Stop(string unit, string value) =>
            retargeted with { Entries = [retargeted.Entries[0] with { Stops = [new PlanExit("stop-1", unit, value, "100")] }] };
        foreach (var (plan, message) in new[]
        {
            // Another spelling of a stored value is new text.
            (retargeted with { Entries = [retargeted.Entries[0] with { Price = "1E2" }] }, $"Entry 1 price: {PlayDocumentTests.PlainDecimalRule}"),
            (retargeted with { BudgetOverride = "+100" }, $"Budget: {PlayDocumentTests.PlainDecimalRule}"),
            (Stop("percent", "-6"), "Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative."),
            // The same text in another unit is a different value.
            (Stop("price", "-5"), $"Entry 1 stop 1: {PlayDocumentTests.PlainDecimalRule}"),
        })
        {
            var refused = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAsync(play.Id, Update(dto, plan), default));
            Assert.Equal((400, message), (refused.StatusCode, refused.Message));
        }
        Assert.Equal(PlayDocuments.Serialize(retargeted), play.Plan);
        // The stored text belongs to that Play: a new Play cannot start with it.
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Create(plan: legacy))).StatusCode);
        // 0% is break-even, which needs a fill on the entry (October 20, 2026); this draft has none.
        var breakEven = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAsync(play.Id, Update(dto, Stop("percent", "0")), default));
        Assert.Equal((400, "Entry 1: a long stop goes below the entry price; 0 % (break-even) needs a fill on the entry."), (breakEven.StatusCode, breakEven.Message));
        // A plain value replaces the stored text.
        dto = await service.UpdateAsync(play.Id, Update(dto, Stop("percent", "5") with { Size = "0.5" }), default);
        Assert.Equal(("5", "0.5"), (dto.Plan.Entries[0].Stops[0].Value, dto.Plan.Size));
    }

    [Fact]
    public async Task Only_drafts_are_deleted_with_their_evidence_objects()
    {
        var draft = await Create();
        await objects.PutAsync("owner/play/a.png", new MemoryStream([1]), default);
        store.EvidenceKeys.Add("owner/play/a.png");
        await service.DeleteAsync(draft.Summary.Id, default);
        Assert.Empty(objects.Objects);
        Assert.Empty(await List());

        var planned = await Create();
        planned = await service.ChangeStatusAsync(planned.Summary.Id, new(planned.Summary.Version, "planned"), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.DeleteAsync(planned.Summary.Id, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.GetAsync(Guid.NewGuid(), default))).StatusCode);
    }
}

public sealed class PlayPostgresTests
{
    private static object Body(Guid accountId, string title = "Breakout", string price = "100") => new
    {
        accountId, instrument = "BTC", instrumentSource = "venue", title, plan = TestPlays.Plan(price),
        drawings = new Dictionary<string, object[]> { ["hyperliquid:BTC"] = [new { id = "d1", kind = "text" }] },
    };

    [PostgresFact]
    public async Task Saved_plays_round_trip_with_revisions_history_and_concurrency()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();

        var created = await client.PostAsJsonAsync("/api/plays", Body(account.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal("d1", play.Drawings.GetProperty("hyperliquid:BTC")[0].GetProperty("id").GetString());

        var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new { expectedVersion = play.Summary.Version, status = "planned" });
        play = (await planned.Content.ReadFromJsonAsync<PlayDto>())!;
        // Entity rules reach the owner as their status and exact message.
        var resumed = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new { expectedVersion = play.Summary.Version, status = "planned" });
        Assert.Equal((HttpStatusCode.Conflict, "Only a paused Play can be resumed."),
            (resumed.StatusCode, (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()));
        var longReason = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version, account.Id,
            "BTC", "venue", "Breakout", TestPlays.Plan(stop: "97"), play.Drawings, "", new string('r', PlayPlanRevision.MaxReasonLength + 1)));
        Assert.Equal((HttpStatusCode.BadRequest, "Reasons are limited to 2000 characters."),
            (longReason.StatusCode, (await longReason.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()));
        var update = new UpdatePlayRequest(play.Summary.Version, account.Id, "BTC", "venue", "Breakout", TestPlays.Plan(stop: "97"), play.Drawings, "", "Moved stop");
        var revised = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", update);
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        // The same expected version again is stale.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", update)).StatusCode);

        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{play.Summary.Id}/history"))!;
        Assert.Equal([1, 2], history.Revisions.Select(r => r.Number));
        Assert.Equal("97", history.Revisions[1].Plan.Entries[0].Stops[0].Value);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/plays/{play.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);

        // Another owner sees nothing and cannot read the history.
        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var other = foreign.AuthorizedClient();
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/plays")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/plays/{play.Summary.Id}/history")).StatusCode);

        await using var db2 = database.Context(owner);
        Assert.Equal("97", await db2.Database.SqlQueryRaw<string>(
            "SELECT \"Plan\"->'entries'->0->'stops'->0->>'value' AS \"Value\" FROM plays").SingleAsync());
    }

    [PostgresFact]
    public async Task Plan_numbers_are_plain_decimals_on_write_and_stored_text_still_saves_through_the_api()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var stored = PlayDocuments.Serialize(TestPlays.LegacyPlan());
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, stored, DateTimeOffset.UnixEpoch);
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); db.Plays.Add(play); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        async Task<string?> Detail(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();

        var created = await client.PostAsJsonAsync("/api/plays", Body(account.Id, price: "1e2"));
        Assert.Equal((HttpStatusCode.BadRequest, $"Entry 1 price: {PlayDocumentTests.PlainDecimalRule}"), (created.StatusCode, await Detail(created)));

        var dto = (await client.GetFromJsonAsync<PlayDto>($"/api/plays/{play.Id}"))!;
        Assert.Equal("1e2", dto.Plan.Entries[0].Price);
        var renamed = await client.PutAsJsonAsync($"/api/plays/{play.Id}", new UpdatePlayRequest(dto.Summary.Version, account.Id,
            "BTC", "venue", "Renamed", dto.Plan, dto.Drawings, "", null));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        dto = (await renamed.Content.ReadFromJsonAsync<PlayDto>())!;
        var changed = await client.PutAsJsonAsync($"/api/plays/{play.Id}", new UpdatePlayRequest(dto.Summary.Version, account.Id,
            "BTC", "venue", "Renamed", dto.Plan with { Size = ".25" }, dto.Drawings, "", null));
        Assert.Equal((HttpStatusCode.BadRequest, $"Size: {PlayDocumentTests.PlainDecimalRule}"), (changed.StatusCode, await Detail(changed)));

        await using var check = database.Context(owner);
        Assert.Equal(stored, PlayDocuments.Serialize(PlayDocuments.Read((await check.Plays.SingleAsync()).Plan)));
    }

    [PostgresFact]
    public async Task Deleting_a_draft_removes_its_evidence()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        var play = TestPlays.Create(account, new PerpetualInstrument("hyperliquid", "BTC"));
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            db.Plays.Add(play);
            db.Evidence.Add(new PlayEvidence(Guid.NewGuid(), play, "k/a.png", "image/png", 1, new string('a', 64), EvidenceSource.Upload, "", DateTimeOffset.UnixEpoch));
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(owner))
        {
            var store = new Vessel.Persistence.PlayStore(db, TestUnitOfWork.For(db));
            Assert.Equal(["k/a.png"], await TestUnitOfWork.For(db).ExecuteAsync(async () => await store.DeleteAsync((await store.FindAsync(play.Id, default))!, default), default));
        }
        await using (var db = database.Context(owner))
        {
            Assert.Empty(await db.Plays.ToListAsync());
            Assert.Empty(await db.Evidence.ToListAsync());
            db.Accounts.Remove(await db.Accounts.SingleAsync());
            await db.SaveChangesAsync();
        }
    }

    [PostgresFact]
    public async Task Migration_keeps_earlier_play_heads_as_planned()
    {
        await using var database = await CoreDatabase.CreateAsync("EvidenceMarkup");
        var owner = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            var account = Guid.NewGuid();
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO accounts ("Id", "OwnerId", "VenueId", "Name", "SyncStatus") VALUES ('{account}', '{owner}', 'manual', 'A', 'manual');
                INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "Status") VALUES ('{Guid.NewGuid()}', '{owner}', '{account}', 'manual', 'BTC', 'Active');
                """, connection);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var db = database.Context(owner))
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync();
        await using (var db = database.Context(owner))
        {
            var play = await db.Plays.SingleAsync();
            Assert.Equal((PlayStatus.Planned, InstrumentSource.Manual, 1L, "{}"), (play.Status, play.InstrumentSource, play.Version, play.Drawings));
            Assert.Equal("1", PlayDocuments.Read(play.Plan).Leverage);
        }
    }
}
