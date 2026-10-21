using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 9a (D-23): settled funding is read in windows that fit the venue's page budget, extending the stored
/// coverage so each Refresh resumes until the lookback is covered, and says so while it is partial.
/// </summary>
public sealed class FundingWindowsTests
{
    private static readonly DateTimeOffset To = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Account Lighter = new(Guid.NewGuid(), Guid.NewGuid(), "lighter", "Lighter");
    private static readonly Func<CancellationToken, Task> Allowed = _ => Task.CompletedTask;

    /// <summary>Hourly settlements per market; a read holding more rows than its budget is truncated, oldest rows kept.</summary>
    private sealed class Venue(int markets, int budget) : IVenueFundingReader
    {
        public string VenueId => "lighter";
        public List<(DateTimeOffset From, DateTimeOffset To)> Reads { get; } = [];
        public Func<int, Exception?> Fail { get; set; } = _ => null;
        public Func<int, VenueFundingReadResult?> Override { get; set; } = _ => null;
        public int? Hang { get; set; }

        public async Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            Reads.Add((from, to));
            if (Reads.Count == Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Fail(Reads.Count) is { } error) throw error;
            if (Override(Reads.Count) is { } result) return result;
            var rows = new List<VenueFundingPayment>();
            for (var hour = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero); hour <= to; hour = hour.AddHours(1))
                if (hour >= from)
                    for (var market = 0; market < markets; market++)
                        rows.Add(new($"{market}:{hour.ToUnixTimeSeconds()}", "BTC", market.ToString(), -0.01m, "USDC", hour, "{}"));
            var truncated = rows.Count > budget;
            return new VenueFundingReadResult(rows.Take(budget).ToList(), from, to, !truncated,
                truncated ? "Incomplete Lighter settled funding: the five-page limit was reached." : "Lighter settled funding.")
            { Truncated = truncated };
        }
    }

    private static Task<FundingWindowRead> Read(Venue venue, DateTimeOffset to, (DateTimeOffset, DateTimeOffset)? covered,
        FundingOptions? options = null, Func<CancellationToken, Task>? beforeRead = null) =>
        FundingWindows.ReadAsync(venue, Lighter, to, covered, options ?? new FundingOptions(), beforeRead ?? Allowed, TimeProvider.System, default);

    [Fact]
    public async Task A_page_budget_that_cannot_hold_30_days_is_covered_window_by_window_across_refreshes()
    {
        // Two markets of hourly funding: 1,442 rows in 30 days against a 500-row budget (Lighter's five pages).
        var venue = new Venue(markets: 2, budget: 500);
        var options = new FundingOptions { MaxReads = 4 };
        var first = (await Read(venue, To, null, options)).Result;
        // 30 days and 15 days do not fit; 7.5-day windows do, newest first, until the read limit.
        Assert.Equal([TimeSpan.FromDays(30), TimeSpan.FromDays(15), TimeSpan.FromDays(7.5), TimeSpan.FromDays(7.5)],
            venue.Reads.Select(r => r.To - r.From));
        Assert.True(first.Complete);
        Assert.Equal((To.AddDays(-15), To), (first.FromUtc, first.ToUtc));
        Assert.Equal(2 * (15 * 24 + 1), first.Payments.Count); // The shared boundary hour is read twice and kept once.
        Assert.Contains("covered from 2026-10-06 12:00 to 2026-10-21 12:00 UTC", first.Notice);
        Assert.Contains("refresh again", first.Notice);
        Assert.Contains("Missing funding is not zero.", first.Notice);

        // The next Refresh, an hour later, reads the new hour and the recheck day, then resumes before the stored coverage.
        venue.Reads.Clear();
        var later = To.AddHours(1);
        var second = (await Read(venue, later, (first.FromUtc, first.ToUtc), options)).Result;
        Assert.Equal((To.AddDays(-1), later), venue.Reads[0]);
        Assert.All(venue.Reads.Skip(1), r => Assert.True(r.To <= To.AddDays(-15)));
        Assert.True(second.Complete);
        Assert.Equal((later.AddDays(-30), later), (second.FromUtc, second.ToUtc));
        Assert.DoesNotContain("refresh again", second.Notice);
        Assert.Equal("Lighter settled funding.", second.Notice);
    }

    [Fact]
    public async Task Covered_funding_is_read_again_only_for_the_recheck_span()
    {
        var venue = new Venue(markets: 1, budget: 10_000);
        var result = (await Read(venue, To, (To.AddDays(-40), To.AddHours(-1)))).Result;
        Assert.Equal([(To.AddHours(-25), To)], venue.Reads);
        Assert.True(result.Complete);
        Assert.Equal((To.AddDays(-30), To), (result.FromUtc, result.ToUtc));
        Assert.Equal(26, result.Payments.Count);

        // Without a recheck, only the new hour; and a stored coverage reaching the boundary reads nothing.
        venue.Reads.Clear();
        await Read(venue, To, (To.AddDays(-40), To.AddHours(-1)), new FundingOptions { Recheck = TimeSpan.Zero });
        Assert.Equal([(To.AddHours(-1), To)], venue.Reads);
        venue.Reads.Clear();
        var none = (await Read(venue, To, (To.AddDays(-40), To), new FundingOptions { Recheck = TimeSpan.Zero })).Result;
        Assert.Empty(venue.Reads);
        Assert.Equal((To.AddDays(-30), To, true), (none.FromUtc, none.ToUtc, none.Complete));
    }

    [Fact]
    public async Task A_venue_that_fits_reads_once_as_before()
    {
        var venue = new Venue(markets: 3, budget: 10_000);
        var result = (await Read(venue, To, null)).Result;
        Assert.Equal([(To.AddDays(-30), To)], venue.Reads);
        Assert.True(result.Complete);
        Assert.Equal("Lighter settled funding.", result.Notice);
    }

    [Fact]
    public async Task Stored_coverage_older_than_the_lookback_does_not_steer_the_read()
    {
        var venue = new Venue(markets: 1, budget: 10_000);
        var result = (await Read(venue, To, (To.AddDays(-60), To.AddDays(-40)))).Result;
        Assert.Equal([(To.AddDays(-30), To)], venue.Reads);
        Assert.Equal((To.AddDays(-30), To), (result.FromUtc, result.ToUtc));
    }

    [Fact]
    public async Task Other_incompleteness_is_returned_as_the_venue_said_without_halving()
    {
        var venue = new Venue(markets: 1, budget: 10_000);
        var refused = new VenueFundingReadResult([], To.AddDays(-30), To, false, "Incomplete Lighter settled funding: a usable read-only token is required.");
        venue.Override = _ => refused;
        var read = await Read(venue, To, null);
        Assert.Single(venue.Reads);
        Assert.Same(refused, read.Result);
    }

    [Fact]
    public async Task Halving_stops_at_the_read_limit_or_minimum_window_and_keeps_the_venues_partial_result()
    {
        var venue = new Venue(markets: 1, budget: 0);
        var read = await Read(venue, To, null);
        Assert.Equal(8, venue.Reads.Count);
        Assert.False(read.Result.Complete);
        Assert.Equal(venue.Reads[^1], (read.Result.FromUtc, read.Result.ToUtc));

        venue.Reads.Clear();
        await Read(venue, To, null, new FundingOptions { Window = TimeSpan.FromHours(4), MinimumWindow = TimeSpan.FromHours(1), MaxReads = 100 });
        Assert.Equal([TimeSpan.FromHours(4), TimeSpan.FromHours(2), TimeSpan.FromHours(1)], venue.Reads.Select(r => r.To - r.From));
        Assert.All(venue.Reads, r => Assert.Equal(0, r.From.Ticks % TimeSpan.TicksPerMillisecond));
    }

    [Fact]
    public async Task A_failure_after_progress_keeps_the_completed_windows()
    {
        var venue = new Venue(markets: 2, budget: 500) { Fail = n => n == 4 ? new VenueReadException("Upstream detail must not appear.") : null };
        var read = await Read(venue, To, null);
        Assert.Null(read.RetryAtUtc);
        Assert.True(read.Result.Complete);
        Assert.Equal((To.AddDays(-7.5), To), (read.Result.FromUtc, read.Result.ToUtc));
        Assert.Contains("a later window could not be read", read.Result.Notice);
        Assert.DoesNotContain("Upstream", read.Result.Notice);
    }

    [Fact]
    public async Task The_funding_time_budget_cancels_a_slow_read_and_keeps_what_completed()
    {
        var options = new FundingOptions { TimeBudget = TimeSpan.FromMilliseconds(300) };
        var slow = new Venue(markets: 2, budget: 500) { Hang = 4 };
        var read = await Read(slow, To, null, options);
        Assert.Equal(4, slow.Reads.Count);
        Assert.Equal((To.AddDays(-7.5), To), (read.Result.FromUtc, read.Result.ToUtc));
        Assert.Contains("time for funding ran out", read.Result.Notice);
        // With nothing completed, the Refresh's funding read is cancelled as before (and reported incomplete by the caller).
        var stuck = new Venue(markets: 1, budget: 10_000) { Hang = 1 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(stuck, To, null, options));
    }

    [Fact]
    public async Task A_rate_limit_after_progress_keeps_it_and_reports_the_wait_and_before_progress_throws()
    {
        var retry = To.AddMinutes(5);
        var venue = new Venue(markets: 2, budget: 500) { Fail = n => n == 4 ? new VenueRateLimitException(retry) : null };
        var read = await Read(venue, To, null);
        Assert.Equal(retry, read.RetryAtUtc);
        Assert.True(read.Result.Complete);
        Assert.Contains("the venue asked Vessel to wait", read.Result.Notice);

        var fresh = new Venue(markets: 1, budget: 10_000) { Fail = _ => new VenueRateLimitException(retry) };
        Assert.Equal(retry, (await Assert.ThrowsAsync<VenueRateLimitException>(() => Read(fresh, To, null))).RetryAtUtc);
        // A venue already waiting is not read at all.
        var waiting = new Venue(markets: 1, budget: 10_000);
        await Assert.ThrowsAsync<VenueRateLimitException>(() => Read(waiting, To, null, beforeRead: _ => throw new VenueRateLimitException(retry)));
        Assert.Empty(waiting.Reads);
    }

    [Fact]
    public async Task A_settlement_read_twice_must_read_the_same()
    {
        var venue = new Venue(markets: 1, budget: 10_000);
        VenueFundingPayment Payment(decimal amount) => new("same", "BTC", "0", amount, "USDC", To.AddDays(-15), "{}");
        venue.Override = n => n switch
        {
            1 => new([], To.AddDays(-30), To, false, "Incomplete.") { Truncated = true },
            2 => new([Payment(-1)], To.AddDays(-15), To, true, "Lighter settled funding."),
            _ => new([Payment(-2)], To.AddDays(-30), To.AddDays(-15), true, "Lighter settled funding."),
        };
        await Assert.ThrowsAsync<VenueReadException>(() => Read(venue, To, null));
    }

    [Fact]
    public async Task One_refresh_keeps_at_most_ten_thousand_payments()
    {
        var venue = new Venue(markets: 1, budget: 10_000);
        venue.Override = n => n switch
        {
            1 => new([], To.AddDays(-30), To, false, "Incomplete.") { Truncated = true },
            _ => new(Enumerable.Range(0, 6_000).Select(i => new VenueFundingPayment($"{n}:{i}", "BTC", "0", -0.01m, "USDC",
                To.AddDays(n == 2 ? -1 : -20), "{}")).ToList(), venue.Reads[^1].From, venue.Reads[^1].To, true, "Lighter settled funding."),
        };
        var result = (await Read(venue, To, null)).Result;
        Assert.Equal(3, venue.Reads.Count);
        Assert.Equal(6_000, result.Payments.Count);
        Assert.Equal((To.AddDays(-15), To), (result.FromUtc, result.ToUtc));
        Assert.Contains("payment limit", result.Notice);
    }

    [Theory]
    [InlineData(32, 7, 1, 8, 1)]
    [InlineData(30, 31, 1, 8, 1)]
    [InlineData(30, 7, 0, 8, 1)]
    [InlineData(30, 7, 1, 0, 1)]
    [InlineData(30, 7, 1, 8, -1)]
    [InlineData(30, 7, 1, 8, 31)]
    [InlineData(30, 7, 1, 8, 1, 0)]
    [InlineData(30, 7, 1, 8, 1, 61)]
    public void Options_are_bounded(int lookbackDays, int windowDays, int minimumMinutes, int maxReads, int recheckDays, int budgetSeconds = 20)
    {
        new FundingOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new FundingOptions
        {
            Lookback = TimeSpan.FromDays(lookbackDays), Window = TimeSpan.FromDays(windowDays),
            MinimumWindow = TimeSpan.FromMinutes(minimumMinutes), MaxReads = maxReads, Recheck = TimeSpan.FromDays(recheckDays),
            TimeBudget = TimeSpan.FromSeconds(budgetSeconds),
        }.Validate());
    }
}
