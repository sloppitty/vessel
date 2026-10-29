using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vessel.Api;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

/// <summary>
/// The stream lifetime is checked before every event frame, not only between pages: a slow client cannot hold the
/// response open past the lifetime for the rest of a page, and the unsent remainder is replayed after reconnect.
/// </summary>
public sealed class VesselEventStreamMidPageExpiryTests
{
    private static readonly Guid Epoch = Guid.Parse("6f1d3c3a-6a0e-4c7b-9a3e-1f2d3c4b5a69");

    [Fact]
    public async Task Expiry_mid_page_stops_before_the_next_frame_and_a_reconnect_delivers_the_rest_exactly_once()
    {
        var clock = new FlushClock();
        var reader = new FakeReader(5);
        var owner = new Owner();

        // Every flush takes one second; the lifetime is three seconds from the ready frame, shorter than the page.
        var first = await StreamAsync(VesselEventCursor.Format(Epoch, 0), reader, owner, clock, TimeSpan.FromSeconds(3));
        Assert.InRange(first.Count, 1, 4);
        Assert.Equal(Enumerable.Range(1, first.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)), first.Select(frame => frame.Sequence));
        Assert.All(first, frame => Assert.Equal(VesselEventCursor.Format(Epoch, long.Parse(frame.Sequence, CultureInfo.InvariantCulture)), frame.Id));

        // The browser resumes from the id of the last fully written frame until the unsent rest has arrived.
        var delivered = first.ToList();
        for (var reconnect = 0; reconnect < 5 && delivered.Count < 5; reconnect++)
        {
            var next = await StreamAsync(delivered[^1].Id, reader, owner, clock, TimeSpan.FromSeconds(3));
            Assert.NotEmpty(next);
            delivered.AddRange(next);
        }
        Assert.Equal(["1", "2", "3", "4", "5"], delivered.Select(frame => frame.Sequence));
    }

    [Fact]
    public async Task A_lifetime_already_spent_after_ready_writes_no_event_frame()
    {
        var clock = new FlushClock { SecondsPerFlush = 0 };
        var reader = new FakeReader(3);
        clock.AdvanceOnStart = 5;
        var frames = await StreamAsync(VesselEventCursor.Format(Epoch, 0), reader, new Owner(), clock, TimeSpan.FromSeconds(1));
        Assert.Empty(frames);
    }

    private static async Task<List<(string Sequence, string Id)>> StreamAsync(string after, IVesselEventReader reader,
        IJournalOwnerContext owner, FlushClock clock, TimeSpan lifetime)
    {
        await using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock)
            .AddSingleton<IPrincipalValidity, AlwaysValidPrincipal>().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var body = new FlushTickingStream(clock);
        context.Response.Body = body;
        await VesselEventEndpoints.StreamAsync(after, reader, owner,
            Options.Create(new VesselEventSettings { StreamMaxLifetime = lifetime }), Options.Create(new JsonOptions()), context);
        var text = Encoding.UTF8.GetString(body.ToArray());
        // The response ends on a frame boundary, never part-way through a frame.
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
        var frames = new List<(string, string)>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            if (!lines.Contains("event: event")) continue;
            var id = lines.Single(line => line.StartsWith("id: ", StringComparison.Ordinal))[4..];
            using var data = System.Text.Json.JsonDocument.Parse(lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
            frames.Add((data.RootElement.GetProperty("sequence").GetString()!, id));
        }
        return frames;
    }

    private sealed class FlushClock : TimeProvider
    {
        public long Seconds;
        public long SecondsPerFlush = 1;
        public long AdvanceOnStart;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp()
        {
            var now = Seconds;
            Seconds += AdvanceOnStart;
            AdvanceOnStart = 0;
            return now;
        }
    }

    private sealed class FlushTickingStream(FlushClock clock) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            clock.Seconds += clock.SecondsPerFlush;
            return Task.CompletedTask;
        }
    }

    private sealed class Owner : IJournalOwnerContext
    {
        public Guid OwnerId { get; } = Guid.NewGuid();
    }

    private sealed class FakeReader(int count) : IVesselEventReader
    {
        private readonly List<VesselEvent> events = Enumerable.Range(1, count).Select(sequence => new VesselEvent(
            Guid.NewGuid(), "play.opened", 1, Guid.NewGuid(), sequence.ToString(CultureInfo.InvariantCulture), Guid.NewGuid(),
            $"key-{sequence}", "info", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "timely", "current", null,
            new(), new("owner"), VesselEventJson.Element(new { }))).ToList();

        public Task<VesselEventPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken)
        {
            Assert.True(VesselEventCursor.TryParse(cursor, out var parsed));
            Assert.Equal(Epoch, parsed.Epoch);
            var page = events.Where(item => long.Parse(item.Sequence, CultureInfo.InvariantCulture) > parsed.Sequence).Take(limit).ToList();
            var next = page.Count == 0 ? parsed.Sequence : long.Parse(page[^1].Sequence, CultureInfo.InvariantCulture);
            return Task.FromResult(new VesselEventPage(page, VesselEventCursor.Format(Epoch, next), false));
        }
    }
}
