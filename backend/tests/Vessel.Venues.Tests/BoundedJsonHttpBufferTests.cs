using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Tests;

/// <summary>
/// The bounded reader sizes its buffer from Content-Length but enforces the cap on actual bytes, parses without a copy
/// and returns documents that stay valid after the read and across later reads.
/// </summary>
public sealed class BoundedJsonHttpBufferTests
{
    private const int Max = 4096;
    private const string Invalid = "synthetic invalid response";
    private static readonly StrictJson Json = new(Invalid);

    private static BoundedJsonHttp Http(Func<HttpResponseMessage> respond, out HttpClient client)
    {
        client = new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://venue.invalid/") };
        return new BoundedJsonHttp(client, TimeProvider.System, Json, "synthetic unavailable", "synthetic timeout", Max, maxDepth: 8);
    }

    private static HttpResponseMessage Response(byte[] body, long? declaredLength, int chunk = 7, TaskCompletionSource? gate = null)
    {
        var content = new StreamContent(new ChunkedStream(body, chunk, gate));
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<JsonDocument> Read(BoundedJsonHttp http, CancellationToken ct = default) =>
        await http.ReadBoundedAsync(async bounded =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
            return await http.SendAsync(request, bounded);
        }, ct);

    private static byte[] Body(int targetBytes)
    {
        // {"items":["xxxx",...]} padded to exactly targetBytes.
        var builder = new StringBuilder("{\"items\":[");
        var index = 0;
        while (builder.Length + 40 < targetBytes) builder.Append(index++ == 0 ? "" : ",").Append("\"item-").Append(index).Append('"');
        builder.Append("],\"pad\":\"");
        builder.Append('p', Math.Max(0, targetBytes - builder.Length - 2)).Append("\"}");
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        Assert.Equal(targetBytes, bytes.Length);
        return bytes;
    }

    public static TheoryData<int, long?> Lengths => new()
    {
        { 300, null },          // absent (chunked)
        { 300, 300 },           // accurate
        { 300, 10 },            // smaller than the body
        { 300, 1 },             // far smaller
        { 300, Max },           // larger than the body, at the cap
        { 300, 0 },             // zero, misleading
        { Max, null },          // exactly the cap, absent
        { Max, Max },           // exactly the cap, accurate
        { Max, 17 },            // exactly the cap, smaller
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public async Task Parses_the_full_body_whatever_content_length_claims(int size, long? declared)
    {
        var body = Body(size);
        var http = Http(() => Response(body, declared), out var client);
        using (client)
        using (var document = await Read(http))
        {
            // Equivalent to parsing a private copy of the same bytes.
            using var reference = JsonDocument.Parse(body.ToArray());
            Assert.Equal(reference.RootElement.GetRawText(), document.RootElement.GetRawText());
        }
    }

    [Theory]
    [InlineData(Max + 1, null)]       // absent
    [InlineData(Max + 1, 100L)]       // misleading, smaller
    [InlineData(Max + 1, (long)Max)]  // misleading, at the cap
    [InlineData(Max * 3, 1L)]         // much larger, tiny claim
    public async Task Actual_bytes_over_the_cap_are_rejected_regardless_of_content_length(int size, long? declared)
    {
        var body = Encoding.UTF8.GetBytes("\"" + new string('x', size - 2) + "\"");
        var http = Http(() => Response(body, declared), out var client);
        using (client)
            Assert.Equal(Invalid, (await Assert.ThrowsAsync<VenueReadException>(() => Read(http))).Message);
    }

    [Theory]
    [InlineData(Max + 1L)]
    [InlineData(long.MaxValue)]
    public async Task Oversized_content_length_is_rejected_before_reading(long declared)
    {
        var stream = new ChunkedStream(Body(300), 7, null);
        var content = new StreamContent(stream);
        content.Headers.ContentLength = declared;
        var http = Http(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, out var client);
        using (client)
            Assert.Equal(Invalid, (await Assert.ThrowsAsync<VenueReadException>(() => Read(http))).Message);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task Depth_and_duplicate_keys_are_still_rejected()
    {
        var deep = Encoding.UTF8.GetBytes(new string('[', 20) + new string(']', 20));
        var duplicate = Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}");
        foreach (var body in new[] { deep, duplicate })
        {
            var http = Http(() => Response(body, body.Length), out var client);
            using (client)
                Assert.Equal(Invalid, (await Assert.ThrowsAsync<VenueReadException>(() => Read(http))).Message);
        }
    }

    [Fact]
    public async Task Caller_cancellation_mid_body_is_cancellation_not_a_venue_error()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = Body(300);
        var http = Http(() => Response(body, body.Length, gate: gate), out var client);
        using var cancellation = new CancellationTokenSource();
        using (client)
        {
            var read = Read(http, cancellation.Token);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
    }

    [Fact]
    public async Task Documents_stay_valid_after_the_read_and_across_later_reads()
    {
        var bodies = new[] { Body(300), Body(1000), Body(Max) };
        var next = 0;
        var http = Http(() => { var body = bodies[next++]; return Response(body, body.Length); }, out var client);
        var documents = new List<JsonDocument>();
        using (client)
        {
            for (var i = 0; i < bodies.Length; i++) documents.Add(await Read(http));
        }
        // The client and responses are gone; collect aggressively so any pooled or released buffer would be reused.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _ = new byte[Max * 4];
        for (var i = 0; i < bodies.Length; i++)
        {
            using var reference = JsonDocument.Parse(bodies[i].ToArray());
            Assert.Equal(reference.RootElement.GetRawText(), documents[i].RootElement.GetRawText());
            Assert.Equal("item-1", documents[i].RootElement.GetProperty("items")[0].GetString());
            documents[i].Dispose();
        }
    }

    [Fact]
    public async Task One_instance_serves_concurrent_reads_independently()
    {
        var http = Http(() => Response(Body(200 + Random.Shared.Next(0, 3000)), null, chunk: 13), out var client);
        using (client)
        {
            var documents = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Read(http))));
            foreach (var document in documents)
            {
                using (document)
                    Assert.Equal("item-1", document.RootElement.GetProperty("items")[0].GetString());
            }
        }
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    /// <summary>Yields the body in small reads; with a gate, stops halfway and waits for cancellation.</summary>
    private sealed class ChunkedStream(byte[] body, int chunk, TaskCompletionSource? gate) : Stream
    {
        private int position;
        public int BytesRead => position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (gate is not null && position >= body.Length / 2)
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            var count = Math.Min(Math.Min(chunk, buffer.Length), body.Length - position);
            body.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
