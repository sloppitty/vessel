using Vessel.Domain.Common;

namespace Vessel.Domain.Workspace;

public sealed class Portfolio : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string TradingMode { get; private set; } = Common.TradingMode.Real;
    public string Name { get; private set; } = null!;
    public string Icon { get; private set; } = PortfolioIcons.Default;
    private Portfolio() { }
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200) throw new DomainValidationException("Name exceeds storage limit.");
        Name = name.Trim();
    }
    public void ChangeIcon(string icon)
    {
        if (!PortfolioIcons.IsKnown(icon)) throw new DomainValidationException("Unknown portfolio icon.");
        Icon = icon;
    }
    public Portfolio(Guid id, Guid ownerId, string name, string icon = PortfolioIcons.Default,
        string tradingMode = Common.TradingMode.Real)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty) throw new ArgumentException("IDs must be nonempty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200) throw new DomainValidationException("Name exceeds storage limit.");
        if (!PortfolioIcons.IsKnown(icon)) throw new DomainValidationException("Unknown portfolio icon.");
        Id = id; OwnerId = ownerId; Name = name.Trim(); Icon = icon;
        TradingMode = Common.TradingMode.Validate(tradingMode);
    }
}

/// <summary>
/// The icons an owner can choose for a portfolio. The browser maps each key to a drawn icon;
/// keep this list in step with <c>frontend/src/features/workspace/portfolio-icons.ts</c>.
/// </summary>
public static class PortfolioIcons
{
    public const string Default = "folder";
    public const int MaxLength = 40;
    public static readonly IReadOnlyList<string> All =
    [
        "folder", "briefcase", "wallet", "piggy-bank", "landmark", "coins", "bitcoin", "gem",
        "trending-up", "chart-line", "chart-candlestick", "chart-column", "target", "compass", "rocket", "zap",
        "flame", "timer", "hourglass", "shield", "anchor", "mountain", "sprout", "crown",
    ];
    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);
    public static bool IsKnown(string? icon) => icon is not null && Known.Contains(icon);
}
