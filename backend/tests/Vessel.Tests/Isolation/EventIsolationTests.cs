using System.Net;
using System.Text.Json;

namespace Vessel.Tests.Isolation;

/// <summary>The event page, its cursors and the event stream across two owners.</summary>
public abstract class EventIsolationCases(IsolationHost api)
{
    private static async Task<(Guid Account, Guid Play)> Journal(Owner owner)
    {
        var account = (await owner.SyncedVenueAccount(Isolated.Marker("venue"))).GetProperty("id").GetGuid();
        return (account, await owner.ClosedPlay(account, Isolated.Marker("events")));
    }

    /// <summary>The page holds the caller's own events of its one journal; returns their types in order.</summary>
    private static List<string> OnlyOwn(JsonElement page, Owner owner, Guid play)
    {
        var events = page.GetProperty("events").EnumerateArray().ToList();
        Assert.All(events, e => Assert.Equal(owner.Id, e.GetProperty("ownerId").GetGuid()));
        var types = events.Select(e => e.GetProperty("type").GetString()!).ToList();
        Assert.Contains("play.opened", types);
        Assert.Contains("play.closed", types);
        Assert.Contains(play.ToString("D"), page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        return types;
    }

    [PostgresFact]
    [Covers("GET", "/api/events")]
    public async Task Event_pages_and_cursors_never_return_the_other_owners_events()
    {
        var (a, b) = await api.Owners();
        var (aAccount, aPlay) = await Journal(a);
        var (bAccount, bPlay) = await Journal(b);
        var aPage = await a.GetJson("/api/events");
        var bPage = await b.GetJson("/api/events");
        // The same journal on both sides produces the same event types, each owner's own.
        Assert.Equal(OnlyOwn(aPage, a, aPlay), OnlyOwn(bPage, b, bPlay));
        Isolated.NoLeak(aPage.GetRawText(), bAccount, bPlay, b.Id);
        Isolated.NoLeak(bPage.GetRawText(), aAccount, aPlay, a.Id);

        // Cursors are positions, not grants: another owner's cursor, at its start or at its end, still pages only
        // the caller's own events.
        var aFirst = await a.GetJson("/api/events?limit=1");
        foreach (var cursor in new[] { aFirst.GetProperty("nextCursor").GetString()!, aPage.GetProperty("nextCursor").GetString()! })
        {
            var page = await b.GetText($"/api/events?after={Uri.EscapeDataString(cursor)}");
            Isolated.NoLeak(page, aAccount, aPlay, a.Id);
        }
        var bFirst = await b.GetJson("/api/events?limit=1");
        Assert.Equal(b.Id, Assert.Single(bFirst.GetProperty("events").EnumerateArray()).GetProperty("ownerId").GetGuid());
        var bRest = await b.GetJson($"/api/events?after={Uri.EscapeDataString(bFirst.GetProperty("nextCursor").GetString()!)}");
        var rest = bRest.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(bPage.GetProperty("events").GetArrayLength() - 1, rest.Count);
        Assert.All(rest, e => Assert.Equal(b.Id, e.GetProperty("ownerId").GetGuid()));
        // A's page is unchanged by B's reads.
        Assert.Equal(aPage.GetRawText(), (await a.GetJson("/api/events")).GetRawText());
    }

    [PostgresFact]
    [Covers("GET", "/api/events/stream")]
    public async Task Event_streams_deliver_only_the_callers_events()
    {
        var (a, b) = await api.Owners();
        var (aAccount, aPlay) = await Journal(a);
        var (bAccount, bPlay) = await Journal(b);
        var aCursor = (await a.GetJson("/api/events?limit=1")).GetProperty("nextCursor").GetString()!;

        foreach (var (owner, play, foreign) in new[] { (a, aPlay, new object[] { bAccount, bPlay, b.Id }), (b, bPlay, [aAccount, aPlay, a.Id]) })
        {
            var own = (await owner.GetJson("/api/events")).GetProperty("events").GetArrayLength();
            var frames = await Frames(owner, "/api/events/stream", count: own);
            Assert.Contains(play.ToString("D"), frames, StringComparison.OrdinalIgnoreCase);
            Isolated.NoLeak(frames, foreign);
        }
        // B resuming from A's cursor still reads only B's events.
        Isolated.NoLeak(await Frames(b, $"/api/events/stream?after={Uri.EscapeDataString(aCursor)}", count: 0), aAccount, aPlay, a.Id);
    }

    [PostgresFact]
    [Covers("GET", "/api/events/stream")]
    public async Task Live_event_stream_delivers_only_the_callers_new_events()
    {
        var (a, b) = await api.Owners();
        // Synced before the stream opens; their Plays are opened and closed while it is live.
        var aAccount = (await a.SyncedVenueAccount(Isolated.Marker("live-a"))).GetProperty("id").GetGuid();
        var bAccount = (await b.SyncedVenueAccount(Isolated.Marker("live-b"))).GetProperty("id").GetGuid();
        var aCursor = (await a.GetJson("/api/events")).GetProperty("nextCursor").GetString()!;
        var bCursor = (await b.GetJson("/api/events")).GetProperty("nextCursor").GetString()!;

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = await b.Http.SendAsync(new(HttpMethod.Get, $"/api/events/stream?after={Uri.EscapeDataString(bCursor)}"),
            HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var reader = new StreamReader(body);
        var text = new System.Text.StringBuilder();
        // Ready: the status frame and the first keepalive, with nothing replayed after B's current cursor.
        await Read(reader, text, line => line == ": keepalive", cancel.Token);
        Assert.Contains("event: status", text.ToString());
        Assert.DoesNotContain("event: event", text.ToString());

        // Committed while B's stream is live: A's events first, then B's.
        var aPlay = await a.ClosedPlay(aAccount, Isolated.Marker("live-a"));
        var bPlay = await b.ClosedPlay(bAccount, Isolated.Marker("live-b"));
        var aEvents = (await a.GetJson($"/api/events?after={Uri.EscapeDataString(aCursor)}")).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        var bEvents = (await b.GetJson($"/api/events?after={Uri.EscapeDataString(bCursor)}")).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.NotEmpty(aEvents);
        Assert.NotEmpty(bEvents);
        // B's last new event arrives, and with it every earlier one; the poll after A's commit has run by then.
        var last = bEvents[^1].ToString("D");
        await Read(reader, text, line => line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains(last, StringComparison.OrdinalIgnoreCase),
            cancel.Token);
        var frames = text.ToString();
        Assert.All(bEvents, id => Assert.Contains(id.ToString("D"), frames, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(bPlay.ToString("D"), frames, StringComparison.OrdinalIgnoreCase);
        Isolated.NoLeak(frames, [a.Id, aAccount, aPlay, .. aEvents.Cast<object>()]);
    }

    /// <summary>Appends stream lines to <paramref name="text"/> until one matches.</summary>
    private static async Task Read(StreamReader reader, System.Text.StringBuilder text, Func<string, bool> until, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            text.AppendLine(line);
            if (until(line)) return;
        }
        Assert.Fail("The stream ended early: " + text);
    }

    [PostgresFact]
    [Covers("GET", "/api/events/stream")]
    public async Task Event_stream_cap_is_per_owner()
    {
        // Fresh owners hold no stream slots, so no earlier case's disconnecting stream can be counted here.
        var (a, b) = await api.Owners();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var response = await a.Http.SendAsync(new(HttpMethod.Get, "/api/events/stream"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                open.Add(response);
            }
            // A's four open streams use A's cap only: A's fifth is refused, B still streams.
            using var refused = await a.Http.GetAsync("/api/events/stream", cancel.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            using var other = await b.Http.SendAsync(new(HttpMethod.Get, "/api/events/stream"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally
        {
            foreach (var response in open) response.Dispose();
        }
    }

    /// <summary>Reads stream frames until the status frame and <paramref name="count"/> event frames arrived, then disconnects.</summary>
    private static async Task<string> Frames(Owner owner, string path, int count)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await owner.Http.SendAsync(new(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var reader = new StreamReader(body);
        var text = new System.Text.StringBuilder();
        var events = 0;
        var status = false;
        // The first keepalive follows the initial page, so every frame of that page has been read by then.
        while (await reader.ReadLineAsync(cancel.Token) is { } line)
        {
            text.AppendLine(line);
            if (line == "event: status") status = true;
            if (line == "event: event") events++;
            if (status && events >= count && line == ": keepalive") break;
        }
        Assert.True(status, text.ToString());
        Assert.Equal(count, events);
        return text.ToString();
    }
}

/// <summary>The cases above with the suite's test scheme selecting the owner.</summary>
public sealed class EventIsolationTests(IsolationApi api) : EventIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases through wallet sessions: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletEventIsolationTests(WalletIsolationApi api) : EventIsolationCases(api), IClassFixture<WalletIsolationApi>;
