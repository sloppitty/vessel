using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Persistence;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Tests;

/// <summary>
/// An unreadable stored payload is a poison event: it settles terminal <c>failed</c> with zero handler attempts in
/// the dispatcher's own transaction, so catch-up moves past it. A missing keyed handler is a host configuration
/// error caught at start.
/// </summary>
public sealed partial class EventSubscriptionTests
{
    [VesselEventPostgresFact]
    public async Task An_unreadable_payload_settles_failed_without_a_handler_attempt_and_catch_up_moves_past_it()
    {
        await using var runtime = await Runtime.CreateAsync();
        var poison = await runtime.RegisterNewAsync("poison");
        var nulled = await runtime.AppendAsync(Draft("nulled"));
        var malformed = await runtime.AppendAsync(Draft("malformed"));
        var fine = await runtime.AppendAsync(Draft("fine"));
        // jsonb keeps the payload valid JSON; it can still not be an event.
        await runtime.ExecuteAsync($"UPDATE events SET payload='null'::jsonb WHERE event_id='{nulled.Id}'");
        await runtime.ExecuteAsync($"UPDATE events SET payload=jsonb_set(payload,'{{version}}','\"one\"') WHERE event_id='{malformed.Id}'");

        // Live delivery: the receipt insert and its terminal settlement commit together.
        var live = await runtime.DispatchAsync(poison, nulled.Id);
        Assert.Equal((DispatchOutcome.Failed, true), (live.Outcome, live.DeadLetter));
        Assert.Equal(("failed", 0), await runtime.ReceiptAsync(poison, nulled.Id));
        Assert.Equal(EventDispatcher.UnreadableEvent, await runtime.ScalarAsync<string>(
            $"SELECT error FROM event_receipts WHERE event_id='{nulled.Id}'"));
        Assert.Equal(DispatchOutcome.AlreadySettled, (await runtime.DispatchAsync(poison, nulled.Id)).Outcome);

        // Catch-up discovers the other poison event, settles it the same way and advances past both.
        runtime.Clock.Advance(TimeSpan.FromMinutes(1));
        var pass = await runtime.CatchUpAsync(poison);
        Assert.Equal((2, 3L), (pass.Discovered, pass.LowWaterMark));
        Assert.Equal(("failed", 0), await runtime.ReceiptAsync(poison, malformed.Id));
        Assert.Equal(("delivered", 1), await runtime.ReceiptAsync(poison, fine.Id));
        Assert.Equal(0, runtime.Subscriber.Calls(nulled.Id));
        Assert.Equal(0, runtime.Subscriber.Calls(malformed.Id));
        Assert.Equal(0, (await runtime.CatchUpAsync(poison)).Discovered);

        // Admin retry still returns failed receipts to pending; an unchanged payload fails again without an attempt.
        Assert.Equal(2, await runtime.RetryFailedAsync(poison, 1));
        Assert.Equal(("pending", 0), await runtime.ReceiptAsync(poison, nulled.Id));
        var retried = await runtime.CatchUpAsync(poison);
        Assert.Equal((2, 3L), (retried.Retried, retried.LowWaterMark));
        Assert.Equal(("failed", 0), await runtime.ReceiptAsync(poison, nulled.Id));
        Assert.Equal(0, runtime.Subscriber.Calls(nulled.Id));
    }

    [VesselEventPostgresFact]
    public async Task Cancellation_before_settlement_leaves_an_unreadable_event_unsettled()
    {
        await using var runtime = await Runtime.CreateAsync();
        var poison = await runtime.RegisterNewAsync("poison");
        var nulled = await runtime.AppendAsync(Draft("nulled"));
        await runtime.ExecuteAsync($"UPDATE events SET payload='null'::jsonb WHERE event_id='{nulled.Id}'");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using (var scope = runtime.Scope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<EventDispatcher>()
                .DispatchAsync(poison, runtime.Owner, nulled.Id, DispatchTrigger.Live, cancelled.Token));
        Assert.Null(await runtime.ReceiptAsync(poison, nulled.Id));
        Assert.Equal(DispatchOutcome.Failed, (await runtime.DispatchAsync(poison, nulled.Id)).Outcome);
    }

    [Fact]
    public void Handler_check_names_every_subscription_without_a_keyed_handler()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IVesselEventSubscriber, GatedSubscriber>("present");
        using var provider = services.BuildServiceProvider();
        EventSubscriptionRegistry.EnsureHandlers(provider, [new("present", "tests", ["#"])]);
        var error = Assert.Throws<InvalidOperationException>(() => EventSubscriptionRegistry.EnsureHandlers(provider,
            [new("present", "tests", ["#"]), new("absent", "tests", ["#"])]));
        Assert.Contains("absent", error.Message);
        Assert.DoesNotContain("present,", error.Message);
    }

    [Fact]
    public async Task A_host_running_a_subscription_without_its_handler_fails_at_start()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Vessel"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
            ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString(),
            ["Vessel:Messaging:RabbitMqUri"] = "amqp://unused:unused@127.0.0.1:1"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IJournalOwnerContext>(new CoreOwner(Guid.NewGuid()));
        builder.Services.AddVesselPersistence(builder.Configuration);
        builder.AddVesselMessaging("unhandled", ["unhandled"], registry: new VesselModuleRegistry([new UnhandledModule()]));
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("No event handler is registered for subscription(s) unhandled", error.Message);
    }

    /// <summary>Declares a subscription but forgets to register its handler.</summary>
    private sealed class UnhandledModule : IVesselModule
    {
        public string Name => "unhandled";
        public IReadOnlyList<CommandQueue> Commands { get; } = [];
        public IReadOnlyList<EventSubscription> Subscriptions { get; } = [new("unhandled", "unhandled", ["#"])];
        public void AddRunningServices(IServiceCollection services, IConfiguration configuration) { }
    }
}
