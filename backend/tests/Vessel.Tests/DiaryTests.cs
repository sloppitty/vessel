using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class ManualLedgerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static Play Play(string direction = "long")
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Manual");
        return new(Guid.NewGuid(), account, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual,
            PlayDocuments.Serialize(TestPlays.Plan() with { Direction = direction }), At);
    }
    private static ManualExecution Row(string role, string quantity, string price, int seconds, string? fee = "0", string? currency = "USDC",
        string side = "buy") => new()
    {
        Id = Guid.NewGuid(), Role = role, EntryId = role == "entry" ? "entry-1" : null, Quantity = quantity, Price = price,
        Fee = fee, FeeCurrency = currency, Side = side, Source = "Paper broker", OccurredAtUtc = At.AddSeconds(seconds)
    };

    [Fact]
    public void Chronological_partial_exits_realize_minus_one_and_keep_open_quantity_until_flat()
    {
        var rows = new[] { Row("entry", "1", "100", 0), Row("exit", "0.4", "105", 1, side: "sell"), Row("exit", "0.6", "95", 2, side: "sell") };
        var partial = ManualLedger.Calculate(Play(), rows.Take(2));
        Assert.Equal((0.6m, 2m, (DateTimeOffset?)null), (partial.Open, partial.Gross, partial.ClosedAt));
        var closed = ManualLedger.Calculate(Play(), rows);
        Assert.Equal((0m, -1m, -1m, true), (closed.Open, closed.Gross, closed.Net, closed.FeesComplete));
        Assert.Equal(At.AddSeconds(2), closed.ClosedAt);
        Assert.Equal(-1, closed.Projection.Sum(f => f.Fill.ClosedPnlUsd));
    }

    [Fact]
    public void Average_cost_and_short_direction_are_chronological()
    {
        var rows = new[] { Row("entry", "1", "100", 0, side: "sell"), Row("exit", ".5", "90", 1),
            Row("entry", "1", "120", 2, side: "sell"), Row("exit", "1.5", "110", 3) };
        // Use the contract's decimal-string syntax.
        rows[1].Quantity = "0.5";
        Assert.Equal(10, ManualLedger.Calculate(Play("short"), rows).Gross);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1", "BTC")]
    [InlineData("1", "usdg")]
    public void Unknown_or_unconverted_cost_is_not_zero(string? fee, string? currency)
    {
        var ledger = ManualLedger.Calculate(Play(), [Row("entry", "1", "100", 0, fee, currency), Row("exit", "1", "110", 1, side: "sell")]);
        Assert.False(ledger.FeesComplete);
        Assert.Null(ledger.Net);
        Assert.Equal(10, ledger.Gross);
    }

    [Fact]
    public void Known_zero_other_currency_and_usdc_cost_are_explicit()
    {
        var ledger = ManualLedger.Calculate(Play(), [Row("entry", "1", "100", 0, "0", "BTC"), Row("exit", "1", "110", 1, "2", "USDC", "sell")]);
        Assert.True(ledger.FeesComplete);
        Assert.Equal(8, ledger.Net);
    }

    [Fact]
    public void Listed_stablecoin_costs_are_deducted_nominally_together()
    {
        var play = Play();
        var ledger = ManualLedger.Calculate(play, [Row("entry", "1", "100", 0, "0.5", "USDG"),
            Row("exit", "1", "110", 1, "0.25", "USDC", "sell")]);
        Assert.True(ledger.FeesComplete);
        Assert.Equal(9.25m, ledger.Net);
        play.MarkPlanned(At);
        play.MarkOpen("Filled.", At.AddSeconds(1));
        play.MarkClosed("Exited.", At.AddSeconds(2));
        var outcome = PlayOutcomeCalculator.Outcome(new ClosedPlayFacts(play, [], ledger.Projection.ToList(),
            ManualSource: true, ManualFeesComplete: ledger.FeesComplete))!;
        Assert.Equal((9.25m, true), (outcome.NetResult, outcome.FeesComplete));
    }

    [Fact]
    public void Overexit_exit_before_entry_and_opposite_roles_at_one_timestamp_are_rejected()
    {
        var entry = Row("entry", "1", "100", 1);
        foreach (var exit in new[] { Row("exit", "2", "110", 2, side: "sell"), Row("exit", "1", "110", 0, side: "sell"),
            Row("exit", "1", "110", 1, side: "sell") })
            Assert.Equal(409, Assert.Throws<ProblemException>(() => ManualLedger.Calculate(Play(), [entry, exit])).StatusCode);
    }

    [Theory]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData(" 1")]
    [InlineData("-1")]
    public void Input_never_silently_rounds(string input) =>
        Assert.Equal(400, Assert.Throws<ProblemException>(() => ManualLedger.Number(input, true)).StatusCode);

    [Fact]
    public void Scientific_notation_is_exact_and_structured_review_shape_has_no_derived_fields()
    {
        Assert.Equal(0.00001230m, ManualLedger.Number("1.230e-5", true));
        var json = JsonSerializer.Serialize(new ReviewAnswers("followed", "late", "Wait", "Set alert"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("isAnswered", json);
        Assert.Contains("\"nextAction\":\"Set alert\"", json);
    }

    [Fact]
    public void Quantity_totals_and_notional_cannot_silently_discard_small_units()
    {
        Assert.Equal(400, Assert.Throws<ProblemException>(() => ManualLedger.Calculate(Play(),
            [Row("entry", "10000000000000000000000000000", "1", 0), Row("entry", "0.1", "1", 1)])).StatusCode);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => ManualLedger.Calculate(Play(),
            [Row("entry", "0.0000000000000000000000000001", "0.0000000000000000000000000001", 0)])).StatusCode);
    }

    [Fact]
    public void Corrected_ledger_cannot_reopen_an_intermediate_completed_round_trip()
    {
        Assert.Equal(409, Assert.Throws<ProblemException>(() => ManualLedger.Calculate(Play(),
            [Row("entry", "1", "100", 0), Row("exit", "1", "110", 1, side: "sell"),
                Row("entry", "1", "100", 2), Row("exit", "1", "110", 3, side: "sell")])).StatusCode);
    }

    [Fact]
    public void Reason_bound_has_explicit_omitted_count_instead_of_silent_truncation()
    {
        var reasons = DiaryService.BoundedReasons(Enumerable.Range(0, 202).Select(i => $"Order {i} missing"));
        Assert.Equal(100, reasons.Count);
        Assert.Contains("103 additional reasons omitted", reasons[^1]);
        Assert.Equal(202, reasons.Count + 103 - 1);
    }
}

public sealed class DiaryPostgresTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static ManualExecutionRequest Fact(long version, string role, string quantity, string price, int seconds,
        string? fee = "0", string? currency = "USDC") => new(Guid.NewGuid(), version, role,
            role == "entry" ? "entry-1" : null, At.AddSeconds(seconds), role == "entry" ? "buy" : "sell",
            quantity, price, fee, currency, "Paper broker");

    private static async Task<PlayDto> Planned(HttpClient client, Guid accountId)
    {
        var created = await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(accountId, "BTC", "manual", "Manual",
            TestPlays.Plan()));
        created.EnsureSuccessStatusCode();
        var play = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var response = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status", new ChangePlayStatusRequest(play.Summary.Version, "planned"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlayDto>())!;
    }
    private static async Task<ManualExecutionsDto> Add(HttpClient client, PlayDto play, ManualExecutionRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManualExecutionsDto>())!;
    }
    private static async Task<Account> Account(CoreDatabase database, Guid owner, string venue = "manual")
    {
        var account = new Account(Guid.NewGuid(), owner, venue, venue);
        await using var db = database.Context(owner);
        db.Accounts.Add(account); await db.SaveChangesAsync();
        return account;
    }

    [PostgresFact]
    public async Task Manual_api_is_atomic_audited_idempotent_closed_and_scored_without_imported_facts()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var play = await Planned(client, account.Id);
        var request = Fact(play.Summary.Version, "entry", "1", "100", 0);
        var entry = await Add(client, play, request);
        Assert.Equal("open", entry.Play.Summary.Status);
        Assert.Equal("Paper broker", entry.Records[0].Source);
        var retry = await Add(client, play, request);
        Assert.Equal(entry.Play.Summary.Version, retry.Play.Summary.Version);
        Assert.Equal(entry.Records[0].Id, retry.Records[0].Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions",
            request with { Price = "101" })).StatusCode);
        var partial = await Add(client, entry.Play, Fact(entry.Play.Summary.Version, "exit", "0.4", "105", 1));
        Assert.Equal(("open", "0.6", "2"), (partial.Play.Summary.Status, partial.Totals.OpenQuantity, partial.Totals.GrossResultUsd));
        var closed = await Add(client, partial.Play, Fact(partial.Play.Summary.Version, "exit", "0.6", "95", 2));
        Assert.Equal(("closed", "-1", "-1"), (closed.Play.Summary.Status, closed.Totals.GrossResultUsd, closed.Totals.NetResultUsd));
        var closeRow = closed.Records.Last();
        var correction = Fact(closed.Play.Summary.Version, "exit", "0.5", "95", 2) with { Reason = "Mistyped quantity" };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions/{closeRow.Id}", correction)).StatusCode);
        var correctedResponse = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions/{closeRow.Id}",
            correction with { Quantity = "0.6", Price = "96", Reason = "Broker correction" });
        correctedResponse.EnsureSuccessStatusCode();
        var corrected = (await correctedResponse.Content.ReadFromJsonAsync<ManualExecutionsDto>())!;
        Assert.Equal("-0.4", corrected.Totals.GrossResultUsd);
        Assert.Equal(4, corrected.History.Count);
        Assert.Equal("95", corrected.History.Last().Before!.Price);
        Assert.Equal("96", corrected.History.Last().After.Price);
        Assert.All(corrected.History.Take(3), h => Assert.Null(h.Before));
        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{play.Summary.Id}/history"))!;
        Assert.All(history.StatusChanges, h => Assert.Equal("owner", h.Source));
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Fills.ToListAsync()); Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty((await ActivityTestSupport.Activity(verify).PageAsync(null)).Operations);
        var report = await new ReviewService(new ReviewStore(verify, new SizingStore(verify, TestUnitOfWork.For(verify))), TimeProvider.System).GetAsync(new(Period: "all"), default);
        Assert.Equal(1, report.Metrics.Count);
        Assert.Equal(("manual", "planned", "-0.4"), (report.Plays[0].ExecutionSource, report.Plays[0].Origin, report.Plays[0].Net));
    }

    [PostgresFact]
    public async Task Manual_ledger_receipt_sets_each_entry_beside_its_plan_and_scores_R_as_Review_does()
    {
        // Phase 1.1a (G-03): the receipt the review is written beside. Entry planned at 100 for 7.5 (margin 250 at 3x).
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var play = await Planned(client, account.Id);
        var now = DateTimeOffset.UtcNow;
        var first = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        ManualExecutionRequest At(long version, string role, string quantity, string price, DateTimeOffset at) =>
            Fact(version, role, quantity, price, 0) with { OccurredAtUtc = at };
        var one = await Add(client, play, At(play.Summary.Version, "entry", "0.25", "100.2", first));
        var open = await Add(client, one.Play, At(one.Play.Summary.Version, "entry", "0.25", "100.6", first));
        var entry = Assert.Single(open.Entries);
        Assert.Equal(("entry-1", "100", "7.5", 1), (entry.EntryId, entry.PlannedPrice, entry.PlannedQuantity, entry.PlanRevision));
        Assert.Equal(("0.5", "100.4"), (entry.FilledQuantity, entry.AverageFillPrice));
        Assert.Equal((null, PlayReceipts.OpenReason, first), (open.Receipt!.EstimatedR, open.Receipt.EstimatedRReason, open.Receipt.FirstEntryAtUtc));

        var exitAt = first.AddMilliseconds(1);
        var closed = await Add(client, open.Play, At(open.Play.Summary.Version, "exit", "0.5", "110", exitAt));
        Assert.Equal("closed", closed.Play.Summary.Status);
        // Net 4.8 over 50.2 notional × 5% stop distance = 2.51 initial risk.
        Assert.Equal(0, closed.UnknownFees);
        Assert.Equal(["USDC 0"], closed.Fees.Select(f => $"{f.Token} {f.Amount}"));
        Assert.Equal((ExactDecimal.Format(4.8m / 2.51m), "2.51", "100.4", "110", exitAt, 3),
            (closed.Receipt!.EstimatedR, closed.Receipt.InitialRisk, closed.Receipt.AverageEntryPrice, closed.Receipt.AverageExitPrice,
                closed.Receipt.LastExitAtUtc, closed.Receipt.FillCount));
        await using var verify = database.Context(owner);
        var report = await new ReviewService(new ReviewStore(verify, new SizingStore(verify, TestUnitOfWork.For(verify))), TimeProvider.System)
            .GetAsync(new(Period: "all"), default);
        Assert.Equal(closed.Receipt.EstimatedR, Assert.Single(report.Plays).RMultiple);
        Assert.Equal(closed.Receipt, (await client.GetFromJsonAsync<ManualExecutionsDto>($"/api/plays/{play.Summary.Id}/manual-executions"))!.Receipt);
    }

    [PostgresFact]
    public async Task A_plan_written_after_its_execution_is_marked_but_answered_and_scored_like_any_other()
    {
        // T29 (D-17): a manual Play planned today for executions recorded days earlier keeps planned origin. It is marked,
        // and by decision 9 it may still answer "Followed" and counts in Review like any other Play.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var planned = await Planned(client, account.Id);
        Assert.True(planned.Summary.PlannedAtUtc > At);
        var entry = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0));
        Assert.True(entry.Receipt!.PlannedAfterExecution);
        var closed = await Add(client, entry.Play, Fact(entry.Play.Summary.Version, "exit", "1", "110", 60));
        Assert.Equal((true, PlayReceipts.PlanAfterFillReason), (closed.Receipt!.PlannedAfterExecution, closed.Receipt.EstimatedRReason));
        var followed = new UpdatePlayRequest(closed.Play.Summary.Version, account.Id, "BTC", "manual", closed.Play.Summary.Title,
            closed.Play.Plan, closed.Play.Drawings, "", StructuredReview: new("followed"));
        (await client.PutAsJsonAsync($"/api/plays/{planned.Summary.Id}", followed)).EnsureSuccessStatusCode();
        await using var verify = database.Context(owner);
        var report = await new ReviewService(new ReviewStore(verify, new SizingStore(verify, TestUnitOfWork.For(verify))), TimeProvider.System)
            .GetAsync(new(Period: "all"), default);
        var row = Assert.Single(report.Plays);
        Assert.Equal((true, "10"), (row.PlannedAfterExecution, row.Net));
        Assert.Equal(1, report.Metrics.Wins);
    }

    [PostgresFact]
    public async Task A_plan_written_before_its_execution_is_not_marked()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var planned = await Planned(client, account.Id);
        var now = DateTimeOffset.UtcNow;
        var later = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var entry = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0) with { OccurredAtUtc = later });
        Assert.False(entry.Receipt!.PlannedAfterExecution);
    }

    private static readonly PreflightCheckDto[] SkippedStops =
    [
        new("liquidation", "unknown"), new("priced-stops", "skipped"), new("allocations", "passed"), new("thesis", "passed"), new("risk", "unknown"),
    ];

    [PostgresFact]
    public async Task Planning_records_the_pre_flight_with_revision_1_and_Review_counts_skipped_checks()
    {
        // Phase 1.5 (G-05): an unpriced stop never blocks Plan it; the skipped check is kept with revision 1 and counted.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var plan = TestPlays.Plan() with { Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "", "100")] }] };
        var created = await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(account.Id, "BTC", "manual", "Unpriced stop", plan));
        created.EnsureSuccessStatusCode();
        var draft = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        var planning = await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status",
            new ChangePlayStatusRequest(draft.Summary.Version, "planned", Preflight: SkippedStops));
        planning.EnsureSuccessStatusCode();
        var planned = (await planning.Content.ReadFromJsonAsync<PlayDto>())!;
        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{draft.Summary.Id}/history"))!;
        var first = Assert.Single(history.Revisions);
        Assert.Equal(1, first.Number);
        Assert.Equal(["priced-stops:skipped", "allocations:passed", "thesis:passed", "risk:unknown", "liquidation:unknown"],
            first.Preflight!.Select(c => $"{c.Code}:{c.Outcome}"));

        var entry = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0));
        await Add(client, entry.Play, Fact(entry.Play.Summary.Version, "exit", "1", "110", 1));
        // A Play planned without a recorded pre-flight, as every Play before this change was.
        var older = await Planned(client, account.Id);
        var olderEntry = await Add(client, older, Fact(older.Summary.Version, "entry", "1", "100", 2));
        await Add(client, olderEntry.Play, Fact(olderEntry.Play.Summary.Version, "exit", "1", "90", 3));
        Assert.Null(Assert.Single((await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{older.Summary.Id}/history"))!.Revisions).Preflight);

        await using var verify = database.Context(owner);
        var report = await new ReviewService(new ReviewStore(verify, new SizingStore(verify, TestUnitOfWork.For(verify))), TimeProvider.System)
            .GetAsync(new(Period: "all"), default);
        Assert.Equal((1, 1), (report.Preflight!.Recorded, report.Preflight.NotRecorded));
        Assert.Equal(["priced-stops:1", "allocations:0", "thesis:0", "risk:0", "liquidation:0"],
            report.Preflight.Skipped.Select(c => $"{c.Code}:{c.Count}"));
    }

    [PostgresFact]
    public async Task A_pre_flight_is_validated_and_accepted_only_when_a_Draft_is_planned()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var created = await client.PostAsJsonAsync("/api/plays", new CreatePlayRequest(account.Id, "BTC", "manual", "Checks", TestPlays.Plan()));
        var draft = (await created.Content.ReadFromJsonAsync<PlayDto>())!;
        foreach (var invalid in new PreflightCheckDto[][]
        {
            [new("priced-stops", "skipped"), new("priced-stops", "passed")],
            [new("stop-loss", "passed")],
            [new("thesis", "ignored")],
        })
        {
            var refused = await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status",
                new ChangePlayStatusRequest(draft.Summary.Version, "planned", Preflight: invalid));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        var planned = (await (await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status",
            new ChangePlayStatusRequest(draft.Summary.Version, "planned", Preflight: [new("thesis", "passed")]))).Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal("planned", planned.Summary.Status);
        var paused = (await (await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status",
            new ChangePlayStatusRequest(planned.Summary.Version, "paused"))).Content.ReadFromJsonAsync<PlayDto>())!;
        // Resuming is not planning a Draft: no pre-flight is taken, and revision 1 keeps the original one.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status",
            new ChangePlayStatusRequest(paused.Summary.Version, "planned", Preflight: SkippedStops))).StatusCode);
        (await client.PostAsJsonAsync($"/api/plays/{draft.Summary.Id}/status", new ChangePlayStatusRequest(paused.Summary.Version, "planned")))
            .EnsureSuccessStatusCode();
        var revision = Assert.Single((await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{draft.Summary.Id}/history"))!.Revisions);
        var kept = Assert.Single(revision.Preflight!);
        Assert.Equal(("thesis", "passed"), (kept.Code, kept.Outcome));
    }

    [PostgresFact]
    public async Task Recent_next_actions_are_the_newest_on_ended_Plays_with_their_source()
    {
        // Phase 1.5 (G-02): at most ten, newest review first; blank ones and Plays still in progress are left out.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        var ids = new List<Guid>();
        await using (var db = database.Context(owner))
        {
            for (var n = 0; n < 12; n++)
            {
                var play = TestPlays.Create(account, new PerpetualInstrument("manual", "BTC"));
                play.Annotate($"Play {n}", "{}", "", At);
                play.MarkPlanned(At);
                play.Cancel(CancelReason.Missed, null, At);
                play.Annotate($"Play {n}", "{}", "", At.AddHours(n), new ReviewAnswers(NextAction: n switch
                {
                    3 => "  ",
                    // Blank to the browser too: SQL's plain trim keeps a no-break space and a byte order mark.
                    5 => "\u00A0\u2003\uFEFF",
                    _ => $"Wait for the retest {n}",
                }));
                db.Plays.Add(play); ids.Add(play.Id);
            }
            var open = TestPlays.Create(account, new PerpetualInstrument("manual", "BTC"));
            open.MarkPlanned(At);
            db.Plays.Add(open);
            await db.SaveChangesAsync();
        }
        // A paper Play's next action, the newest of all, stays in Paper mode; real ones never appear there.
        Guid paperPlay;
        await using (var db = database.Context(owner))
        {
            var workspace = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner),
                TestVenues.With(new FixtureReader()), TestUnitOfWork.For(db));
            var portfolio = await workspace.CreatePortfolioAsync(new("Paper", TradingMode: "paper"), default);
            var created = await workspace.CreateAccountAsync(new(portfolio.Id, "Paper", "hyperliquid", TradingMode: "paper"), default);
            var play = TestPlays.Create(await db.Accounts.SingleAsync(a => a.Id == created.Id), new PerpetualInstrument("hyperliquid", "BTC"));
            play.Annotate("Paper idea", "{}", "", At);
            play.MarkPlanned(At);
            play.Cancel(CancelReason.Missed, null, At);
            play.Annotate("Paper idea", "{}", "", At.AddHours(20), new ReviewAnswers(NextAction: "Test the paper retest"));
            db.Plays.Add(play); paperPlay = play.Id;
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var actions = (await client.GetFromJsonAsync<List<NextActionDto>>("/api/plays/next-actions"))!;
        Assert.Equal(PlayService.MaxNextActions, actions.Count);
        Assert.Equal(new[] { 11, 10, 9, 8, 7, 6, 4, 2, 1, 0 }.Select(n => (ids[n], $"Play {n}", $"Wait for the retest {n}")),
            actions.Select(a => (a.PlayId, a.Title, a.NextAction)));
        Assert.Equal(At.AddHours(11), actions[0].ReviewedAtUtc);
        var paper = Assert.Single((await client.GetFromJsonAsync<List<NextActionDto>>("/api/plays/next-actions?tradingMode=paper"))!);
        Assert.Equal((paperPlay, "Test the paper retest"), (paper.PlayId, paper.NextAction));
    }

    [PostgresFact]
    public async Task Manual_timestamp_precision_is_rejected_before_writes_and_microsecond_ledger_survives_fresh_reads()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        ManualExecutionsDto closed;
        await using (var factory = new CoreApiFactory(owner, connection: database.ConnectionString))
        {
            using var client = factory.AuthorizedClient();
            var play = await Planned(client, account.Id);
            var path = $"/api/plays/{play.Summary.Id}/manual-executions";
            var entryRequest = Fact(play.Summary.Version, "entry", "1", "100", 0);
            var invalidEntry = await client.PostAsJsonAsync(path, entryRequest with { OccurredAtUtc = At.AddTicks(1) });
            Assert.Equal(HttpStatusCode.BadRequest, invalidEntry.StatusCode);
            Assert.Contains("microsecond", await invalidEntry.Content.ReadAsStringAsync());
            var untouched = (await client.GetFromJsonAsync<ManualExecutionsDto>(path))!;
            Assert.Empty(untouched.Records); Assert.Empty(untouched.History);
            Assert.Equal(play.Summary.Version, untouched.Play.Summary.Version);

            var entry = await Add(client, play, entryRequest);
            var exitRequest = Fact(entry.Play.Summary.Version, "exit", "1", "110", 0) with { OccurredAtUtc = At.AddTicks(1) };
            var invalidExit = await client.PostAsJsonAsync(path, exitRequest);
            Assert.Equal(HttpStatusCode.BadRequest, invalidExit.StatusCode);
            Assert.Contains("microsecond", await invalidExit.Content.ReadAsStringAsync());
            var stillOpen = (await client.GetFromJsonAsync<ManualExecutionsDto>(path))!;
            Assert.Single(stillOpen.Records); Assert.Single(stillOpen.History);
            Assert.Equal(("open", entry.Play.Summary.Version), (stillOpen.Play.Summary.Status, stillOpen.Play.Summary.Version));

            closed = await Add(client, entry.Play, exitRequest with { OccurredAtUtc = At.AddTicks(10) });
            Assert.Equal(("closed", "10"), (closed.Play.Summary.Status, closed.Totals.GrossResultUsd));
            Assert.Equal(At.AddTicks(10), closed.Records[1].OccurredAtUtc);
            var correctionPath = $"{path}/{closed.Records[1].Id}";
            var correction = exitRequest with { RequestId = Guid.NewGuid(), ExpectedVersion = closed.Play.Summary.Version, Reason = "Correct time" };
            var invalidCorrection = await client.PutAsJsonAsync(correctionPath, correction);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCorrection.StatusCode);
            Assert.Contains("microsecond", await invalidCorrection.Content.ReadAsStringAsync());
            var corrected = await client.PutAsJsonAsync(correctionPath, correction with { OccurredAtUtc = At.AddTicks(20) });
            corrected.EnsureSuccessStatusCode();
            closed = (await corrected.Content.ReadFromJsonAsync<ManualExecutionsDto>())!;
        }

        // A new host and scoped DbContexts must read persisted values, not tracked pre-save timestamps.
        await using var freshFactory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var freshClient = freshFactory.AuthorizedClient();
        var persisted = (await freshClient.GetFromJsonAsync<ManualExecutionsDto>($"/api/plays/{closed.Play.Summary.Id}/manual-executions"))!;
        Assert.Equal(("closed", closed.Play.Summary.Version), (persisted.Play.Summary.Status, persisted.Play.Summary.Version));
        Assert.Equal(new[] { At, At.AddTicks(20) }, persisted.Records.Select(r => r.OccurredAtUtc));
        Assert.Equal(closed.Totals, persisted.Totals);
        Assert.Equal("10", persisted.Totals.NetResultUsd);
        Assert.Equal(3, persisted.History.Count);
        Assert.Equal(At.AddTicks(10), persisted.History[^1].Before!.OccurredAtUtc);
        Assert.Equal(At.AddTicks(20), persisted.History[^1].After.OccurredAtUtc);
        var review = (await freshClient.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!;
        Assert.Equal(1, review.Metrics.Count);
        Assert.Equal("10", review.Metrics.Net);
        Assert.Equal(At.AddTicks(20), Assert.Single(review.Plays).ClosedAtUtc);
        Assert.Empty((await freshClient.GetFromJsonAsync<ReviewExclusionsDto>("/api/review/exclusions?period=all"))!.Items);
    }

    [PostgresFact]
    public async Task Unknown_manual_cost_exclusions_and_structured_review_history_use_real_terminal_data()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var planned = await Planned(client, account.Id);
        var premature = new UpdatePlayRequest(planned.Summary.Version, account.Id, "BTC", "manual", planned.Summary.Title, planned.Plan,
            planned.Drawings, "", StructuredReview: new("followed"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{planned.Summary.Id}", premature)).StatusCode);
        var entry = await Add(client, planned, Fact(planned.Summary.Version, "entry", "1", "100", 0, null, null));
        var closed = await Add(client, entry.Play, Fact(entry.Play.Summary.Version, "exit", "1", "110", 1));
        Assert.False(closed.Totals.FeesComplete); Assert.Null(closed.Totals.NetResultUsd);
        // Known fees are listed by currency; the entry with unknown costs is counted, never added as zero (1.1a).
        Assert.Equal(1, closed.UnknownFees);
        Assert.Equal(["USDC 0"], closed.Fees.Select(f => $"{f.Token} {f.Amount}"));
        var review = premature with { ExpectedVersion = closed.Play.Summary.Version, Review = "Broker lacked cost data",
            StructuredReview = new("deviated", "late", "Wait for confirmation", "Set alert") };
        var response = await client.PutAsJsonAsync($"/api/plays/{planned.Summary.Id}", review); response.EnsureSuccessStatusCode();
        var reviewed = (await response.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.True(reviewed.Summary.HasReview); Assert.NotNull(reviewed.Summary.ReviewedAtUtc);
        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{planned.Summary.Id}/history"))!;
        Assert.Single(history.Revisions); Assert.Single(history.Reviews!);
        Assert.Equal("Set alert", history.Reviews![0].StructuredReview.NextAction);
        var exclusions = (await client.GetFromJsonAsync<ReviewExclusionsDto>("/api/review/exclusions?period=all"))!;
        Assert.Equal(1, exclusions.Total);
        Assert.Contains(exclusions.Items[0].Reasons, r => r.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        var report = (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!;
        Assert.Equal(0, report.Metrics.Count); Assert.Equal(1, report.Coverage.IncompleteFees);
        var costs = await client.PutAsJsonAsync($"/api/plays/{planned.Summary.Id}/manual-executions/{entry.Records[0].Id}",
            Fact(reviewed.Summary.Version, "entry", "1", "100", 0) with { Reason = "Confirmed zero fee" });
        costs.EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<ReviewExclusionsDto>("/api/review/exclusions?period=all"))!.Items);
        Assert.Equal(1, (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!.Metrics.Reviewed);
    }

    [PostgresFact]
    public async Task Owner_disabled_foreign_and_venue_boundaries_and_authentication_are_enforced()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner); var venue = await Account(database, owner, "hyperliquid");
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient(); var play = await Planned(client, account.Id);
        using var guest = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync($"/api/plays/{play.Summary.Id}/manual-executions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/review/exclusions")).StatusCode);
        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var other = foreign.AuthorizedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/plays/{play.Summary.Id}/manual-executions")).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<ReviewExclusionsDto>("/api/review/exclusions?period=all"))!.Items);
        await using (var db = database.Context(owner))
        {
            var importedPlay = TestPlays.Create(venue, new("hyperliquid", "BTC"));
            db.Plays.Add(importedPlay); await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/plays/{importedPlay.Id}/manual-executions")).StatusCode);
            (await db.Accounts.SingleAsync(a => a.Id == account.Id)).UpdateSettings(account.Name, null, false);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/manual-executions",
            Fact(play.Summary.Version, "entry", "1", "100", 0))).StatusCode);
    }

    internal static ImportedFill Imported(Account account, string id, string order, string side, string effect, decimal quantity,
        decimal price, int seconds, decimal pnl = 0) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC",
        SourceFillId = id, ExternalOrderId = order, Side = side, Direction = effect, PositionEffect = effect,
        Quantity = quantity, Price = price, Fee = 0, FeeToken = "USDC", ClosedPnlUsd = pnl,
        OccurredAtUtc = At.AddSeconds(seconds), TransactionHash = ""
    };
    internal static ImportedOrder Order(Account account, string id, string side, decimal quantity) => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = "BTC", ExternalOrderId = id,
        Side = side, OrderType = "limit", LimitPrice = 100, OriginalSize = quantity, RemainingSize = 0,
        PlacedAtUtc = At, Status = "filled", VenueStatus = "filled", StatusAtUtc = At.AddSeconds(2), ObservedAtUtc = At.AddSeconds(3)
    };
    internal static DiaryService Service(VesselDbContext db) => new(new PlayStore(db, TestUnitOfWork.For(db)), new DiaryStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)), TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db));

    [PostgresFact]
    public async Task Retrospective_preview_atomic_creation_provenance_review_only_save_and_sizing_exclusion()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid");
        var entry = Imported(account, "open", "o1", "buy", "open", 1, 100, 0);
        var exit = Imported(account, "close", "o2", "sell", "close", 1, 110, 1, 10);
        await using (var db = database.Context(owner))
        {
            db.Fills.AddRange(entry, exit); db.Orders.AddRange(Order(account, "o1", "buy", 1), Order(account, "o2", "sell", 1));
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var activity = (await client.GetFromJsonAsync<ActivityPageDto>("/api/activity"))!;
        var operation = Assert.Single(activity.Operations);
        var url = $"/api/activity/retrospective?accountId={account.Id}&operationId={operation.Id}";
        var preview = (await client.GetFromJsonAsync<RetrospectivePreviewDto>(url))!;
        Assert.True(preview.Eligible, string.Join(" ", preview.Reasons));
        await using (var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString))
        {
            using var foreignClient = foreign.AuthorizedClient();
            Assert.Equal(HttpStatusCode.NotFound, (await foreignClient.GetAsync(url)).StatusCode);
        }
        var request = new CreateRetrospectiveRequest(Guid.NewGuid(), account.Id, operation.Id, preview.Fingerprint, "Post-trade", "Owner review");
        var response = await client.PostAsJsonAsync("/api/plays/retrospective", request); response.EnsureSuccessStatusCode();
        var play = (await response.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal(("retrospective", "closed", 0, "0"), (play.Summary.Origin, play.Summary.Status, play.Summary.PlanRevision, play.Plan.Leverage));
        Assert.Equal(exit.OccurredAtUtc, play.Summary.EndedAtUtc);
        Assert.Null(play.Summary.PlannedAtUtc); Assert.Empty(play.Plan.Entries); Assert.Empty(play.Plan.Notes.Thesis);
        Assert.Equal(play.Summary.Id, (await (await client.PostAsJsonAsync("/api/plays/retrospective", request)).Content.ReadFromJsonAsync<PlayDto>())!.Summary.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/plays/retrospective", request with { Title = "Changed" })).StatusCode);
        var reviewedResponse = await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(play.Summary.Version,
            account.Id, "BTC", "venue", play.Summary.Title, play.Plan, play.Drawings, "Updated retrospective",
            StructuredReview: new("not-applicable", null, "Stay patient", "Wait")));
        reviewedResponse.EnsureSuccessStatusCode();
        var reviewed = (await reviewedResponse.Content.ReadFromJsonAsync<PlayDto>())!;
        Assert.Equal(0, reviewed.Summary.PlanRevision);
        var history = (await client.GetFromJsonAsync<PlayHistoryDto>($"/api/plays/{play.Summary.Id}/history"))!;
        Assert.Empty(history.Revisions); Assert.Empty(history.StatusChanges); Assert.Equal(2, history.Reviews!.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/plays/{play.Summary.Id}", new UpdatePlayRequest(
            reviewed.Summary.Version, account.Id, "BTC", "venue", play.Summary.Title, play.Plan, play.Drawings, reviewed.Review,
            StructuredReview: new("followed")))).StatusCode);
        var report = (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!;
        Assert.Equal(("10", "10", "retrospective", "imported"), (report.Plays[0].Net, report.Plays[0].ReturnPercent,
            report.Plays[0].Origin, report.Plays[0].ExecutionSource));
        Assert.Null(report.Plays[0].RMultiple); Assert.Equal(0, report.Metrics.RiskCount);
        // The retrospective Play stays outside the Play record; its round trip is only the Activity fallback's position.
        var sizing = (await client.GetFromJsonAsync<SizingDto>("/api/sizing"))!.Record;
        Assert.Equal(("activity", 1), (sizing.Source, sizing.ClosedPlays));
        Assert.Null(Assert.Single(sizing.Recent).PlayId);
        await using var verify = database.Context(owner);
        Assert.Equal(2, await verify.OrderLinks.CountAsync()); Assert.Equal(2, await verify.Fills.CountAsync());
        Assert.Equal(100, (await verify.Fills.SingleAsync(f => f.Id == entry.Id)).Price);
        Assert.Equal(10, (await verify.Fills.SingleAsync(f => f.Id == exit.Id)).ClosedPnlUsd);
        Assert.False((await client.GetFromJsonAsync<RetrospectivePreviewDto>(url))!.Eligible);
        (await verify.Accounts.SingleAsync()).UpdateSettings(account.Name, null, false);
        await verify.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/plays/retrospective", request)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all"))!.Plays);
    }

    [PostgresFact]
    public async Task Retrospective_stale_preview_and_missing_executions_do_not_partially_create()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid");
        await using (var db = database.Context(owner))
        {
            db.Fills.AddRange(Imported(account, "e", "e", "buy", "open", 1, 100, 0), Imported(account, "x", "x", "sell", "close", 1, 110, 1, 10));
            db.Orders.AddRange(Order(account, "e", "buy", 1), Order(account, "x", "sell", 1)); await db.SaveChangesAsync();
        }
        RetrospectivePreviewDto preview;
        await using (var db = database.Context(owner))
            preview = await Service(db).PreviewAsync(account.Id, (await ActivityTestSupport.Activity(db).PageAsync(account.Id)).Operations[0].Id, default);
        await using (var db = database.Context(owner))
        {
            (await db.Orders.SingleAsync(o => o.ExternalOrderId == "x")).OriginalSize = 2; await db.SaveChangesAsync();
        }
        await using var writing = database.Context(owner);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => Service(writing).CreateRetrospectiveAsync(
            new(Guid.NewGuid(), account.Id, preview.Operation.Id, preview.Fingerprint, "Stale"), default))).StatusCode);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.Plays.ToListAsync()); Assert.Empty(await verify.OrderLinks.ToListAsync());
        var fresh = await Service(verify).PreviewAsync(account.Id, preview.Operation.Id, default);
        Assert.False(fresh.Eligible); Assert.Contains(fresh.Reasons, r => r.Contains("Order x") && r.Contains("missing"));
    }

    [PostgresFact]
    public async Task Concurrent_retrospective_creations_or_linking_use_one_account_lock_and_never_mutate_fills()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid");
        await using (var seed = database.Context(owner))
        {
            seed.Fills.AddRange(Imported(account, "e", "e", "buy", "open", 1, 100, 0),
                Imported(account, "x", "x", "sell", "close", 1, 110, 1, 10));
            seed.Orders.AddRange(Order(account, "e", "buy", 1), Order(account, "x", "sell", 1)); await seed.SaveChangesAsync();
        }
        RetrospectivePreviewDto preview;
        await using (var reading = database.Context(owner))
            preview = await Service(reading).PreviewAsync(account.Id,
                (await ActivityTestSupport.Activity(reading).PageAsync(account.Id)).Operations[0].Id, default);
        var request = new CreateRetrospectiveRequest(Guid.NewGuid(), account.Id, preview.Operation.Id, preview.Fingerprint, "Concurrent");
        async Task<(PlayDto? Play, int? Status)> Create(CreateRetrospectiveRequest body)
        {
            await using var db = database.Context(owner);
            try { return (await Service(db).CreateRetrospectiveAsync(body, default), null); }
            catch (ProblemException error) { return (null, error.StatusCode); }
        }
        var results = await Task.WhenAll(Create(request), Create(request with { RequestId = Guid.NewGuid() }));
        Assert.Single(results, r => r.Play is not null); Assert.Equal(409, Assert.Single(results, r => r.Status is not null).Status);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Plays.ToListAsync()); Assert.Equal(2, await verify.OrderLinks.CountAsync());
        Assert.Equal(2, await verify.Fills.CountAsync());
        // Same-key concurrency returns precisely the first result, not a second mutation.
        var retry = await Create(results[0].Play is not null ? request : request with { RequestId = (await verify.DiaryRequests.SingleAsync()).Id });
        Assert.NotNull(retry.Play);
    }

    [PostgresFact]
    public async Task Sync_persistence_and_plan_updates_take_account_then_play_and_protect_unfilled_links()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid"); Play play;
        await using (var seed = database.Context(owner))
        {
            play = TestPlays.Create(account, new("hyperliquid", "BTC"));
            var (status, revision) = play.MarkPlanned(At);
            seed.Plays.Add(play); seed.StatusChanges.Add(status); seed.PlanRevisions.Add(revision);
            var order = Order(account, "pending", "buy", 1); order.Status = "open"; order.RemainingSize = 1;
            seed.Orders.Add(order);
            seed.OrderLinks.Add(new(play, order, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, At));
            await seed.SaveChangesAsync();
        }
        // An unfilled linked entry is already protected.
        await using (var editing = database.Context(owner))
        {
            var service = new PlayService(new PlayStore(editing, TestUnitOfWork.For(editing)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(editing));
            var dto = await service.GetAsync(play.Id, default);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.UpdateAsync(play.Id,
                new(dto.Summary.Version, account.Id, "BTC", "venue", dto.Summary.Title, dto.Plan with
                { Entries = [dto.Plan.Entries[0] with { Price = "101" }] }, dto.Drawings, "", "Edit pending entry"), default))).StatusCode);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var syncing = database.Context(owner);
        var sync = TestUnitOfWork.For(syncing).ExecuteAsync(() => new WorkspaceStore(syncing, TestUnitOfWork.For(syncing)).WithAccountLockAsync(account.Id, async _ =>
        {
            entered.TrySetResult(); await release.Task;
            syncing.Fills.Add(Imported(account, "synced", "pending", "buy", "open", 1, 100, 0));
            await syncing.SaveChangesAsync(); return true;
        }, default), default);
        await entered.Task;
        await using var writing = database.Context(owner);
        var editor = new PlayService(new PlayStore(writing, TestUnitOfWork.For(writing)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(writing));
        var current = await editor.GetAsync(play.Id, default);
        var update = editor.UpdateAsync(play.Id, new(current.Summary.Version, account.Id, "BTC", "venue", current.Summary.Title,
            current.Plan with { Entries = [current.Plan.Entries[0] with { Stops = [new("stop-1", "price", "97", "100")] }] },
            current.Drawings, "", "Trail"), default);
        try
        {
            await Task.Delay(100);
            Assert.False(update.IsCompleted); // Waiting on the importing account, not racing a filled-entry check.
        }
        finally { release.TrySetResult(); }
        await sync;
        Assert.Equal(2, (await update).Summary.PlanRevision);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.Fills.ToListAsync());
        Assert.Equal("100", PlayDocuments.Read((await verify.Plays.SingleAsync()).Plan).Entries[0].Price);
    }

    [PostgresFact]
    public async Task Whole_order_preview_rejects_orders_crossing_operations_flips_mixed_associations_and_omissions()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid");
        await using (var seed = database.Context(owner))
        {
            seed.Fills.AddRange(Imported(account, "e1", "shared", "buy", "open", 1, 100, 0),
                Imported(account, "x1", "x1", "sell", "close", 1, 110, 1, 10),
                Imported(account, "e2", "shared", "buy", "open", 1, 100, 2),
                Imported(account, "x2", "x2", "sell", "close", 1, 110, 3, 10));
            seed.Orders.AddRange(Order(account, "shared", "buy", 2), Order(account, "x1", "sell", 1), Order(account, "x2", "sell", 1));
            await seed.SaveChangesAsync();
        }
        await using var reading = database.Context(owner);
        var activity = await ActivityTestSupport.Activity(reading).PageAsync(account.Id);
        Assert.Equal(2, activity.Operations.Count);
        foreach (var operation in activity.Operations)
        {
            var preview = await Service(reading).PreviewAsync(account.Id, operation.Id, default);
            Assert.False(preview.Eligible);
            Assert.Contains(preview.Reasons, r => r.Contains("shared") && r.Contains("boundaries"));
        }
        // A separate account has a flip split across two round trips, never safe whole-fill attribution.
        var flipAccount = await Account(database, owner, "hyperliquid");
        await using (var seed = database.Context(owner))
        {
            seed.Fills.AddRange(Imported(flipAccount, "fe", "fe", "buy", "open", 1, 100, 0),
                Imported(flipAccount, "flip", "flip", "sell", "flip", 2, 110, 1, 10),
                Imported(flipAccount, "fx", "fx", "buy", "close", 1, 100, 2, 10));
            seed.Orders.AddRange(Order(flipAccount, "fe", "buy", 1), Order(flipAccount, "flip", "sell", 2), Order(flipAccount, "fx", "buy", 1));
            await seed.SaveChangesAsync();
        }
        var flips = await ActivityTestSupport.Activity(reading).PageAsync(flipAccount.Id);
        foreach (var operation in flips.Operations)
            Assert.False((await Service(reading).PreviewAsync(flipAccount.Id, operation.Id, default)).Eligible);

        var largeAccount = await Account(database, owner, "hyperliquid");
        await using (var seed = database.Context(owner))
        {
            seed.Fills.AddRange(Enumerable.Range(0, 201).Select(i => Imported(largeAccount, $"large-{i}", "many", "buy", "open", 1, 100, i)));
            seed.Fills.Add(Imported(largeAccount, "large-close", "flat", "sell", "close", 201, 110, 202, 2010));
            seed.Orders.AddRange(Order(largeAccount, "many", "buy", 201), Order(largeAccount, "flat", "sell", 201));
            await seed.SaveChangesAsync();
        }
        var large = (await ActivityTestSupport.Activity(reading).PageAsync(largeAccount.Id)).Operations[0];
        var bounded = await Service(reading).PreviewAsync(largeAccount.Id, large.Id, default);
        Assert.False(bounded.Eligible);
        Assert.Equal(200, bounded.Operation.Fills.Count); Assert.Equal(2, bounded.Operation.OmittedFillCount);
        Assert.Contains(bounded.Reasons, r => r.Contains("omitted"));
    }

    [PostgresFact]
    public async Task Manual_fact_request_validation_no_store_and_body_limits_are_applied_before_mutation()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner);
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient(); var play = await Planned(client, account.Id);
        var valid = Fact(play.Summary.Version, "entry", "1", "100", 0);
        var url = $"/api/plays/{play.Summary.Id}/manual-executions";
        foreach (var request in new[]
        {
            valid with { RequestId = Guid.Empty }, valid with { EntryId = "foreign-entry" },
            valid with { Fee = null }, valid with { FeeCurrency = "" },
            valid with { Quantity = "0" }, valid with { Price = "100.00000000000000000000000000001" },
            valid with { Source = "<" + new string('x', 100) }, valid with { Source = " line\n" },
            valid with { OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(1) },
            valid with { OccurredAtUtc = At.ToOffset(TimeSpan.FromHours(2)) }
        })
        {
            var response = await client.PostAsJsonAsync(url, request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var oversized = new StringContent(new string(' ', 20_000), System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(url, oversized)).StatusCode);
        await using var verify = database.Context(owner);
        Assert.Empty(await verify.ManualExecutions.ToListAsync()); Assert.Empty(await verify.ManualExecutionHistory.ToListAsync());
        Assert.Empty(await verify.DiaryRequests.ToListAsync());
    }

    [PostgresFact]
    public async Task Diary_migration_preserves_legacy_review_and_plan_without_inventing_review_time()
    {
        await using var database = await CoreDatabase.CreateAsync("20261012192227_AvailableMargin"); var owner = Guid.NewGuid();
        var account = Guid.NewGuid(); var playId = Guid.NewGuid();
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","VenueId","Name","SyncStatus") VALUES ({account},{owner},'manual','Legacy','manual');
            """);
        var plan = PlayDocuments.Serialize(TestPlays.Plan());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO plays ("Id","OwnerId","AccountId","VenueId","ContractId","InstrumentSource","Status","Plan","Review")
            VALUES ({playId},{owner},{account},'manual','BTC','Manual','Closed',CAST({plan} AS jsonb),'Legacy review');
            """);
        await db.Database.MigrateAsync();
        var play = await db.Plays.SingleAsync();
        Assert.Equal(PlayOrigin.Planned, play.Origin); Assert.Equal("Legacy review", play.Review);
        Assert.True(play.HasReview); Assert.Null(play.ReviewedAtUtc); Assert.False(play.Answers.IsAnswered);
        Assert.Equal(TestPlays.Plan().Notes, PlayDocuments.Read(play.Plan).Notes);
        Assert.Empty(await db.ReviewRevisions.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task Review_exclusion_pages_reuse_period_scope_and_identify_offending_orders_and_fills()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        var account = await Account(database, owner, "hyperliquid");
        await using (var seed = database.Context(owner))
        {
            var plan = TestPlays.Create(account, new("hyperliquid", "BTC"));
            var (change, revision) = plan.MarkPlanned(At);
            seed.Plays.Add(plan); seed.StatusChanges.Add(change); seed.PlanRevisions.Add(revision);
            plan.MarkOpen("Imported", At); plan.MarkClosed("Imported", At.AddSeconds(1));
            var (entry, exit) = (Order(account, "e", "buy", 2), Order(account, "x", "sell", 1));
            seed.OrderLinks.AddRange(new PlayOrderLink(plan, entry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, At),
                new PlayOrderLink(plan, exit, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Owner, At));
            seed.Fills.AddRange(Imported(account, "e", "e", "buy", "open", 1, 100, 0), Imported(account, "x", "x", "sell", "close", 1, 110, 1, 10));
            seed.Orders.AddRange(entry, exit); await seed.SaveChangesAsync();
        }
        await using var reading = database.Context(owner);
        var service = new ReviewService(new ReviewStore(reading, new SizingStore(reading, TestUnitOfWork.For(reading))), TimeProvider.System);
        var report = await service.GetAsync(new(Period: "all"), default);
        var excluded = await service.ExclusionsAsync(new(Period: "all"), default);
        Assert.Equal(report.Coverage.IncompleteExecutions, excluded.Total);
        Assert.Contains(excluded.Items[0].Reasons, r => r.Contains("Order e") && r.Contains("retained"));
        Assert.Empty((await service.ExclusionsAsync(new(Period: "month"), default)).Items);
        Assert.Empty((await service.ExclusionsAsync(new(Period: "all", AccountId: Guid.NewGuid()), default)).Items);
        Assert.Empty((await service.ExclusionsAsync(new(Period: "all", Offset: 50), default)).Items);
    }
}
