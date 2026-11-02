using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Vessel.Application.Evidence;
using Vessel.Application.Plays;

namespace Vessel.Tests.Isolation;

/// <summary>Evidence metadata, bytes, notes and markup across two owners.</summary>
public abstract class EvidenceIsolationCases(IsolationHost api)
{
    private static readonly ImageMarkup Box = new(10, 10, [new("a", "box", "#ffffff", 2, From: new(1, 1), To: new(5, 5))]);

    private static MultipartFormDataContent Form(byte[] bytes, string note)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new() { { file, "file", "chart.png" }, { new StringContent(note), "note" }, { new StringContent("upload"), "source" } };
    }

    private static async Task<Guid> Play(Owner owner)
    {
        var account = (await owner.ManualAccount(Isolated.Marker("manual"))).GetProperty("id").GetGuid();
        var play = await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "manual", Isolated.Marker("play"), TestPlays.Plan()),
            HttpStatusCode.Created);
        return play.GetProperty("summary").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> Upload(Owner owner, Guid play, byte[] bytes, string note)
    {
        using var response = await owner.Http.PostAsync($"/api/plays/{play}/evidence", Form(bytes, note));
        return (await Isolated.Json(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<byte[]> Content(Owner owner, Guid evidence)
    {
        using var response = await owner.Http.GetAsync($"/api/evidence/{evidence}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    [PostgresFact]
    [Covers("GET", "/api/plays/{playId:guid}/evidence")]
    [Covers("POST", "/api/plays/{playId:guid}/evidence")]
    [Covers("GET", "/api/evidence/{id:guid}/content")]
    [Covers("PATCH", "/api/evidence/{id:guid}")]
    [Covers("PUT", "/api/evidence/{id:guid}/markup")]
    [Covers("DELETE", "/api/evidence/{id:guid}/markup")]
    [Covers("DELETE", "/api/evidence/{id:guid}")]
    public async Task Evidence_of_another_owner_answers_like_unknown_and_its_bytes_stay_private()
    {
        var (a, b) = await api.Owners();
        var aPlay = await Play(a);
        byte[] aBytes = [.. EvidenceFixtures.Png, .. RandomNumberGenerator.GetBytes(16)];
        var note = Isolated.Marker("note-a");
        var evidence = await Upload(a, aPlay, aBytes, note);
        // Positive controls: list, bytes, note and markup.
        Assert.Equal(aBytes, await Content(a, evidence));
        await a.Send(HttpMethod.Patch, $"/api/evidence/{evidence}", new UpdateEvidenceNoteRequest(note));
        await a.Send(HttpMethod.Put, $"/api/evidence/{evidence}/markup", Box);
        Assert.Equal(evidence, Assert.Single((await a.GetJson($"/api/plays/{aPlay}/evidence")).EnumerateArray()).GetProperty("id").GetGuid());
        var state = await Snapshot.Of(a, $"/api/plays/{aPlay}/evidence");
        // Each attempt is followed by A's metadata and A's original bytes.
        async Task Attempt(Func<Task> attempt)
        {
            await state.After(attempt);
            Assert.Equal(aBytes, await Content(a, evidence));
        }

        var bPlay = await Play(b);
        await Attempt(() => Isolated.NotFound(b, aPlay, Isolated.Request(HttpMethod.Get, id => $"/api/plays/{id}/evidence")));
        await Attempt(() => Isolated.NotFound(b, aPlay, id => new(HttpMethod.Post, $"/api/plays/{id}/evidence") { Content = Form(EvidenceFixtures.Png, "into-a") }));
        await Attempt(() => Isolated.NotFound(b, evidence, Isolated.Request(HttpMethod.Get, id => $"/api/evidence/{id}/content")));
        await Attempt(() => Isolated.NotFound(b, evidence, Isolated.Request(HttpMethod.Patch, id => $"/api/evidence/{id}", _ => new UpdateEvidenceNoteRequest("taken-by-b"))));
        await Attempt(() => Isolated.NotFound(b, evidence, Isolated.Request(HttpMethod.Put, id => $"/api/evidence/{id}/markup", _ => Box with { Width = 20 })));
        await Attempt(() => Isolated.NotFound(b, evidence, Isolated.Request(HttpMethod.Delete, id => $"/api/evidence/{id}/markup")));
        await Attempt(() => Isolated.NotFound(b, evidence, Isolated.Request(HttpMethod.Delete, id => $"/api/evidence/{id}")));
        Assert.Empty((await b.GetJson($"/api/plays/{bPlay}/evidence")).EnumerateArray());

        // Identical bytes uploaded by B are B's own object: deleting it leaves A's evidence and bytes intact.
        var copy = await Upload(b, bPlay, aBytes, "copy-b");
        Assert.NotEqual(evidence, copy);
        Assert.Equal(aBytes, await Content(b, copy));
        Isolated.NoLeak(await b.GetText($"/api/plays/{bPlay}/evidence"), evidence, note, aPlay);
        await Attempt(() => b.Send(HttpMethod.Delete, $"/api/evidence/{copy}", expected: HttpStatusCode.NoContent));
        // A's own changes still apply.
        await a.Send(HttpMethod.Delete, $"/api/evidence/{evidence}/markup");
        await a.Send(HttpMethod.Delete, $"/api/evidence/{evidence}", expected: HttpStatusCode.NoContent);
    }
}

/// <summary>The cases above with the suite's test scheme selecting the owner.</summary>
public sealed class EvidenceIsolationTests(IsolationApi api) : EvidenceIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases through wallet sessions: two signed-in users, each request mapped to its owner by the session scheme.</summary>
public sealed class WalletEvidenceIsolationTests(WalletIsolationApi api) : EvidenceIsolationCases(api), IClassFixture<WalletIsolationApi>;
