using Vessel.Domain.Common;

namespace Vessel.Domain.Accounts;

public sealed class Account : IOwned
{
    private const string OrderHistorySection = "\nOrder history: ";
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string TradingMode { get; private set; } = Common.TradingMode.Real;
    public decimal? StartingCapitalUsd { get; private set; }
    public string VenueId { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public Guid? PortfolioId { get; private set; }
    public string? Address { get; private set; }
    public string? SourceId { get; private set; }
    public decimal? ManualAccountValueUsd { get; private set; }
    public DateTimeOffset? LastSyncedAtUtc { get; private set; }
    public string SyncStatus { get; private set; } = "not-synced";
    public string? LastSyncError { get; private set; }
    public string? HistoryNotice { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public long SettingsRevision { get; private set; } = 1;
    private Account() { }

    public void Configure(Guid? portfolioId, string? address, decimal? manualAccountValueUsd, string? sourceId = null)
    {
        if (portfolioId == Guid.Empty || manualAccountValueUsd < 0)
            throw new DomainValidationException("Invalid account configuration.");
        if (TradingMode == Common.TradingMode.Paper &&
            (portfolioId is null || address is not null || sourceId is not null || manualAccountValueUsd is not null))
            throw new DomainValidationException("Paper accounts require a paper portfolio and cannot use real identities or manual balances.");
        PortfolioId = portfolioId; Address = address?.ToLowerInvariant(); ManualAccountValueUsd = manualAccountValueUsd;
        SourceId = sourceId ?? Address;
        SyncStatus = TradingMode == Common.TradingMode.Paper ? "paper" : VenueId == ManualVenue.Id ? "manual" : "not-synced";
    }

    public void UpdateSettings(string name, Guid? portfolioId, bool isEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || portfolioId == Guid.Empty || TradingMode == Common.TradingMode.Paper && portfolioId is null)
            throw new DomainValidationException("Invalid account settings.");
        var nextRevision = checked(SettingsRevision + 1);
        Name = name.Trim(); PortfolioId = portfolioId; IsEnabled = isEnabled;
        SettingsRevision = nextRevision;
    }

    public void RecordSync(DateTimeOffset observedAtUtc, string historyNotice)
    {
        RequireReal();
        if (LastSyncedAtUtc is null || observedAtUtc > LastSyncedAtUtc)
            LastSyncedAtUtc = observedAtUtc;
        var orderSection = HistoryNotice?.IndexOf(OrderHistorySection, StringComparison.Ordinal) ?? -1;
        HistoryNotice = historyNotice + (orderSection < 0 ? "" : HistoryNotice![orderSection..]);
        SyncStatus = "synced"; LastSyncError = null;
    }

    /// <summary>Replaces only order coverage, keeping the latest public coverage and sync/settings state.</summary>
    public void RecordOrderHistoryNotice(string? notice)
    {
        RequireReal();
        var orderSection = HistoryNotice?.IndexOf(OrderHistorySection, StringComparison.Ordinal) ?? -1;
        var publicNotice = orderSection < 0 ? HistoryNotice : HistoryNotice![..orderSection];
        HistoryNotice = string.IsNullOrWhiteSpace(notice) ? publicNotice : publicNotice + OrderHistorySection + notice;
    }

    public void RecordSyncFailure()
    {
        RequireReal();
        SyncStatus = "error"; LastSyncError = "The venue refresh failed. Try again later.";
    }

    public Account(Guid id, Guid ownerId, string venueId, string name,
        string tradingMode = Common.TradingMode.Real, decimal? startingCapitalUsd = null)
    {
        if (id == Guid.Empty || ownerId == Guid.Empty)
            throw new ArgumentException("Account and owner IDs must be nonempty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(venueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (venueId.Length > 64 || name.Length > 200)
            throw new DomainValidationException("Account text exceeds storage limits.");
        Id = id;
        OwnerId = ownerId;
        TradingMode = Common.TradingMode.Validate(tradingMode);
        if (startingCapitalUsd < 0 || tradingMode == Common.TradingMode.Real && startingCapitalUsd is not null)
            throw new DomainValidationException("Starting capital is a nonnegative paper-account creation value only.");
        StartingCapitalUsd = tradingMode == Common.TradingMode.Paper ? startingCapitalUsd ?? 4000m : null;
        VenueId = venueId;
        Name = name.Trim();
        SyncStatus = tradingMode == Common.TradingMode.Paper ? "paper" : venueId == ManualVenue.Id ? "manual" : "not-synced";
    }

    private void RequireReal()
    {
        if (TradingMode != Common.TradingMode.Real)
            throw new DomainValidationException("Paper accounts cannot import real venue facts.");
    }
}
