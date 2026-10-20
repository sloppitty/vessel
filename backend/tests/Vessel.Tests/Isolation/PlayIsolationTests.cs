using System.Net;
using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;

namespace Vessel.Tests.Isolation;

/// <summary>Plays, revisions, status, history, order links and the manual diary across two owners.</summary>
public abstract class PlayIsolationCases(IsolationHost api)
{
    private static readonly JsonElement NoDrawings = JsonDocument.Parse("{}").RootElement.Clone();

    private static async Task<JsonElement> Draft(Owner owner, Guid account, string title, string source = "venue") =>
        await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", source, title, TestPlays.Plan()), HttpStatusCode.Created);

    private static async Task<JsonElement> Planned(Owner owner, Guid account, string title, string source = "venue")
    {
        var draft = await Draft(owner, account, title, source);
        return await owner.Post($"/api/plays/{Id(draft)}/status", new ChangePlayStatusRequest(Version(draft), "planned"));
    }

    private static Guid Id(JsonElement play) => play.GetProperty("summary").GetProperty("id").GetGuid();
    private static long Version(JsonElement play) => play.GetProperty("summary").GetProperty("version").GetInt64();

    private static UpdatePlayRequest Update(JsonElement play, Guid account, string title, string source = "venue") =>
        new(Version(play), account, "BTC", source, title, TestPlays.Plan(price: "101"), NoDrawings, "", "Moved entry");

    [PostgresFact]
    [Covers("GET", "/api/plays/{id:guid}")]
    [Covers("POST", "/api/plays")]
    [Covers("PUT", "/api/plays/{id:guid}")]
    [Covers("POST", "/api/plays/{id:guid}/status")]
    [Covers("GET", "/api/plays/{id:guid}/history")]
    [Covers("DELETE", "/api/plays/{id:guid}")]
    public async Task Plays_of_another_owner_answer_like_unknown_ones_and_stay_unchanged()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue-a"))).GetProperty("id").GetGuid();
        var title = Isolated.Marker("play-a");
        var planned = await Planned(a, aAccount, title);
        var play = Id(planned);
        // Positive control: a revision with a reason, then history and detail reads.
        var revised = await a.Send(HttpMethod.Put, $"/api/plays/{play}", Update(planned, aAccount, title));
        Assert.Equal(2, revised.GetProperty("summary").GetProperty("planRevision").GetInt32());
        Assert.Equal(2, (await a.GetJson($"/api/plays/{play}/history")).GetProperty("revisions").GetArrayLength());
        var draft = Id(await Draft(a, aAccount, Isolated.Marker("draft-a")));
        var paths = new[] { $"/api/plays/{play}", $"/api/plays/{play}/history", $"/api/plays/{draft}", "/api/plays?view=all", "/api/plays/counts" };
        var state = await Snapshot.Of(a, paths);

        var bAccount = (await b.ManualAccount(Isolated.Marker("manual-b"))).GetProperty("id").GetGuid();
        var own = await Draft(b, bAccount, Isolated.Marker("draft-b"), "manual");
        foreach (var target in new[] { play, draft })
        {
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}"), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/history"), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{id}",
                _ => new UpdatePlayRequest(Version(revised), bAccount, "BTC", "manual", "taken-by-b", TestPlays.Plan(), NoDrawings, "", "Taken")),
                PlayNotFound));
            foreach (var status in new[] { "planned", "paused", "cancelled" })
                await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/status",
                    _ => new ChangePlayStatusRequest(Version(revised), status, status == "cancelled" ? "missed" : null)), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Delete, id => $"/api/plays/{id}"), PlayNotFound));
        }

        // A foreign account in a create or update body answers like an unknown account and stores nothing.
        var both = await Snapshot.Of((a, paths), (b, ["/api/plays?view=all", "/api/plays/counts", $"/api/plays/{Id(own)}"]));
        await both.After(() => Isolated.SameAsUnknown(b, aAccount, Isolated.Request(HttpMethod.Post, _ => "/api/plays",
            id => new CreatePlayRequest(id, "BTC", "venue", "into-foreign-account", TestPlays.Plan()))));
        await both.After(() => Isolated.SameAsUnknown(b, aAccount, Isolated.Request(HttpMethod.Put, _ => $"/api/plays/{Id(own)}",
            id => Update(own, id, "moved-to-foreign-account"))));
        // A can still change its own Plays with the versions it held: nothing consumed them.
        await a.Post($"/api/plays/{play}/status", new ChangePlayStatusRequest(Version(revised), "paused"));
        await a.Send(HttpMethod.Delete, $"/api/plays/{draft}", expected: HttpStatusCode.NoContent);
    }

    [PostgresFact]
    [Covers("GET", "/api/plays")]
    [Covers("GET", "/api/plays/counts")]
    public async Task Play_lists_counts_searches_and_scopes_never_include_the_other_owner()
    {
        var (a, b) = await api.Owners();
        var portfolio = await a.Portfolio(Isolated.Marker("portfolio-a"));
        var aAccount = (await a.ManualAccount(Isolated.Marker("manual-a"), portfolio)).GetProperty("id").GetGuid();
        var shared = Isolated.Marker("shared-title");
        var aPlay = Id(await Planned(a, aAccount, shared + " a", "manual"));
        var aDraft = Id(await Draft(a, aAccount, shared + " a draft", "manual"));
        var bAccount = (await b.ManualAccount(Isolated.Marker("manual-b"))).GetProperty("id").GetGuid();
        var bPlay = Id(await Draft(b, bAccount, shared + " b", "manual"));

        var search = Uri.EscapeDataString(shared);
        foreach (var (owner, mine, theirs) in new[] { (a, new[] { aPlay, aDraft }, new[] { bPlay }), (b, new[] { bPlay }, new[] { aPlay, aDraft }) })
        {
            foreach (var view in new[] { "drafts", "progress", "review", "reviewed", "closed", "cancelled", "all" })
            {
                var text = await owner.GetText($"/api/plays?view={view}&q={search}");
                Isolated.NoLeak(text, [.. theirs.Cast<object>()]);
            }
            var all = await owner.GetJson($"/api/plays?view=all&q={search}");
            Assert.Equal(mine.Order(), all.GetProperty("plays").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).Order());
            Assert.Equal(mine.Length, all.GetProperty("totalCount").GetInt32());
            Assert.Equal(mine.Length, (await owner.GetJson($"/api/plays/counts?q={search}")).GetProperty("all").GetInt32());
            Assert.Equal(mine.Length, (await owner.GetJson("/api/plays/counts")).GetProperty("all").GetInt32());
        }
        // A foreign account or portfolio scope answers like an unknown one.
        foreach (var path in new Func<Guid, string>[] { id => $"/api/plays?view=all&scope=account:{id}", id => $"/api/plays/counts?scope=account:{id}" })
            await Isolated.NotFound(b, aAccount, Isolated.Request(HttpMethod.Get, path));
        foreach (var path in new Func<Guid, string>[] { id => $"/api/plays?view=all&scope=portfolio:{id}", id => $"/api/plays/counts?scope=portfolio:{id}" })
            await Isolated.NotFound(b, portfolio, Isolated.Request(HttpMethod.Get, path));
        Assert.Equal(1, (await b.GetJson("/api/plays/counts?scope=unassigned")).GetProperty("all").GetInt32());
    }

    [PostgresFact]
    [Covers("GET", "/api/plays/{id:guid}/execution")]
    [Covers("POST", "/api/plays/{id:guid}/execution/check")]
    [Covers("POST", "/api/plays/{id:guid}/execution/links")]
    [Covers("DELETE", "/api/plays/{id:guid}/execution/links/{linkId:guid}")]
    [Covers("POST", "/api/plays/{id:guid}/execution/links/{linkId:guid}/rebind")]
    [Covers("POST", "/api/plays/{id:guid}/unresolved")]
    [Covers("POST", "/api/plays/{id:guid}/return-to-open")]
    public async Task Order_links_of_another_owner_answer_like_unknown_ones_and_stay_unchanged()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("venue-a"))).GetProperty("id").GetGuid();
        var play = Id(await Planned(a, aAccount, Isolated.Marker("linked-a")));
        // Positive control: A links its retained entry order explicitly.
        var execution = await a.Post($"/api/plays/{play}/execution/links", new LinkOrderRequest("entry", "entry", "entry-1"));
        var link = execution.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("order").GetProperty("orderId").GetString() == "entry")
            .GetProperty("id").GetGuid();
        var paths = new[] { $"/api/plays/{play}", $"/api/plays/{play}/execution" };

        // B follows the same address, so the same native order IDs exist in its own account too.
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("venue-b"))).GetProperty("id").GetGuid();
        var bPlanned = await Planned(b, bAccount, Isolated.Marker("linked-b"));
        var bPlay = Id(bPlanned);
        var bPaths = new[] { $"/api/plays/{bPlay}", $"/api/plays/{bPlay}/execution" };
        // Past the cooldown both refreshes set, before any check: a foreign check that reached A's account would be
        // admitted and change A's monitor state, not hide behind a cooldown the positive control has escaped.
        var now = api.Clock.Now;
        api.Clock.Now = now.AddMinutes(2);
        // A worker is alive at that clock, so the positive controls are admitted rather than refused as offline.
        await api.MonitorOnline();
        try
        {
            var state = await Snapshot.Of((a, paths), (b, bPaths));
            Isolated.NoLeak(await Isolated.State(b, bPaths), play, link, aAccount);
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/execution"), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/execution/check"), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/execution/links",
                _ => new LinkOrderRequest("exit", "exit")), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Delete, id => $"/api/plays/{id}/execution/links/{link}"),
                PlayNotFound));
            // A's link ID under B's own Play reaches the link lookup: it unlinks nothing.
            await state.After(() => Isolated.NotFound(b, link, Isolated.Request(HttpMethod.Delete, id => $"/api/plays/{bPlay}/execution/links/{id}"),
                "Link not found."));
            // A rebind of A's link answers like an unknown Play, and under B's own current Play like an unknown link.
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/execution/links/{link}/rebind",
                _ => new RebindLevelRequest(1, "entry", "100", "Moved at the venue")), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, link, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{bPlay}/execution/links/{id}/rebind",
                _ => new RebindLevelRequest(Version(bPlanned), "entry", "100", "Moved at the venue")), "Linked stop or target not found."));
            // Declaring A's Play unresolved or returning it to Open answers like an unknown Play (phase 1, 1.3a).
            var aPlay = await a.GetJson($"/api/plays/{play}");
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/unresolved",
                _ => new DeclareUnresolvedRequest(Version(aPlay), "other", "taken-by-b")), PlayNotFound));
            await state.After(() => Isolated.NotFound(b, play, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/return-to-open",
                _ => new ReturnToOpenRequest(Version(aPlay), "taken-by-b")), PlayNotFound));

            // B links the same native order ID in its own account: a separate link that leaves A's untouched.
            var aState = await Snapshot.Of(a, paths);
            var bExecution = await b.Post($"/api/plays/{bPlay}/execution/links", new LinkOrderRequest("entry", "entry", "entry-1"));
            var bLink = bExecution.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("order").GetProperty("orderId").GetString() == "entry")
                .GetProperty("id").GetGuid();
            Assert.NotEqual(link, bLink);
            Isolated.NoLeak(bExecution.GetRawText(), play, link, aAccount);
            await b.Send(HttpMethod.Delete, $"/api/plays/{bPlay}/execution/links/{bLink}");
            await aState.Unchanged();
            // Positive controls: A checks its own Play and unlinks its own link afterwards.
            var check = await a.Post($"/api/plays/{play}/execution/check", expected: HttpStatusCode.Accepted);
            Assert.Equal(("pending", aAccount), (check.GetProperty("monitor").GetProperty("manualState").GetString(),
                check.GetProperty("monitor").GetProperty("accountId").GetGuid()));
            await a.Send(HttpMethod.Delete, $"/api/plays/{play}/execution/links/{link}");
            // Positive control: A's own rebind reaches its Play, whose version moved on: a conflict, not an unknown Play.
            await a.Post($"/api/plays/{play}/execution/links/{link}/rebind", new RebindLevelRequest(0, "entry", "100", "Moved at the venue"),
                HttpStatusCode.Conflict);
            // Positive controls: A's own declaration and return reach its Play: a stale version is a conflict, not an unknown Play.
            await a.Post($"/api/plays/{play}/unresolved", new DeclareUnresolvedRequest(0, "other", "Stuck."), HttpStatusCode.Conflict);
            await a.Post($"/api/plays/{play}/return-to-open", new ReturnToOpenRequest(0, "Back."), HttpStatusCode.Conflict);
        }
        finally { api.Clock.Now = now; }
    }

    private const string PlayNotFound = "Play not found.";

    [PostgresFact]
    [Covers("GET", "/api/plays/{id:guid}/manual-executions")]
    [Covers("POST", "/api/plays/{id:guid}/manual-executions")]
    [Covers("PUT", "/api/plays/{id:guid}/manual-executions/{executionId:guid}")]
    [Covers("POST", "/api/plays/{id:guid}/manual-executions/corrections")]
    public async Task Manual_diary_of_another_owner_answers_like_unknown_and_request_ids_are_per_owner()
    {
        var (a, b) = await api.Owners();
        var aAccount = (await a.ManualAccount(Isolated.Marker("manual-a"))).GetProperty("id").GetGuid();
        var aPlanned = await Planned(a, aAccount, Isolated.Marker("diary-a"), "manual");
        var aPlay = Id(aPlanned);
        var requestId = Guid.NewGuid();
        var entry = Fact(requestId, Version(aPlanned), "1", "100");
        var recorded = await a.Post($"/api/plays/{aPlay}/manual-executions", entry);
        var execution = Assert.Single(recorded.GetProperty("records").EnumerateArray()).GetProperty("id").GetGuid();
        // A corrects its own execution, so its correction history holds more than the original record.
        var aCorrected = await a.Send(HttpMethod.Put, $"/api/plays/{aPlay}/manual-executions/{execution}",
            Fact(Guid.NewGuid(), Version(recorded.GetProperty("play")), "1", "101") with { Reason = "Broker price" });
        Assert.Equal(2, aCorrected.GetProperty("history").GetArrayLength());
        var aVersion = Version(aCorrected.GetProperty("play"));
        var paths = new[] { $"/api/plays/{aPlay}/manual-executions", $"/api/plays/{aPlay}", $"/api/plays/{aPlay}/history" };
        var state = await Snapshot.Of(a, paths);

        var bAccount = (await b.ManualAccount(Isolated.Marker("manual-b"))).GetProperty("id").GetGuid();
        var bPlanned = await Planned(b, bAccount, Isolated.Marker("diary-b"), "manual");
        var bPlay = Id(bPlanned);
        await state.After(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/manual-executions"), PlayNotFound));
        await state.After(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/manual-executions",
            _ => Fact(Guid.NewGuid(), aVersion, "2", "90")), PlayNotFound));
        // A replay of A's own request ID against A's Play is still a foreign Play: no stored result is returned.
        await state.After(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/manual-executions", _ => entry),
            PlayNotFound));
        await state.After(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{id}/manual-executions/{execution}",
            _ => Fact(Guid.NewGuid(), aVersion, "1", "80") with { Reason = "Taken" }), PlayNotFound));
        // A correction batch on A's Play answers like an unknown Play (phase 1, 1.3b).
        await state.After(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/manual-executions/corrections",
            _ => Batch(Guid.NewGuid(), aVersion, execution, "80")), PlayNotFound));

        // B reuses A's request ID with different facts on its own Play: it records B's own entry, neither replaying
        // A's result nor conflicting with it. The entry opens B's Play, so B's corrections reach the execution lookup.
        var reused = await b.Post($"/api/plays/{bPlay}/manual-executions", Fact(requestId, Version(bPlanned), "2", "200"));
        var bRecord = Assert.Single(reused.GetProperty("records").EnumerateArray());
        var bExecution = bRecord.GetProperty("id").GetGuid();
        Assert.NotEqual(execution, bExecution);
        Assert.Equal(("2", "200"), (bRecord.GetProperty("quantity").GetString(), bRecord.GetProperty("price").GetString()));
        Assert.Equal(bPlay, Id(reused.GetProperty("play")));
        Assert.Equal("open", reused.GetProperty("play").GetProperty("summary").GetProperty("status").GetString());
        Isolated.NoLeak(reused.GetRawText(), aPlay, execution);
        await state.Unchanged();
        // Positive control: B corrects its own execution on its Open Play.
        var bCorrected = await b.Send(HttpMethod.Put, $"/api/plays/{bPlay}/manual-executions/{bExecution}",
            Fact(Guid.NewGuid(), Version(reused.GetProperty("play")), "2", "201") with { Reason = "Broker price" });
        Assert.Equal("201", Assert.Single(bCorrected.GetProperty("records").EnumerateArray()).GetProperty("price").GetString());
        Assert.Equal(2, bCorrected.GetProperty("history").GetArrayLength());
        var bVersion = Version(bCorrected.GetProperty("play"));

        // A's execution ID under B's own Open Play, with B's current version, role, entry and side: refused by the
        // execution lookup like an unknown ID, and neither ledger nor history changes.
        var both = await Snapshot.Of((a, paths), (b, [$"/api/plays/{bPlay}/manual-executions", $"/api/plays/{bPlay}", $"/api/plays/{bPlay}/history"]));
        await both.After(() => Isolated.NotFound(b, execution, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{bPlay}/manual-executions/{id}",
            _ => Fact(Guid.NewGuid(), bVersion, "1", "80") with { Reason = "Taken" }), "Manual execution not found."));
        // So is A's execution ID inside a batch on B's own Play.
        await both.After(() => Isolated.NotFound(b, execution, Isolated.Request(HttpMethod.Post, _ => $"/api/plays/{bPlay}/manual-executions/corrections",
            id => Batch(Guid.NewGuid(), bVersion, id, "80")), "Manual execution not found."));
        // A's retry of its own request still replays A's result.
        var replay = await a.Post($"/api/plays/{aPlay}/manual-executions", entry);
        Assert.Equal(execution, Assert.Single(replay.GetProperty("records").EnumerateArray()).GetProperty("id").GetGuid());
        await both.Unchanged();
        // Positive control: B corrects its own execution in a batch.
        var bBatch = await b.Post($"/api/plays/{bPlay}/manual-executions/corrections", Batch(Guid.NewGuid(), bVersion, bExecution, "202", "2"));
        Assert.Equal("202", Assert.Single(bBatch.GetProperty("records").EnumerateArray()).GetProperty("price").GetString());
    }

    /// <summary>One owner's ended Plays: scored and reviewed, excluded for unknown costs, and cancelled.</summary>
    private sealed record Ended(Owner Owner, Guid Account, JsonElement Scored, Guid Excluded, Guid Cancelled, string Text, ReviewAnswers Answers)
    {
        public Guid ScoredId => Id(Scored);
        public object[] Markers => [Account, ScoredId, Excluded, Cancelled, Text, Answers.MistakeTag!, Answers.Lesson, Answers.NextAction];
    }

    /// <summary>A manual Play opened and closed through the diary. Unknown entry costs exclude it from Review.</summary>
    private static async Task<JsonElement> ClosedManual(Owner owner, Guid account, string title, bool knownCosts)
    {
        var planned = await Planned(owner, account, title, "manual");
        var entry = await owner.Post($"/api/plays/{Id(planned)}/manual-executions", Fact(Guid.NewGuid(), Version(planned), "1", "100") with
        {
            Fee = knownCosts ? "0" : null, FeeCurrency = knownCosts ? "USDC" : null
        });
        var exit = await owner.Post($"/api/plays/{Id(planned)}/manual-executions", new ManualExecutionRequest(Guid.NewGuid(),
            Version(entry.GetProperty("play")), "exit", null, AccountHistoryOrders.At.AddSeconds(1), "sell", "1", "110", "0", "USDC", "Paper broker"));
        Assert.Equal("closed", exit.GetProperty("play").GetProperty("summary").GetProperty("status").GetString());
        return exit.GetProperty("play");
    }

    private static UpdatePlayRequest ReviewOf(JsonElement play, Guid account, string text, ReviewAnswers answers) =>
        new(Version(play), account, "BTC", "manual", play.GetProperty("summary").GetProperty("title").GetString()!, TestPlays.Plan(), NoDrawings,
            text, StructuredReview: answers);

    private static async Task<Ended> EndedJournal(Owner owner)
    {
        var account = (await owner.ManualAccount(Isolated.Marker("manual"))).GetProperty("id").GetGuid();
        var scored = await ClosedManual(owner, account, Isolated.Marker("scored"), knownCosts: true);
        var excluded = Id(await ClosedManual(owner, account, Isolated.Marker("excluded"), knownCosts: false));
        var planned = await Planned(owner, account, Isolated.Marker("cancelled"), "manual");
        var cancelled = await owner.Post($"/api/plays/{Id(planned)}/status", new ChangePlayStatusRequest(Version(planned), "cancelled", "missed"));
        var text = Isolated.Marker("review");
        var answers = new ReviewAnswers("deviated", Isolated.Marker("tag"), Isolated.Marker("lesson"), Isolated.Marker("next"));
        var reviewed = await owner.Send(HttpMethod.Put, $"/api/plays/{Id(scored)}", ReviewOf(scored, account, text, answers));
        Assert.True(reviewed.GetProperty("summary").GetProperty("hasReview").GetBoolean());
        return new(owner, account, reviewed, excluded, Id(cancelled), text, answers);
    }

    private static List<Guid> Ids(JsonElement list, string property = "plays", string id = "id") =>
        [.. list.GetProperty(property).EnumerateArray().Select(p => p.GetProperty(id).GetGuid()).Order()];

    [PostgresFact]
    [Covers("GET", "/api/review")]
    [Covers("GET", "/api/review/exclusions")]
    [Covers("GET", "/api/review/process")]
    [Covers("GET", "/api/review/mistake-tags")]
    [Covers("PUT", "/api/plays/{id:guid}")]
    [Covers("GET", "/api/plays/{id:guid}/history")]
    [Covers("GET", "/api/plays/next-actions")]
    [Covers("POST", "/api/plays/{id:guid}/review/complete")]
    [Covers("POST", "/api/plays/{id:guid}/review/reopen")]
    [Covers("POST", "/api/plays/{id:guid}/review/not-needed")]
    [Covers("GET", "/api/plays/{id:guid}/notes")]
    [Covers("POST", "/api/plays/{id:guid}/notes")]
    public async Task Reviews_exclusions_and_ended_tabs_stay_separate_and_foreign_reviews_answer_like_unknown_ones()
    {
        var (aOwner, bOwner) = await api.Owners();
        var a = await EndedJournal(aOwner);
        var b = await EndedJournal(bOwner);
        foreach (var (mine, theirs) in new[] { (a, b), (b, a) })
        {
            var owner = mine.Owner;
            // Positive results: the scored Play counts, the excluded one is listed with its reason.
            var review = await owner.GetJson("/api/review?period=all");
            Assert.Equal(1, review.GetProperty("metrics").GetProperty("count").GetInt32());
            Assert.Equal("10", review.GetProperty("metrics").GetProperty("net").GetString());
            var scored = Assert.Single(review.GetProperty("plays").EnumerateArray());
            Assert.Equal((mine.ScoredId, true), (scored.GetProperty("id").GetGuid(), scored.GetProperty("reviewed").GetBoolean()));
            Assert.Equal(1, review.GetProperty("coverage").GetProperty("incompleteFees").GetInt32());
            // The owner's cancelled Play is listed with the exclusions too (phase 1, 1.4b), and never the other owner's.
            Assert.Equal(1, review.GetProperty("coverage").GetProperty("cancelled").GetInt32());
            var exclusions = await owner.GetJson("/api/review/exclusions?period=all");
            Assert.Equal(2, exclusions.GetProperty("total").GetInt32());
            Assert.Equal(new[] { mine.Excluded, mine.Cancelled }.Order(),
                exclusions.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("playId").GetGuid()).Order());
            var excluded = exclusions.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("playId").GetGuid() == mine.Excluded);
            Assert.Contains(excluded.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains("unknown", StringComparison.OrdinalIgnoreCase));
            // The stored review and its history are the owner's own.
            var play = await owner.GetJson($"/api/plays/{mine.ScoredId}");
            Assert.Equal(mine.Text, play.GetProperty("review").GetString());
            Assert.Equal((mine.Answers.MistakeTag, mine.Answers.Lesson), (play.GetProperty("structuredReview").GetProperty("mistakeTag").GetString(),
                play.GetProperty("structuredReview").GetProperty("lesson").GetString()));
            // The next actions offered at planning are the owner's own (phase 1, 1.5).
            var next = Assert.Single((await owner.GetJson("/api/plays/next-actions")).EnumerateArray());
            Assert.Equal((mine.ScoredId, mine.Answers.NextAction), (next.GetProperty("playId").GetGuid(), next.GetProperty("nextAction").GetString()));
            var history = await owner.GetJson($"/api/plays/{mine.ScoredId}/history");
            Assert.Equal(mine.Answers.NextAction, Assert.Single(history.GetProperty("reviews").EnumerateArray())
                .GetProperty("structuredReview").GetProperty("nextAction").GetString());
            // Process cases, their drill-downs and the mistake tags offered for autocomplete are the owner's own (phase 1, 1.4b).
            var process = review.GetProperty("process");
            Assert.Equal(1, process.GetProperty("deviated").GetProperty("wins").GetInt32());
            Assert.Equal(mine.Answers.MistakeTag, Assert.Single(process.GetProperty("mistakeTags").EnumerateArray()).GetProperty("tag").GetString());
            var cell = await owner.GetJson("/api/review/process?period=all&cell=deviated-win");
            Assert.Equal(mine.ScoredId, Assert.Single(cell.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            var byTag = await owner.GetJson($"/api/review/process?period=all&mistakeTag={Uri.EscapeDataString(mine.Answers.MistakeTag!)}");
            Assert.Equal(mine.ScoredId, Assert.Single(byTag.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            var foreignTag = await owner.GetJson($"/api/review/process?period=all&mistakeTag={Uri.EscapeDataString(theirs.Answers.MistakeTag!)}");
            Assert.Equal(0, foreignTag.GetProperty("total").GetInt32());
            Assert.Equal([mine.Answers.MistakeTag], (await owner.GetJson("/api/review/mistake-tags")).EnumerateArray().Select(t => t.GetString()));
            // Each ended tab and count holds exactly the owner's own Plays.
            var tabs = new Dictionary<string, Guid[]>
            {
                ["reviewed"] = [mine.ScoredId], ["closed"] = [mine.ScoredId, mine.Excluded], ["cancelled"] = [mine.Cancelled],
                ["review"] = [mine.Excluded, mine.Cancelled], ["all"] = [mine.ScoredId, mine.Excluded, mine.Cancelled]
            };
            var counts = await owner.GetJson("/api/plays/counts");
            foreach (var (view, ids) in tabs)
            {
                var list = await owner.GetJson($"/api/plays?view={view}");
                Assert.Equal(ids.Order(), Ids(list));
                Assert.Equal(ids.Length, list.GetProperty("totalCount").GetInt32());
                Assert.Equal(ids.Length, counts.GetProperty(view).GetInt32());
            }
            foreach (var path in new[] { "/api/review?period=all", "/api/review/exclusions?period=all", "/api/plays/counts", "/api/plays?view=all",
                "/api/plays?view=reviewed", "/api/plays?view=review", $"/api/plays/{mine.ScoredId}", $"/api/plays/{mine.ScoredId}/history",
                "/api/plays/next-actions" })
                Isolated.NoLeak(await owner.GetText(path), theirs.Markers);
        }

        // B rewrites or clears the review of A's Plays, with A's current version and either account, and reads its history:
        // each answers like an unknown Play and A's review stays as it was.
        var aPaths = new[] { $"/api/plays/{a.ScoredId}", $"/api/plays/{a.ScoredId}/history", $"/api/plays/{a.Excluded}", $"/api/plays/{a.Cancelled}",
            "/api/review?period=all", "/api/review/exclusions?period=all", "/api/plays/counts", "/api/plays/next-actions" };
        var state = await Snapshot.Of((a.Owner, aPaths), (b.Owner, ["/api/review?period=all", "/api/plays/counts", $"/api/plays/{b.ScoredId}"]));
        var taken = new ReviewAnswers("followed", "taken-by-b", "Taken", "Taken");
        foreach (var account in new[] { a.Account, b.Account })
        {
            await state.After(() => Isolated.NotFound(b.Owner, a.ScoredId, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{id}",
                _ => ReviewOf(a.Scored, account, "taken-by-b", taken)), PlayNotFound));
            await state.After(() => Isolated.NotFound(b.Owner, a.ScoredId, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{id}",
                _ => ReviewOf(a.Scored, account, "", new ReviewAnswers())), PlayNotFound));
        }
        foreach (var target in new[] { a.Excluded, a.Cancelled })
        {
            var current = await a.Owner.GetJson($"/api/plays/{target}");
            await state.After(() => Isolated.NotFound(b.Owner, target, Isolated.Request(HttpMethod.Put, id => $"/api/plays/{id}",
                _ => ReviewOf(current, b.Account, "taken-by-b", taken)), PlayNotFound));
        }
        foreach (var target in new[] { a.ScoredId, a.Excluded, a.Cancelled })
            await state.After(() => Isolated.NotFound(b.Owner, target, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/history"), PlayNotFound));
        // Positive control: A revises its own review with the version it held; nothing consumed it.
        var revised = await a.Owner.Send(HttpMethod.Put, $"/api/plays/{a.ScoredId}", ReviewOf(a.Scored, a.Account, a.Text + " revised", a.Answers));
        Assert.Equal(a.Text + " revised", revised.GetProperty("review").GetString());
        Assert.Equal(2, (await a.Owner.GetJson($"/api/plays/{a.ScoredId}/history")).GetProperty("reviews").GetArrayLength());
        // Review completion and the log (phase 1, 1.2): B's requests on A's ended Plays answer like unknown Plays, and A's
        // completion, reviews and log stay as they were.
        var reviewPaths = new[] { $"/api/plays/{a.ScoredId}", $"/api/plays/{a.Cancelled}", $"/api/plays/{a.ScoredId}/notes",
            $"/api/plays/{a.ScoredId}/history", "/api/plays/counts" };
        var reviewState = await Snapshot.Of(a.Owner, reviewPaths);
        foreach (var target in new[] { a.ScoredId, a.Cancelled })
        {
            var current = await a.Owner.GetJson($"/api/plays/{target}");
            foreach (var action in new[] { "complete", "reopen", "not-needed" })
                await reviewState.After(() => Isolated.NotFound(b.Owner, target, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/review/{action}",
                    _ => new ReviewCompletionRequest(Version(current))), PlayNotFound));
            await reviewState.After(() => Isolated.NotFound(b.Owner, target, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/notes"), PlayNotFound));
            await reviewState.After(() => Isolated.NotFound(b.Owner, target, Isolated.Request(HttpMethod.Post, id => $"/api/plays/{id}/notes",
                _ => new AddPlayNoteRequest(Guid.NewGuid(), "taken-by-b")), PlayNotFound));
        }
        // Positive control: A marks its cancelled Play as needing no review and logs on its scored Play; B reusing A's log
        // request ID on its own Play writes B's entry, never reads A's.
        var aCancelled = await a.Owner.GetJson($"/api/plays/{a.Cancelled}");
        var marked = await a.Owner.Post($"/api/plays/{a.Cancelled}/review/not-needed", new ReviewCompletionRequest(Version(aCancelled)));
        Assert.Equal("not-needed", marked.GetProperty("summary").GetProperty("reviewCompletion").GetString());
        var request = new AddPlayNoteRequest(Guid.NewGuid(), Isolated.Marker("note-a"), "calm");
        Assert.Single((await a.Owner.Post($"/api/plays/{a.ScoredId}/notes", request)).EnumerateArray());
        var bNotes = await b.Owner.Post($"/api/plays/{b.ScoredId}/notes", request with { Text = Isolated.Marker("note-b") });
        Isolated.NoLeak(bNotes.GetRawText(), request.Text);
        Assert.Single(bNotes.EnumerateArray());
    }

    private static ManualExecutionRequest Fact(Guid requestId, long version, string quantity, string price) =>
        new(requestId, version, "entry", "entry-1", AccountHistoryOrders.At, "buy", quantity, price, "0", "USDC", "Paper broker");
    private static ManualCorrectionBatchRequest Batch(Guid requestId, long version, Guid execution, string price, string quantity = "1") =>
        new(requestId, version, "Taken", [new(execution, "entry", "entry-1", AccountHistoryOrders.At, "buy", quantity, price, "0", "USDC", "Paper broker")]);
}

/// <summary>The cases above in Token mode, with the suite's test scheme selecting the owner.</summary>
public sealed class PlayIsolationTests(IsolationApi api) : PlayIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletPlayIsolationTests(WalletIsolationApi api) : PlayIsolationCases(api), IClassFixture<WalletIsolationApi>;
