using Vessel.Domain.Common;

namespace Vessel.Domain.MarketSources;

/// <summary>
/// A venue market source the owner turned off, such as one Hyperliquid builder DEX. A row exists only while it is off, so a
/// new source starts on. It hides the source from the instrument selector and nothing else: Plays, imports, positions and
/// the monitor keep working on its markets.
/// </summary>
public sealed class DisabledMarketSource : IOwned
{
    public const int MaxVenueLength = 64, MaxSourceLength = 16;

    public Guid OwnerId { get; private set; }
    public string VenueId { get; private set; } = "";
    public string SourceId { get; private set; } = "";
    public DateTimeOffset DisabledAtUtc { get; private set; }
    private DisabledMarketSource() { }

    public DisabledMarketSource(Guid ownerId, string venueId, string sourceId, DateTimeOffset disabledAtUtc)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner ID must be nonempty.");
        OwnerId = ownerId;
        VenueId = venueId;
        SourceId = sourceId;
        DisabledAtUtc = disabledAtUtc;
    }
}
