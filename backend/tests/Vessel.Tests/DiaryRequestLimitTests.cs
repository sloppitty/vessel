using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Vessel.Tests;

/// <summary>
/// One Kestrel host for the whole matrix: every case only sends requests that fail validation or authentication,
/// so nothing persists. Each test gets its own HttpClient (and connections).
/// </summary>
public sealed class DiaryLimitHost : IAsyncDisposable
{
    internal CoreApiFactory Factory { get; }

    public DiaryLimitHost()
    {
        Factory = new CoreApiFactory(Guid.NewGuid());
        // TestServer does not enforce Kestrel's chunked request-body limit.
        Factory.UseKestrel(0);
        Factory.StartServer();
    }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

public sealed class DiaryRequestLimitTests(DiaryLimitHost host) : IClassFixture<DiaryLimitHost>
{
    public static IEnumerable<object[]> Routes()
    {
        const string playId = "11111111-1111-1111-1111-111111111111";
        const string executionId = "22222222-2222-2222-2222-222222222222";
        foreach (var (method, path, limit, segment, mixedSegment) in new[]
        {
            ("POST", $"/api/plays/{playId}/manual-executions", 16_384, "manual-executions", "MaNuAl-ExEcUtIoNs"),
            ("PUT", $"/api/plays/{playId}/manual-executions/{executionId}", 16_384, "manual-executions", "MaNuAl-ExEcUtIoNs"),
            ("POST", "/api/plays/retrospective", 131_072, "retrospective", "ReTrOsPeCtIvE")
        })
        foreach (var variant in new[]
        {
            path,
            path.Replace(segment, segment.ToUpperInvariant()),
            path.Replace("/api/plays", "/Api/PlAyS").Replace(segment, mixedSegment),
            path.ToUpperInvariant()
        })
        foreach (var suffix in new[] { "", "/" })
        foreach (var chunked in new[] { false, true })
            yield return [method, variant + suffix, limit, chunked];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Authenticated_diary_limits_match_routes_before_binding(
        string method, string path, int limit, bool chunked)
    {
        using var client = host.Factory.AuthorizedClient();

        // Invalid JSON fields avoid persistence while proving this route matches.
        // Check the exact Content-Length boundary; leave room for chunk framing.
        using var withinLimit = await SendAsync(client, method, path, chunked ? limit / 2 : limit, chunked);
        await AssertProblemAsync(withinLimit, HttpStatusCode.BadRequest,
            "The request body or parameters are invalid.");
        using var overLimit = await SendAsync(client, method, path, limit + 1, chunked);
        if (chunked)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);
            Assert.True(overLimit.Headers.CacheControl?.NoStore);
            Assert.Empty(await overLimit.Content.ReadAsStringAsync());
        }
        else
            await AssertProblemAsync(overLimit, HttpStatusCode.RequestEntityTooLarge, "Diary request is too large.");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unauthorized_diary_requests_are_rejected_before_body_limits(
        string method, string path, int limit, bool chunked)
    {
        using var client = host.Factory.CreateClient();
        // No session, and a bearer value that the API no longer reads at all.
        foreach (var token in new[] { null, "invalid-test-token" })
        {
            client.DefaultRequestHeaders.Authorization = token is null
                ? null : new AuthenticationHeaderValue("Bearer", token);
            using var response = await SendAsync(client, method, path, limit + 1, chunked);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("too large", body);
            Assert.DoesNotContain("invalid-test-token", body);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, int length, bool chunked)
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

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
    }

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
