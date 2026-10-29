using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Events;
using Vessel.Application.Notifications;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Credentials;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Messaging.Modules.Notifications;
using Vessel.Persistence;
using Vessel.Persistence.Events;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Tests;

/// <summary>The Notification Hub against PostgreSQL, through the real dispatcher, store and locks.</summary>
public sealed class NotificationHubPostgresTests
{
    [VesselEventPostgresFact]
    public async Task A_run_becomes_one_notification_and_its_peers_are_covered_not_repeated()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC breakout");
        var run = Guid.CreateVersion7();
        var events = await hub.AppendAsync(
            hub.Fill(run, account, play, "entry", "buy", "0.5", "84541"),
            hub.Lifecycle("play.opened", run, account, play, new { previous = "planned", current = "open", source = "venue", reason = (string?)null }));

        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(events[1]));
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(events[0]));
        Assert.Equal(DispatchOutcome.AlreadySettled, await hub.DispatchAsync(events[0]));
        var notification = Assert.Single(await hub.AllAsync());
        Assert.Equal(("run", "executions", "unread"), (notification.Kind, notification.Category, notification.State));
        Assert.Equal("BTC breakout opened", notification.Content.Title);
        Assert.Equal("Buy 0.5 at 84541", notification.Content.Summary);
        Assert.Equal(1, (await hub.HeadAsync()).UnreadCount);
        Assert.Equal(2, await hub.CountAsync("SELECT count(*) FROM notification_sources WHERE notification_id IS NOT NULL"));
    }

    [VesselEventPostgresFact]
    public async Task Each_candidate_passes_every_check_and_a_detail_line_waits_for_a_headline()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("ETH swing");
        var run = Guid.CreateVersion7();
        // A link change alone notifies nothing and is not consumed.
        var detail = (await hub.AppendAsync(hub.Lifecycle("execution.link-changed", run, account, play,
            new { linkId = Guid.NewGuid(), previous = (string?)null, current = "linked", role = "exit", source = "automatic" })))[0];
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(detail));
        Assert.Equal(0, await hub.CountAsync("SELECT count(*) FROM notification_sources"));
        // A later unit of the same run brings a headline; an owner-triggered and an over-age peer are left out.
        var later = await hub.AppendAsync(
            hub.Fill(run, account, play, "exit", "sell", "1", "2510"),
            hub.Fill(run, account, play, "target", "sell", "1", "2600") with { Source = new("owner") },
            hub.Fill(run, account, play, "stop", "sell", "1", "2400") with
                { OccurredAt = hub.Clock.GetUtcNow().AddDays(-9), DetectedAt = hub.Clock.GetUtcNow().AddDays(-8) });
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(later[0]));
        var notification = Assert.Single(await hub.AllAsync());
        Assert.Equal("ETH swing: exit filled", notification.Content.Title);
        Assert.Contains("Exit order linked", notification.Content.Lines);
        Assert.DoesNotContain(notification.Content.Lines, line => line.Contains("2600") || line.Contains("2400"));
        // The detail's own receipt settled as skipped earlier; the later batch still covered it.
        Assert.Equal(DispatchOutcome.AlreadySettled, await hub.DispatchAsync(detail));
        Assert.Equal(1, await hub.CountAsync($"SELECT count(*) FROM notification_sources WHERE event_id='{detail.Id}' AND notification_id IS NOT NULL"));
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(later[1]));
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(later[2]));
    }

    [VesselEventPostgresFact]
    public async Task Overflow_beyond_the_group_limit_follows_up_without_losing_an_event()
    {
        await using var hub = await Hub.CreateAsync(runGroupLimit: 2);
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var run = Guid.CreateVersion7();
        var events = await hub.AppendAsync(Enumerable.Range(1, 5)
            .Select(i => hub.Fill(run, account, play, "entry", "buy", $"{i}", "100")).ToArray());
        // Newest first: each waking event is covered by its own dispatch even when older peers fill the limit.
        foreach (var message in events.Reverse()) Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(message));
        Assert.Equal(5, await hub.CountAsync("SELECT count(*) FROM notification_sources WHERE notification_id IS NOT NULL"));
        Assert.Equal(3, (await hub.AllAsync()).Count);
    }

    [VesselEventPostgresFact]
    public async Task Concurrent_dispatches_of_one_run_create_one_notification()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("SOL");
        for (var round = 0; round < 5; round++)
        {
            var run = Guid.CreateVersion7();
            var events = await hub.AppendAsync(
                hub.Fill(run, account, play, "entry", "buy", "1", "150"),
                hub.Fill(run, account, play, "exit", "sell", "1", "151"),
                hub.Lifecycle("play.status-changed", run, account, play, new { previous = "open", current = "paused", source = "venue", reason = (string?)null }));
            var outcomes = await Task.WhenAll(events.Select(e => hub.DispatchAsync(e)));
            Assert.All(outcomes, outcome => Assert.Equal(DispatchOutcome.Delivered, outcome));
        }
        Assert.Equal(5, (await hub.AllAsync()).Count);
        Assert.Equal(15, await hub.CountAsync("SELECT count(*) FROM notification_sources"));
    }

    [VesselEventPostgresFact]
    public async Task Discrepancy_generations_withdraw_unread_openings_and_announce_only_resolutions_of_read_ones()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var discrepancy = await hub.SeedDiscrepancyAsync(account, play);

        // Opened, unread, then resolved: withdrawn, and the resolution says nothing.
        var opened = await hub.DiscrepancyAsync(discrepancy, account, play, 1, "opened", "critical");
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(opened));
        Assert.Equal(1, (await hub.HeadAsync()).UnreadCount);
        var resolved = await hub.DiscrepancyAsync(discrepancy, account, play, 2, "resolved", "info");
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(resolved));
        Assert.Equal("withdrawn", Assert.Single(await hub.AllAsync()).State);
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);

        // Reopened and read, then resolved: the resolution is news.
        var reopened = await hub.DiscrepancyAsync(discrepancy, account, play, 3, "opened", "critical");
        await hub.DispatchAsync(reopened);
        var current = (await hub.AllAsync()).Single(n => n.State == "unread");
        await hub.ReadAsync(current.Id);
        await hub.DispatchAsync(await hub.DiscrepancyAsync(discrepancy, account, play, 4, "resolved", "info"));
        var announced = (await hub.AllAsync()).Single(n => n.State == "unread");
        Assert.Equal(("info", "BTC: resolved"), (announced.Severity, announced.Content.Title));
    }

    [VesselEventPostgresFact]
    public async Task Withdrawal_runs_before_preferences_trigger_and_order_rules()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var discrepancy = await hub.SeedDiscrepancyAsync(account, play);
        await hub.SavePreferencesAsync(new(true, "warning", NotificationValues.Categories, 1), 0);
        await hub.DispatchAsync(await hub.DiscrepancyAsync(discrepancy, account, play, 1, "opened", "warning"));
        Assert.Equal(1, (await hub.HeadAsync()).UnreadCount);
        // An owner dismissal is owner-triggered and info: below the minimum, yet it still withdraws.
        var dismissed = await hub.DiscrepancyAsync(discrepancy, account, play, 2, "dismissed", "info", trigger: "owner");
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(dismissed));
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);

        // Out of order: a newer generation handled first decides; the older event changes nothing.
        var second = await hub.SeedDiscrepancyAsync(account, play);
        var early = await hub.DiscrepancyAsync(second, account, play, 1, "opened", "critical");
        var late = await hub.DiscrepancyAsync(second, account, play, 2, "resolved", "info");
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(late));
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(early));
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);
    }

    [VesselEventPostgresFact]
    public async Task Unverifiable_keeps_the_opening_and_relevance_skips_superseded_or_missing_subjects()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var discrepancy = await hub.SeedDiscrepancyAsync(account, play);
        await hub.DispatchAsync(await hub.DiscrepancyAsync(discrepancy, account, play, 1, "opened", "critical"));
        await hub.DispatchAsync(await hub.DiscrepancyAsync(discrepancy, account, play, 2, "unverifiable", "warning"));
        Assert.Equal(2, (await hub.AllAsync()).Count(n => n.State == "unread"));

        // The record has announced a newer revision: the stale opening creates nothing.
        var stale = await hub.SeedDiscrepancyAsync(account, play, publishedRevision: 5);
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(await hub.DiscrepancyAsync(stale, account, play, 1, "opened", "critical", publish: false)));
        Assert.Equal(2, (await hub.HeadAsync()).UnreadCount);
    }

    [VesselEventPostgresFact]
    public async Task Monitor_problems_notify_and_a_recovery_withdraws_unread_or_announces_read_ones()
    {
        await using var hub = await Hub.CreateAsync();
        var account = await hub.SeedAccountAsync("Main");
        await hub.SetMonitorRevisionAsync(account, 1);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 1, "degraded", "live"));
        var problem = Assert.Single(await hub.AllAsync());
        Assert.Equal(("warning", "Monitoring degraded on Main"), (problem.Severity, problem.Content.Title));
        await hub.SetMonitorRevisionAsync(account, 2);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 2, "live", "degraded"));
        Assert.Equal("withdrawn", Assert.Single(await hub.AllAsync()).State);

        await hub.SetMonitorRevisionAsync(account, 3);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 3, "stale", "live"));
        await hub.ReadAsync((await hub.AllAsync()).Single(n => n.State == "unread").Id);
        await hub.SetMonitorRevisionAsync(account, 4);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 4, "live", "stale"));
        var restored = (await hub.AllAsync()).Single(n => n.State == "unread");
        Assert.Equal(("info", "Monitoring restored on Main"), (restored.Severity, restored.Content.Title));
    }

    [VesselEventPostgresFact]
    public async Task Credential_and_link_relevance_use_current_state()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC", venue: "lighter");
        var revision = await hub.SeedCredentialAsync(account, hub.Clock.GetUtcNow().AddDays(5));
        await hub.DispatchAsync(await hub.CredentialAsync(account, revision));
        var expiring = Assert.Single(await hub.AllAsync());
        Assert.Equal("account-credential", Assert.Single(expiring.Content.Links).Kind);
        // A later announcement for a token that is no longer current supersedes the unread one and creates nothing.
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(await hub.CredentialAsync(account, "00ff")));
        Assert.Equal("withdrawn", Assert.Single(await hub.AllAsync()).State);

        // A suggestion whose order is linked elsewhere is hidden, not actionable.
        var link = await hub.SeedSuggestionAsync(account, play, competingLink: true);
        var run = Guid.CreateVersion7();
        var suggested = await hub.AppendAsync(hub.Lifecycle("link.needs-confirmation", run, account, play,
            new { linkId = link, previous = (string?)null, current = "suggested", role = "entry", source = "automatic" }, "warning"));
        Assert.Equal(DispatchOutcome.Skipped, await hub.DispatchAsync(suggested[0]));
    }

    [VesselEventPostgresFact]
    public async Task Claims_are_atomic_bounded_and_respect_the_popup_window_and_switch()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC");
        for (var index = 0; index < 5; index++)
        {
            var run = Guid.CreateVersion7();
            await hub.DispatchAsync((await hub.AppendAsync(hub.Fill(run, account, play, "entry", "buy", $"{index + 1}", "100")))[0]);
            if (index == 0) hub.Clock.Advance(TimeSpan.FromDays(2));
        }
        var claims = await Task.WhenAll(hub.ClaimAsync(), hub.ClaimAsync());
        var popups = claims.SelectMany(c => c.Popups).ToList();
        Assert.Equal(3, popups.Count);
        Assert.Equal(5, claims.Sum(c => c.Popups.Count + c.More));
        Assert.Contains(claims, c => c.Popups.Count == 0 && c.More == 0);
        Assert.Empty((await hub.ClaimAsync()).Popups);
        Assert.Equal(5, (await hub.HeadAsync()).UnreadCount);

        await hub.SavePreferencesAsync(new(false, "info", NotificationValues.Categories, 1), 0);
        await hub.DispatchAsync((await hub.AppendAsync(hub.Fill(Guid.CreateVersion7(), account, play, "entry", "buy", "9", "100")))[0]);
        Assert.Equal(5, (await hub.AllAsync()).Count);
    }

    [VesselEventPostgresFact]
    public async Task The_feed_is_one_snapshot_mark_all_uses_created_sequences_and_resets_are_explicit()
    {
        await using var hub = await Hub.CreateAsync();
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var first = (await hub.AppendAsync(hub.Fill(Guid.CreateVersion7(), account, play, "entry", "buy", "1", "100")))[0];
        await hub.DispatchAsync(first);
        var head = await hub.HeadAsync();
        await hub.DispatchAsync((await hub.AppendAsync(hub.Fill(Guid.CreateVersion7(), account, play, "exit", "sell", "1", "101")))[0]);
        var feed = await hub.FeedAsync(head.NextCursor);
        Assert.False(feed.ResetRequired);
        Assert.Equal(2, feed.UnreadCount);
        Assert.Single(feed.Items);

        // Mark all up to the earlier head reads only what existed then, even after later feed changes.
        Assert.Equal(1, await hub.ReadAllAsync(head.NextCursor));
        Assert.Equal(1, (await hub.HeadAsync()).UnreadCount);
        var after = await hub.FeedAsync(feed.NextCursor);
        Assert.Equal("read", Assert.Single(after.Items).Notification.State);

        // A preference change and an epoch rotation each make old cursors reset.
        await hub.SavePreferencesAsync(NotificationPreferences.Defaults with { MinimumSeverity = "warning" }, 0);
        Assert.True((await hub.FeedAsync(after.NextCursor)).ResetRequired);
        var current = (await hub.HeadAsync()).NextCursor;
        await hub.ExecuteAsync("UPDATE event_cursors SET epoch=gen_random_uuid()");
        Assert.True((await hub.FeedAsync(current)).ResetRequired);
        var problem = await Assert.ThrowsAsync<ProblemException>(() => hub.ReadAllAsync(current));
        Assert.Equal(409, problem.StatusCode);
    }

    [VesselEventPostgresFact]
    public async Task Disable_withdraws_and_hides_enable_shows_and_deletion_removes()
    {
        await using var hub = await Hub.CreateAsync();
        var account = await hub.SeedAccountAsync("Side");
        await hub.SetMonitorRevisionAsync(account, 1);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 1, "degraded", "live"));
        var reset = (await hub.HeadAsync()).NextCursor;
        await hub.SetEnabledAsync(account, false);
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);
        Assert.Equal(0, (await hub.PageAsync("all")).AllTotal);
        Assert.True((await hub.FeedAsync(reset)).ResetRequired);
        await hub.SetEnabledAsync(account, true);
        Assert.Equal("withdrawn", Assert.Single((await hub.PageAsync("all")).Items).State);
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);

        await hub.SetMonitorRevisionAsync(account, 2);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 2, "stale", "degraded"));
        Assert.Equal(1, (await hub.HeadAsync()).UnreadCount);
        await hub.DeleteAccountAsync(account);
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);
        Assert.Equal(0, await hub.CountAsync("SELECT count(*) FROM notifications"));
        Assert.Equal(0, await hub.CountAsync("SELECT count(*) FROM notification_sources"));
    }

    [VesselEventPostgresFact]
    public async Task The_hub_racing_disable_or_deletion_neither_deadlocks_nor_leaves_unread_on_a_disabled_account()
    {
        await using var hub = await Hub.CreateAsync();
        for (var round = 0; round < 20; round++)
        {
            var account = await hub.SeedAccountAsync($"Race {round}");
            await hub.SetMonitorRevisionAsync(account, 1);
            var problem = await hub.MonitorAsync(account, 1, "degraded", "live");
            if (round % 2 == 0)
            {
                await Task.WhenAll(hub.DispatchAsync(problem), hub.SetEnabledAsync(account, false));
                Assert.Equal(0, await hub.CountAsync(
                    $"SELECT count(*) FROM notification_deliveries d JOIN notifications n USING (owner_id,notification_id) WHERE n.account_id='{account}' AND d.state='unread'"));
            }
            else
            {
                await Task.WhenAll(hub.DispatchAsync(problem), hub.DeleteAccountAsync(account));
                Assert.Equal(0, await hub.CountAsync($"SELECT count(*) FROM notifications WHERE account_id='{account}'"));
            }
        }
        Assert.Equal(await hub.CountAsync("SELECT count(*) FROM notification_deliveries WHERE state='unread'"),
            (await hub.HeadAsync()).UnreadCount);
    }

    [VesselEventPostgresFact]
    public async Task Tests_bypass_owner_rules_cooldown_atomically_and_retention_prunes_by_age()
    {
        await using var hub = await Hub.CreateAsync();
        Assert.Null(await hub.StartTestAsync());
        Assert.NotNull(await hub.StartTestAsync());
        hub.Clock.Advance(TimeSpan.FromSeconds(11));
        var test = (await hub.AppendAsync(new VesselEventDraft(NotificationRules.TestType, Guid.CreateVersion7(), $"notification-test:{Guid.NewGuid():N}",
            "info", hub.Clock.GetUtcNow(), hub.Clock.GetUtcNow(), new(), new("owner"), JsonSerializer.SerializeToElement(new { }))))[0];
        Assert.Equal(DispatchOutcome.Delivered, await hub.DispatchAsync(test));
        var created = Assert.Single(await hub.AllAsync());
        Assert.Equal(("test", "Test notification"), (created.Kind, created.Content.Title));

        hub.Clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(1, await hub.RetainAsync());
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);
        Assert.Empty(await hub.AllAsync());
    }

    [VesselEventPostgresFact]
    public async Task An_ineligible_prefix_does_not_hide_a_later_eligible_run_event()
    {
        await using var hub = await Hub.CreateAsync(runGroupLimit: 1);
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var run = Guid.CreateVersion7();
        var ignored = Enumerable.Range(0, 5).Select(_ =>
            hub.Fill(run, account, play, "entry", "buy", "1", "100") with { Source = new("owner") });
        var events = await hub.AppendAsync([.. ignored, hub.Fill(run, account, play, "exit", "sell", "1", "101")]);
        foreach (var message in events) await hub.DispatchAsync(message);
        Assert.Single(await hub.AllAsync());
    }

    [VesselEventPostgresFact]
    public async Task A_detail_prefix_does_not_hide_a_run_headline_at_the_group_limit()
    {
        await using var hub = await Hub.CreateAsync(runGroupLimit: 1);
        var (account, play) = await hub.SeedPlayAsync("BTC");
        var run = Guid.CreateVersion7();
        var events = await hub.AppendAsync(
            hub.Lifecycle("execution.link-changed", run, account, play,
                new { current = "linked", role = "entry" }),
            hub.Fill(run, account, play, "entry", "buy", "1", "100"));
        foreach (var message in events) await hub.DispatchAsync(message);
        Assert.Single(await hub.AllAsync());
    }

    [VesselEventPostgresFact]
    public async Task Monitor_recovery_requires_the_latest_problem_to_have_been_read()
    {
        await using var hub = await Hub.CreateAsync();
        var account = await hub.SeedAccountAsync("Main");
        await hub.SetMonitorRevisionAsync(account, 1);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 1, "degraded", "live"));
        await hub.ReadAsync(Assert.Single(await hub.AllAsync()).Id);
        await hub.SetMonitorRevisionAsync(account, 2);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 2, "stale", "degraded"));
        await hub.SetMonitorRevisionAsync(account, 3);
        await hub.DispatchAsync(await hub.MonitorAsync(account, 3, "live", "stale"));
        Assert.Equal(0, (await hub.HeadAsync()).UnreadCount);
    }

    // ---- Runtime ----------------------------------------------------------------------------------------------

    private sealed class Hub : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly EventSubscription subscription = new NotificationsModule().Subscriptions[0];

        private Hub(CoreDatabase database, int runGroupLimit)
        {
            Database = database;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vessel"] = database.ConnectionString,
                ["Vessel:Auth:OwnerId"] = Owner.ToString(),
                ["Vessel:Notifications:RunGroupLimit"] = runGroupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Vessel.Application.Ownership.IJournalOwnerContext>(new CoreOwner(Owner));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(Owner));
            services.AddVesselPersistence(configuration);
            services.AddVesselEvents(configuration);
            services.AddScoped<IVesselEventOutbox, NoOutbox>();
            new NotificationsModule().AddRunningServices(services, configuration);
            provider = services.BuildServiceProvider();
        }

        public CoreDatabase Database { get; }
        public Guid Owner { get; } = Guid.NewGuid();
        public HubClock Clock { get; } = new();

        public static async Task<Hub> CreateAsync(int runGroupLimit = 200)
        {
            var hub = new Hub(await CoreDatabase.CreateAsync(), runGroupLimit);
            await using var scope = hub.provider.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<EventSubscriptionRegistry>().RegisterAsync(hub.subscription, default));
            return hub;
        }

        public async Task<Guid> SeedAccountAsync(string name, string venue = "hyperliquid")
        {
            var account = new Account(Guid.NewGuid(), Owner, venue, name);
            await using var db = Database.Context(Owner);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return account.Id;
        }

        public async Task<(Guid Account, Guid Play)> SeedPlayAsync(string title, string venue = "hyperliquid")
        {
            var account = new Account(Guid.NewGuid(), Owner, venue, "Main");
            var play = TestPlays.Create(account, new(venue, "BTC"));
            await using var db = Database.Context(Owner);
            db.Accounts.Add(account); db.Plays.Add(play);
            await db.SaveChangesAsync();
            await ExecuteAsync($"UPDATE plays SET \"Title\"='{title}', \"Status\"='Open' WHERE \"Id\"='{play.Id}'");
            return (account.Id, play.Id);
        }

        public async Task<Guid> SeedDiscrepancyAsync(Guid account, Guid play, long? publishedRevision = null)
        {
            var row = new PlayDiscrepancy
            {
                OwnerId = Owner, AccountId = account, PlayId = play, ContractId = "BTC", NativeContractId = "BTC",
                SubjectKey = Guid.NewGuid().ToString("N"), Kind = "protection.missing", Severity = "critical", State = "open",
                Fingerprint = "f", OpenedAtUtc = Clock.GetUtcNow(), LastObservedAtUtc = Clock.GetUtcNow(), PublishedRevision = publishedRevision
            };
            await using var db = Database.Context(Owner);
            db.Set<PlayDiscrepancy>().Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        public async Task SetMonitorRevisionAsync(Guid account, long revision)
        {
            await using (var db = Database.Context(Owner))
            {
                if (!await db.Set<MonitorAccount>().AnyAsync(m => m.AccountId == account))
                {
                    db.Set<MonitorAccount>().Add(new MonitorAccount { AccountId = account, OwnerId = Owner });
                    await db.SaveChangesAsync();
                }
            }
            await ExecuteAsync($"UPDATE monitor_accounts SET \"StateRevision\"={revision} WHERE \"AccountId\"='{account}'");
        }

        public async Task<string> SeedCredentialAsync(Guid account, DateTimeOffset expires)
        {
            var credential = new AccountCredential
            {
                OwnerId = Owner, AccountId = account, Purpose = "lighter-read-token", KeyId = "k", Nonce = new byte[12], Ciphertext = [2],
                Tag = new byte[16], Scope = "single", ExpiresAt = expires, CreatedAt = Clock.GetUtcNow(), LastVerifiedAt = Clock.GetUtcNow()
            };
            await using var db = Database.Context(Owner);
            db.AccountCredentials.Add(credential);
            await db.SaveChangesAsync();
            return Vessel.Application.Credentials.CredentialAttempt.RevisionOf(credential);
        }

        public async Task<Guid> SeedSuggestionAsync(Guid account, Guid play, bool competingLink)
        {
            await using var db = Database.Context(Owner);
            var stored = await db.Plays.SingleAsync(p => p.Id == play);
            var now = Clock.GetUtcNow();
            var order = new ImportedOrder
            {
                Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account, ContractId = "BTC", ExternalOrderId = "order-1", Side = "buy",
                OrderType = "limit", OriginalSize = 1m, RemainingSize = 1m, Status = "open", VenueStatus = "open",
                PlacedAtUtc = now.AddMinutes(-1), StatusAtUtc = now, ObservedAtUtc = now
            };
            db.Orders.Add(order);
            var suggestion = new PlayOrderLink(stored, order, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Suggested, OrderLinkSource.Automatic, now);
            db.OrderLinks.Add(suggestion);
            if (competingLink)
            {
                var owner = await db.Accounts.SingleAsync(a => a.Id == account);
                var other = TestPlays.Create(owner, new(owner.VenueId, "BTC"));
                db.Plays.Add(other);
                db.OrderLinks.Add(new PlayOrderLink(other, order, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Owner, now));
            }
            await db.SaveChangesAsync();
            return suggestion.Id;
        }

        public VesselEventDraft Fill(Guid run, Guid account, Guid play, string role, string side, string quantity, string price) =>
            new("execution.filled", run, $"order:{account}:{role}-{Guid.NewGuid():N}:filled:{quantity}", role == "stop" ? "warning" : "info",
                Clock.GetUtcNow(), Clock.GetUtcNow(), new(account, "hyperliquid", "BTC", play), new("monitor"),
                JsonSerializer.SerializeToElement(new { role, side, filledQuantity = quantity, cumulativeQuantity = quantity,
                    remainingQuantity = "0", averagePrice = price, complete = true }));

        public VesselEventDraft Lifecycle(string type, Guid run, Guid account, Guid play, object data, string severity = "info") =>
            new(type, run, $"{type}:{Guid.NewGuid():N}", severity, Clock.GetUtcNow(), Clock.GetUtcNow(),
                new(account, "hyperliquid", "BTC", play), new("monitor"), JsonSerializer.SerializeToElement(data));

        public async Task<VesselEvent> DiscrepancyAsync(Guid id, Guid account, Guid play, long revision, string transition, string severity,
            string trigger = "monitor", bool publish = true)
        {
            // The producer records the announced revision on the record in the same transaction.
            if (publish) await ExecuteAsync($"UPDATE play_discrepancies SET \"PublishedRevision\"={revision} WHERE \"Id\"='{id}'");
            return (await AppendAsync(new VesselEventDraft(
                transition switch { "opened" => "discrepancy.opened", "unverifiable" => "discrepancy.unverifiable", _ => "discrepancy.resolved" },
                Guid.CreateVersion7(), $"discrepancy:{id}:{revision}:{transition}", severity, Clock.GetUtcNow(), Clock.GetUtcNow(),
                new(account, "hyperliquid", "BTC", play, DiscrepancyId: id), new(trigger),
                JsonSerializer.SerializeToElement(new { kind = "protection.missing", state = "open" }))))[0];
        }

        public async Task<VesselEvent> MonitorAsync(Guid account, long revision, string state, string previous) =>
            (await AppendAsync(new VesselEventDraft("monitor.state-changed", Guid.CreateVersion7(), $"monitor:{account}:{revision}:{state}",
                previous == "live" ? "warning" : "info", Clock.GetUtcNow(), Clock.GetUtcNow(), new(account), new("monitor"),
                JsonSerializer.SerializeToElement(new { previous, state, error = (string?)null }))))[0];

        public async Task<VesselEvent> CredentialAsync(Guid account, string revision) =>
            (await AppendAsync(new VesselEventDraft("credential.expiring", Guid.CreateVersion7(), $"credential:{account}:{revision}:expiring",
                "warning", Clock.GetUtcNow(), Clock.GetUtcNow(), new(account, "lighter"), new("monitor"),
                JsonSerializer.SerializeToElement(new { expiresAtUtc = Clock.GetUtcNow().AddDays(5) }))))[0];

        public async Task<IReadOnlyList<VesselEvent>> AppendAsync(params VesselEventDraft[] drafts)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var sink = scope.ServiceProvider.GetRequiredService<IVesselEventSink>();
                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async () =>
                {
                    foreach (var draft in drafts) await sink.AppendAsync(draft, default);
                    return true;
                }, default);
            }
            await using var reader = Database.Context(Owner);
            var page = await EventReads.ReadAsync(reader, 0, 200, default);
            return drafts.Select(d => page.Events.Single(e => e.DedupKey == d.DedupKey)).ToList();
        }

        public async Task<DispatchOutcome> DispatchAsync(VesselEvent message)
        {
            await using var scope = provider.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<EventDispatcher>()
                .DispatchAsync(subscription, Owner, message.Id, DispatchTrigger.Live, default)).Outcome;
        }

        public async Task<IReadOnlyList<NotificationDto>> AllAsync() => (await PageAsync("all")).Items;

        public Task<NotificationPage> PageAsync(string tab) => Service(s => s.PageAsync(new(tab, PageSize: 100), default));
        public Task<NotificationHeadDto> HeadAsync() => Service(s => s.HeadAsync(default));
        public Task<NotificationFeed> FeedAsync(string cursor) => Service(s => s.FeedAsync(cursor, 100, default));
        public Task<NotificationClaim> ClaimAsync() => Service(s => s.ClaimAsync(default));
        public Task<NotificationDto> ReadAsync(Guid id) => Service(s => s.ReadAsync(id, default));
        public Task<int> ReadAllAsync(string cursor) => Service(s => s.ReadAllAsync(cursor, default));
        public Task SavePreferencesAsync(NotificationPreferences preferences, long expected) => Service(s =>
            s.SavePreferencesAsync(new(preferences.Enabled, preferences.MinimumSeverity, preferences.Categories, expected), default));

        public Task<TimeSpan?> StartTestAsync() => Store((store, work) =>
            work.ExecuteAsync(() => store.StartTestAsync(Clock.GetUtcNow(), TimeSpan.FromSeconds(10), default), default));

        public Task<int> RetainAsync() => Store((store, work) =>
            work.ExecuteAsync(() => store.RetainAsync(Clock.GetUtcNow().AddDays(-90), default), default));

        public async Task SetEnabledAsync(Guid accountId, bool enabled)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VesselDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<Vessel.Application.Workspace.IWorkspaceStore>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(() => store.WithAccountLockAsync(accountId, async account =>
            {
                account.UpdateSettings(account.Name, account.PortfolioId, enabled);
                await store.SaveAsync(default);
                await store.AccountVisibilityChangedAsync(accountId, enabled, default);
                return true;
            }, default), default);
        }

        public async Task DeleteAccountAsync(Guid accountId)
        {
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<Vessel.Application.Workspace.IWorkspaceStore>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(() =>
                store.WithAccountLockAsync(accountId, async account =>
                {
                    await store.DeleteAccountAsync(account, default);
                    return true;
                }, default), default);
        }

        private async Task<T> Service<T>(Func<NotificationService, Task<T>> action)
        {
            await using var scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<NotificationService>());
        }

        private async Task Service(Func<NotificationService, Task> action)
        {
            await using var scope = provider.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<NotificationService>());
        }

        private async Task<T> Store<T>(Func<INotificationStore, IUnitOfWork, Task<T>> action)
        {
            await using var scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<INotificationStore>(), scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    internal sealed class HubClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
