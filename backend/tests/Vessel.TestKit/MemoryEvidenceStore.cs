using Vessel.Application.Evidence;
using Vessel.Domain.Evidence;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

internal sealed class MemoryEvidenceStore(Guid ownerId) : IEvidenceMetadataStore
{
    public List<Play> Plays { get; } = [];
    public List<PlayEvidence> Items { get; } = [];
    public Exception? AddFailure { get; set; }
    public int Saves { get; private set; }
    public Task<Play?> PlayAsync(Guid playId, CancellationToken ct) => Task.FromResult(Plays.SingleOrDefault(p => p.Id == playId && p.OwnerId == ownerId));
    public Task<List<PlayEvidence>> ListAsync(Guid playId, CancellationToken ct) =>
        Task.FromResult(Items.Where(e => e.PlayId == playId && e.OwnerId == ownerId).OrderBy(e => e.CreatedAtUtc).ToList());
    public Task<int> CountAsync(Guid playId, CancellationToken ct) => Task.FromResult(Items.Count(e => e.PlayId == playId && e.OwnerId == ownerId));
    public Task<PlayEvidence?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(e => e.Id == id && e.OwnerId == ownerId));
    public Task<bool> AddAsync(PlayEvidence evidence, int maxPerPlay, CancellationToken ct)
    {
        if (AddFailure is not null) return Task.FromException<bool>(AddFailure);
        if (Items.Count(e => e.PlayId == evidence.PlayId) >= maxPerPlay) return Task.FromResult(false);
        Items.Add(evidence);
        return Task.FromResult(true);
    }
    public Task RemoveAsync(PlayEvidence evidence, CancellationToken ct) { Items.Remove(evidence); return Task.CompletedTask; }
    public Task SaveAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
}
