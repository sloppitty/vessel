using Vessel.Application.Evidence;

namespace Vessel.Tests;

internal sealed class MemoryObjectStore : IEvidenceObjectStore
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public bool FailDeletes { get; set; }
    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        if (!Objects.TryAdd(key, copy.ToArray())) throw new IOException("Key exists.");
    }
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (FailDeletes) throw new IOException("Storage unavailable.");
        Objects.Remove(key);
        return Task.CompletedTask;
    }
}
