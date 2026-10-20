using System.Globalization;
using System.Text.Json;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ManualLedgerPrecisionTests
{
    private const string Large = "10000000000000000000000000000";
    private const string LargePlusOne = "10000000000000000000000000001";
    private const string TwiceLarge = "20000000000000000000000000000";
    private static readonly DateTimeOffset At = ReviewClock.Now.AddDays(-1);

    [Theory]
    [InlineData("long", "0.9")]
    [InlineData("long", "0.1")]
    [InlineData("short", "0.9")]
    [InlineData("short", "0.1")]
    public void Final_exit_that_cannot_consume_cost_exactly_is_rejected(string direction, string finalPrice)
    {
        var play = CreatePlay(direction);
        var rows = new[]
        {
            Row(play, "entry", "1", Large, 0),
            Row(play, "entry", "2", LargePlusOne, 1),
            Row(play, "exit", "1", LargePlusOne, 2),
            Row(play, "exit", "1", TwiceLarge, 3),
            Row(play, "exit", "1", finalPrice, 4)
        };

        var partial = ManualLedger.Calculate(play, rows[..^1]);
        Assert.Equal(1, partial.Open);
        Assert.Null(partial.ClosedAt);
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play, rows));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long", "1.1", "0.1")]
    [InlineData("short", "1.1", "-0.1")]
    [InlineData("long", "1.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("short", "1.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    public void Supported_large_cancellation_preserves_the_small_completed_result(
        string direction, string firstExitPrice, string expected)
    {
        var play = CreatePlay(direction);
        var revision = play.MarkPlanned(At.AddSeconds(-1)).Revision;
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "2", "1", 0),
            Row(play, "exit", "1", firstExitPrice, 1),
            Row(play, "entry", "1", TwiceLarge, 2),
            Row(play, "exit", "1", TwiceLarge, 3),
            Row(play, "exit", "1", "1", 4)
        ]);
        play.RecordManualOpen(At);
        play.RecordManualClose(At.AddSeconds(4), ReviewClock.Now);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ledger.Gross);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ledger.Net);
        var outcome = PlayOutcomeCalculator.Outcome(new(play, [revision], ledger.Projection, ManualSource: true));
        Assert.NotNull(outcome);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), outcome.NetResult);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Completed_gross_that_requires_rounding_is_rejected(string direction)
    {
        var play = CreatePlay(direction);
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play,
        [
            Row(play, "entry", "3", "1", 0),
            Row(play, "exit", "1", "1.1", 1),
            Row(play, "exit", "1", Large, 2),
            Row(play, "exit", "1", "1", 3)
        ]));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Completed_net_that_requires_rounding_is_rejected(string direction)
    {
        var play = CreatePlay(direction);
        var exit = Row(play, "exit", "1", TwiceLarge, 1);
        exit.Fee = "0.1";
        var error = Assert.Throws<ProblemException>(() => ManualLedger.Calculate(play,
            [Row(play, "entry", "1", Large, 0), exit]));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Fee_correction_that_requires_completed_net_rounding_is_atomic(string direction)
    {
        var f = new PrecisionFixture(direction);
        await f.Record("entry", "1", Large, 0);
        await f.Record("exit", "1", TwiceLarge, 1);
        var last = f.Rows[^1];
        var before = f.Snapshot();
        var request = f.Request("exit", "1", TwiceLarge, 1) with { Fee = "0.1", Reason = "Correct fee" };

        var error = await Assert.ThrowsAsync<ProblemException>(() =>
            f.Service.RecordAsync(f.Play.Id, last.Id, request, default));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(PlayStatus.Closed, f.Play.Status);
        Assert.Equal("0", last.Fee);
        var review = await f.Review.GetAsync(new("all"), default);
        Assert.Equal(direction == "long" ? Large : "-" + Large, review.Metrics.Net);
        Assert.Equal(1, review.Metrics.Count);
    }

    [Theory]
    [InlineData("long", "0.9", false)]
    [InlineData("long", "0.1", false)]
    [InlineData("short", "0.9", false)]
    [InlineData("short", "0.1", false)]
    [InlineData("long", "0.9", true)]
    [InlineData("long", "0.1", true)]
    [InlineData("short", "0.9", true)]
    [InlineData("short", "0.1", true)]
    public async Task Unrepresentable_final_exit_or_correction_changes_no_facts_history_or_lifecycle(
        string direction, string finalPrice, bool correction)
    {
        var f = new PrecisionFixture(direction);
        await f.Record("entry", "1", Large, 0);
        await f.Record("entry", "2", LargePlusOne, 1);
        await f.Record("exit", "1", LargePlusOne, 2);
        await f.Record("exit", "1", TwiceLarge, 3);
        if (correction) await f.Record("exit", "1", Large, 4);
        var last = correction ? f.Rows[^1] : null;
        var before = f.Snapshot();
        var beforeDto = JsonSerializer.Serialize(await f.Service.ManualAsync(f.Play.Id, default));
        var request = f.Request("exit", "1", finalPrice, 4) with { Reason = correction ? "Correct closing price" : null };

        var error = await Assert.ThrowsAsync<ProblemException>(() =>
            f.Service.RecordAsync(f.Play.Id, last?.Id, request, default));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("exact decimal precision", error.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(beforeDto, JsonSerializer.Serialize(await f.Service.ManualAsync(f.Play.Id, default)));
        Assert.Equal(correction ? PlayStatus.Closed : PlayStatus.Open, f.Play.Status);
        var review = await f.Review.GetAsync(new("all"), default);
        Assert.Equal(correction ? 1 : 0, review.Metrics.Count);
        if (correction)
            Assert.Equal(direction == "long" ? "9999999999999999999999999999" : "-9999999999999999999999999999", review.Metrics.Net);
    }

    [Theory]
    [InlineData("long", "0.1", "0.05")]
    [InlineData("short", "-0.1", "-0.15")]
    public async Task Accepted_cancellation_and_fee_correction_agree_in_diary_outcome_and_review(
        string direction, string expectedGross, string correctedNet)
    {
        var f = new PrecisionFixture(direction);
        await f.Record("entry", "2", "1", 0);
        await f.Record("exit", "1", "1.1", 1);
        await f.Record("entry", "1", TwiceLarge, 2);
        await f.Record("exit", "1", TwiceLarge, 3);
        var closed = await f.Record("exit", "1", "1", 4);

        Assert.Equal("closed", closed.Play.Summary.Status);
        Assert.Equal(expectedGross, closed.Totals.GrossResultUsd);
        Assert.Equal(expectedGross, closed.Totals.NetResultUsd);
        await AssertScored(expectedGross);
        var request = f.Request("exit", "1", "1", 4) with { Fee = "0.0500", Reason = "Correct fee" };
        var corrected = await f.Service.RecordAsync(f.Play.Id, f.Rows[^1].Id, request, default);
        Assert.Equal(expectedGross, corrected.Totals.GrossResultUsd);
        Assert.Equal(correctedNet, corrected.Totals.NetResultUsd);
        Assert.Equal("0.0500", corrected.Records[^1].Fee);
        Assert.Equal(6, corrected.History.Count);
        await AssertScored(correctedNet);

        async Task AssertScored(string expected)
        {
            var ledger = ManualLedger.Calculate(f.Play, f.Rows);
            var outcome = PlayOutcomeCalculator.Outcome(new(f.Play, f.Plays.Revisions, ledger.Projection, ManualSource: true));
            Assert.NotNull(outcome);
            Assert.Equal(expected, ManualLedger.Text(outcome.NetResult));
            var review = await f.Review.GetAsync(new("all"), default);
            Assert.Equal(1, review.Metrics.Count);
            Assert.Equal(expected, review.Metrics.Net);
            Assert.Equal(0, review.Metrics.Scratches);
            Assert.Equal(expected.StartsWith('-') ? 1 : 0, review.Metrics.Losses);
            Assert.Equal(expected.StartsWith('-') ? 0 : 1, review.Metrics.Wins);
            Assert.Equal(expected, Assert.Single(review.Plays).Net);
        }
    }

    [Theory]
    [InlineData("long", "6", "7", "8", "4")]
    [InlineData("short", "6", "7", "8", "-4")]
    [InlineData("long", "100", "100.1", "101", "2.8")]
    [InlineData("short", "100", "100.1", "101", "-2.8")]
    [InlineData("long", "6", "7", "100", "280")]
    [InlineData("short", "6", "7", "100", "-280")]
    public async Task Ordinary_repeating_average_keeps_each_partial_exit_and_exact_completed_scoring(
        string direction, string firstPrice, string secondPrice, string exitPrice, string expected)
    {
        var f = new PrecisionFixture(direction);
        await f.Record("entry", "1", firstPrice, 0);
        await f.Record("entry", "2", secondPrice, 1);
        var first = await f.Record("exit", "1", exitPrice, 2);
        var second = await f.Record("exit", "1", exitPrice, 3);
        Assert.Equal("open", first.Play.Summary.Status);
        Assert.Equal("open", second.Play.Summary.Status);
        var closed = await f.Record("exit", "1", exitPrice, 4);
        Assert.Equal("closed", closed.Play.Summary.Status);
        Assert.Equal(expected, closed.Totals.GrossResultUsd);
        Assert.Equal(expected, closed.Totals.NetResultUsd);
        Assert.Equal(expected, (await f.Review.GetAsync(new("all"), default)).Metrics.Net);
    }

    [Theory]
    [InlineData("long", "105", "10")]
    [InlineData("short", "105", "-10")]
    [InlineData("long", "150", "100")]
    [InlineData("short", "150", "-100")]
    public void Ordinary_repeating_partial_with_a_higher_final_price_closes_exactly(
        string direction, string finalPrice, string expected)
    {
        var play = CreatePlay(direction);
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "1", "100", 0),
            Row(play, "entry", "2", "101", 1),
            Row(play, "exit", "1", "102", 2),
            Row(play, "exit", "2", finalPrice, 3)
        ]);

        Assert.Equal(At.AddSeconds(3), ledger.ClosedAt);
        Assert.Equal(expected, ManualLedger.Text(ledger.Gross));
        Assert.Equal(ledger.Gross, ledger.Net);
        Assert.Equal(ledger.Gross, ledger.Projection.Sum(fill => fill.Fill.ClosedPnlUsd));
    }

    [Theory]
    [InlineData("long", "105", "10", "9.95")]
    [InlineData("short", "105", "-10", "-10.05")]
    [InlineData("long", "150", "100", "99.95")]
    [InlineData("short", "150", "-100", "-100.05")]
    public async Task Ordinary_higher_final_price_and_fee_correction_agree_in_diary_outcome_and_review(
        string direction, string finalPrice, string expectedGross, string expectedNet)
    {
        var f = new PrecisionFixture(direction);
        await f.Record("entry", "1", "100", 0);
        await f.Record("entry", "2", "101", 1);
        var partial = await f.Record("exit", "1", "102", 2);
        Assert.Equal("open", partial.Play.Summary.Status);
        var closed = await f.Record("exit", "2", finalPrice, 3);
        Assert.Equal("closed", closed.Play.Summary.Status);
        Assert.Equal(expectedGross, closed.Totals.NetResultUsd);
        Assert.Equal(expectedGross, (await f.Review.GetAsync(new("all"), default)).Metrics.Net);
        var request = f.Request("exit", "2", finalPrice, 3) with { Fee = "0.0500", Reason = "Correct fee" };
        var corrected = await f.Service.RecordAsync(f.Play.Id, f.Rows[^1].Id, request, default);
        Assert.Equal(expectedGross, corrected.Totals.GrossResultUsd);
        Assert.Equal(expectedNet, corrected.Totals.NetResultUsd);
        var ledger = ManualLedger.Calculate(f.Play, f.Rows);
        var outcome = PlayOutcomeCalculator.Outcome(new(f.Play, f.Plays.Revisions, ledger.Projection, ManualSource: true));
        Assert.NotNull(outcome);
        Assert.Equal(expectedNet, ManualLedger.Text(outcome.NetResult));
        Assert.Equal(expectedNet, (await f.Review.GetAsync(new("all"), default)).Metrics.Net);
    }

    [Theory]
    [InlineData("long", "987654224")]
    [InlineData("short", "-987654224")]
    public void Later_partial_exit_preserves_headroom_from_an_earlier_repeating_allocation(string direction, string expected)
    {
        var play = CreatePlay(direction);
        var ledger = ManualLedger.Calculate(play,
        [
            Row(play, "entry", "1", "100", 0),
            Row(play, "entry", "2", "101", 1),
            Row(play, "exit", "1", "102", 2),
            Row(play, "exit", "1", "103", 3),
            Row(play, "exit", "1", "987654321", 4)
        ]);
        Assert.Equal(expected, ManualLedger.Text(ledger.Gross));
        Assert.Equal(ledger.Gross, ledger.Net);
        Assert.Equal(ledger.Gross, ledger.Projection.Sum(fill => fill.Fill.ClosedPnlUsd));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Outcome_excludes_a_completed_net_that_requires_rounding(string direction)
    {
        var play = CreatePlay(direction);
        var revision = play.MarkPlanned(At.AddSeconds(-1)).Revision;
        var ledger = ManualLedger.Calculate(play,
            [Row(play, "entry", "1", Large, 0), Row(play, "exit", "1", TwiceLarge, 1)]);
        play.RecordManualOpen(At);
        play.RecordManualClose(At.AddSeconds(1), ReviewClock.Now);
        ledger.Projection[^1].Fill.Fee = 0.1m;

        Assert.Null(PlayOutcomeCalculator.Outcome(new(play, [revision], ledger.Projection, ManualSource: true), out var problem));
        Assert.Contains("exact precision", problem);
    }

    private static Play CreatePlay(string direction, Account? account = null)
    {
        account ??= new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Precision");
        return new(Guid.NewGuid(), account, new("manual", "BTC"), InstrumentSource.Manual,
            PlayDocuments.Serialize(TestPlays.Plan() with { Direction = direction }), At.AddSeconds(-1));
    }

    private static ManualExecution Row(Play play, string role, string quantity, string price, int seconds)
    {
        var side = PlayDocuments.Read(play.Plan).Direction == "long" ? "buy" : "sell";
        return new()
        {
            Id = Guid.NewGuid(), OwnerId = play.OwnerId, PlayId = play.Id, Role = role,
            EntryId = role == "entry" ? "entry-1" : null,
            Side = role == "entry" ? side : side == "buy" ? "sell" : "buy",
            Quantity = quantity, Price = price, Fee = "0", FeeCurrency = "USDC",
            Source = "Paper broker", OccurredAtUtc = At.AddSeconds(seconds)
        };
    }

    private sealed class PrecisionFixture : IDiaryStore, IPlayExecutionStore, IReviewStore
    {
        public Account Account { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "manual", "Precision");
        public MemoryPlayStore Plays { get; }
        public Play Play { get; }
        public List<ManualExecution> Rows { get; } = [];
        private readonly List<ManualExecutionHistory> history = [];
        private readonly List<DiaryRequest> requests = [];
        public DiaryService Service => new(Plays, this, this, new VenueRegistry([VenueDescriptor.Manual], [], [], [], [], [], [], []), new ReviewClock(), TestUnitOfWork.None);
        public ReviewService Review => new(this, new ReviewClock());

        public PrecisionFixture(string direction)
        {
            Plays = new(Account.OwnerId);
            Plays.Accounts.Add(Account);
            Play = CreatePlay(direction, Account);
            Plays.Add(Play);
            Plays.Add(Play.MarkPlanned(At.AddSeconds(-1)).Revision);
        }

        public ManualExecutionRequest Request(string role, string quantity, string price, int seconds)
        {
            var row = Row(Play, role, quantity, price, seconds);
            return new(Guid.NewGuid(), Play.Version, role, row.EntryId, row.OccurredAtUtc, row.Side,
                quantity, price, row.Fee, row.FeeCurrency, row.Source);
        }

        public Task<ManualExecutionsDto> Record(string role, string quantity, string price, int seconds) =>
            Service.RecordAsync(Play.Id, null, Request(role, quantity, price, seconds), default);

        public string Snapshot() => JsonSerializer.Serialize(new
        {
            Rows, history, requests, Plays.Plays, Plays.Revisions, Plays.Changes, Plays.Links, Plays.Saves
        });

        public Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
        {
            List<ClosedPlayFacts> closed = [];
            if (Play.Status == PlayStatus.Closed)
            {
                var ledger = ManualLedger.Calculate(Play, Rows);
                closed.Add(new(Play, Plays.Revisions, ledger.Projection, ManualSource: true,
                    ManualFeesComplete: ledger.FeesComplete));
            }
            return Task.FromResult(new ReviewFacts([], [Account], closed,
                Play.Status == PlayStatus.Open ? 1 : 0, 0, null));
        }

        public Task<T> WithRequestLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct) => action();
        public Task<T> WithAccountLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken ct) => action();
        public Task<DiaryRequest?> RequestAsync(Guid id, CancellationToken ct) => Task.FromResult(requests.SingleOrDefault(r => r.Id == id));
        public Task<List<ManualExecution>> ManualAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.ToList());
        public Task<List<ManualExecutionHistory>> ManualHistoryAsync(Guid id, CancellationToken ct) => Task.FromResult(history.ToList());
        public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => Plays.AccountAsync(id, ct);
        public Task<List<Play>> ActivePlaysAsync(Guid id, CancellationToken ct, bool settling = false) => Task.FromResult(Plays.Plays.ToList());
        public Task<List<ImportedOrder>> OrdersAsync(Guid id, CancellationToken ct) => Task.FromResult(new List<ImportedOrder>());
        public Task<List<PlayOrderLink>> LinksAsync(Guid id, CancellationToken ct) => Task.FromResult(Plays.Links.ToList());
        public Task<List<ImportedFill>> FillsAsync(Guid id, IReadOnlyCollection<OrderKey> orders, CancellationToken ct) => Task.FromResult(new List<ImportedFill>());
        public Task<ActivityFacts> ActivityAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => Plays.SaveAsync(ct);
        public void Add(ManualExecution row) => Rows.Add(row);
        public void Add(ManualExecutionHistory row) => history.Add(row);
        public void Add(DiaryRequest row) => requests.Add(row);
        public void Add(PlayOrderLink link) => Plays.Links.Add(link);
        public void Add(PlayStatusChange change) => Plays.Add(change);
    }
}
