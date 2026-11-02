using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Ownership;
using Vessel.Api;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

public sealed class NotificationApiTests
{

    [VesselEventPostgresFact]
    public async Task Owner_routes_validate_scope_preferences_and_the_test_cooldown()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();

        var head = await client.GetAsync("/api/notifications/head");
        Assert.True(head.Headers.CacheControl?.NoStore);
        var start = (await head.Content.ReadFromJsonAsync<NotificationHeadDto>())!;
        Assert.Equal(("0.0.0", 0, true, false), (start.NextCursor, start.UnreadCount, start.Enabled, start.WorkerOnline));

        var preferences = (await client.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences"))!;
        Assert.Equal((true, "info", 0L), (preferences.Enabled, preferences.MinimumSeverity, preferences.Revision));
        // The test database has no provisioned subscription: no status yet and nothing failed.
        Assert.Equal((null, 0), (preferences.SubscriptionStatus, preferences.FailedDeliveries));
        var saved = await client.PutAsJsonAsync("/api/notifications/preferences",
            new { enabled = true, minimumSeverity = "warning", categories = new[] { "executions", "discrepancies" }, expectedRevision = 0 });
        Assert.Equal(1L, (await saved.Content.ReadFromJsonAsync<NotificationPreferencesDto>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/notifications/preferences",
            new { enabled = false, minimumSeverity = "info", categories = Array.Empty<string>(), expectedRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/notifications/preferences",
            new { enabled = true, minimumSeverity = "loud", categories = Array.Empty<string>(), expectedRevision = 1 })).StatusCode);
        // A preference change resets old notification cursors.
        Assert.Equal("0.1.0", (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!.NextCursor);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/notifications/test", null)).StatusCode);
        var refused = await client.PostAsync("/api/notifications/test", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(int.Parse(refused.Headers.GetValues("Retry-After").Single()), 1, 10);
        var test = Assert.Single((await client.GetFromJsonAsync<VesselEventPage>("/api/events?after=0&limit=200"))!.Events);
        Assert.Equal((NotificationRules.TestType, "owner"), (test.Type, test.Source.Trigger));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/notifications?tab=mine")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/notifications?pageSize=500")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/notifications?page=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/notifications/read", new { upTo = "nope" })).StatusCode);
        var page = (await client.GetFromJsonAsync<NotificationPage>("/api/notifications?tab=all"))!;
        Assert.Equal((0, 1, 25), (page.Total, page.Page, page.PageSize));
        var claim = (await (await client.PostAsync("/api/notifications/claim", null)).Content.ReadFromJsonAsync<NotificationClaim>())!;
        Assert.Equal((0, 0), (claim.Popups.Count, claim.More));

        await using var foreign = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var other = foreign.AuthorizedClient();
        Assert.Equal(0L, (await other.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences"))!.Revision);
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsync("/api/notifications/test", null)).StatusCode);
    }

    [Fact]
    public async Task The_stream_carries_independent_notification_frames_and_resets()
    {
        var owner = new CoreOwner(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        using var body = new MemoryStream();
        await using var services = new ServiceCollection().AddSingleton<IPrincipalValidity, AlwaysValidPrincipal>().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token, RequestServices = services };
        context.Response.Body = body;
        var epoch = Guid.NewGuid().ToString("N");
        var at = DateTimeOffset.UtcNow;
        var dto = new NotificationDto(Guid.NewGuid(), "run", "executions", "info",
            new("BTC opened", null, [], false, [], new(null, null, null, null, null, null)), at, at, at, "unread", null);
        var reads = 0;
        Task<NotificationFeed> Feed(string? after, int limit, CancellationToken ct) => Task.FromResult(Interlocked.Increment(ref reads) switch
        {
            1 => new NotificationFeed([new($"{epoch}.0.7", dto)], $"{epoch}.0.7", 3, true, false),
            2 => new NotificationFeed([], $"{epoch}.1.9", 2, true, true),
            _ => new NotificationFeed([], after ?? "", 2, true, false)
        });
        var stream = VesselEventEndpoints.StreamAsync("0", new StillReader(epoch), owner, Options.Create(new VesselEventSettings { StreamPollSeconds = 1 }),
            Options.Create(new JsonOptions()), context, $"{epoch}.0.6", Feed);
        for (var attempt = 0; attempt < 40 && Volatile.Read(ref reads) < 2; attempt++) await Task.Delay(100);
        await Task.Delay(100);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.StartsWith("event: status\n", text);
        Assert.Contains($"event: notifications\ndata: {{\"state\":\"ready\",\"nextCursor\":\"{epoch}.0.6\",\"unreadCount\":3,\"enabled\":true}}", text);
        // A notification frame never sets the SSE id: its cursor is in the data.
        Assert.Contains($"event: notification\ndata: {{\"cursor\":\"{epoch}.0.7\",\"unreadCount\":3,\"enabled\":true,\"notification\":{{", text);
        Assert.DoesNotContain("event: notification\nid:", text);
        Assert.Contains($"event: notifications\ndata: {{\"state\":\"reset\",\"nextCursor\":\"{epoch}.1.9\",\"unreadCount\":2,\"enabled\":true}}", text);
        await cancellation.CancelAsync();
        await stream;

        // An invalid notification cursor is refused before the stream starts.
        await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() => VesselEventEndpoints.StreamAsync("0", new StillReader(epoch), owner,
            Options.Create(new VesselEventSettings()), Options.Create(new JsonOptions()), new DefaultHttpContext(), "nope", Feed));
    }

    private sealed class StillReader(string epoch) : IVesselEventReader
    {
        public Task<VesselEventPage> ReadAsync(string? after, int limit, CancellationToken ct) => Task.FromResult(new VesselEventPage([], epoch + ".0", false));
    }
}
