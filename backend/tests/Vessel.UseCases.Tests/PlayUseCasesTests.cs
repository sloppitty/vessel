using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class PlayDocumentUseCasesTests : PlayDocumentTestsValues
{

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
            (retargeted with { Entries = [retargeted.Entries[0] with { Price = "1E2" }] }, $"Entry 1 price: {PlayDocumentTestsValues.PlainDecimalRule}"),
            (retargeted with { BudgetOverride = "+100" }, $"Budget: {PlayDocumentTestsValues.PlainDecimalRule}"),
            (Stop("percent", "-6"), "Entry 1 stop 1: use a price for a stop beyond the entry; a percentage cannot be negative."),
            // The same text in another unit is a different value.
            (Stop("price", "-5"), $"Entry 1 stop 1: {PlayDocumentTestsValues.PlainDecimalRule}"),
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
