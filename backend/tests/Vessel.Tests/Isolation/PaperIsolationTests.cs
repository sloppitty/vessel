using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;

namespace Vessel.Tests.Isolation;

public abstract class PaperIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/paper/settings")]
    [Covers("PUT", "/api/paper/settings")]
    [Covers("GET", "/api/plays/{id:guid}/paper-state")]
    public async Task Paper_settings_and_execution_views_stay_private_to_the_authenticated_owner()
    {
        var (a, b) = await api.Owners();
        await a.Send(HttpMethod.Put, "/api/paper/settings", new { enabled = true, checkIntervalMinutes = 17 });
        await b.Send(HttpMethod.Put, "/api/paper/settings", new { enabled = false, checkIntervalMinutes = 93 });
        var aPlay = await Draft(a);
        var bPlay = await Draft(b);
        var mine = await a.GetJson($"/api/plays/{aPlay}/paper-state?tradingMode=paper");
        Assert.Equal(aPlay, mine.GetProperty("playId").GetGuid());
        Assert.Empty(mine.GetProperty("executions").EnumerateArray());
        var bBefore = await b.GetText($"/api/plays/{bPlay}/paper-state?tradingMode=paper");
        foreach (var (caller, foreign) in new[] { (a, bPlay), (b, aPlay) })
            await Isolated.NotFound(caller, foreign,
                id => new HttpRequestMessage(HttpMethod.Get, $"/API/PlAyS/{id}/PaPeR-StAtE?tradingMode=paper"), "Play not found.");
        await a.Send(HttpMethod.Put, "/api/paper/settings", new { enabled = false, checkIntervalMinutes = 27 });
        var aSettings = await a.GetJson("/api/paper/settings");
        var bSettings = await b.GetJson("/api/paper/settings");
        Assert.Equal(27, aSettings.GetProperty("checkIntervalMinutes").GetInt32());
        Assert.False(aSettings.GetProperty("enabled").GetBoolean());
        Assert.Equal(93, bSettings.GetProperty("checkIntervalMinutes").GetInt32());
        Assert.False(bSettings.GetProperty("enabled").GetBoolean());
        Assert.Equal(bBefore, await b.GetText($"/api/plays/{bPlay}/paper-state?tradingMode=paper"));
        await using var aDb = api.Database.Context(a.Id);
        await using var bDb = api.Database.Context(b.Id);
        Assert.Equal(a.Id, (await aDb.PaperSettings.SingleAsync()).OwnerId);
        Assert.Equal(b.Id, (await bDb.PaperSettings.SingleAsync()).OwnerId);
        Assert.Equal(4000, await aDb.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
        Assert.Equal(4000, await bDb.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
        using var anonymous = api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/paper/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/paper/settings",
            new { enabled = true, checkIntervalMinutes = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/plays/{aPlay}/paper-state?tradingMode=paper")).StatusCode);
    }

    private static async Task<Guid> Draft(Owner owner)
    {
        var portfolio = await owner.Post("/api/portfolios?tradingMode=paper",
            new CreatePortfolioRequest(Isolated.Marker("paper"), TradingMode: "paper"), HttpStatusCode.Created);
        var account = await owner.Post("/api/accounts?tradingMode=paper",
            new CreateAccountRequest(portfolio.GetProperty("id").GetGuid(), "Paper", "hyperliquid", TradingMode: "paper"), HttpStatusCode.Created);
        var play = await owner.Post("/api/plays?tradingMode=paper",
            new CreatePlayRequest(account.GetProperty("id").GetGuid(), "BTC", "venue", "Private simulated idea", TestPlays.Plan()), HttpStatusCode.Created);
        return play.GetProperty("summary").GetProperty("id").GetGuid();
    }
}

public sealed class PaperIsolationTests(IsolationApi api) : PaperIsolationCases(api), IClassFixture<IsolationApi>;
public sealed class WalletPaperIsolationTests(WalletIsolationApi api) : PaperIsolationCases(api), IClassFixture<WalletIsolationApi>;
