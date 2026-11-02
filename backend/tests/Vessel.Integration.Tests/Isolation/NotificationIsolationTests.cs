using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Application.Ownership;
using Vessel.Identity;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Notifications;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;
using Wolverine;

namespace Vessel.Tests.Isolation;

/// <summary>
/// In-app notifications across two owners: head, page, unread count, claims, reads, read-all, preferences, stream
/// frames and the per-user test cap. Every notification is seeded through the real path: the owner's own
/// <c>POST /api/notifications/test</c> records the event in its scope and the <c>notifications</c> subscription
/// dispatches it (<see cref="IsolationNotifications"/>).
/// </summary>
public abstract class NotificationIsolationCases(IsolationHost api)
{
    private const string NotFound = "Notification not found.";
    private static readonly string[] Paths =
        ["/api/notifications/head", "/api/notifications?tab=all", "/api/notifications?tab=unread", "/api/notifications/preferences"];

    private static Func<Guid, HttpRequestMessage> Read => Isolated.Request(HttpMethod.Post, id => $"/api/notifications/{id}/read");

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();

    private static int Unread(JsonElement head) => head.GetProperty("unreadCount").GetInt32();

    [PostgresFact]
    [Covers("GET", "/api/notifications/head")]
    [Covers("GET", "/api/notifications")]
    [Covers("POST", "/api/notifications/claim")]
    [Covers("POST", "/api/notifications/{id:guid}/read")]
    [Covers("POST", "/api/notifications/read")]
    public async Task Notifications_of_another_owner_are_invisible_and_answer_like_unknown_ones()
    {
        var (a, b) = await api.Owners();
        await using var hub = await IsolationNotifications.CreateAsync(api, a, b);
        var now = api.Clock.Now;
        try
        {
            var aNote = await hub.Send(a);
            var bFirst = await hub.Send(b);
            // B has two, so its own per-ID read (positive control) still leaves one for its read-all.
            api.Clock.Now = now.Add(new NotificationOptions().TestCooldown);
            var bSecond = await hub.Send(b);

            // Positive controls: each owner's head, page and unread tab hold exactly its own notifications.
            Assert.Equal([aNote], Ids(await a.GetJson("/api/notifications?tab=all")));
            Assert.Equal([aNote], Ids(await a.GetJson("/api/notifications?tab=unread")));
            Assert.Equal(1, Unread(await a.GetJson("/api/notifications/head")));
            Assert.Equal([bSecond, bFirst], Ids(await b.GetJson("/api/notifications?tab=all")));
            var bPage = await b.GetJson("/api/notifications?tab=unread");
            Assert.Equal([bSecond, bFirst], Ids(bPage));
            Assert.Equal((2, 2), (bPage.GetProperty("unreadTotal").GetInt32(), bPage.GetProperty("allTotal").GetInt32()));
            Assert.Equal(2, Unread(await b.GetJson("/api/notifications/head")));
            foreach (var path in Paths) Isolated.NoLeak(await b.GetText(path), aNote);
            foreach (var path in Paths) Isolated.NoLeak(await a.GetText(path), bFirst, bSecond);

            var state = await Snapshot.Of(a, Paths);
            // B reading A's notification answers exactly as an unknown ID, and A's stays unread.
            await state.After(() => Isolated.NotFound(b, aNote, Read, NotFound));
            // B's claim wins only B's popups and consumes none of A's.
            var claim = await state.After(() => b.Post("/api/notifications/claim"));
            Assert.Equal([bSecond, bFirst], claim.GetProperty("popups").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));
            Isolated.NoLeak(claim.GetRawText(), aNote);
            // B's own per-ID read is accepted and reads one of B's.
            var read = await state.After(() => b.Post($"/api/notifications/{bFirst}/read"));
            Assert.Equal((bFirst, "read"), (read.GetProperty("id").GetGuid(), read.GetProperty("state").GetString()));
            Assert.Equal(1, Unread(await b.GetJson("/api/notifications/head")));
            // Cursors are positions, not grants: B's read-all up to A's head is refused exactly as a cursor of another,
            // random epoch at the same position, reading nothing on either side.
            var aHead = (await a.GetJson("/api/notifications/head")).GetProperty("nextCursor").GetString()!;
            var unknown = Guid.NewGuid().ToString("N") + aHead[aHead.IndexOf('.')..];
            Assert.Equal(HttpStatusCode.Conflict, await state.After(() => Isolated.SameAsUnknown(b, aHead, unknown,
                cursor => new(HttpMethod.Post, "/api/notifications/read") { Content = JsonContent.Create(new { upTo = cursor }) })));
            Assert.Equal(1, Unread(await b.GetJson("/api/notifications/head")));
            var bHead = (await b.GetJson("/api/notifications/head")).GetProperty("nextCursor").GetString()!;
            await state.After(() => b.Post("/api/notifications/read", new { upTo = bHead }));
            Assert.Equal(0, Unread(await b.GetJson("/api/notifications/head")));
            Assert.Empty(Ids(await b.GetJson("/api/notifications?tab=unread")));
            Assert.Equal(1, Unread(await a.GetJson("/api/notifications/head")));

            // A's own claim still wins its popup, and A can still read it.
            var mine = await a.Post("/api/notifications/claim");
            Assert.Equal([aNote], mine.GetProperty("popups").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));
            // A reading B's (now read) notification is as unknown too, in the other direction.
            var bState = await Snapshot.Of(b, Paths);
            await bState.After(() => Isolated.NotFound(a, bFirst, Read, NotFound));
            Assert.Equal("read", (await a.Post($"/api/notifications/{aNote}/read")).GetProperty("state").GetString());
            await bState.Unchanged();
        }
        finally { api.Clock.Now = now; }
    }

    [PostgresFact]
    [Covers("GET", "/api/notifications/preferences")]
    [Covers("PUT", "/api/notifications/preferences")]
    public async Task Notification_preferences_belong_to_one_user()
    {
        var (a, b) = await api.Owners();
        await using var hub = await IsolationNotifications.CreateAsync(api, a, b);
        var aNote = await hub.Send(a);
        await hub.Send(b);
        var defaults = await a.GetJson("/api/notifications/preferences");
        Assert.Equal((true, "info", 0L), (defaults.GetProperty("enabled").GetBoolean(), defaults.GetProperty("minimumSeverity").GetString(),
            defaults.GetProperty("revision").GetInt64()));

        var state = await Snapshot.Of(a, Paths);
        // B turns its in-app notifications off and raises its threshold: A's preferences, revision and head stay as they were.
        var saved = await state.After(() => b.Send(HttpMethod.Put, "/api/notifications/preferences",
            new UpdateNotificationPreferencesRequest(false, "critical", [], 0)));
        Assert.Equal((false, "critical", 1L), (saved.GetProperty("enabled").GetBoolean(), saved.GetProperty("minimumSeverity").GetString(),
            saved.GetProperty("revision").GetInt64()));
        Assert.False((await b.GetJson("/api/notifications/head")).GetProperty("enabled").GetBoolean());
        // B's switch gates only B's popups.
        Assert.Empty((await state.After(() => b.Post("/api/notifications/claim"))).GetProperty("popups").EnumerateArray());
        Assert.True((await a.GetJson("/api/notifications/head")).GetProperty("enabled").GetBoolean());
        Assert.Equal([aNote], (await a.Post("/api/notifications/claim")).GetProperty("popups").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));

        // A's revision is its own: A saves against revision 0, untouched by B's save.
        var aSaved = await a.Send(HttpMethod.Put, "/api/notifications/preferences",
            new UpdateNotificationPreferencesRequest(true, "warning", [], 0));
        Assert.Equal(("warning", 1L), (aSaved.GetProperty("minimumSeverity").GetString(), aSaved.GetProperty("revision").GetInt64()));
        var bAfter = await b.GetJson("/api/notifications/preferences");
        Assert.Equal((false, "critical", 1L), (bAfter.GetProperty("enabled").GetBoolean(), bAfter.GetProperty("minimumSeverity").GetString(),
            bAfter.GetProperty("revision").GetInt64()));
    }

    [PostgresFact]
    [Covers("GET", "/api/events/stream")]
    public async Task Notification_frames_on_an_event_stream_carry_only_the_callers_notifications()
    {
        var (a, b) = await api.Owners();
        await using var hub = await IsolationNotifications.CreateAsync(api, a, b);
        var now = api.Clock.Now;
        try
        {
            var aFirst = await hub.Send(a);
            var bFirst = await hub.Send(b);
            var aHead = (await a.GetJson("/api/notifications/head")).GetProperty("nextCursor").GetString()!;
            // Each owner's stream from the start of its own feed replays its own notification only.
            foreach (var (owner, own, foreign) in new[] { (a, aFirst, bFirst), (b, bFirst, aFirst) })
            {
                await using var stream = await NotificationStream.Open(owner, Start(await owner.GetJson("/api/notifications/head")));
                var frames = await stream.Until(own);
                Isolated.NoLeak(frames, foreign);
            }
            // A cursor is a position, not a grant: B resuming from A's head still sees only B's feed.
            await using (var resumed = await NotificationStream.Open(b, aHead))
                Isolated.NoLeak(await resumed.Until(null), aFirst);

            // Live: with B's stream open, A's next notification commits before B's; B's stream reaches B's without A's.
            await using var live = await NotificationStream.Open(b, Start(await b.GetJson("/api/notifications/head")));
            Isolated.NoLeak(await live.Until(bFirst), aFirst);
            api.Clock.Now = now.Add(new NotificationOptions().TestCooldown);
            var aSecond = await hub.Send(a);
            var bSecond = await hub.Send(b);
            Isolated.NoLeak(await live.Until(bSecond), aFirst, aSecond);
            Assert.Equal(2, Unread(await a.GetJson("/api/notifications/head")));
        }
        finally { api.Clock.Now = now; }
    }

    [PostgresFact]
    [Covers("POST", "/api/notifications/test")]
    public async Task Test_notification_cap_is_per_user()
    {
        var (a, b) = await api.Owners();
        await using var hub = await IsolationNotifications.CreateAsync(api, a, b);
        var cooldown = new NotificationOptions().TestCooldown;
        var now = api.Clock.Now;
        try
        {
            // Six of A's requests, each past the durable cooldown and all within one minute, are accepted.
            for (var i = 0; i < 6; i++)
            {
                api.Clock.Now = now + cooldown * i;
                await a.Post("/api/notifications/test", expected: HttpStatusCode.Accepted);
            }
            Assert.Equal(6, await hub.Requests(a));
            // A's seventh within that minute meets A's own per-user cap, which is checked before the cooldown and names
            // itself by its code: 429 with Retry-After, and no event.
            Assert.True(cooldown * 5 < TimeSpan.FromMinutes(1));
            var aState = await Snapshot.Of(a, Paths);
            using (var refused = await a.Http.PostAsync("/api/notifications/test", null))
            {
                await Isolated.Problem(refused, HttpStatusCode.TooManyRequests, "rate-limited");
                // The window opened with A's first request, so it frees a slot one minute after it.
                Assert.Equal(TimeSpan.FromMinutes(1) - cooldown * 5, refused.Headers.RetryAfter?.Delta);
            }
            Assert.Equal(6, await hub.Requests(a));
            // At the same clock B's own request is accepted, records B's event and becomes B's notification only.
            await aState.After(() => b.Post("/api/notifications/test", expected: HttpStatusCode.Accepted));
            Assert.Equal(1, await hub.Requests(b));
            var bNote = await hub.DeliverLatest(b);
            Assert.Equal([bNote], Ids(await b.GetJson("/api/notifications?tab=all")));
            Isolated.NoLeak(await a.GetText("/api/notifications?tab=all"), bNote);
            await aState.Unchanged();
        }
        finally { api.Clock.Now = now; }
    }

    /// <summary>The start of the owner's notification feed: its head cursor at sequence 0.</summary>
    private static string Start(JsonElement head)
    {
        var cursor = head.GetProperty("nextCursor").GetString()!;
        return cursor[..(cursor.LastIndexOf('.') + 1)] + "0";
    }

    /// <summary>One open event stream with the notification feed multiplexed, read frame by frame.</summary>
    private sealed class NotificationStream : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancel;
        private readonly HttpResponseMessage response;
        private readonly StreamReader reader;
        private readonly StringBuilder text = new();
        private bool ready;

        private NotificationStream(CancellationTokenSource cancel, HttpResponseMessage response, StreamReader reader) =>
            (this.cancel, this.response, this.reader) = (cancel, response, reader);

        public static async Task<NotificationStream> Open(Owner owner, string notificationsAfter)
        {
            var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await owner.Http.SendAsync(new(HttpMethod.Get,
                $"/api/events/stream?after=0&notificationsAfter={Uri.EscapeDataString(notificationsAfter)}"),
                HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new(cancel, response, new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token)));
        }

        /// <summary>
        /// Reads until a <c>notification</c> frame for <paramref name="id"/> arrived and the next keepalive followed it,
        /// or, without an ID, until the first keepalive after the <c>notifications</c> frame. Returns everything read.
        /// </summary>
        public async Task<string> Until(Guid? id)
        {
            var found = id is null;
            var notification = false;
            while (await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                text.AppendLine(line);
                if (line == "event: notifications") ready = true;
                if (line == "event: notification") notification = true;
                else if (notification && line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    notification = false;
                    using var frame = JsonDocument.Parse(line[6..]);
                    if (frame.RootElement.GetProperty("notification").GetProperty("id").GetGuid() == id) found = true;
                }
                if (ready && found && line == ": keepalive") return text.ToString();
            }
            Assert.Fail("The stream ended before the expected frames: " + text);
            return text.ToString();
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            cancel.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The cases above with the suite's test scheme selecting the owner.</summary>
public sealed class NotificationIsolationTests(IsolationApi api) : NotificationIsolationCases(api), IClassFixture<IsolationApi>;

/// <summary>The same cases through wallet sessions: two signed-in users, dispatched against the identity directory the API itself uses.</summary>
public sealed class WalletNotificationIsolationTests(WalletIsolationApi api) : NotificationIsolationCases(api), IClassFixture<WalletIsolationApi>;

/// <summary>
/// The <c>notifications</c> subscription as its worker runs it, over the isolation host's journal: the registration is
/// server-wide, and each event is dispatched by the real listener into its own owner's scope with the hub as subscriber.
/// Through wallet sessions the owner directory is the identity one the API resolves; the test-scheme host's directory
/// knows only the installation owner, so the case's two owners are listed active instead.
/// </summary>
internal sealed class IsolationNotifications : IAsyncDisposable
{
    private readonly NotificationsModule module = new();
    private readonly IJournalOwnerDirectory directory;
    private readonly ServiceProvider services;

    private IsolationNotifications(IsolationHost api, IJournalOwnerDirectory directory)
    {
        this.directory = directory;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = api.Database.ConnectionString
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
        collection.AddSingleton(directory);
        collection.AddSingleton<TimeProvider>(api.Clock);
        collection.AddVesselPersistence(configuration);
        collection.AddVesselEvents(configuration);
        collection.AddScoped<IVesselEventOutbox, NoOutbox>();
        module.AddRunningServices(collection, configuration);
        services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private IOwnerScopeFactory Scopes => services.GetRequiredService<IOwnerScopeFactory>();

    public static async Task<IsolationNotifications> CreateAsync(IsolationHost api, params Owner[] owners)
    {
        IJournalOwnerDirectory directory;
        if (api is WalletIsolationApi)
            directory = Assert.IsType<IdentityOwnerDirectory>(api.Factory.Services.GetRequiredService<IJournalOwnerDirectory>());
        else
        {
            var listed = new MutableOwnerDirectory();
            foreach (var owner in owners) listed.Set(owner.Id, JournalOwnerStatus.Active);
            directory = listed;
        }
        foreach (var owner in owners) Assert.Equal(JournalOwnerStatus.Active, await directory.StatusAsync(owner.Id, default));
        var hub = new IsolationNotifications(api, directory);
        // Registered once per host database; later cases find it registered.
        await hub.services.GetRequiredService<SystemEventSubscriptionStore>().RegisterAsync(hub.module.Subscriptions[0], default);
        return hub;
    }

    /// <summary>The owner requests a test notification through its own client; the event is dispatched. Returns the notification.</summary>
    public async Task<Guid> Send(Owner owner)
    {
        await owner.Post("/api/notifications/test", expected: HttpStatusCode.Accepted);
        return await DeliverLatest(owner);
    }

    /// <summary>Dispatches the owner's latest test event through the subscription; returns the one notification it created.</summary>
    public async Task<Guid> DeliverLatest(Owner owner)
    {
        var before = await Notifications(owner.Id);
        var message = (await Events(owner.Id)).Last(e => e.Type == NotificationRules.TestType);
        Assert.Equal(owner.Id, message.OwnerId);
        var topology = new MessagingTopology(new MessagingSettings(), new VesselModuleRegistry([module]));
        await EventSubscriptionListener.Handle(message,
            new Envelope { Destination = new Uri("rabbitmq://queue/" + topology.SubscriptionQueue(module.Subscriptions[0].Name)) },
            new MessagingHost(NotificationsModule.ModuleName, [module]), topology, Scopes, directory,
            NullLogger<EventSubscriptionHandlerLog>.Instance, default);
        return Assert.Single((await Notifications(owner.Id)).Except(before));
    }

    /// <summary>How many test requests the owner's journal recorded.</summary>
    public async Task<int> Requests(Owner owner) => (await Events(owner.Id)).Count(e => e.Type == NotificationRules.TestType);

    private async Task<IReadOnlyList<VesselEvent>> Events(Guid owner)
    {
        await using var scope = Scopes.Create(owner);
        return (await scope.Services.GetRequiredService<IVesselEventReader>().ReadAsync("0", 200, default)).Events;
    }

    private async Task<List<Guid>> Notifications(Guid owner)
    {
        await using var scope = Scopes.Create(owner);
        var page = await scope.Services.GetRequiredService<NotificationService>().PageAsync(new("all", null, null, 1, 100), default);
        return page.Items.Select(n => n.Id).ToList();
    }

    public ValueTask DisposeAsync() => services.DisposeAsync();

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Reset() { }
    }
}
