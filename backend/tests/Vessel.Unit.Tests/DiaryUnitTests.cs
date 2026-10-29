using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;

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
}
