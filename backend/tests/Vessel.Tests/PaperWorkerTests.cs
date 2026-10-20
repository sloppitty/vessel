using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vessel.Application.MarketData;
using Vessel.Application.Paper;
using Vessel.Messaging;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class PaperWorkerTests
{
    [Fact]
    public void Paper_is_explicit_standalone_and_has_no_broker_or_transport_host()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Worker:Modules:0"] = "paper",
            ["Vessel:Auth:Token"] = "test-paper-token-only-not-a-live-secret",
            ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString()
        });
        WorkerHost.Configure(builder);
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(IPaperTradingService));
        Assert.Contains(builder.Services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(PaperScheduler));
        Assert.DoesNotContain(builder.Services, x => x.ServiceType == typeof(MessagingHost));
        Assert.Equal("/tmp/vessel-paper-heartbeat", PaperWorkerOptions.From(builder.Configuration).HeartbeatFile);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("notifications")]
    [InlineData("unknown")]
    public void Paper_cannot_silently_mix_with_an_existing_or_unknown_module(string other)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Worker:Modules:0"] = "paper", ["Vessel:Worker:Modules:1"] = other
        });
        Assert.Throws<InvalidOperationException>(() => WorkerHost.Configure(builder));
    }

    [Theory]
    [InlineData(0, 1440, 200)]
    [InlineData(30, 5000, 200)]
    [InlineData(30, 1440, 0)]
    public void Paper_worker_public_reads_and_scheduling_are_bounded(int tick, int candles, int plays) =>
        Assert.Throws<InvalidOperationException>(() => new PaperWorkerOptions
        { SchedulerTickSeconds = tick, MaxCandlesPerRead = candles, MaxPlaysPerTick = plays }.Validate());

    [Fact]
    public void Minute_close_is_canonical_and_nonminute_source_intervals_are_not_reinterpreted()
    {
        var at = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        var raw = new VenueCandle(at.ToUnixTimeMilliseconds(), at.AddMinutes(1).ToUnixTimeMilliseconds() - 1,
            "100", "101", "99", "100", "1", 1);
        var parsed = PaperScheduler.ParseCandle(raw);
        Assert.Equal(at.AddMinutes(1), parsed.CloseAtUtc);
        Assert.Throws<FormatException>(() => PaperScheduler.ParseCandle(raw with { CloseTime = at.AddHours(1).ToUnixTimeMilliseconds() - 1 }));
    }
}
