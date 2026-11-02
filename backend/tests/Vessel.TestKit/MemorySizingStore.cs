using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Sizing;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

internal sealed class MemorySizingStore : ISizingStore
{
    public SizingSettings? Settings { get; set; }
    public List<ClosedPlayFacts> Closed { get; } = [];
    public int Saves { get; private set; }
    public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult(Settings);
    public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => Task.FromResult(Closed.ToList());
    public int Unresolved { get; set; }
    public Task<int> UnresolvedPlaysAsync(CancellationToken ct) => Task.FromResult(Unresolved);
    public List<ImportedOrder> Orders { get; } = [];
    public Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct) =>
        Task.FromResult(Orders.Where(o => orders.Contains(OrderKey.Of(o))).ToList());
    public void Add(SizingSettings settings) => Settings = settings;
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
}
