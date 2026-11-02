using Vessel.Application.Events;

namespace Vessel.Tests.Identity;

public partial class StreamRevalidationTestsSupport
{

    internal sealed class EmptyReader : IVesselEventReader
    {
        private readonly string cursor = Guid.NewGuid().ToString("N") + ".0";
        public Task<VesselEventPage> ReadAsync(string? after, int limit, CancellationToken ct) => Task.FromResult(new VesselEventPage([], cursor, false));
    }
}
