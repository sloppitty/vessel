using System.Net;
using System.Net.Http.Json;
using Vessel.Application.Plays;
using Vessel.Application.Risk;

namespace Vessel.Tests.Isolation;

/// <summary>Open risk, its preview and the soft limit answer each owner from its own Plays and settings only (Phase 2, item 1).</summary>
public abstract class RiskIsolationCases(IsolationHost api)
{
    private static readonly RiskPlanDto Edited = new("long", "quantity", "2", "1", [new("e1", "100", "100", [new("s1", "price", "95", "100")])]);

    /// <summary>A planned Play on a manual account: 250 margin at 3× over 100 is 7.5 units, 37.5 to the stop at 95.</summary>
    private static async Task<(Guid Account, Guid Play)> Planned(Owner owner)
    {
        var account = (await owner.ManualAccount(Isolated.Marker("risk"))).GetProperty("id").GetGuid();
        var draft = await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "manual", Isolated.Marker("play"), TestPlays.Plan()),
            HttpStatusCode.Created);
        var id = draft.GetProperty("summary").GetProperty("id").GetGuid();
        await owner.Post($"/api/plays/{id}/status", new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(), "planned"));
        return (account, id);
    }

    [PostgresFact]
    [Covers("GET", "/api/risk/open")]
    [Covers("POST", "/api/risk/open/preview")]
    [Covers("GET", "/api/risk/settings")]
    [Covers("PUT", "/api/risk/settings")]
    public async Task Open_risk_previews_and_limits_stay_with_their_owner()
    {
        var (a, b) = await api.Owners();
        var (aAccount, aPlay) = await Planned(a);
        var (bAccount, bPlay) = await Planned(b);
        await a.Send(HttpMethod.Put, "/api/risk/settings", new UpdateRiskSettingsRequest("2"));
        foreach (var (owner, play, limit) in new[] { (a, aPlay, "2"), (b, bPlay, (string?)null) })
        {
            var risk = await owner.GetJson("/api/risk/open");
            Assert.Equal([play], risk.GetProperty("plays").EnumerateArray().Select(p => p.GetProperty("playId").GetGuid()));
            Assert.Equal("37.5", risk.GetProperty("long").GetProperty("knownUsd").GetString());
            Assert.Equal(limit, risk.GetProperty("limitPercent").GetString());
            Assert.Equal(limit, (await owner.GetJson("/api/risk/settings")).GetProperty("openRiskLimitPercent").GetString());
        }
        // Another owner's account or Play reads as unknown: the preview measures nothing of theirs.
        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Post, "/api/risk/open/preview")
            { Content = JsonContent.Create(new OpenRiskPreviewRequest(null, id, Edited)) }, "Account not found.");
        // Another owner's Play reads as unknown too: no fallback to a Draft, nothing of theirs measured.
        foreach (var (caller, account, foreign) in new[] { (a, aAccount, bPlay), (b, bAccount, aPlay) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Post, "/api/risk/open/preview")
            { Content = JsonContent.Create(new OpenRiskPreviewRequest(id, account, Edited)) }, "Play not found.");
        // A scope naming another owner's account is not found, like an unknown one.
        foreach (var (caller, foreign) in new[] { (a, bAccount), (b, aAccount) })
            await Isolated.NotFound(caller, foreign, id => new HttpRequestMessage(HttpMethod.Get, $"/api/risk/open?scope=account:{id}"), "Account not found.");
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class RiskIsolationTests(IsolationApi api) : RiskIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletRiskIsolationTests(WalletIsolationApi api) : RiskIsolationCases(api), IClassFixture<WalletIsolationApi>;
