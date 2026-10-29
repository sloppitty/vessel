using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Self-tests of the foreign-versus-unknown comparator (<see cref="Isolated.SameAsUnknown(Owner, Guid, Func{Guid, HttpRequestMessage})"/>)
/// over a synthetic handler: a header that only the foreign answer carries must fail it, the volatile ones must not.
/// </summary>
public sealed class ComparatorSelfTests
{
    private static readonly Guid Foreign = Guid.NewGuid();

    /// <summary>Answers the same 404 for every ID; <paramref name="foreignOnly"/> adds to the answer for <see cref="Foreign"/>.</summary>
    private sealed class Stub(Action<HttpResponseMessage>? foreignOnly, string charset = "utf-8", string? foreignCharset = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var id = path[(path.LastIndexOf('/') + 1)..];
            var foreign = id == Foreign.ToString("D");
            var body = $$"""{"title":"Not Found","status":404,"detail":"Account not found.","traceId":"{{Guid.NewGuid()}}","id":"{{id}}"}""";
            var response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json")
            {
                CharSet = foreign && foreignCharset is not null ? foreignCharset : charset
            };
            // Volatile headers differ on every answer and are not compared.
            response.Headers.Date = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(1, 100));
            response.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString("N"));
            response.Headers.Add("traceparent", $"00-{Guid.NewGuid():N}-{Guid.NewGuid().ToString("N")[..16]}-01");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            // A header may echo the ID the caller sent, like the body.
            response.Headers.Add("X-Resource", id);
            if (foreign) foreignOnly?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    private static Task<HttpStatusCode> Compare(HttpMessageHandler handler)
    {
        var owner = new Owner(Guid.NewGuid(), new HttpClient(handler) { BaseAddress = new("http://isolation.test") });
        return Isolated.SameAsUnknown(owner, Foreign, Isolated.Request(HttpMethod.Get, id => $"/api/accounts/{id}"));
    }

    [Fact]
    public async Task Identical_answers_pass_despite_volatile_headers()
    {
        Assert.Equal(HttpStatusCode.NotFound, await Compare(new Stub(null)));
    }

    public static TheoryData<string> Leaks => ["etag", "set-cookie", "cache-control-public", "charset", "vary", "location"];

    [Theory]
    [MemberData(nameof(Leaks))]
    public async Task A_header_only_the_foreign_answer_carries_fails(string leak)
    {
        HttpMessageHandler handler = leak switch
        {
            "etag" => new Stub(r => r.Headers.ETag = new EntityTagHeaderValue("\"a1\"")),
            "set-cookie" => new Stub(r => r.Headers.Add("Set-Cookie", "vessel-hint=1; Path=/; HttpOnly")),
            "cache-control-public" => new Stub(r => r.Headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromMinutes(5) }),
            "charset" => new Stub(null, foreignCharset: "iso-8859-1"),
            "vary" => new Stub(r => r.Headers.Vary.Add("X-Vessel-Test-Owner")),
            "location" => new Stub(r => r.Headers.Location = new("/api/accounts", UriKind.Relative)),
            _ => throw new ArgumentOutOfRangeException(nameof(leak))
        };
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => Compare(handler));
    }
}
