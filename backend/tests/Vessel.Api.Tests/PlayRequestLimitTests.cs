using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Issue #110: a Play's create and update bodies are bounded before they are parsed, with room for the largest valid
/// request. Requests here fail validation or the limit, so nothing persists.
/// </summary>
public sealed class PlayRequestLimitTests(DiaryLimitHost host) : IClassFixture<DiaryLimitHost>
{
    private const int Limit = 2_097_152;

    public static IEnumerable<object[]> Routes()
    {
        foreach (var (method, path) in new[] { ("POST", "/api/plays"), ("PUT", "/api/plays/11111111-1111-1111-1111-111111111111") })
        foreach (var chunked in new[] { false, true })
            yield return [method, path, chunked];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Play_bodies_over_the_limit_are_refused_before_binding(string method, string path, bool chunked)
    {
        using var client = host.Factory.AuthorizedClient();

        using var withinLimit = await SendAsync(client, method, path, chunked ? Limit / 2 : Limit, chunked);
        Assert.Equal(HttpStatusCode.BadRequest, withinLimit.StatusCode);
        using var overLimit = await SendAsync(client, method, path, Limit + 1, chunked);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);
        if (chunked) Assert.Empty(await overLimit.Content.ReadAsStringAsync());
        else
        {
            using var problem = JsonDocument.Parse(await overLimit.Content.ReadAsStringAsync());
            Assert.Equal("Play request is too large.", problem.RootElement.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public void The_largest_valid_play_request_fits_under_the_limit()
    {
        // Drawings just under their stored limit, with every other text at its maximum and escaped (six bytes a character),
        // as System.Text.Json writes it; a browser's JSON.stringify writes them shorter.
        var drawings = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["hyperliquid:BTC"] = Enumerable.Range(0, PlayDocuments.MaxDrawingsPerInstrument)
                .Select(i => new { id = $"d{i}", text = new string('x', 4_900) }).ToList()
        });
        Assert.InRange(PlayDocuments.NormalizeDrawings(drawings).Length, 980_000, PlayDocuments.MaxDrawingsLength);
        var text = new string('€', PlayDocuments.MaxNoteLength);
        var exits = Enumerable.Range(0, PlayDocuments.MaxStops).Select(i => new PlanExit($"exit-{i}", "price", "123456.123456", "10")).ToList();
        var plan = new PlayPlanDocument("long", "margin", "250", "3", null,
            Enumerable.Range(0, PlayDocuments.MaxEntries).Select(i => new PlanEntry($"entry-{i}", $"Entry {i}", "#b9c9e4", "5", "100", exits, exits)).ToList(),
            new PlanNotes(text, text, text, text));
        var request = new UpdatePlayRequest(long.MaxValue, Guid.NewGuid(), new string('X', 128), "venue",
            new string('€', Play.MaxTitleLength), plan, drawings, new string('€', Play.MaxReviewLength),
            new string('€', PlayPlanRevision.MaxReasonLength),
            new ReviewAnswers("not-applicable", new string('€', 100), new string('€', 4000), new string('€', 4000), "followed"));

        var body = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.InRange(body.Length, PlayDocuments.MaxDrawingsLength, Limit);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, int length, bool chunked)
    {
        // Chunked JSON must read past the limit before it can reject missing fields.
        var padding = new string(' ', length - 2);
        var body = Encoding.UTF8.GetBytes(chunked ? padding + "{}" : "{}" + padding);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = chunked ? new ChunkedContent(body) : new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await client.SendAsync(request);
    }

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
