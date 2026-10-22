using System.Net;
using Vessel.Application.Plays;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Phase 2, item 3: each owner's if-then rule is their own, one per trading mode; a Play keeps the version it was planned
/// under, with the owner's answer on revision 1.
/// </summary>
public abstract class RuleIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/plays/rule")]
    [Covers("PUT", "/api/plays/rule")]
    [Covers("DELETE", "/api/plays/rule")]
    public async Task The_rule_is_each_owners_own_per_mode_and_a_Play_keeps_the_version_it_was_planned_under()
    {
        var (a, b) = await api.Owners();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await a.GetJson("/api/plays/rule")).GetProperty("active").ValueKind);
        var set = await a.Send(HttpMethod.Put, "/api/plays/rule", new SetPlayRuleRequest("the stop is hit", "I wait a day"));
        Assert.Equal(1, set.GetProperty("active").GetProperty("version").GetInt32());
        // Another owner, and the other mode, have none.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await b.GetJson("/api/plays/rule")).GetProperty("active").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await a.GetJson("/api/plays/rule?tradingMode=paper")).GetProperty("active").ValueKind);

        // Planning acknowledges version 1; revision 1 keeps it, and the Play carries its text.
        var account = (await a.ManualAccount(Isolated.Marker("rule"))).GetProperty("id").GetGuid();
        var draft = await a.Post("/api/plays", new CreatePlayRequest(account, "BTC", "manual", Isolated.Marker("play"), TestPlays.Plan()), HttpStatusCode.Created);
        var id = draft.GetProperty("summary").GetProperty("id").GetGuid();
        var planned = await a.Post($"/api/plays/{id}/status", new ChangePlayStatusRequest(draft.GetProperty("summary").GetProperty("version").GetInt64(),
            "planned", Rule: new(1, "acknowledged")));
        Assert.Equal(1, planned.GetProperty("rule").GetProperty("version").GetInt32());
        var revision = (await a.GetJson($"/api/plays/{id}/history")).GetProperty("revisions")[0].GetProperty("rule");
        Assert.Equal((1, "acknowledged", "I wait a day"), (revision.GetProperty("version").GetInt32(), revision.GetProperty("answer").GetString(),
            revision.GetProperty("then").GetString()));

        // A stale write is refused; a new version leaves the planned Play on version 1.
        await a.Send(HttpMethod.Put, "/api/plays/rule", new SetPlayRuleRequest("x", "y"), HttpStatusCode.Conflict);
        var second = await a.Send(HttpMethod.Put, "/api/plays/rule", new SetPlayRuleRequest("the stop is hit twice", "I stop for the week", ExpectedVersion: 1));
        Assert.Equal(2, second.GetProperty("active").GetProperty("version").GetInt32());
        var play = await a.GetJson($"/api/plays/{id}");
        Assert.Equal((1, "I wait a day"), (play.GetProperty("rule").GetProperty("version").GetInt32(), play.GetProperty("rule").GetProperty("then").GetString()));

        // Another owner's version and Play are not theirs.
        await b.Send(HttpMethod.Put, "/api/plays/rule", new SetPlayRuleRequest("x", "y", ExpectedVersion: 2), HttpStatusCode.Conflict);
        await b.Send(HttpMethod.Put, "/api/plays/rule", new SetPlayRuleRequest("x", "y", id), HttpStatusCode.NotFound);
        Assert.Equal(2, (await a.GetJson("/api/plays/rule")).GetProperty("active").GetProperty("version").GetInt32());

        // Archiving leaves none; archiving again is stale.
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            (await a.Send(HttpMethod.Delete, "/api/plays/rule?expectedVersion=2")).GetProperty("active").ValueKind);
        await a.Send(HttpMethod.Delete, "/api/plays/rule?expectedVersion=2", expected: HttpStatusCode.Conflict);
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class RuleIsolationTests(IsolationApi api) : RuleIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletRuleIsolationTests(WalletIsolationApi api) : RuleIsolationCases(api), IClassFixture<WalletIsolationApi>;
