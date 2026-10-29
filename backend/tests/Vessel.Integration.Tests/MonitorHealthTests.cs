using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class MonitorHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_system_discovery_or_heartbeat_storage_cannot_make_container_health_green(bool recordFailure)
    {
        var file = Path.Combine(Path.GetTempPath(), "vessel-b2-health-" + Guid.NewGuid().ToString("N"));
        var state = new FailingState();
        using var scheduler = new MonitorScheduler(new NoScopes(), new MutableOwnerDirectory(), TimeProvider.System,
            new() { HeartbeatFile = file }, new(), NullLogger<MonitorScheduler>.Instance, new Candidates(recordFailure), state,
            new() { Deployment = "monitor", Modules = ["monitor"] });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.TickAsync(default));
            Assert.Equal(recordFailure ? 1 : 0, state.Records);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_tick_reads_a_bounded_number_of_candidate_pages_and_resumes_from_the_last_examined_row()
    {
        var file = Path.Combine(Path.GetTempPath(), "vessel-b2-pages-" + Guid.NewGuid().ToString("N"));
        var owner = Guid.NewGuid();
        var directory = new MutableOwnerDirectory();
        directory.Set(owner, JournalOwnerStatus.Suspended);
        var pages = new EndlessCandidates(owner);
        using var scheduler = new MonitorScheduler(new NoScopes(), directory, TimeProvider.System,
            new() { HeartbeatFile = file }, new(), NullLogger<MonitorScheduler>.Instance, pages, new RecordingState(),
            new() { Deployment = "monitor", Modules = ["monitor"] });
        try
        {
            await scheduler.TickAsync(default);
            Assert.Equal(MonitorScheduler.CandidatePagesPerTick, pages.Calls.Count);
            await scheduler.TickAsync(default);
            Assert.Equal(2 * MonitorScheduler.CandidatePagesPerTick, pages.Calls.Count);
            // Every page, including the first of the next tick, starts right after the last row the previous one returned.
            Assert.Null(pages.Calls[0]);
            for (var index = 1; index < pages.Calls.Count; index++) Assert.Equal(pages.Returned[index - 1], pages.Calls[index]);
        }
        finally { File.Delete(file); }
    }

    private sealed class EndlessCandidates(Guid owner) : ISystemMonitorStore
    {
        public List<DueMonitorKey?> Calls { get; } = [];
        public List<DueMonitorKey> Returned { get; } = [];
        public Task<IReadOnlyList<DueMonitorCandidate>> DueCandidatesAsync(DueMonitorKey? after, DateTimeOffset dueBy, int limit, CancellationToken ct)
        {
            Calls.Add(after);
            var page = Enumerable.Range(0, limit).Select(_ => new DueMonitorCandidate(owner, Guid.CreateVersion7(), "hyperliquid", dueBy)).ToArray();
            Returned.Add(page[^1].Key);
            return Task.FromResult<IReadOnlyList<DueMonitorCandidate>>(page);
        }
    }
    private sealed class RecordingState : IServerStateStore
    {
        public Task RecordHeartbeatAsync(string deployment, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoScopes : IOwnerScopeFactory
    {
        public IOwnerScope Create(Guid ownerId) => throw new NotSupportedException();
    }
    private sealed class Candidates(bool productive) : ISystemMonitorStore
    {
        public Task<IReadOnlyList<DueMonitorCandidate>> DueCandidatesAsync(DueMonitorKey? after, DateTimeOffset dueBy, int limit, CancellationToken ct) =>
            productive ? Task.FromResult<IReadOnlyList<DueMonitorCandidate>>([]) : throw new InvalidOperationException("Synthetic query failure");
    }
    private sealed class FailingState : IServerStateStore
    {
        public int Records;
        public Task RecordHeartbeatAsync(string deployment, DateTimeOffset now, CancellationToken ct)
        {
            Records++; throw new InvalidOperationException("Synthetic heartbeat failure");
        }
        public Task<ServerMonitorHeartbeat?> HeartbeatAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTimeOffset?> VenueRetryAtAsync(string venueId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> VenueRetriesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeferVenueAsync(string venueId, DateTimeOffset retryAtUtc, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
}
