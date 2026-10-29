using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vessel.Application.Events;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

/// <summary>
/// The browser event stream has a maximum lifetime: the server ends the response cleanly between frames, and a
/// reconnect from the last resume cursor receives everything committed since, with nothing skipped.
/// </summary>
public sealed class VesselEventStreamLifetimeTests
{
    [VesselEventPostgresFact]
    public async Task The_stream_ends_cleanly_after_its_lifetime_and_a_reconnect_resumes_from_the_cursor()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic manual account");
        await using (var db = database.Context(owner))
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString, settings: new()
        {
            ["Vessel:Events:StreamMaxLifetime"] = "00:00:03",
            ["Vessel:Events:StreamPollSeconds"] = "1"
        });
        using var client = factory.AuthorizedClient();
        await PlannedPlayAsync(client, account.Id, "Before the stream");
        var head = (await (await client.GetAsync("/api/events?after=0&limit=200")).Content.ReadFromJsonAsync<VesselEventPage>())!;
        Assert.Single(head.Events);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string lastCursor;
        var watch = Stopwatch.StartNew();
        using (var response = await client.GetAsync($"/api/events/stream?after={head.NextCursor}",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var stream = new SseReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var ready = await stream.FrameAsync(timeout.Token);
            Assert.Equal(("status", "ready"), (ready!.Value.Type, ready.Value.Data.GetProperty("state").GetString()));
            var during = await PlannedPlayAsync(client, account.Id, "During the stream");
            var delivered = await stream.FrameAsync(timeout.Token);
            Assert.Equal("event", delivered!.Value.Type);
            Assert.Equal(during, delivered.Value.Data.GetProperty("subject").GetProperty("playId").GetGuid());
            lastCursor = delivered.Value.Id!;
            // Then the server closes: no further frame, a clean end of the body and no partial frame.
            Assert.Null(await stream.FrameAsync(timeout.Token));
            Assert.False(timeout.IsCancellationRequested);
            Assert.True(stream.EndedOnFrameBoundary);
        }
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(20));

        var after = await PlannedPlayAsync(client, account.Id, "After the stream");
        using (var response = await client.GetAsync($"/api/events/stream?after={lastCursor}",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            using var stream = new SseReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var ready = await stream.FrameAsync(timeout.Token);
            Assert.Equal(("status", "ready", lastCursor), (ready!.Value.Type, ready.Value.Data.GetProperty("state").GetString(),
                ready.Value.Data.GetProperty("nextCursor").GetString()));
            var resumed = await stream.FrameAsync(timeout.Token);
            Assert.Equal("event", resumed!.Value.Type);
            Assert.Equal(after, resumed.Value.Data.GetProperty("subject").GetProperty("playId").GetGuid());
            Assert.Equal("3", resumed.Value.Data.GetProperty("sequence").GetString());
        }
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("1.00:00:01")]
    [InlineData("-00:01:00")]
    public void An_out_of_range_stream_lifetime_is_rejected(string lifetime)
    {
        var services = new ServiceCollection();
        services.AddVesselEvents(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vessel:Events:StreamMaxLifetime"] = lifetime }).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<VesselEventSettings>>().Value);
    }

    [Fact]
    public void The_default_stream_lifetime_is_thirty_minutes()
    {
        var services = new ServiceCollection();
        services.AddVesselEvents(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(30), provider.GetRequiredService<IOptions<VesselEventSettings>>().Value.StreamMaxLifetime);
    }

    private static async Task<Guid> PlannedPlayAsync(HttpClient client, Guid accountId, string title)
    {
        var create = await client.PostAsJsonAsync("/api/plays", new
        {
            accountId, instrument = "BTC", instrumentSource = "manual", title, plan = TestPlays.Plan()
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var play = (await create.Content.ReadFromJsonAsync<PlayDto>())!;
        var planned = await client.PostAsJsonAsync($"/api/plays/{play.Summary.Id}/status",
            new { expectedVersion = play.Summary.Version, status = "planned" });
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        return play.Summary.Id;
    }

    private sealed class SseReader(Stream body) : IDisposable
    {
        private readonly StreamReader reader = new(body);
        public bool EndedOnFrameBoundary { get; private set; }

        /// <summary>The next event or status frame (comments skipped), or null when the body ends.</summary>
        public async Task<(string Type, JsonElement Data, string? Id)?> FrameAsync(CancellationToken ct)
        {
            string? type = null, data = null, id = null;
            var partial = false;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    partial = false;
                    if (type is null || data is null) continue;
                    using var document = JsonDocument.Parse(data);
                    return (type, document.RootElement.Clone(), id);
                }
                partial = true;
                if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            }
            EndedOnFrameBoundary = !partial && type is null && data is null;
            return null;
        }

        public void Dispose() => reader.Dispose();
    }
}
