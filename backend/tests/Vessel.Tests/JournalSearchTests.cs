using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Api;
using Vessel.Application.Common;
using Vessel.Application.Evidence;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Numerics;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Search;

namespace Vessel.Tests;

/// <summary>Phase 2, item 13: the search rules shared by the projection and the query.</summary>
public sealed class JournalSearchTests
{
    [Fact]
    public void Text_is_composed_folded_and_spaced_once_with_positions_kept()
    {
        // Full-width letters compose, line breaks and tabs become one space, invisible characters go, the ends are trimmed.
        Assert.Equal("BTC breakout retest", JournalSearch.Normalize("  ＢＴＣ\r\n\tbreak​out   retest \n"));
        // An unpaired surrogate cannot be normalized; it becomes the replacement character instead of failing the save.
        Assert.Equal("a�b", JournalSearch.Normalize("a\uD800b"));
        Assert.Equal("", JournalSearch.Normalize(" \n​ "));
        var text = JournalSearch.Normalize("ÉCLAIR Δέλτα 𐐀 Straße");
        var folded = JournalSearch.Fold(text);
        Assert.Equal(text.Length, folded.Length);
        Assert.Equal("éclair δέλτα 𐐨 straße", folded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("​a")]
    public void A_query_needs_two_characters(string? q) =>
        Assert.Equal(400, Assert.Throws<ProblemException>(() => JournalSearch.Query(q)).StatusCode);

    [Fact]
    public void Queries_are_bounded_folded_and_escaped_for_like()
    {
        Assert.Equal(("Ab", "ab"), JournalSearch.Query(" Ab "));
        Assert.Equal(100, JournalSearch.Query(new string('x', 100)).Query.Length);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => JournalSearch.Query(new string('x', 101))).StatusCode);
        Assert.Equal(@"%50\%\_off\\x%", JournalSearch.LikePattern(@"50%_off\x"));
    }

    [Fact]
    public void An_excerpt_holds_the_first_match_with_its_position_and_never_splits_a_character()
    {
        var text = new string('a', 300) + " Needle " + new string('b', 300);
        var (excerpt, start, length) = JournalSearch.Excerpt(text, JournalSearch.Fold(text), "needle");
        Assert.StartsWith("…", excerpt);
        Assert.EndsWith("…", excerpt);
        Assert.InRange(excerpt.Length, JournalSearch.ExcerptLength, JournalSearch.ExcerptLength + 2);
        Assert.Equal("Needle", excerpt.Substring(start, length));
        // Near the start nothing is cut before it; the window still holds the match.
        var (head, at, _) = JournalSearch.Excerpt("Needle " + new string('c', 400), JournalSearch.Fold("Needle " + new string('c', 400)), "needle");
        Assert.Equal((0, false), (at, head.StartsWith('…')));
        // A window edge inside a surrogate pair moves outward.
        var pairs = string.Concat(Enumerable.Repeat("𐐀", 200)) + "needle" + string.Concat(Enumerable.Repeat("𐐀", 200));
        var (around, from, size) = JournalSearch.Excerpt(pairs, JournalSearch.Fold(pairs), "needle");
        Assert.Equal("needle", around.Substring(from, size));
        Assert.All(around.Trim('…').Chunk(2).Where(c => char.IsSurrogate(c[0])), c => Assert.True(char.IsSurrogatePair(c[0], c[1])));
    }

    [Fact]
    public void Plays_without_a_closed_result_have_no_figures_and_unindexed_ones_say_so()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Manual");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        var now = DateTimeOffset.UnixEpoch.AddDays(2);
        Assert.Equal(new PlayOutcomeFacts("active", null, null, null, null), JournalSearch.Outcome(play, null, now));
        play.Cancel(CancelReason.Missed, null, now);
        Assert.Equal(new PlayOutcomeFacts("cancelled", null, null, null, null), JournalSearch.Outcome(play, null, now));
        Assert.Equal(new PlayOutcomeSummaryDto("cancelled", null, null, null, JournalSearch.CancelledReason),
            JournalSearch.Dto(JournalSearch.Outcome(play, null, now)));
        Assert.Equal(new PlayOutcomeSummaryDto("not-indexed", null, null, null, JournalSearch.NotIndexedReason), JournalSearch.Dto(null));
        // Each field keeps its own band of positions, below the next field's.
        Assert.Equal("log", JournalSearch.FieldOf(JournalSearch.Position("log", 499)));
        Assert.True(JournalSearch.Position("nextAction", 0) < JournalSearch.Position("log", 0));
    }
}

/// <summary>Phase 2, item 13 against PostgreSQL: every write path, rollbacks, modes, paging and the one-time initialization.</summary>
public sealed class JournalSearchPostgresTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

    /// <summary>A unit of work that projects, as every host composes it.</summary>
    private static UnitOfWork Projected(VesselDbContext db, params IUnitOfWorkParticipant[] after) =>
        new(db, [new PlaySearchProjector(db), .. after]);

    private static PlayService Service(VesselDbContext db, UnitOfWork unit) =>
        new(new PlayStore(db, unit), new MemoryObjectStore(), TimeProvider.System, unit);

    private static async Task<int> RebuildAsync(VesselDbContext db, bool apply = true, bool all = false) =>
        (await new PlaySearchRebuild(db, Projected(db)).RunAsync(apply, all, 2, default)).Projected;

    private static async Task<JsonElement> Search(HttpClient client, string q, string mode = "real")
    {
        using var response = await client.GetAsync($"/api/plays/search?q={Uri.EscapeDataString(q)}&tradingMode={mode}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] Fields(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("field").GetString()!).ToArray();

    private static string Marked(JsonElement item)
    {
        var excerpt = item.GetProperty("excerpt").GetString()!;
        return excerpt.Substring(item.GetProperty("matchStart").GetInt32(), item.GetProperty("matchLength").GetInt32());
    }

    private static ManualExecutionRequest Fact(long version, string role, string price, DateTimeOffset at, string? reason = null) =>
        new(Guid.NewGuid(), version, role, role == "entry" ? "entry-1" : null, at, role == "entry" ? "buy" : "sell", "1", price, "0", "USDC",
            "Paper broker", reason);

    private sealed class FailingCommit : IUnitOfWorkParticipant
    {
        public Task BeforeCommitAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("The commit fails.");
        public Task AfterCommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [PostgresFact]
    public async Task Every_journal_write_reaches_the_search_and_the_list_in_its_own_unit()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Manual");
        await using (var db = database.Context(owner)) { db.Accounts.Add(account); await db.SaveChangesAsync(); }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString,
            configure: services => services.AddSingleton<IEvidenceObjectStore>(new MemoryObjectStore()));
        using var client = factory.AuthorizedClient();
        async Task<T> Json<T>(HttpResponseMessage response)
        {
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, body);
                return JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
            }
        }

        // A save: title and plan notes, found by the field they are in.
        var plan = TestPlays.Plan() with { Notes = new("Breakout retest of the WEEKLY range", "Close below 94", "", "") };
        var draft = await Json<PlayDto>(await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(account.Id, "BTC", "manual", "Quokka idea", plan)));
        var id = draft.Summary.Id;
        var thesis = await Search(client, "weekly range");
        Assert.Equal(["thesis"], Fields(thesis));
        Assert.Equal("WEEKLY range", Marked(thesis.GetProperty("items")[0]));
        Assert.Equal(id, thesis.GetProperty("items")[0].GetProperty("play").GetProperty("id").GetGuid());
        Assert.Equal(["title"], Fields(await Search(client, "QUOKKA")));
        var planned = await Json<PlayDto>(await client.PostAsJsonAsync($"/api/plays/{id}/status", new ChangePlayStatusRequest(draft.Summary.Version, "planned")));

        // A log append, an image note written, edited and deleted.
        await Json<JsonElement>(await client.PostAsJsonAsync($"/api/plays/{id}/notes", new AddPlayNoteRequest(Guid.NewGuid(), "Moved the stop after the wombat candle")));
        Assert.Equal(["log"], Fields(await Search(client, "wombat")));
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(EvidenceFixtures.Png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "file", "chart.png");
        form.Add(new StringContent("Platypus divergence"), "note");
        var evidence = await Json<JsonElement>(await client.PostAsync($"/api/plays/{id}/evidence", form));
        var evidenceId = evidence.GetProperty("id").GetGuid();
        Assert.Equal(["image"], Fields(await Search(client, "platypus")));
        await Json<JsonElement>(await client.PatchAsJsonAsync($"/api/evidence/{evidenceId}", new UpdateEvidenceNoteRequest("Echidna divergence")));
        Assert.Equal(0, (await Search(client, "platypus")).GetProperty("total").GetInt32());
        Assert.Equal(["image"], Fields(await Search(client, "echidna")));
        using (var removed = await client.DeleteAsync($"/api/evidence/{evidenceId}")) Assert.True(removed.IsSuccessStatusCode);
        Assert.Equal(0, (await Search(client, "echidna")).GetProperty("total").GetInt32());

        // A plan revision replaces the notes it changed.
        var revised = plan with { Notes = plan.Notes with { Thesis = "Range reclaim", Strategy = "Kookaburra pullback" } };
        var saved = await Json<PlayDto>(await client.PutAsJsonAsync($"/api/plays/{id}", new UpdatePlayRequest(planned.Summary.Version, account.Id, "BTC",
            "manual", "Quokka idea", revised, JsonDocument.Parse("{}").RootElement, "", "Refined")));
        Assert.Equal(["strategy"], Fields(await Search(client, "kookaburra")));
        Assert.Equal(0, (await Search(client, "weekly range")).GetProperty("total").GetInt32());

        // Manual executions close it: the list shows Review's exit time, result and R without reading the plan.
        var first = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var entry = await Json<ManualExecutionsDto>(await client.PostAsJsonAsync($"/api/plays/{id}/manual-executions", Fact(saved.Summary.Version, "entry", "100", first)));
        var closed = await Json<ManualExecutionsDto>(await client.PostAsJsonAsync($"/api/plays/{id}/manual-executions",
            Fact(entry.Play.Summary.Version, "exit", "110", first.AddMilliseconds(5))));
        Assert.Equal("closed", closed.Play.Summary.Status);
        async Task<(PlayOutcomeSummaryDto Outcome, JsonElement Review)> Compare()
        {
            var list = await Json<PlayListDto>(await client.GetAsync("/api/plays?view=closed"));
            var review = await Json<JsonElement>(await client.GetAsync("/api/review?period=all"));
            return (Assert.Single(list.Plays).Outcome!, Assert.Single(review.GetProperty("plays").EnumerateArray()));
        }
        var (outcome, row) = await Compare();
        Assert.Equal(("scored", "10", ExactDecimal.Format(10m / 5m)), (outcome.State, outcome.ResultUsd, outcome.EstimatedR));
        Assert.Equal((row.GetProperty("net").GetString(), row.GetProperty("rMultiple").GetString(), row.GetProperty("closedAtUtc").GetDateTimeOffset()),
            (outcome.ResultUsd, outcome.EstimatedR, outcome.ExitAtUtc!.Value));
        Assert.Equal(first.AddMilliseconds(5), outcome.ExitAtUtc);
        Assert.Null(outcome.Reason);

        // A review with its answers, each found by its own field.
        var current = await Json<PlayDto>(await client.GetAsync($"/api/plays/{id}"));
        var reviewed = await Json<PlayDto>(await client.PutAsJsonAsync($"/api/plays/{id}", new UpdatePlayRequest(current.Summary.Version, account.Id, "BTC",
            "manual", current.Summary.Title, current.Plan, current.Drawings, "Numbat summary", null,
            new ReviewAnswers("followed", "Dingo chase", "Wallaby patience", "Bilby checklist"))));
        Assert.Equal(["review"], Fields(await Search(client, "numbat")));
        Assert.Equal(["mistakeTag"], Fields(await Search(client, "dingo")));
        Assert.Equal(["lesson"], Fields(await Search(client, "wallaby")));
        Assert.Equal(["nextAction"], Fields(await Search(client, "bilby")));

        // A manual correction moves the result and R with Review.
        await Json<ManualExecutionsDto>(await client.PutAsJsonAsync($"/api/plays/{id}/manual-executions/{closed.Records.Last().Id}",
            Fact(reviewed.Summary.Version, "exit", "120", first.AddMilliseconds(5), "Broker correction")));
        (outcome, row) = await Compare();
        Assert.Equal(("20", ExactDecimal.Format(20m / 5m)), (outcome.ResultUsd, outcome.EstimatedR));
        Assert.Equal((row.GetProperty("net").GetString(), row.GetProperty("rMultiple").GetString()), (outcome.ResultUsd, outcome.EstimatedR));

        // A deleted Draft leaves nothing behind.
        var gone = await Json<PlayDto>(await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(account.Id, "BTC", "manual", "Quoll draft", TestPlays.Plan())));
        Assert.Equal(1, (await Search(client, "quoll")).GetProperty("total").GetInt32());
        using (var deleted = await client.DeleteAsync($"/api/plays/{gone.Summary.Id}")) Assert.True(deleted.IsSuccessStatusCode);
        Assert.Equal(0, (await Search(client, "quoll")).GetProperty("total").GetInt32());
        await using var check = database.Context(owner);
        Assert.False(await check.PlaySearch.AnyAsync(s => s.PlayId == gone.Summary.Id));
        Assert.False(await check.PlaySearchTexts.AnyAsync(t => t.PlayId == gone.Summary.Id));
        Assert.Equal(0, (await Search(client, "quokka")).GetProperty("notIndexed").GetInt32());
    }

    [PostgresFact]
    public async Task A_unit_that_rolls_back_leaves_the_projection_as_it_was()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Manual");
        await using var db = database.Context(owner);
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var unit = Projected(db);
        var created = await Service(db, unit).CreateAsync(new(account.Id, "BTC", "manual", "Kiwi start", TestPlays.Plan()), default);
        Assert.Equal(1, (await Service(db, unit).SearchAsync("kiwi", null, default)).Total);

        // The projection was written in the failing unit, after the change, and both roll back together.
        await using var failing = database.Context(owner);
        var broken = Projected(failing, new FailingCommit());
        var service = Service(failing, broken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(created.Summary.Id, new UpdatePlayRequest(created.Summary.Version,
            account.Id, "BTC", "manual", "Takahe rename", TestPlays.Plan(), JsonDocument.Parse("{}").RootElement, ""), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new(account.Id, "BTC", "manual", "Weka draft", TestPlays.Plan()), default));
        await using var check = database.Context(owner);
        var reader = Service(check, Projected(check));
        Assert.Equal(0, (await reader.SearchAsync("takahe", null, default)).Total);
        Assert.Equal(0, (await reader.SearchAsync("weka", null, default)).Total);
        Assert.Equal(["title"], (await reader.SearchAsync("kiwi", null, default)).Items.Select(i => i.Field));
        Assert.Equal(1, await check.PlaySearch.CountAsync());
    }

    [PostgresFact]
    public async Task Venue_facts_reach_a_closed_Play_through_its_linked_orders()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        play.Annotate("Linked trade", "{}", "", T);
        var (plannedChange, revision) = play.MarkPlanned(T);
        var opened = play.MarkOpen("Filled", T.AddMinutes(2));
        var closedChange = play.MarkClosed("Flat", T.AddMinutes(20));
        ImportedOrder Order(string id, string side, decimal size, string status) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC", ExternalOrderId = id, Side = side,
            OrderType = "limit", LimitPrice = 100, OriginalSize = size, RemainingSize = 0, PlacedAtUtc = T, Status = status,
            VenueStatus = status, StatusAtUtc = T.AddMinutes(1), ObservedAtUtc = T.AddMinutes(1)
        };
        ImportedFill Fill(string id, string order, string side, string effect, decimal price, decimal pnl, int minutes) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC", SourceFillId = id, ExternalOrderId = order,
            Side = side, Direction = effect, PositionEffect = effect, Quantity = 1, Price = price, Fee = 0, FeeToken = "USDC",
            ClosedPnlUsd = pnl, OccurredAtUtc = T.AddMinutes(minutes), TransactionHash = ""
        };
        var entryOrder = Order("o-entry", "buy", 1, "filled");
        var exitOrder = Order("o-exit", "sell", 1, "open");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play); seed.PlanRevisions.Add(revision);
            seed.StatusChanges.AddRange(plannedChange, opened, closedChange); seed.Orders.AddRange(entryOrder, exitOrder);
            seed.OrderLinks.AddRange(new PlayOrderLink(play, entryOrder, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, T),
                new PlayOrderLink(play, exitOrder, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, T));
            seed.Fills.Add(Fill("f-entry", "o-entry", "buy", ExecutionFacts.Open, 100, 0, 2));
            // An unlinked order's fill reaches no Play.
            seed.Orders.Add(Order("o-other", "sell", 1, "filled"));
            seed.Fills.Add(Fill("f-other", "o-other", "sell", ExecutionFacts.Close, 120, 20, 3));
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        Assert.Equal(1, await RebuildAsync(db));
        async Task<PlayOutcomeSummaryDto> Outcome()
        {
            await using var read = database.Context(owner);
            return Assert.Single((await Service(read, Projected(read)).ListAsync("closed", null, null, null, null, default)).Plays).Outcome!;
        }
        // Without its exit the closed Play is excluded: no exit time, no result, no R, never zero.
        Assert.Equal(new PlayOutcomeSummaryDto("executions", null, null, null, PlayReceipts.ExecutionsReason), await Outcome());

        // The exit fill arrives in a refresh-like unit: its order's link reaches the Play.
        var unit = Projected(db);
        await unit.ExecuteAsync(async () => { db.Fills.Add(Fill("f-exit", "o-exit", "sell", ExecutionFacts.Close, 110, 10, 15)); return true; }, default);
        Assert.Equal(new PlayOutcomeSummaryDto("scored", T.AddMinutes(15), "10", "2", null), await Outcome());

        // The exit order then reports a larger filled size than its retained fills: known missing executions exclude it again.
        await unit.ExecuteAsync(async () =>
        {
            var order = await db.Orders.SingleAsync(o => o.ExternalOrderId == "o-exit");
            (order.OriginalSize, order.Status) = (2, "filled");
            return true;
        }, default);
        Assert.Equal(new PlayOutcomeSummaryDto("executions", T.AddMinutes(15), null, null, PlayReceipts.ExecutionsReason), await Outcome());
    }

    [PostgresFact]
    public async Task Paper_evaluation_and_trading_modes_stay_apart()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var paperAccount = new Account(Guid.NewGuid(), owner, "hyperliquid", "Hypotheses", "paper", 4000);
        paperAccount.Configure(portfolio.Id, null, null);
        var realAccount = new Account(Guid.NewGuid(), owner, "manual", "Real");
        var paper = TestPlays.Create(paperAccount, new("hyperliquid", "BTC"));
        paper.Annotate("Shared marker paper", "{}", "", T);
        var (planned, revision) = paper.MarkPlanned(T);
        var open = paper.ApplyPaperExecutionState(true, false, T.AddMinutes(3))!;
        var closed = paper.ApplyPaperExecutionState(true, true, T.AddMinutes(9))!;
        var real = TestPlays.Create(realAccount, new("manual", "BTC"));
        real.Annotate("Shared marker real", "{}", "", T);
        await using (var seed = database.Context(owner))
        {
            seed.Portfolios.Add(portfolio); seed.Accounts.AddRange(paperAccount, realAccount); seed.Plays.AddRange(paper, real);
            seed.PlanRevisions.Add(revision); seed.StatusChanges.AddRange(planned, open, closed);
            seed.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(owner, paperAccount.Id, 4000, T));
            seed.PaperSimulations.Add(new PaperSimulation { PlayId = paper.Id, OwnerId = owner, AccountId = paperAccount.Id, State = "ready" });
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        Assert.Equal(2, await RebuildAsync(db));
        var service = Service(db, Projected(db));
        // A mode's search holds only its own Plays.
        var realHit = Assert.Single((await service.SearchAsync("shared marker", null, default)).Items);
        var paperHit = Assert.Single((await service.SearchAsync("shared marker", null, default, "paper")).Items);
        Assert.Equal((real.Id, "real", paper.Id, "paper"), (realHit.Play.Id, realHit.Play.TradingMode, paperHit.Play.Id, paperHit.Play.TradingMode));
        // Without simulated executions the closed paper Play is not scored.
        Assert.Equal("executions", paperHit.Play.Outcome!.State);

        // Paper evaluation writes the simulated entry and exit in a unit: the simulated result, never an actual fill.
        await Projected(db).ExecuteAsync(async () =>
        {
            var minute = T.AddMinutes(2);
            db.PaperExecutions.AddRange(
                new PaperExecution { Id = Guid.NewGuid(), OwnerId = owner, AccountId = paperAccount.Id, PlayId = paper.Id, EntryId = "entry-1",
                    Role = "entry", Price = 100, Quantity = 1, MarginUsd = 33, PlanVersion = 1, CandleOpenAtUtc = minute,
                    CandleCloseAtUtc = minute.AddMinutes(1), OccurredAtUtc = minute.AddMinutes(1) },
                new PaperExecution { Id = Guid.NewGuid(), OwnerId = owner, AccountId = paperAccount.Id, PlayId = paper.Id, EntryId = "entry-1",
                    LevelId = "target-1", Role = "target", Price = 110, Quantity = 1, PnlUsd = 10, MarginUsd = -33, PlanVersion = 1,
                    CandleOpenAtUtc = minute.AddMinutes(5), CandleCloseAtUtc = minute.AddMinutes(6), OccurredAtUtc = minute.AddMinutes(6) });
            return true;
        }, default);
        var scored = Assert.Single((await service.ListAsync("closed", null, null, null, null, default, "paper")).Plays).Outcome!;
        Assert.Equal(("scored", T.AddMinutes(8), "10", "2"), (scored.State, scored.ExitAtUtc, scored.ResultUsd, scored.EstimatedR));
        Assert.Equal("active", Assert.Single((await service.ListAsync("drafts", null, null, null, null, default)).Plays).Outcome!.State);
    }

    [PostgresFact]
    public async Task Search_pages_by_fifty_newest_first_and_matches_wildcards_literally()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Manual");
        var plays = Enumerable.Range(0, 60).Select(i =>
        {
            var play = TestPlays.Create(account, new("manual", "BTC"));
            play.Annotate($"Take 50%_off number {i}", "{}", "", T.AddMinutes(i));
            return play;
        }).ToList();
        var decoy = TestPlays.Create(account, new("manual", "BTC"));
        decoy.Annotate("Take 500 off", "{}", "", T);
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.AddRange(plays); seed.Plays.Add(decoy);
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var service = Service(db, Projected(db));
        // Until it is initialized, search finds nothing and says how many Plays it cannot see.
        var before = await service.SearchAsync("50%_", null, default);
        Assert.Equal((0, 61), (before.Total, before.NotIndexed));
        await RebuildAsync(db);
        var page = await service.SearchAsync("50%_", null, default);
        Assert.Equal((60, 50, 50, 0), (page.Total, page.Items.Count, page.NextOffset, page.NotIndexed));
        Assert.Equal(plays.AsEnumerable().Reverse().Take(50).Select(p => p.Id), page.Items.Select(i => i.Play.Id));
        Assert.All(page.Items, item => Assert.Equal("50%_", item.Excerpt.Substring(item.MatchStart, item.MatchLength)));
        var last = await service.SearchAsync("50%_", 50, default);
        Assert.Equal((10, (int?)null, 50), (last.Items.Count, last.NextOffset, last.Offset));
        Assert.Empty((await service.SearchAsync("0%", 100, default)).Items);
        Assert.Equal(61, (await service.SearchAsync("take", null, default)).Total);
        Assert.Equal(0, (await service.SearchAsync(@"_\", null, default)).Total);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.SearchAsync("50%_", -1, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.SearchAsync("50%_", null, default, "both"))).StatusCode);
    }

    [PostgresFact]
    public async Task Initialization_is_an_explicit_dry_run_by_default_and_idempotent()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var owner in owners)
        {
            var account = new Account(Guid.NewGuid(), owner, "manual", "Manual");
            await using var seed = database.Context(owner);
            seed.Accounts.Add(account);
            seed.Plays.AddRange(Enumerable.Range(0, 3).Select(i => TestPlays.Create(account, new("manual", "BTC"))));
            await seed.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = database.ConnectionString
        }).Build();
        async Task<(int Code, string Output)> Run(params string[] args)
        {
            using var output = new StringWriter();
            return (await PlaySearchCommand.RunAsync(args, configuration, output), output.ToString());
        }
        async Task<int> Indexed()
        {
            var total = 0;
            foreach (var owner in owners) { await using var db = database.Context(owner); total += await db.PlaySearch.CountAsync(); }
            return total;
        }

        Assert.Equal(2, (await Run("--batch", "0")).Code);
        var dry = await Run();
        Assert.Equal(0, dry.Code);
        Assert.Contains("Dry run: 6 of 6 Plays for 2 owner(s) would be indexed.", dry.Output);
        Assert.Equal(0, await Indexed());
        var apply = await Run("--apply", "--batch", "2");
        Assert.Equal(0, apply.Code);
        Assert.Contains("Indexed 6 of 6 Plays for 2 owner(s).", apply.Output);
        Assert.Equal(6, await Indexed());
        Assert.Contains("Indexed 0 of 6 Plays", (await Run("--apply")).Output);
        Assert.Contains("Indexed 6 of 6 Plays", (await Run("--apply", "--all")).Output);
        Assert.Equal(6, await Indexed());
        foreach (var owner in owners)
        {
            await using var db = database.Context(owner);
            Assert.Equal(0, (await Service(db, Projected(db)).SearchAsync("breakout", null, default)).NotIndexed);
        }
    }
}
