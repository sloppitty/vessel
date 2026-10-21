using Vessel.Domain.Common;

namespace Vessel.Domain.Risk;

/// <summary>
/// The owner's soft risk limits (Phase 2). Every limit is blank until the owner sets it, and a limit is flagged, never
/// enforced. The open-risk limit is a percentage of the scope's balance.
/// </summary>
public sealed class RiskSettings : IOwned
{
    public const decimal MaxPercent = 100m;
    public const int MaxPlaces = 4;

    public Guid OwnerId { get; private set; }
    public decimal? OpenRiskLimitPercent { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    private RiskSettings() { }

    public RiskSettings(Guid ownerId, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner ID must be nonempty.");
        OwnerId = ownerId;
        UpdatedAtUtc = now;
    }

    /// <summary>Null clears the limit; otherwise above 0 and at most 100 percent, with at most four decimals.</summary>
    public void SetOpenRiskLimit(decimal? percent, DateTimeOffset now)
    {
        if (percent is { } value && (value <= 0 || value > MaxPercent || decimal.Round(value, MaxPlaces) != value))
            throw new DomainValidationException($"The open-risk limit is above 0% and at most {MaxPercent}%, with at most {MaxPlaces} decimals.");
        OpenRiskLimitPercent = percent;
        UpdatedAtUtc = now;
    }
}
