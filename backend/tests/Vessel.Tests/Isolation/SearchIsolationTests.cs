using System.Net;
using Vessel.Application.Plays;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Phase 2, item 13: journal search reads the caller's own projection in the mode asked for, and the list's outcome of a
/// closed venue Play (F-07) is Review's.
/// </summary>
public abstract class SearchIsolationCases(IsolationHost api)
{
    [PostgresFact]
    [Covers("GET", "/api/plays/search")]
    public async Task Search_finds_only_the_callers_Plays_in_the_mode_asked_for()
    {
        var (a, b) = await api.Owners();
        var marker = Isolated.Marker("journal");
        var account = (await a.SyncedVenueAccount(Isolated.Marker("search"))).GetProperty("id").GetGuid();
        var id = await a.ClosedPlay(account, Isolated.Marker("closed"));
        await a.Post($"/api/plays/{id}/notes", new AddPlayNoteRequest(Guid.NewGuid(), $"Watched {marker} on the close"));

        var found = await a.GetJson($"/api/plays/search?q={marker}");
        Assert.Equal((1, 0), (found.GetProperty("total").GetInt32(), found.GetProperty("notIndexed").GetInt32()));
        var hit = found.GetProperty("items")[0];
        Assert.Equal((id, "log"), (hit.GetProperty("play").GetProperty("id").GetGuid(), hit.GetProperty("field").GetString()));

        // The list's outcome is Review's: its latest exit, venue result and estimated R, here unknown for a plan written later.
        var outcome = (await a.GetJson("/api/plays?view=closed")).GetProperty("plays")[0].GetProperty("outcome");
        var row = (await a.GetJson("/api/review?period=all")).GetProperty("plays").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal("scored", outcome.GetProperty("state").GetString());
        Assert.Equal(row.GetProperty("net").GetString(), outcome.GetProperty("resultUsd").GetString());
        Assert.Equal(row.GetProperty("closedAtUtc").GetDateTimeOffset(), outcome.GetProperty("exitAtUtc").GetDateTimeOffset());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("rMultiple").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, outcome.GetProperty("estimatedR").ValueKind);
        Assert.Equal(PlayReceipts.PlanAfterFillReason, outcome.GetProperty("reason").GetString());

        // Another owner finds nothing and counts none of the first owner's Plays; the other mode neither. Only the query
        // itself comes back.
        var foreign = await b.GetText($"/api/plays/search?q={marker}");
        Isolated.NoLeak(foreign.Replace(marker, ""), "journal-", id, account);
        var other = await b.GetJson($"/api/plays/search?q={marker}");
        Assert.Equal((0, 0), (other.GetProperty("total").GetInt32(), other.GetProperty("notIndexed").GetInt32()));
        Assert.Equal(0, (await a.GetJson($"/api/plays/search?q={marker}&tradingMode=paper")).GetProperty("total").GetInt32());
        using var bad = await a.Http.GetAsync("/api/plays/search?q=x");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

/// <summary>The cases above in Token mode.</summary>
public sealed class SearchIsolationTests(IsolationApi api) : SearchIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases in Wallet mode.</summary>
public sealed class WalletSearchIsolationTests(WalletIsolationApi api) : SearchIsolationCases(api), IClassFixture<WalletIsolationApi>;
