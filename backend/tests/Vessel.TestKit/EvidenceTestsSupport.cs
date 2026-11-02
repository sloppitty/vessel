using Vessel.Domain.Plays;

namespace Vessel.Tests;

public partial class EvidenceTestsSupport
{
    internal readonly Guid owner = Guid.NewGuid();
    internal readonly MemoryEvidenceStore store;
    internal readonly Play play;
    internal readonly string root = Path.Combine(Path.GetTempPath(), "vessel-evidence-" + Guid.NewGuid().ToString("N"));

    public EvidenceTestsSupport()
    {
        store = new MemoryEvidenceStore(owner);
        play = EvidenceFixtures.PlayFor(owner);
        store.Plays.Add(play);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

public partial class EvidenceApiTestsSupport
{
    internal readonly Guid owner = Guid.NewGuid();
    internal readonly MemoryEvidenceStore store;
    internal readonly Play play;
    internal readonly string root = Path.Combine(Path.GetTempPath(), "vessel-evidence-api-" + Guid.NewGuid().ToString("N"));

    public EvidenceApiTestsSupport()
    {
        store = new MemoryEvidenceStore(owner);
        play = EvidenceFixtures.PlayFor(owner);
        store.Plays.Add(play);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
