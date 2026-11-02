using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

/// <summary>Phase 2, item 6b: <c>userFillsByTime</c> pages for an owner-started backfill.</summary>
public sealed partial class HyperliquidAdapterTests
{
    private static Account FillHistoryAccount()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Backfill");
        account.Configure(null, Address, null);
        return account;
    }

    [Fact]
    public async Task Fill_history_reads_one_inclusive_window_parsed_like_userFills_with_its_raw_bounds()
    {
        const long at = 1790812799000;
        var spot = Mutate(Fill, node => { node["coin"] = "@107"; node["tid"] = "7"; node["time"] = at - 5; });
        var responses = new[] { Meta, $"[{spot},{Fill}]" };
        using var handler = new FixtureHandler((index, _) => Task.FromResult(Json(responses[index])));
        using var client = Client(handler);
        IVenueFillHistoryReader reader = new HyperliquidPerpetualReader(client, new TestClock());
        Assert.Equal((2000, 10_000), (reader.PageLimit, reader.Reach));
        var from = DateTimeOffset.FromUnixTimeMilliseconds(at - 1000);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(at);
        var page = await reader.ReadFillPageAsync(FillHistoryAccount(), from, to, default);
        // The spot row counts toward the raw page, by its trade ID and time, but is out of scope, not omitted.
        Assert.Equal((2, 0), (page.RawCount, page.OmittedExecutions));
        var fill = Assert.Single(page.Fills);
        Assert.Equal(Fill, fill.RawJson);
        Assert.Equal([new VenueRawRow("7", DateTimeOffset.FromUnixTimeMilliseconds(at - 5)), new VenueRawRow(fill.SourceFillId, to)], page.Raw);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        var request = payload.RootElement;
        Assert.Equal(("userFillsByTime", Address.ToLowerInvariant(), at - 1000, at, false), (request.GetProperty("type").GetString(),
            request.GetProperty("user").GetString(), request.GetProperty("startTime").GetInt64(), request.GetProperty("endTime").GetInt64(),
            request.GetProperty("aggregateByTime").GetBoolean()));
    }

    [Fact]
    public async Task Fill_history_refuses_rows_outside_the_window_and_windows_that_are_not_whole_past_milliseconds()
    {
        const long at = 1790812799000;
        var responses = new[] { Meta, $"[{Fill}]" };
        using var handler = new FixtureHandler((index, _) => Task.FromResult(Json(responses[index])));
        using var client = Client(handler);
        IVenueFillHistoryReader reader = new HyperliquidPerpetualReader(client, new TestClock());
        // The fill sits one millisecond after this window's end.
        var outside = await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadFillPageAsync(FillHistoryAccount(),
            DateTimeOffset.FromUnixTimeMilliseconds(at - 10), DateTimeOffset.FromUnixTimeMilliseconds(at - 1), default));
        Assert.Equal("Hyperliquid returned an invalid or unsupported response.", outside.Message);
        // A raw row without a trade ID cannot witness continuity.
        var anonymous = Mutate(Fill, node => { node["coin"] = "@107"; node.AsObject().Remove("tid"); });
        using (var unnamed = new FixtureHandler((index, _) => Task.FromResult(Json(new[] { Meta, $"[{anonymous}]" }[index]))))
        using (var unnamedClient = Client(unnamed))
            await Assert.ThrowsAsync<VenueReadException>(() => new HyperliquidPerpetualReader(unnamedClient, new TestClock()).ReadFillPageAsync(
                FillHistoryAccount(), DateTimeOffset.FromUnixTimeMilliseconds(at - 10), DateTimeOffset.FromUnixTimeMilliseconds(at), default));
        foreach (var (from, to) in new[] { (DateTimeOffset.FromUnixTimeMilliseconds(at).AddTicks(1), DateTimeOffset.FromUnixTimeMilliseconds(at + 1)),
                     (DateTimeOffset.FromUnixTimeMilliseconds(at), DateTimeOffset.FromUnixTimeMilliseconds(at - 1)),
                     (DateTimeOffset.FromUnixTimeMilliseconds(StateTime), DateTimeOffset.FromUnixTimeMilliseconds(StateTime + 60_000)) })
            await Assert.ThrowsAsync<VenueReadException>(() => reader.ReadFillPageAsync(FillHistoryAccount(), from, to, default));
    }
}
