using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Api;

namespace Vessel.Tests;

/// <summary>
/// Issue #113: the real-only boundary is endpoint metadata, not path matching. Every endpoint of the running app is
/// classified here, so a new endpoint fails until it is listed as real-only (with <c>.RealOnly()</c>) or as mode-aware.
/// </summary>
public sealed class TradingModeBoundaryApiTests
{
    // Endpoints that read or change actual venue facts: discovery, monitoring, risk, retained facts, retrospective Plays and
    // fill allocations. A paper request to any of them is a 409.
    private static readonly string[] RealOnly =
    [
        "GET /api/accounts/{id:guid}/facts",
        "GET /api/accounts/{id:guid}/history-coverage",
        "GET /api/accounts/{id:guid}/import-conflicts",
        "GET /api/activity/retrospective",
        "GET /api/monitor",
        "GET /api/plays/{id:guid}/execution/allocations",
        "GET /api/risk/loss",
        "GET /api/risk/open",
        "GET /api/risk/settings",
        "GET /api/venues/{venueId}/accounts",
        "POST /api/accounts/{id:guid}/history-gaps/{gapId:guid}/backfill",
        "POST /api/plays/retrospective",
        "POST /api/plays/{id:guid}/execution/allocations",
        "POST /api/risk/open/preview",
        "POST /api/venues/{venueId}/accounts/credential",
        "PUT /api/risk/settings",
    ];

    // Endpoints that serve both modes, or that have no trading mode (identity, notifications, events, settings).
    private static readonly string[] ModeAware =
    [
        "DELETE /api/accounts/{id:guid}",
        "DELETE /api/accounts/{id:guid}/credential",
        "DELETE /api/auth/pairings/{id:guid}",
        "DELETE /api/auth/sessions/{id}",
        "DELETE /api/auth/wallets/{id}",
        "DELETE /api/evidence/{id:guid}",
        "DELETE /api/evidence/{id:guid}/markup",
        "DELETE /api/plays/rule",
        "DELETE /api/plays/{id:guid}",
        "DELETE /api/plays/{id:guid}/execution/links/{linkId:guid}",
        "DELETE /api/portfolios/{id:guid}",
        "GET /api/accounts",
        "GET /api/accounts/{id:guid}",
        "GET /api/accounts/{id:guid}/candles",
        "GET /api/accounts/{id:guid}/credential",
        "GET /api/accounts/{id:guid}/fills",
        "GET /api/accounts/{id:guid}/instruments",
        "GET /api/accounts/{id:guid}/market-context",
        "GET /api/accounts/{id:guid}/market-stream",
        "GET /api/accounts/{id:guid}/monitor",
        "GET /api/accounts/{id:guid}/snapshot",
        "GET /api/activity",
        "GET /api/activity/fills",
        "GET /api/auth/config",
        "GET /api/auth/pairings/{id:guid}",
        "GET /api/auth/session",
        "GET /api/auth/sessions",
        "GET /api/auth/wallets",
        "GET /api/events",
        "GET /api/events/stream",
        "GET /api/evidence/{id:guid}/content",
        "GET /api/notifications",
        "GET /api/notifications/head",
        "GET /api/notifications/preferences",
        "GET /api/overview",
        "GET /api/overview/summary",
        "GET /api/paper/settings",
        "GET /api/plays",
        "GET /api/plays/counts",
        "GET /api/plays/next-actions",
        "GET /api/plays/rule",
        "GET /api/plays/search",
        "GET /api/plays/{id:guid}",
        "GET /api/plays/{id:guid}/bundle",
        "GET /api/plays/{id:guid}/execution",
        "GET /api/plays/{id:guid}/history",
        "GET /api/plays/{id:guid}/manual-executions",
        "GET /api/plays/{id:guid}/notes",
        "GET /api/plays/{id:guid}/paper-state",
        "GET /api/plays/{playId:guid}/evidence",
        "GET /api/portfolios",
        "GET /api/portfolios/{id:guid}",
        "GET /api/review",
        "GET /api/review/exclusions",
        "GET /api/review/export",
        "GET /api/review/mistake-tags",
        "GET /api/review/process",
        "GET /api/settings/market-sources",
        "GET /api/settings/reporting",
        "GET /api/sizing",
        "GET /api/system",
        "PATCH /api/auth/profile",
        "PATCH /api/auth/wallets/{id}",
        "PATCH /api/evidence/{id:guid}",
        "PATCH /api/portfolios/{id:guid}",
        "POST /api/accounts",
        "POST /api/accounts/portfolio",
        "POST /api/accounts/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss",
        "POST /api/accounts/{id:guid}/monitor/run",
        "POST /api/accounts/{id:guid}/sync",
        "POST /api/auth/challenge",
        "POST /api/auth/pairings",
        "POST /api/auth/pairings/claim",
        "POST /api/auth/pairings/complete",
        "POST /api/auth/pairings/{id:guid}/approve",
        "POST /api/auth/register",
        "POST /api/auth/sessions/revoke-others",
        "POST /api/auth/sign-out",
        "POST /api/auth/step-up",
        "POST /api/auth/step-up/challenge",
        "POST /api/auth/verify",
        "POST /api/auth/wallets",
        "POST /api/auth/wallets/challenge",
        "POST /api/notifications/claim",
        "POST /api/notifications/read",
        "POST /api/notifications/test",
        "POST /api/notifications/{id:guid}/read",
        "POST /api/plays",
        "POST /api/plays/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss",
        "POST /api/plays/{id:guid}/execution/check",
        "POST /api/plays/{id:guid}/execution/links",
        "POST /api/plays/{id:guid}/execution/links/{linkId:guid}/rebind",
        "POST /api/plays/{id:guid}/execution/rejections",
        "POST /api/plays/{id:guid}/manual-executions",
        "POST /api/plays/{id:guid}/manual-executions/corrections",
        "POST /api/plays/{id:guid}/notes",
        "POST /api/plays/{id:guid}/return-to-open",
        "POST /api/plays/{id:guid}/review/complete",
        "POST /api/plays/{id:guid}/review/not-needed",
        "POST /api/plays/{id:guid}/review/reopen",
        "POST /api/plays/{id:guid}/status",
        "POST /api/plays/{id:guid}/unresolved",
        "POST /api/plays/{playId:guid}/evidence",
        "POST /api/portfolios",
        "PUT /api/accounts/{id:guid}",
        "PUT /api/accounts/{id:guid}/credential",
        "PUT /api/evidence/{id:guid}/markup",
        "PUT /api/notifications/preferences",
        "PUT /api/paper/settings",
        "PUT /api/plays/rule",
        "PUT /api/plays/{id:guid}",
        "PUT /api/plays/{id:guid}/manual-executions/{executionId:guid}",
        "PUT /api/settings/market-sources/{venueId}/{sourceId}",
        "PUT /api/settings/reporting",
        "PUT /api/sizing/settings",
    ];

    private static List<(string Key, RouteEndpoint Endpoint)> ApiEndpoints(CoreApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => ($"{method} {e.RoutePattern.RawText!.TrimEnd('/')}", e)))
            .OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();

    [Fact]
    public void Every_api_endpoint_is_classified_and_exactly_the_real_only_ones_carry_the_metadata()
    {
        using var factory = new CoreApiFactory(Guid.NewGuid());
        var endpoints = ApiEndpoints(factory);
        var unclassified = endpoints.Select(x => x.Key).Except(RealOnly).Except(ModeAware).ToList();
        Assert.True(unclassified.Count == 0, "Classify each new endpoint as RealOnly (and add .RealOnly()) or ModeAware:\n" +
            string.Join("\n", unclassified.Select(key => $"        \"{key}\",")));
        Assert.Empty(RealOnly.Concat(ModeAware).Except(endpoints.Select(x => x.Key)));
        Assert.Empty(RealOnly.Intersect(ModeAware));
        var marked = endpoints.Where(x => x.Endpoint.Metadata.GetMetadata<RealOnlyMetadata>() is not null).Select(x => x.Key).ToList();
        Assert.Equal(RealOnly.Order(StringComparer.Ordinal), marked);
    }

    [Fact]
    public async Task Every_real_only_endpoint_refuses_a_paper_request_with_the_same_409_before_reading_anything()
    {
        // No persistence connection, adapters or body: reaching a handler, a store or the body reader would not be a 409.
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.AuthorizedClient();
        var marked = ApiEndpoints(factory).Where(x => x.Endpoint.Metadata.GetMetadata<RealOnlyMetadata>() is not null).ToList();
        Assert.Equal(RealOnly.Length, marked.Count);
        foreach (var (key, endpoint) in marked)
        {
            var method = key[..key.IndexOf(' ', StringComparison.Ordinal)];
            var path = Path(endpoint.RoutePattern.RawText!);
            foreach (var url in new[] { path, path.ToUpperInvariant() })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url + "?tradingMode=paper");
                if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{key} answered {(int)response.StatusCode}.");
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = problem.RootElement;
                Assert.Equal(["detail", "status", "title", "traceId", "type"],
                    root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
                Assert.Equal((409, "Conflict", TradingModeBoundary.RealOnlyRefusal, "https://tools.ietf.org/html/rfc9110#section-15.5.10"),
                    (root.GetProperty("status").GetInt32(), root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(),
                        root.GetProperty("type").GetString()));
            }
        }
    }

    [Fact]
    public async Task A_real_or_absent_mode_passes_the_boundary_and_an_unknown_mode_is_a_400()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.AuthorizedClient();
        // Past the boundary the handler runs (this host has no database, so it fails there), never the paper refusal.
        foreach (var query in new[] { "", "?tradingMode=real" })
        {
            var response = await client.GetAsync("/api/monitor" + query);
            Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.DoesNotContain(TradingModeBoundary.RealOnlyRefusal, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        foreach (var query in new[] { "?tradingMode=Paper", "?tradingMode=", "?tradingMode=paper&tradingMode=paper" })
        {
            var response = await client.GetAsync("/api/monitor" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Trading mode must be real or paper.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    private static string Path(string pattern) => string.Join('/', pattern.Split('/').Select(segment => segment switch
    {
        ['{', ..] when segment.Contains(":guid", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
        ['{', ..] => "hyperliquid",
        _ => segment,
    }));
}
