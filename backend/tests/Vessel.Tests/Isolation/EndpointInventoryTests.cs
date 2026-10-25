using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Every route the API maps is either named by a two-owner isolation case (<see cref="CoversAttribute"/>) or listed
/// below, exactly, with the reason it holds no owner data. A new route fails this test until someone decides which it
/// is. Both suite hosts (the test scheme and real wallet sessions) are inventoried as they start and map the same routes.
/// </summary>
public sealed class EndpointInventoryTests(IsolationApi api, WalletIsolationApi wallet)
    : IClassFixture<IsolationApi>, IClassFixture<WalletIsolationApi>
{
    /// <summary>Routes without owner-scoped data. Each entry says why.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        // Anonymous liveness: a constant body, no database, no owner.
        ["GET /health/live"] = "anonymous health",
        // Anonymous readiness: database and migration state only, never owner rows.
        ["GET /health/ready"] = "anonymous health",
        // Development-only OpenAPI document: the API's shape, generated from code, never owner data.
        ["GET /openapi/{documentName}.json"] = "development API description",
        // The effective registration policy and an origin hint; anonymous by design.
        ["GET /api/auth/config"] = "public auth configuration",
        // Public sign-in routes run before any session and are anonymous by design (contract §6, §7). They read users,
        // wallets and the allow-list only after a valid signature and return only the signer's own session.
        ["POST /api/auth/challenge"] = "public sign-in",
        ["POST /api/auth/verify"] = "public sign-in",
        ["POST /api/auth/register"] = "public sign-in",
    };

    private static List<string> Routes(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {Normalize(endpoint.RoutePattern.RawText)}"))
            .Distinct().Order(StringComparer.Ordinal).ToList();

    private static string Normalize(string? pattern) => "/" + (pattern ?? "").Trim('/');

    private static HashSet<string> Covered() => typeof(EndpointInventoryTests).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(EndpointInventoryTests).Namespace)
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        .Where(method => method.GetCustomAttributes<FactAttribute>().Any())
        .SelectMany(method => method.GetCustomAttributes<CoversAttribute>()).Select(covers => covers.Route).ToHashSet(StringComparer.Ordinal);

    [PostgresFact]
    public void Every_route_is_covered_or_exactly_exempt()
    {
        var routes = Routes(wallet.Factory.Services);
        var covered = Covered();
        Assert.NotEmpty(routes);
        // The test scheme only stands in for sessions: both hosts map exactly the same routes.
        Assert.Equal(routes, Routes(api.Factory.Services));
        var missing = routes.Where(route => !covered.Contains(route) && !Exempt.ContainsKey(route)).ToList();
        Assert.True(missing.Count == 0, "Routes without an isolation case or exemption:\n" + string.Join("\n", missing));
        // An exemption must name a mapped route.
        var unmapped = Exempt.Keys.Where(route => !routes.Contains(route)).ToList();
        Assert.True(unmapped.Count == 0, "Exemptions for routes that are not mapped:\n" + string.Join("\n", unmapped));
        // Stale names would claim coverage of a route that is not mapped.
        var stale = covered.Where(route => !routes.Contains(route)).ToList();
        Assert.True(stale.Count == 0, "Coverage for routes that are not mapped:\n" + string.Join("\n", stale));
        // Every session route is covered by two-user cases, never exempt.
        var session = routes.Where(route => route[(route.IndexOf(' ') + 1)..].StartsWith("/api/auth/", StringComparison.Ordinal) &&
            !Exempt.ContainsKey(route)).ToList();
        Assert.Equal(SessionRoutes.Order(StringComparer.Ordinal), session);
        Assert.All(session, route => Assert.Contains(route, covered));
    }

    /// <summary>The session routes of contract §6, which require a live session and are covered by two-user cases.</summary>
    private static readonly string[] SessionRoutes =
    [
        "GET /api/auth/session", "POST /api/auth/sign-out", "GET /api/auth/sessions", "DELETE /api/auth/sessions/{id}",
        "POST /api/auth/sessions/revoke-others", "PATCH /api/auth/profile", "GET /api/auth/wallets",
        "POST /api/auth/step-up/challenge", "POST /api/auth/step-up", "POST /api/auth/wallets/challenge", "POST /api/auth/wallets",
        "PATCH /api/auth/wallets/{id}", "DELETE /api/auth/wallets/{id}"
    ];

    [PostgresFact]
    [Covers("GET", "/api/system")]
    public async Task Test_scheme_selects_the_owner_and_issues_the_production_claims()
    {
        var (a, b) = await api.Owners();
        foreach (var owner in new[] { a, b })
        {
            var system = await owner.GetJson("/api/system");
            Assert.Equal(owner.Id, system.GetProperty("owner").GetProperty("id").GetGuid());
            Assert.Equal(TestOwnerAuthHandler.NameOf(owner.Id), system.GetProperty("owner").GetProperty("displayName").GetString());
        }
        Assert.NotEqual(a.Id, b.Id);
    }

    [PostgresFact]
    public async Task Bearer_credentials_are_ignored_and_requests_without_an_owner_are_rejected()
    {
        using var anonymous = api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/portfolios")).StatusCode);
        // Token sign-in was removed: a bearer header authenticates nobody.
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only-retired-bearer-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/portfolios")).StatusCode);
        using var invalid = api.Factory.CreateClient();
        invalid.DefaultRequestHeaders.Add(TestOwnerAuthHandler.Header, Guid.Empty.ToString("D"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync("/api/portfolios")).StatusCode);
    }

    [PostgresFact]
    [Covers("GET", "/api/system")]
    public async Task Session_scheme_selects_the_signed_in_user_as_owner()
    {
        var (a, b) = await wallet.Owners();
        foreach (var owner in new[] { a, b })
        {
            var system = await owner.GetJson("/api/system");
            Assert.Equal(owner.Id, system.GetProperty("owner").GetProperty("id").GetGuid());
            Assert.Equal(owner.Id, (await owner.GetJson("/api/auth/session")).GetProperty("user").GetProperty("id").GetGuid());
        }
        Assert.NotEqual(a.Id, b.Id);
        // No bearer scheme is registered: a bearer header authenticates nobody.
        using var bearer = wallet.Client();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only-retired-bearer-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/portfolios")).StatusCode);
    }
}
