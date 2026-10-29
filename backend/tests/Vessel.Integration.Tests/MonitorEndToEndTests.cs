using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Messaging;
using Vessel.Messaging.Modules;
using Vessel.Messaging.Modules.Monitor;
using Vessel.Messaging.Modules.Verification;
using Vessel.Worker;

namespace Vessel.Tests;

// Broker tests share one disposable RabbitMQ; each fixture uses its own topology prefix and envelope schemas.
[CollectionDefinition("Monitor transport", DisableParallelization = true)]
public sealed class MonitorTransportCollection;

[Collection("Monitor transport")]
public sealed class MonitorEndToEndTests
{
    [VesselEventRabbitFact]
    public async Task Hosted_scheduler_imports_partial_full_and_stop_fills_without_a_browser_and_restarts_without_duplicates()
    {
        await using var fixture = await MonitorFixture.StartAsync();
        // The host's initial scheduler tick, not an HTTP request, established the watch.
        Assert.Equal(1, fixture.Venue.AccountReads);
        Assert.Equal(1, fixture.Venue.OrderReads);
        await fixture.AssertStatusAsync(PlayStatus.Planned, PlayStatus.Open);
        Assert.DoesNotContain((await fixture.EventsAsync()).Events, e => e.Type == "execution.filled");
        await using (var db = fixture.Database.Context(fixture.Owner))
        {
            Assert.Equal(2, await db.Plays.CountAsync());
            Assert.Equal("pre-watch", (await db.Fills.SingleAsync()).SourceFillId);
            Assert.Equal(4, await db.OrderLinks.CountAsync(l => l.State == OrderLinkState.Linked));
        }

        await fixture.ScheduledReadAsync(1);
        await fixture.AssertStatusAsync(PlayStatus.Open, PlayStatus.Open);
        var partial = Assert.Single((await fixture.EventsAsync()).Events, e => e.Type == "execution.filled");
        AssertFill(partial, fixture.BtcPlay, "entry", "0.4", "0.4", "0.6", false, "entry-partial");

        await fixture.ScheduledReadAsync(2);
        await fixture.AssertStatusAsync(PlayStatus.Open, PlayStatus.Open);
        var full = (await fixture.EventsAsync()).Events.Single(e =>
            e.Type == "execution.filled" && e.Source.FillIds!.Contains("entry-rest"));
        AssertFill(full, fixture.BtcPlay, "entry", "0.6", "1", "0", true, "entry-rest");

        await fixture.ScheduledReadAsync(3);
        await fixture.AssertStatusAsync(PlayStatus.Closed, PlayStatus.Open);
        var page = await fixture.EventsAsync();
        var lifecycle = page.Events.Where(e => e.Type is "execution.filled" or "play.opened" or "play.closed").ToArray();
        Assert.Equal(5, lifecycle.Length);
        Assert.Single(lifecycle, e => e.Type == "play.opened");
        var closed = Assert.Single(lifecycle, e => e.Type == "play.closed");
        Assert.Equal(fixture.BtcPlay, closed.Subject.PlayId);
        Assert.Equal("1", closed.Data.GetProperty("enteredQuantity").GetString());
        Assert.Equal("1", closed.Data.GetProperty("exitedQuantity").GetString());
        Assert.Equal("-5", closed.Data.GetProperty("closedPnlUsd").GetString());
        Assert.Equal(ExecutionFacts.PnlGross, closed.Data.GetProperty("closedPnlBasis").GetString());
        var fee = Assert.Single(closed.Data.GetProperty("fees").EnumerateArray());
        Assert.Equal("USDC", fee.GetProperty("token").GetString());
        Assert.Equal("0.2", fee.GetProperty("amount").GetString());
        var stopped = lifecycle.Single(e => e.Type == "execution.filled" && e.Data.GetProperty("role").GetString() == "stop");
        AssertFill(stopped, fixture.BtcPlay, "stop", "1", "1", "0", true, "stop-fill");
        Assert.Equal("warning", stopped.Severity);
        Assert.All(lifecycle, e => Assert.Equal("monitor", e.Source.Trigger));
        Assert.DoesNotContain(page.Events, e => e.Source.FillIds?.Contains("pre-watch") == true);

        // Repeated fresh observations and a new actual worker host keep business IDs and receipts stable.
        await fixture.ScheduledReadAsync(3);
        Assert.Equal(page.Events.Select(e => e.Id), (await fixture.EventsAsync()).Events.Select(e => e.Id));
        await fixture.RestartWorkerWhenDueAsync();
        Assert.Equal(page.Events.Select(e => e.Id), (await fixture.EventsAsync()).Events.Select(e => e.Id));
        Assert.Equal(6, fixture.Venue.AccountReads);
        Assert.Equal(6, fixture.Venue.OrderReads);
        await using (var db = fixture.Database.Context(fixture.Owner))
        {
            Assert.Equal(4, await db.Fills.CountAsync());
            Assert.Equal(4, await db.Orders.CountAsync());
            Assert.Equal(4, await db.OrderLinks.CountAsync());
            Assert.Equal(2, await db.StatusChanges.CountAsync());
        }
        await fixture.WaitForReceiptsAsync(page.Events.Count);

        using var client = fixture.ApiClient();
        var execution = await client.GetFromJsonAsync<PlayExecutionDto>($"/api/plays/{fixture.BtcPlay}/execution");
        Assert.NotNull(execution);
        Assert.Equal("closed", execution.Status);
        Assert.Equal("1", execution.Totals.EnteredQuantity);
        Assert.Equal("1", execution.Totals.ExitedQuantity);
        Assert.Equal("0", execution.Totals.OpenQuantity);
        Assert.Equal("-5", execution.Totals.ClosedPnlUsd);
        Assert.Equal("0.2", Assert.Single(execution.Totals.Fees).Amount);
        await AssertCatchupAsync(client, page);
        // Read-only execution/event endpoints must not trigger an extra venue read.
        Assert.Equal(6, fixture.Venue.AccountReads);
    }

    [VesselEventRabbitFact]
    public async Task Http_check_runs_real_broker_handler_and_enforces_busy_failed_cooldown_and_owner_routes()
    {
        await using var fixture = await MonitorFixture.StartAsync();
        using var client = fixture.ApiClient();
        var route = $"/api/accounts/{fixture.AccountId}/monitor/run";
        var previous = await fixture.MonitorAsync();
        fixture.Clock.Set(previous.NextManualAllowedAtUtc!.Value);
        using (var gate = fixture.Venue.BlockNextRead())
        {
            using var response = await client.PostAsync(route, null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<MonitorRunAccepted>();
            Assert.NotNull(accepted);
            Assert.NotEqual(Guid.Empty, accepted.RequestId);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // No scheduler tick was fired. Only the real command's hand-off could wake the scheduler for this read;
            // the listener itself acknowledges once the pending request is validated and never polls the venue.
            var running = await fixture.MonitorAsync();
            Assert.Equal(accepted.RequestId, running.RequestId);
            Assert.Equal("running", running.ManualState);
            await AssertBusyAsync(client, route);
            gate.Release.TrySetResult();
            await fixture.WaitForRunAsync(previous.Generation + 1);
        }
        var success = await fixture.MonitorAsync();
        Assert.Equal("live", success.State);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), success.NextManualAllowedAtUtc);
        await AssertBusyAsync(client, route, 60);

        fixture.Clock.Set(success.NextManualAllowedAtUtc!.Value);
        using (var gate = fixture.Venue.BlockNextRead(fail: true))
        {
            using var response = await client.PostAsync(route, null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertBusyAsync(client, route);
            fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(20));
            gate.Release.TrySetResult();
            await fixture.WaitForRunAsync(success.Generation + 1);
        }
        var failed = await fixture.MonitorAsync();
        Assert.Equal("degraded", failed.State);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(success.LastSuccessAtUtc, failed.LastSuccessAtUtc);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), failed.NextManualAllowedAtUtc);
        await AssertBusyAsync(client, route, 60);
        fixture.Clock.Set(failed.NextManualAllowedAtUtc!.Value.AddSeconds(-1));
        await AssertBusyAsync(client, route, 1);
        fixture.Clock.Set(failed.NextManualAllowedAtUtc.Value);
        using (var recovered = await client.PostAsync(route, null))
            Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        await fixture.WaitForRunAsync(failed.Generation + 1);
        Assert.Equal("live", (await fixture.MonitorAsync()).State);
        Assert.Equal(4, fixture.Venue.AccountReads);
        Assert.Equal(3, fixture.Venue.OrderReads);

        var (foreignAccount, foreignPlay) = await fixture.SeedForeignAsync();
        foreach (var path in new[]
        {
            $"/api/accounts/{foreignAccount}/monitor", $"/api/plays/{foreignPlay}/execution"
        })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        foreach (var path in new[]
        {
            $"/api/accounts/{foreignAccount}/monitor/run", $"/api/plays/{foreignPlay}/execution/check"
        })
        {
            using var denied = await client.PostAsync(path, null);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        var summary = await client.GetFromJsonAsync<MonitorSummaryDto>("/api/monitor");
        Assert.NotNull(summary);
        Assert.Equal(fixture.AccountId, Assert.Single(summary.Accounts).AccountId);
        var events = await client.GetFromJsonAsync<VesselEventPage>("/api/events?after=0&limit=200");
        Assert.NotNull(events);
        Assert.All(events.Events, e => Assert.Equal(fixture.Owner, e.OwnerId));
        Assert.Equal(4, fixture.Venue.AccountReads);
        await fixture.WaitForReceiptsAsync(events.Events.Count);
    }

    // The browser guards (frontend/src/api/monitor.ts, plays.ts) validate this real HTTP JSON in
    // frontend/src/test/contracts/monitor-contract.test.ts. Regenerate with VESSEL_UPDATE_CONTRACT_FIXTURES=1.
    [VesselEventRabbitFact]
    public async Task Monitor_http_json_matches_the_browser_contract_fixture()
    {
        await using var fixture = await MonitorFixture.StartAsync();
        using var client = fixture.ApiClient();
        async Task<JsonElement> Read(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
        var previous = await fixture.MonitorAsync();
        fixture.Clock.Set(previous.NextManualAllowedAtUtc!.Value);
        JsonElement accepted, running, summary;
        // No worker may claim the run before the accepted (pending) response is captured.
        await fixture.StopWorkerAsync();
        using (var response = await client.PostAsync($"/api/accounts/{fixture.AccountId}/monitor/run", null))
            accepted = await Read(response);
        using (var gate = fixture.Venue.BlockNextRead())
        {
            await fixture.StartWorkerAsync();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using (var response = await client.GetAsync($"/api/accounts/{fixture.AccountId}/monitor")) running = await Read(response);
            using (var response = await client.GetAsync("/api/monitor")) summary = await Read(response);
            gate.Release.TrySetResult();
            await fixture.WaitForRunAsync(previous.Generation + 1);
        }
        JsonElement idle, execution, events;
        using (var response = await client.GetAsync($"/api/accounts/{fixture.AccountId}/monitor")) idle = await Read(response);
        using (var response = await client.GetAsync($"/api/plays/{fixture.BtcPlay}/execution")) execution = await Read(response);
        using (var response = await client.GetAsync("/api/events?limit=200")) events = await Read(response);
        Assert.Equal("pending", accepted.GetProperty("monitor").GetProperty("manualState").GetString());
        Assert.Equal("owner", running.GetProperty("runTrigger").GetString());
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("runTrigger").ValueKind);
        var completed = Assert.Single(events.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "monitor.run-completed");
        // No HTTP route in this flow produces a discrepancy; serialize one with the API's own options.
        var json = fixture.ApiJsonOptions();
        var discrepancy = JsonSerializer.SerializeToElement(new MonitorDiscrepancyDto(Guid.NewGuid(), fixture.AccountId, fixture.BtcPlay,
            "BTC", "position.excess", "warning", "open", fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), null, null, null,
            JsonSerializer.SerializeToElement(new { venueQuantity = "0.100000000000000001", playQuantity = "0.1" }),
            LastObservedAtUtc: fixture.Clock.GetUtcNow()), json);
        // Outcomes that need lifetimes to pass: a publicly unverifiable candidate and an unverifiable exit watch.
        var unverifiable = JsonSerializer.SerializeToElement(new MonitorDiscrepancyDto(Guid.NewGuid(), fixture.AccountId, fixture.BtcPlay,
            "BTC", "position.missing", "warning", "candidate", fixture.Clock.GetUtcNow(), null, null, null, null,
            JsonSerializer.SerializeToElement(new { venueQuantity = "0", playQuantity = "0.1" }), "unverifiable", fixture.Clock.GetUtcNow(),
            fixture.Clock.GetUtcNow().AddMinutes(-30)), json);
        var watch = JsonSerializer.SerializeToElement(new MonitorWatchDto(Guid.NewGuid(), fixture.BtcPlay, "exit-order", "stop", "unverifiable",
            fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow()), json);
        // Stop coverage needs an Open Play: a known covered outcome whose stop rests away from its planned price, and a
        // stale unsupported one.
        var coverage = JsonSerializer.SerializeToElement(new MonitorCoverageDto(fixture.BtcPlay, "BTC", "known", "known", true, "stop-orders",
            fixture.Clock.GetUtcNow(), [new("entry-1", "stop-1", "resting-elsewhere", "stop-order", fixture.Clock.GetUtcNow(), "94.5", "95")]), json);
        var coverageUnsupported = JsonSerializer.SerializeToElement(new MonitorCoverageDto(fixture.BtcPlay, "BTC", "stale", "unsupported", null,
            "current-book-not-read", fixture.Clock.GetUtcNow().AddMinutes(-30), [new("entry-1", "stop-1", "not-read", null, null, null, "95")]), json);
        var document = new JsonObject
        {
            ["runAccepted"] = JsonNode.Parse(accepted.GetRawText()),
            ["accountMonitorRunning"] = JsonNode.Parse(running.GetRawText()),
            ["accountMonitorIdle"] = JsonNode.Parse(idle.GetRawText()),
            ["summary"] = JsonNode.Parse(summary.GetRawText()),
            ["playExecution"] = JsonNode.Parse(execution.GetRawText()),
            ["runCompletedEvent"] = JsonNode.Parse(completed.GetRawText()),
            ["eventPageNextCursor"] = JsonNode.Parse(events.GetProperty("nextCursor").GetRawText()),
            ["discrepancy"] = JsonNode.Parse(discrepancy.GetRawText()),
            ["discrepancyUnverifiable"] = JsonNode.Parse(unverifiable.GetRawText()),
            ["watchUnverifiable"] = JsonNode.Parse(watch.GetRawText()),
            ["coverage"] = JsonNode.Parse(coverage.GetRawText()),
            ["coverageUnsupported"] = JsonNode.Parse(coverageUnsupported.GetRawText()),
        };
        var actual = ContractFixtures.Normalize(document.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) + "\n";
        var path = ContractFixtures.Path("monitor-api.json");
        if (Environment.GetEnvironmentVariable("VESSEL_UPDATE_CONTRACT_FIXTURES") == "1") await File.WriteAllTextAsync(path, actual);
        Assert.Equal(await File.ReadAllTextAsync(path), actual);
    }

    private static void AssertFill(VesselEvent message, Guid play, string role, string quantity,
        string cumulative, string remaining, bool complete, string fillId)
    {
        Assert.Equal(play, message.Subject.PlayId);
        Assert.Equal(role, message.Data.GetProperty("role").GetString());
        Assert.Equal(quantity, message.Data.GetProperty("filledQuantity").GetString());
        Assert.Equal(cumulative, message.Data.GetProperty("cumulativeQuantity").GetString());
        Assert.Equal(remaining, message.Data.GetProperty("remainingQuantity").GetString());
        Assert.Equal(complete, message.Data.GetProperty("complete").GetBoolean());
        Assert.Equal(fillId, Assert.Single(message.Source.FillIds!));
    }

    private static async Task AssertBusyAsync(HttpClient client, string route, int? seconds = null)
    {
        using var response = await client.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retry = response.Headers.RetryAfter?.Delta;
        Assert.NotNull(retry);
        Assert.True(retry > TimeSpan.Zero);
        if (seconds is { } expected) Assert.Equal(TimeSpan.FromSeconds(expected), retry);
    }

    private static async Task AssertCatchupAsync(HttpClient client, VesselEventPage page)
    {
        var sequence = page.Events[page.Events.Count / 2].Sequence;
        Assert.True(VesselEventCursor.TryParse(page.NextCursor, out var current));
        var after = VesselEventCursor.Format(current.Epoch, long.Parse(sequence, CultureInfo.InvariantCulture));
        var expected = page.Events.Where(e => long.Parse(e.Sequence, CultureInfo.InvariantCulture) >
            long.Parse(sequence, CultureInfo.InvariantCulture)).ToArray();
        Assert.NotEmpty(expected);
        var catchup = await client.GetFromJsonAsync<VesselEventPage>($"/api/events?after={after}&limit=200");
        Assert.NotNull(catchup);
        Assert.False(catchup.ResetRequired);
        Assert.Equal(expected.Select(e => e.Id), catchup.Events.Select(e => e.Id));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync($"/api/events/stream?after={after}",
            HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        async Task<(string Type, JsonElement Data)> Frame()
        {
            string? type = null;
            string? data = null;
            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
                if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                if (line.Length == 0 && type is not null && data is not null)
                {
                    using var document = JsonDocument.Parse(data);
                    return (type, document.RootElement.Clone());
                }
            }
            throw new InvalidOperationException("The event stream ended before its catch-up page.");
        }
        var ready = await Frame();
        Assert.Equal("status", ready.Type);
        Assert.Equal("ready", ready.Data.GetProperty("state").GetString());
        Assert.Equal(after, ready.Data.GetProperty("nextCursor").GetString());
        foreach (var message in expected)
        {
            var frame = await Frame();
            Assert.Equal("event", frame.Type);
            Assert.Equal(message.Id, frame.Data.GetProperty("id").GetGuid());
            Assert.Equal(message.Sequence, frame.Data.GetProperty("sequence").GetString());
            Assert.Equal(message.Type, frame.Data.GetProperty("type").GetString());
        }
        await cancellation.CancelAsync();
    }

    private sealed class MonitorFixture(CoreDatabase database) : IAsyncDisposable
    {
        public CoreDatabase Database { get; } = database;
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid AccountId { get; private set; }
        public Guid BtcPlay { get; private set; }
        private Guid EthPlay { get; set; }
        public TestClock Clock { get; } = new();
        public SyntheticVenue Venue { get; private set; } = null!;
        private readonly string id = Guid.NewGuid().ToString("N")[..12];
        private string Schema(string deployment) => $"e2e_{deployment}_{id}";
        private static readonly string[] Deployments =
            [MessagingHosting.ApiDeployment, MonitorModule.ModuleName, VerificationModule.ModuleName];
        private readonly string heartbeat = Path.Combine(Path.GetTempPath(), "vessel-e2e-" + Guid.NewGuid().ToString("N"));
        private Dictionary<string, string?> config = null!;
        private IHost? worker;
        private IHost? verification;
        private MonitorApiFactory? api;

        public static async Task<MonitorFixture> StartAsync()
        {
            var fixture = new MonitorFixture(await CoreDatabase.CreateAsync());
            try
            {
                fixture.Venue = new(fixture.Clock);
                fixture.config = new()
                {
                    ["ConnectionStrings:Vessel"] = fixture.Database.ConnectionString,
                    ["Vessel:Auth:OwnerId"] = fixture.Owner.ToString(),
                    ["Vessel:Identity:Origins:0"] = TestAuthentication.Origin,
                    ["Vessel:Messaging:RabbitMqUri"] = Environment.GetEnvironmentVariable("Vessel_TEST_EVENT_RABBIT"),
                    ["Vessel:Messaging:Prefix"] = "e2e" + fixture.id,
                    ["Vessel:Messaging:Verification"] = "true",
                    ["Vessel:Monitor:HeartbeatFile"] = fixture.heartbeat
                };
                foreach (var deployment in Deployments)
                    fixture.config[$"Vessel:Messaging:StorageSchemas:{deployment}"] = fixture.Schema(deployment);
                await fixture.SeedAsync();
                await MessagingProvisioning.ProvisionAsync(new ConfigurationBuilder().AddInMemoryCollection(fixture.config).Build(), default);
                // A verification subscription proves every committed event is delivered once to a subscriber.
                fixture.verification = fixture.BuildVerification();
                await fixture.verification.StartAsync();
                fixture.worker = fixture.BuildWorker();
                fixture.api = new(fixture.config, fixture.Clock, fixture.Venue, fixture.Owner);
                await fixture.worker.StartAsync();
                await fixture.WaitForRunAsync(1);
                Assert.Equal("live", (await fixture.MonitorAsync()).State);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private IHost BuildWorker()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(config);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            WorkerHost.Configure(builder);
            ReplaceInputs(builder.Services, Clock, Venue, Owner);
            return builder.Build();
        }

        private IHost BuildVerification()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(config);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IJournalOwnerContext>(new CoreOwner(Owner));
            builder.Services.AddSingleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(Owner));
            builder.Services.AddVesselPersistence(builder.Configuration);
            builder.AddVesselMessaging(VerificationModule.ModuleName, [VerificationModule.ModuleName]);
            return builder.Build();
        }

        private async Task SeedAsync()
        {
            await using var db = Database.Context(Owner);
            var account = Account(Owner);
            var btc = Planned(account, "BTC");
            var eth = Planned(account, "ETH");
            eth.MarkOpen("Pre-watch position", Clock.GetUtcNow().AddDays(-1));
            // The pre-watch orders were retained by an earlier import, as links require; the venue reads keep them current.
            var old = Clock.GetUtcNow().AddDays(-1);
            var ethEntry = TestOrders.For(eth, "eth-entry");
            ethEntry.LimitPrice = 200; ethEntry.PlacedAtUtc = ethEntry.StatusAtUtc = ethEntry.ObservedAtUtc = old;
            var ethStop = TestOrders.For(eth, "eth-stop", "sell", "open");
            ethStop.OrderType = "Stop Market"; ethStop.LimitPrice = 190; ethStop.TriggerPrice = 190; ethStop.ReduceOnly = true;
            ethStop.PlacedAtUtc = ethStop.StatusAtUtc = ethStop.ObservedAtUtc = old;
            db.AddRange(account, btc, eth, ethEntry, ethStop,
                new PlayOrderLink(eth, ethEntry, OrderLinkRole.Entry, "entry-1", null,
                    OrderLinkState.Linked, OrderLinkSource.Owner, old),
                new PlayOrderLink(eth, ethStop, OrderLinkRole.Stop, "entry-1", "stop-1",
                    OrderLinkState.Linked, OrderLinkSource.Owner, old));
            await db.SaveChangesAsync();
            AccountId = account.Id; BtcPlay = btc.Id; EthPlay = eth.Id;
        }

        public async Task<(Guid Account, Guid Play)> SeedForeignAsync()
        {
            var owner = Guid.NewGuid();
            await using var db = Database.Context(owner);
            var account = Account(owner);
            var play = Planned(account, "BTC");
            db.AddRange(account, play);
            await db.SaveChangesAsync();
            return (account.Id, play.Id);
        }

        private static Account Account(Guid owner)
        {
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic monitor account");
            account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
            return account;
        }
        private Play Planned(Account account, string instrument)
        {
            var at = Clock.GetUtcNow().AddDays(-2);
            var plan = instrument == "BTC" ? TestPlays.Plan() : TestPlays.Plan("200", "190");
            if (instrument == "ETH")
                plan = plan with { Entries = [plan.Entries[0] with { Targets = [new("target-1", "price", "210", "100")] }] };
            var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", instrument),
                InstrumentSource.Venue, PlayDocuments.Serialize(plan), at);
            play.MarkPlanned(at);
            return play;
        }

        public JsonSerializerOptions ApiJsonOptions() =>
            api!.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        public HttpClient ApiClient()
        {
            var client = api!.CreateClient();
            TestAuthentication.SignIn(client.DefaultRequestHeaders, Owner);
            return client;
        }
        public async Task<MonitorAccount> MonitorAsync()
        {
            await using var db = Database.Context(Owner);
            return await db.Set<MonitorAccount>().AsNoTracking().SingleAsync(m => m.AccountId == AccountId);
        }
        public async Task WaitForRunAsync(long generation) => await Eventually(async () =>
        {
            await using var db = Database.Context(Owner);
            return await db.Set<MonitorAccount>().AnyAsync(m => m.AccountId == AccountId &&
                m.Generation >= generation && m.ManualState == "idle");
        }, $"Worker run {generation} did not complete.");

        public async Task ScheduledReadAsync(int phase)
        {
            var previous = await MonitorAsync();
            Venue.Phase = phase;
            Clock.Set(previous.NextRunAtUtc!.Value.AddMilliseconds(1));
            Clock.TickScheduler();
            await WaitForRunAsync(previous.Generation + 1);
            var current = await MonitorAsync();
            Assert.Equal(Clock.GetUtcNow(), current.LastSuccessAtUtc);
            Assert.Equal("live", current.State);
        }
        public async Task StopWorkerAsync()
        {
            await worker!.StopAsync();
            worker.Dispose();
            worker = null;
        }
        public async Task StartWorkerAsync()
        {
            worker = BuildWorker();
            await worker.StartAsync();
        }
        public async Task RestartWorkerWhenDueAsync()
        {
            var previous = await MonitorAsync();
            await worker!.StopAsync();
            worker.Dispose();
            Clock.Set(previous.NextRunAtUtc!.Value.AddMilliseconds(1));
            worker = BuildWorker();
            await worker.StartAsync();
            await WaitForRunAsync(previous.Generation + 1);
            await AssertStatusAsync(PlayStatus.Closed, PlayStatus.Open);
        }
        public async Task AssertStatusAsync(PlayStatus btc, PlayStatus eth)
        {
            await using var db = Database.Context(Owner);
            Assert.Equal(btc, (await db.Plays.SingleAsync(p => p.Id == BtcPlay)).Status);
            Assert.Equal(eth, (await db.Plays.SingleAsync(p => p.Id == EthPlay)).Status);
        }
        public async Task<VesselEventPage> EventsAsync()
        {
            await using var db = Database.Context(Owner);
            return await EventReads.ReadAsync(db, 0, 200, default);
        }
        public Task WaitForReceiptsAsync(int count) => Eventually(async () =>
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM event_receipts WHERE subscription='verification' AND state='delivered' AND attempts=1
                """, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == count;
        }, "Worker did not acknowledge every business EventId exactly once.");

        private async Task DeleteTopologyAsync()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            var settings = MessagingHosting.Settings(configuration);
            var topology = new MessagingTopology(settings, VesselModuleRegistry.From(settings));
            var factory = new RabbitMQ.Client.ConnectionFactory { Uri = settings.BrokerUri() };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in new[] { topology.CommandQueue(MonitorModule.ModuleName), topology.SubscriptionQueue(VerificationModule.SubscriptionName) })
            {
                await channel.QueueDeleteAsync(queue, false, false);
                await channel.QueueDeleteAsync(MessagingTopology.DeadLetterQueue(queue), false, false);
                await channel.ExchangeDeleteAsync(MessagingTopology.DeadLetterExchange(queue), false);
            }
            await channel.ExchangeDeleteAsync(topology.EventExchange, false);
        }

        public async ValueTask DisposeAsync()
        {
            if (api is not null) await api.DisposeAsync();
            foreach (var host in new[] { worker, verification })
            {
                if (host is null) continue;
                await host.StopAsync();
                host.Dispose();
            }
            await DeleteTopologyAsync();
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            foreach (var deployment in Deployments)
            {
                await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema(deployment)} CASCADE", connection);
                await drop.ExecuteNonQueryAsync();
            }
            File.Delete(heartbeat);
            await Database.DisposeAsync();
        }
    }

    private sealed class MonitorApiFactory(Dictionary<string, string?> config, TestClock clock, SyntheticVenue venue, Guid owner)
        : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(config));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                ReplaceInputs(services, clock, venue, owner);
                TestAuthentication.Add(services);
            });
        }
    }

    /// <summary>The synthetic clock and venue, and one active owner instead of the identity users this database lacks.</summary>
    private static void ReplaceInputs(IServiceCollection services, TestClock clock, SyntheticVenue venue, Guid owner)
    {
        services.RemoveAll<IJournalOwnerDirectory>();
        services.AddSingleton<IJournalOwnerDirectory>(new ConfiguredOwnerDirectory(owner));
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.RemoveAll<IVenueRegistry>();
        services.AddSingleton<IVenueRegistry>(TestVenues.With(venue));
    }

    private static async Task Eventually(Func<Task<bool>> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition()) return;
            try { await Task.Delay(50, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }
        Assert.Fail(message);
    }

    private sealed class SyntheticVenue(TestClock clock) : IPerpetualVenueReader, IVenueOrderReader
    {
        private int accountReads;
        private int orderReads;
        private ReadGate? nextGate;
        public int AccountReads => Volatile.Read(ref accountReads);
        public int OrderReads => Volatile.Read(ref orderReads);
        public int Phase { get; set; }
        public string VenueId => "hyperliquid";
        private readonly DateTimeOffset old = clock.GetUtcNow().AddDays(-1);
        private readonly DateTimeOffset placed = clock.GetUtcNow().AddMinutes(-5);
        private readonly VenueInstrument[] instruments = [new("BTC", 5, 50, "USDC"), new("ETH", 5, 50, "USDC")];
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>(instruments);
        public ReadGate BlockNextRead(bool fail = false)
        {
            var gate = new ReadGate(fail);
            Assert.Null(Interlocked.Exchange(ref nextGate, gate));
            return gate;
        }
        public async Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            Interlocked.Increment(ref accountReads);
            if (Interlocked.Exchange(ref nextGate, null) is { } gate)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(ct);
                if (gate.Fail) throw new VenueReadException("Synthetic read failure");
            }
            var now = clock.GetUtcNow();
            var fills = new List<VenueFill> { Fill("pre-watch", "ETH", "eth-entry", "buy", 200, 1, 0.02m, old) };
            if (Phase >= 1) fills.Add(Fill("entry-partial", "BTC", "btc-entry", "buy", 100, 0.4m, 0.04m, placed.AddMinutes(6)));
            if (Phase >= 2) fills.Add(Fill("entry-rest", "BTC", "btc-entry", "buy", 100, 0.6m, 0.06m, placed.AddMinutes(7)));
            if (Phase >= 3) fills.Add(Fill("stop-fill", "BTC", "btc-stop", "sell", 95, 1, 0.1m, placed.AddMinutes(8), -5));
            var positions = new List<VenuePosition> { new("ETH", 1, 200, 0, 50, 3) };
            if (Phase is 1 or 2) positions.Add(new("BTC", Phase == 1 ? 0.4m : 1m, 100, 0, 25, 3));
            return new(new(now, "synthetic", 1000, 900, 75, positions), instruments, fills, "Complete synthetic history",
                PositionsComplete: true, FillHistoryComplete: true, FillHistoryTruncated: false, OldestRawFillAtUtc: old);
        }
        private static VenueFill Fill(string id, string contract, string order, string side, decimal price,
            decimal quantity, decimal fee, DateTimeOffset at, decimal pnl = 0) =>
            new(id, contract, side, side == "buy" ? "Open Long" : "Close Long", price, quantity, fee,
                "USDC", pnl, at, order, "synthetic-tx-" + id, "{}", PositionEffect: side == "buy" ? ExecutionFacts.Open : ExecutionFacts.Close);
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct)
        {
            Interlocked.Increment(ref orderReads);
            var now = clock.GetUtcNow();
            VenueOrder Order(string id, string contract, bool stop, decimal price, decimal remaining, bool filled, DateTimeOffset at) =>
                new(id, contract, stop ? "sell" : "buy", stop ? "Stop Market" : "Limit", price,
                    stop ? price : null, stop, false, 1, remaining, at,
                    filled ? VenueOrderStatus.Filled : VenueOrderStatus.Open, filled ? "filled" : "open", now);
            return Task.FromResult(new VenueOrderReadResult([
                Order("btc-entry", "BTC", false, 100, Phase == 0 ? 1 : Phase == 1 ? 0.6m : 0, Phase >= 2, placed),
                Order("btc-stop", "BTC", true, 95, Phase >= 3 ? 0 : 1, Phase >= 3, placed),
                Order("eth-entry", "ETH", false, 200, 0, true, old),
                Order("eth-stop", "ETH", true, 190, 1, false, old)
            ], now, "Complete synthetic orders", OpenOrdersComplete: true));
        }
    }

    private sealed class ReadGate(bool fail) : IDisposable
    {
        public bool Fail { get; } = fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Release.TrySetResult();
    }

    // Keep the real hosted scheduler loop, but release its timer explicitly. No wall-clock ten-minute waits.
    // Runs finish within the five-minute lease; renewal timers are deliberately not advanced by these tests.
    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.AddHours(1).Ticks / 10 * 10;
        private readonly List<TestTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset now) => Interlocked.Exchange(ref ticks, now.Ticks / 10 * 10);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, period);
            lock (timers) timers.Add(timer);
            return timer;
        }
        public void TickScheduler()
        {
            TestTimer[] snapshot;
            lock (timers) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.TickScheduler();
        }
        private sealed class TestTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            private int disposed;
            public void TickScheduler()
            {
                if (Volatile.Read(ref disposed) == 0 && period == MonitorOptions.SchedulerTick) callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { period = newPeriod; return Volatile.Read(ref disposed) == 0; }
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
