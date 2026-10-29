using Vessel.Application.Common;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vessel.Api;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

public sealed class VesselEventStreamTests : IDisposable
{
    private readonly ServiceProvider services = new ServiceCollection()
        .AddSingleton<IPrincipalValidity, AlwaysValidPrincipal>().BuildServiceProvider();
    public void Dispose() => services.Dispose();

    [Fact]
    public async Task Missing_principal_validity_fails_closed_before_reading_and_releases_stream_capacity()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        await using var missing = new ServiceCollection().BuildServiceProvider();
        var reader = new FixedReader();
        var context = Context(new MemoryStream(), default);
        context.RequestServices = missing;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VesselEventEndpoints.StreamAsync(
            "0", reader, owner, Options.Create(new VesselEventSettings()), Options.Create(new JsonOptions()), context));
        Assert.Contains(nameof(IPrincipalValidity), error.Message);
        Assert.Equal(0, reader.Calls);
        Assert.False(context.Response.HasStarted);
        using var cancellation = new CancellationTokenSource();
        var streams = Enumerable.Range(0, 4).Select(_ => Open(owner, reader, cancellation.Token)).ToArray();
        Assert.All(streams, stream => Assert.False(stream.IsCompleted));
        await cancellation.CancelAsync();
        await Task.WhenAll(streams);
    }

    [Fact]
    public async Task Stream_bounds_four_sessions_per_owner_and_releases_all_on_disconnect()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var reader = new FixedReader();
        var tasks = Enumerable.Range(0, 4).Select(_ => Open(owner, reader, cancellation.Token)).ToList();
        var rejected = await Assert.ThrowsAsync<ProblemException>(() => Open(owner, reader, cancellation.Token));
        Assert.Equal(429, rejected.StatusCode);
        using var otherCancellation = new CancellationTokenSource();
        var other = Open(new(Guid.NewGuid()), reader, otherCancellation.Token);
        Assert.False(other.IsCompleted);
        await otherCancellation.CancelAsync();
        await other;
        await cancellation.CancelAsync();
        await Task.WhenAll(tasks);
        using var again = new CancellationTokenSource();
        var fresh = Open(owner, reader, again.Token);
        Assert.False(fresh.IsCompleted);
        await again.CancelAsync();
        await fresh;
    }

    [Fact]
    public async Task Reset_frame_skips_historical_events_and_keeps_a_string_cursor()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        using var body = new MemoryStream();
        var context = Context(body, cancellation.Token);
        var epoch = Guid.NewGuid().ToString("N");
        var read = new FixedReader(new([], epoch + ".9007199254740993", true));
        var stream = VesselEventEndpoints.StreamAsync(Guid.NewGuid().ToString("N") + ".999", read, owner,
            Options.Create(new VesselEventSettings()), Options.Create(new JsonOptions()), context);
        Assert.False(stream.IsCompleted);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("event: status\ndata: {\"state\":\"reset\",\"nextCursor\":\"" + epoch + ".9007199254740993\"}", text);
        Assert.DoesNotContain("event: event", text);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("text/event-stream; charset=utf-8", context.Response.ContentType);
        await cancellation.CancelAsync();
        await stream;
    }

    [Fact]
    public async Task Failed_initial_read_releases_session_before_headers_or_retry()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(owner, new FailingReader(), default));
        using var cancellation = new CancellationTokenSource();
        var streams = Enumerable.Range(0, 4).Select(_ => Open(owner, new FixedReader(), cancellation.Token)).ToArray();
        await cancellation.CancelAsync();
        await Task.WhenAll(streams);
    }

    [Fact]
    public async Task Ready_does_not_acknowledge_unwritten_catchup_and_events_are_sent_in_cursor_order()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        using var body = new MemoryStream();
        var at = DateTimeOffset.UtcNow;
        var message = new VesselEvent(Guid.CreateVersion7(), "monitor.state-changed", 1, owner.OwnerId, "1",
            Guid.CreateVersion7(), "first", "info", at, at, "timely", "current", null, new(), new("monitor"),
            JsonSerializer.SerializeToElement(new { current = "live" }));
        var context = Context(body, cancellation.Token);
        var epoch = Guid.NewGuid().ToString("N");
        var stream = VesselEventEndpoints.StreamAsync("0", new FixedReader(new([message], epoch + ".1", false)), owner,
            Options.Create(new VesselEventSettings()), Options.Create(new JsonOptions()), context);
        var text = Encoding.UTF8.GetString(body.ToArray());
        // Ready normalizes the accepted cursor with the stream epoch; each event frame carries its resume id.
        Assert.StartsWith("event: status\ndata: {\"state\":\"ready\",\"nextCursor\":\"" + epoch + ".0\"}", text);
        Assert.Contains("event: event\nid: " + epoch + ".1\ndata: {\"id\":", text);
        Assert.Contains("\"sequence\":\"1\"", text);
        await cancellation.CancelAsync();
        await stream;
    }

    private Task Open(CoreOwner owner, IVesselEventReader reader, CancellationToken ct) =>
        VesselEventEndpoints.StreamAsync("0", reader, owner, Options.Create(new VesselEventSettings()),
            Options.Create(new JsonOptions()), Context(new MemoryStream(), ct));
    private DefaultHttpContext Context(Stream body, CancellationToken ct)
    {
        var context = new DefaultHttpContext { RequestAborted = ct, RequestServices = services };
        context.Response.Body = body;
        return context;
    }
    private sealed class FixedReader(VesselEventPage? initial = null) : IVesselEventReader
    {
        public int Calls { get; private set; }
        public Task<VesselEventPage> ReadAsync(string? after, int limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(initial ?? new([], after ?? "0", false));
        }
    }
    private sealed class FailingReader : IVesselEventReader
    {
        public Task<VesselEventPage> ReadAsync(string? after, int limit, CancellationToken ct) =>
            throw new InvalidOperationException("Synthetic database outage.");
    }
}
