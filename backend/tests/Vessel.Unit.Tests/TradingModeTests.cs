using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Application.Workspace;
using Vessel.Domain.Common;

namespace Vessel.Tests;

/// <summary>Issue #113: the type changes no wire or stored value. Each mode is exactly the string it always was.</summary>
public sealed class TradingModeTests
{
    private sealed record Optional([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TradingMode? Mode);

    [Theory]
    [InlineData(TradingMode.Real, "real")]
    [InlineData(TradingMode.Paper, "paper")]
    public void Each_mode_is_its_existing_string_on_the_wire_and_in_storage(TradingMode mode, string value)
    {
        Assert.Equal(value, mode.ToValue());
        Assert.Equal(mode, TradingModes.Parse(value));
        foreach (var options in new[] { JsonSerializerOptions.Default, JsonSerializerOptions.Web })
        {
            Assert.Equal($"\"{value}\"", JsonSerializer.Serialize(mode, options));
            Assert.Equal(mode, JsonSerializer.Deserialize<TradingMode>($"\"{value}\"", options));
            Assert.Equal($$"""{"mode":"{{value}}"}""", JsonSerializer.Serialize(new Optional(mode), JsonSerializerOptions.Web));
        }
    }

    [Fact]
    public void An_absent_optional_mode_stays_absent_and_anything_else_is_refused()
    {
        Assert.Equal("{}", JsonSerializer.Serialize(new Optional(null), JsonSerializerOptions.Web));
        foreach (var json in new[] { "\"Real\"", "\"PAPER\"", "\"\"", "1", "0", "true" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TradingMode>(json, JsonSerializerOptions.Web));
        foreach (var value in new[] { "Real", "Paper", "", " real", "1", null })
        {
            Assert.False(TradingModes.TryParse(value, out _));
            Assert.Equal(TradingModes.Invalid, Assert.Throws<DomainValidationException>(() => TradingModes.Parse(value)).Message);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => default(TradingMode).ToValue());
        Assert.Throws<DomainValidationException>(() => TradingModes.Validate(default));
    }

    [Fact]
    public void An_absent_request_mode_is_the_real_journal_as_before()
    {
        Assert.Equal(TradingMode.Real, TradingModes.FromRequest(null));
        Assert.Equal(TradingMode.Paper, TradingModes.FromRequest("paper"));
        Assert.Throws<DomainValidationException>(() => TradingModes.FromRequest("both"));
        Assert.Equal(TradingMode.Real, WorkspaceService.ValidateTradingMode(null));
        Assert.Equal(400, Assert.Throws<Vessel.Application.Common.ProblemException>(() => WorkspaceService.ValidateTradingMode("Paper")).StatusCode);
    }

    [Fact]
    public void Account_and_portfolio_responses_keep_the_mode_strings_and_field_order()
    {
        var account = new AccountDto(Guid.Empty, null, "A", "manual", null, null, null, "manual", null, 0, null, true, 1,
            null, null, null, null, null, null, null, null, TradingMode.Paper, StartingCapitalUsd: "4000");
        var json = JsonSerializer.Serialize(account, JsonSerializerOptions.Web);
        Assert.Contains("\"availableBalanceSource\":null,\"tradingMode\":\"paper\",\"startingCapitalUsd\":\"4000\"", json, StringComparison.Ordinal);
        var portfolio = new PortfolioDto(Guid.Empty, "P", 0, null, "unavailable", null, "unavailable", "folder", null, 0, "unavailable", 0,
            TradingMode.Real);
        Assert.EndsWith("\"enabledAccountCount\":0,\"tradingMode\":\"real\"}", JsonSerializer.Serialize(portfolio, JsonSerializerOptions.Web),
            StringComparison.Ordinal);
    }
}
