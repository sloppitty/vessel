using System.Net;
using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

public sealed partial class LighterAdapterTests
{
    private static string CoverageSnapshot(Action<JsonNode>? change = null)
    {
        var root = JsonNode.Parse(Snapshot)!;
        root["total"] = 1;
        root["next_cursor"] = "";
        change?.Invoke(root);
        return root.ToJsonString();
    }

    private static Handler CoverageHandler(string? snapshot = null) => new()
    {
        Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal)
            ? Response(snapshot ?? CoverageSnapshot()) : null
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_current_positions_are_complete_when_the_index_response_is_verified_and_uncapped(bool empty)
    {
        var snapshot = CoverageSnapshot(root =>
        {
            if (empty) root["accounts"]![0]!["positions"] = new JsonArray();
        });
        using var handler = CoverageHandler(snapshot);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.False(result.FillHistoryTruncated);
        Assert.Equal(Now.AddMinutes(-1), result.OldestRawFillAtUtc);
        Assert.Equal(empty ? 0 : 1, result.Snapshot.Positions.Count);
        Assert.Equal(1000.0012345678901234m, result.Snapshot.AccountValueUsd);
        Assert.Equal(939.9012345678901234m, result.Snapshot.AvailableMarginUsd);
        var fill = Assert.Single(result.Fills);
        Assert.Equal("9223372036854775806", fill.SourceFillId);
        Assert.Equal("9007199254740995", fill.OrderId);
        Assert.Equal((0m, 2.123400m, ExecutionFacts.FeeReported), (fill.Fee, fill.ClosedPnlUsd, fill.FeeBasis));
        Assert.All(handler.Requests, request => Assert.Null(request.Token));
    }

    [Theory]
    [InlineData("missing", null)]
    [InlineData("next_cursor", null)]
    [InlineData("next_cursor", "\"another-page\"")]
    [InlineData("next_cursor", "null")]
    [InlineData("next_cursor", "123")]
    [InlineData("total", null)]
    [InlineData("total", "2")]
    public async Task Coverage_missing_or_partial_account_markers_do_not_assert_flatness(string field, string? value)
    {
        var snapshot = field == "missing" ? Snapshot : CoverageSnapshot(root =>
        {
            if (value is null) root.AsObject().Remove(field);
            else root[field] = JsonNode.Parse(value);
        });
        using var handler = CoverageHandler(snapshot);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.False(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(-0.001m, Assert.Single(result.Snapshot.Positions).SignedQuantity);
        Assert.Single(result.Fills);
    }

    [Fact]
    public async Task Coverage_position_list_at_the_local_cap_is_not_complete()
    {
        var snapshot = CoverageSnapshot(root => root["accounts"]![0]!["positions"] =
            new JsonArray(Enumerable.Range(0, 2048).Select(_ => (JsonNode?)new JsonObject { ["position"] = "0" }).ToArray()));
        using var handler = CoverageHandler(snapshot);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.False(result.PositionsComplete);
        Assert.Empty(result.Snapshot.Positions);
    }

    [Fact]
    public async Task Coverage_fill_omissions_preserve_valid_facts_without_asserting_complete_history()
    {
        var missingPnl = Change(Trade, "ask_account_pnl", null);
        var omittedZeroFee = Change(Trade, "maker_fee", null);
        var feeBearing = Change(Trade, "maker_fee", JsonValue.Create(1));
        var selfTrade = Change(Trade, "bid_account_id", JsonValue.Create(Source));
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(string.Join(",", Trade, missingPnl, omittedZeroFee, feeBearing, selfTrade)))
            : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.False(result.FillHistoryTruncated);
        Assert.Equal("9223372036854775806", Assert.Single(result.Fills).SourceFillId);
        Assert.Contains("Omitted executions: 1 without reported PnL, 1 fee-bearing or unverified-fee, 1 self-trade.", result.HistoryNotice);
        // One missing-PnL and one fee execution. The zero-fee duplicate is valid; self-trades leave position unchanged.
        Assert.Equal(2, result.OmittedExecutions);
    }

    // Phase 0, 0.1 (D-25): review probe verify-new-dotnet/NewVerify.cs LighterNotice. A venue-forced execution
    // with a zero fee is not a fee-bearing one: it is named for what it is and still counts as omitted.
    [Theory]
    [InlineData("liquidation", "Omitted executions: 1 liquidation.")]
    [InlineData("deleverage", "Omitted executions: 1 deleverage.")]
    [InlineData("market-settlement", "Omitted executions: 1 market settlement.")]
    public void Coverage_a_forced_execution_with_a_zero_fee_is_named_rather_than_called_fee_bearing(string type, string notice)
    {
        var catalogue = new LighterReader.Catalogue([new("1", "BTC", true, new("BTC", 5, 50, "USDC"))], new HashSet<string>(), LighterExchange.Mainnet);
        var row = Change(Change(Trade, "type", JsonValue.Create(type)), "integrator_maker_fee", JsonValue.Create(0));
        using var document = System.Text.Json.JsonDocument.Parse(Trades(row));
        var page = LighterReader.ReadFills(document.RootElement, catalogue, Source, Now.AddMinutes(5), true);
        Assert.Empty(page.Fills);
        Assert.Equal(0, page.Fees);
        Assert.Equal(1, page.Liquidations + page.Deleverages + page.Settlements);
        Assert.Equal(notice, LighterReader.OmissionNotice(page.MissingPnl, page.Fees, page.Liquidations, page.Deleverages, page.Settlements, page.SelfTrades));
    }

    [Fact]
    public async Task Coverage_forced_executions_are_counted_apart_from_fee_bearing_ones_and_stay_omitted()
    {
        var forced = (string type, string? fee) =>
        {
            var row = Change(Trade, "type", JsonValue.Create(type));
            return fee is null ? row : Change(row, "maker_fee", JsonValue.Create(int.Parse(fee)));
        };
        // A forced row with a nonzero fee is still forced: its type decides first.
        var rows = new[] { Trade, forced("liquidation", null), forced("deleverage", null), forced("deleverage", "1"),
            forced("market-settlement", null), Change(Trade, "maker_fee", JsonValue.Create(1)) };
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(string.Join(",", rows))) : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Equal("9223372036854775806", Assert.Single(result.Fills).SourceFillId);
        Assert.Contains("Omitted executions: 1 fee-bearing or unverified-fee, 1 liquidation, 2 deleverages, 1 market settlement.", result.HistoryNotice);
        Assert.Equal(5, result.OmittedExecutions);
        Assert.False(result.FillHistoryComplete);
    }

    [Fact]
    public async Task Coverage_a_single_missing_pnl_execution_is_a_known_omission_without_truncation()
    {
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(string.Join(",", Trade, Change(Trade, "ask_account_pnl", null))))
            : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.False(result.FillHistoryTruncated);
        Assert.Equal(1, result.OmittedExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_capped_or_partially_refused_history_remains_incomplete(bool refused)
    {
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        var pages = 0;
        handler.Reply = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)) return snapshotReply(request);
            pages++;
            if (refused && pages == 2) return Response("""{"code":21106,"message":"invalid auth"}""", HttpStatusCode.Unauthorized);
            return Response(Change(Trades(Trade), "next_cursor", JsonValue.Create("page-" + pages)));
        };
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(refused ? (bool?)null : true, result.FillHistoryTruncated);
        Assert.Equal(refused ? (DateTimeOffset?)null : Now.AddMinutes(-1), result.OldestRawFillAtUtc);
        Assert.Single(result.Fills); // Repeated source IDs still deduplicate.
        Assert.Equal(refused ? 2 : 5, pages);
        Assert.Contains(refused ? "history is unavailable" : "five-page history limit", result.HistoryNotice);
    }

    [Fact]
    public async Task Coverage_fill_cap_and_oldest_boundary_are_independent_of_fee_and_pnl_exclusions()
    {
        var older = Now.AddMinutes(-20);
        var omitted = Change(Trade, "maker_fee", JsonValue.Create(1));
        omitted = Change(omitted, "timestamp", JsonValue.Create(older.ToUnixTimeMilliseconds()));
        var pageRows = string.Join(",", Enumerable.Repeat(omitted, 99).Prepend(Trade));
        var pages = 0;
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Change(Trades(pageRows), "next_cursor", JsonValue.Create(++pages < 5 ? "page-" + pages : "")))
            : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Equal(5, pages);
        Assert.True(result.FillHistoryTruncated); // Raw 500-row cap, although the last page has no cursor.
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(older, result.OldestRawFillAtUtc);
        Assert.Equal(Now.AddMinutes(-1), Assert.Single(result.Fills).OccurredAtUtc);
        Assert.Contains("495 fee-bearing", result.HistoryNotice);
    }

    [Fact]
    public async Task Coverage_excluded_raw_timestamp_gaps_remain_unknown_even_when_a_later_page_has_valid_times()
    {
        var gap = Change(Change(Trade, "maker_fee", JsonValue.Create(1)), "timestamp", null);
        var pages = 0;
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Change(Trades(++pages == 1 ? gap : Trade), "next_cursor", JsonValue.Create(pages == 1 ? "page-2" : "")))
            : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.False(result.FillHistoryTruncated);
        Assert.False(result.FillHistoryComplete);
        Assert.Null(result.OldestRawFillAtUtc);
        Assert.Single(result.Fills);
    }

    [Fact]
    public async Task Coverage_unavailable_or_blocked_fill_reads_have_unknown_truncation_and_boundary()
    {
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/accountLimits", StringComparison.Ordinal)
            ? Response("""{"code":200,"current_maker_fee_tick":1,"current_taker_fee_tick":0,"user_tier":"plus"}""")
            : snapshotReply(request);
        var result = await Reader(handler, new(Token)).ReadAsync(Account(), default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Null(result.FillHistoryTruncated);
        Assert.Null(result.OldestRawFillAtUtc);
        Assert.Empty(result.Fills);
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("/trades?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coverage_missing_token_keeps_order_read_failure_and_public_position_coverage_separate()
    {
        using var handler = CoverageHandler();
        var reader = Reader(handler);
        await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadOrdersAsync(Account(), default));
        Assert.Empty(handler.Requests);
        var result = await reader.ReadAsync(Source, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public async Task Coverage_verified_current_orders_are_complete_only_below_the_active_cap(int count, bool complete)
    {
        var rows = Enumerable.Range(0, count).Select(i =>
        {
            var id = (9007199254740995L + i).ToString();
            return Change(Change(Order, "order_index", JsonValue.Create(id)), "order_id", JsonValue.Create(id));
        });
        using var handler = new Handler { Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/accountActiveOrders", StringComparison.Ordinal)
                ? Response(Orders(string.Join(",", rows))) : null };
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.Equal(complete, result.OpenOrdersComplete);
        Assert.Equal(count, result.Orders.Count);
        Assert.All(result.Orders, order =>
        {
            Assert.Equal(VenueOrderStatus.Open, order.Status);
            Assert.Equal((80000.01m, 80000.1m, "1"), (order.LimitPrice, order.TriggerPrice, order.VenueContractId));
        });
    }

    [Theory]
    [InlineData("cursor", "\"more\"", 1)]
    [InlineData("cursor", "null", 1)]
    [InlineData("cursor", "123", 1)]
    [InlineData("order_flags", "1", 0)]
    [InlineData("type", "\"twap\"", 0)]
    [InlineData("zero-size", null, 0)]
    [InlineData("status", "\"unknown\"", 1)]
    public async Task Coverage_partial_or_omitted_active_orders_cannot_assert_missing_protection(string field, string? value, int expected)
    {
        var row = field == "zero-size"
            ? Change(Change(Order, "initial_base_amount", JsonValue.Create("0")), "remaining_base_amount", JsonValue.Create("0"))
            : field == "cursor" ? Order : Change(Order, field, JsonNode.Parse(value!));
        var body = Orders(row);
        if (field == "cursor")
        {
            var root = JsonNode.Parse(body)!;
            root["next_cursor"] = JsonNode.Parse(value!);
            body = root.ToJsonString();
        }
        using var handler = new Handler { Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/accountActiveOrders", StringComparison.Ordinal) ? Response(body) : null };
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal(expected, result.Orders.Count);
    }

    [Fact]
    public async Task Coverage_capped_inactive_history_does_not_invalidate_a_complete_current_order_list()
    {
        var pages = 0;
        using var handler = new Handler { Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/accountInactiveOrders", StringComparison.Ordinal)
                ? Response($$"""{"code":200,"orders":[],"next_cursor":"page-{{++pages}}"}""") : null };
        var result = await Reader(handler, new(Token)).ReadOrdersAsync(Account(), default);
        Assert.True(result.OpenOrdersComplete);
        Assert.Single(result.Orders);
        Assert.Equal(5, pages);
        Assert.Contains("five-page history limit", result.Notice);
    }

    [Fact]
    public async Task Coverage_failed_history_request_still_fails_the_whole_order_read()
    {
        using var handler = new Handler { Reply = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/accountInactiveOrders", StringComparison.Ordinal)
                ? Response("{}", HttpStatusCode.ServiceUnavailable) : null };
        await Assert.ThrowsAsync<VenueReadException>(() => Reader(handler, new(Token)).ReadOrdersAsync(Account(), default));
    }
}
