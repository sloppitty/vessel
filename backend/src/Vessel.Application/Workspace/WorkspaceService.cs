using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Workspace;

public sealed class WorkspaceService(IWorkspaceStore store, IJournalOwnerContext owner, IVenueRegistry venues, IUnitOfWork unitOfWork)
{
    public static string ValidateTradingMode(string? tradingMode)
    {
        var mode = tradingMode ?? TradingMode.Real;
        return TradingMode.IsKnown(mode) ? mode : throw new ProblemException(400, "Trading mode must be real or paper.");
    }

    private static void RequireMode(Account account, string mode)
    {
        if (account.TradingMode != mode) throw new ProblemException(404, "Account not found.");
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ProblemException(400, "Name is required and must be at most 200 characters.");
    }

    private static void ValidateIcon(string? icon)
    {
        if (icon is not null && !PortfolioIcons.IsKnown(icon))
            throw new ProblemException(400, "Choose one of the available portfolio icons.");
    }

    public async Task<PortfolioDto> CreatePortfolioAsync(CreatePortfolioRequest request, CancellationToken ct)
    {
        ValidateName(request.Name);
        ValidateIcon(request.Icon);
        var mode = ValidateTradingMode(request.TradingMode);
        if (request.AccountIds is not null) ValidateAccountIds(request.AccountIds);
        ValidateExpected(request.AccountIds, request.Expected);
        return await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(async () =>
        {
            var accounts = await LockAccounts(request.AccountIds ?? [], ct);
            foreach (var account in accounts) RequireMode(account, mode);
            CheckExpected(accounts, request.Expected);
            var portfolio = new Portfolio(Guid.NewGuid(), owner.OwnerId, request.Name, request.Icon ?? PortfolioIcons.Default, mode);
            await store.AddPortfolioAsync(portfolio, ct);
            MoveAccounts(accounts, portfolio.Id);
            await store.SaveAsync(ct);
            return await PortfolioAsync(portfolio.Id, ct, mode);
        }, ct), ct);
    }

    private static void ValidateAccountIds(IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count is 0 or > 200 || ids.Distinct().Count() != ids.Count)
            throw new ProblemException(400, "Choose between 1 and 200 distinct accounts.");
    }

    private static void ValidateExpected(IReadOnlyList<Guid>? ids, IReadOnlyList<AccountSettingsExpectation>? expected)
    {
        if (expected is null) return;
        if (ids is null || expected.Count == 0 || expected.Count != ids.Count ||
            expected.Any(e => e is null || e.SettingsRevision < 1 || !ids.Contains(e.AccountId)) ||
            expected.Select(e => e.AccountId).Distinct().Count() != expected.Count)
            throw new ProblemException(400, "Provide exactly one positive settings revision for each selected account.");
    }

    private static void CheckExpected(IReadOnlyList<Account> accounts, IReadOnlyList<AccountSettingsExpectation>? expected)
    {
        if (expected is null) return;
        var revisions = expected.ToDictionary(e => e.AccountId, e => e.SettingsRevision);
        if (accounts.Any(a => a.SettingsRevision != revisions[a.Id]))
            throw new ProblemException(409, "Account settings changed elsewhere. No accounts were moved. Reload accounts and review the membership before retrying.");
    }

    private async Task<List<Account>> LockAccounts(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var accounts = new List<Account>();
        // Take every row lock before changing settings; the management transaction holds them until commit.
        foreach (var id in ids.Order())
            accounts.Add(await store.WithAccountLockAsync(id, account => Task.FromResult(account), ct));
        return accounts;
    }

    private static void MoveAccounts(IEnumerable<Account> accounts, Guid? portfolioId)
    {
        foreach (var account in accounts)
            if (account.PortfolioId != portfolioId)
                account.UpdateSettings(account.Name, portfolioId, account.IsEnabled);
    }

    public async Task<IReadOnlyList<AccountDto>> MoveAccountsAsync(MoveAccountsRequest request, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        ValidateAccountIds(request.AccountIds);
        ValidateExpected(request.AccountIds, request.Expected);
        return await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(async () =>
        {
            await ValidatePortfolio(request.PortfolioId, ct, mode);
            var accounts = await LockAccounts(request.AccountIds!, ct);
            foreach (var account in accounts) RequireMode(account, mode);
            CheckExpected(accounts, request.Expected);
            MoveAccounts(accounts, request.PortfolioId);
            await store.SaveAsync(ct);
            var result = new List<AccountDto>();
            foreach (var account in accounts)
                result.Add(await AccountAsync(account.Id, ct, mode));
            return result;
        }, ct), ct);
    }

    public async Task<AccountDto> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct)
    {
        ValidateName(request.Name);
        var mode = ValidateTradingMode(request.TradingMode);
        var descriptor = venues.Descriptor(request.VenueId)
            ?? throw new ProblemException(400, $"Choose {OrList(venues.Descriptors.Select(d => d.Id))}.");

        if (request.PortfolioId == Guid.Empty) throw new ProblemException(404, "Portfolio not found.");
        decimal? value = null, startingCapital = null;
        string? sourceId = null, address = null;
        if (mode == TradingMode.Paper)
        {
            if (!descriptor.Capabilities.PaperExecution)
                throw new ProblemException(400, "This venue does not support paper execution.");
            if (request.Address is not null || request.SourceId is not null || request.ManualAccountValueUsd is not null)
                throw new ProblemException(400, "Paper accounts cannot use source identities or manual balances.");
            if (request.StartingCapitalUsd is null) startingCapital = 4000m;
            else if (ExactDecimal.TryParseOwnerAmount(request.StartingCapitalUsd, out var parsed)) startingCapital = parsed;
            else throw new ProblemException(400, "Starting capital must be an exact nonnegative decimal string.");
        }
        else if (request.StartingCapitalUsd is not null)
            throw new ProblemException(400, "Starting capital is available only when creating a paper account.");
        else if (descriptor.Id == VenueDescriptor.ManualId)
        {
            if (request.Address is not null || request.SourceId is not null)
                throw new ProblemException(400, "Manual accounts do not use a source identity.");
            if (request.ManualAccountValueUsd is { } text)
            {
                if (!ExactDecimal.TryParseOwnerAmount(text, out var parsed))
                    throw new ProblemException(400, "Manual account value must be an exact nonnegative decimal string.");
                value = parsed;
            }
        }
        else
        {
            if (request.ManualAccountValueUsd is not null) throw new ProblemException(400, "Venue balances cannot be supplied manually.");
            if (descriptor.Source == VenueSources.EvmAddress)
            {
                sourceId = request.SourceId ?? request.Address;
                if (!AccountSource.IsEvmAddress(sourceId) ||
                    request.Address is not null && !string.Equals(request.Address, sourceId, StringComparison.OrdinalIgnoreCase))
                    throw new ProblemException(400, "A matching 42-character public hexadecimal address is required.");
                address = sourceId = sourceId!.ToLowerInvariant();
            }
            else if (descriptor.Source == VenueSources.AccountIndex)
            {
                if (request.Address is not null || !AccountSource.IsAccountIndex(request.SourceId))
                    throw new ProblemException(400, "Source ID must be a canonical nonnegative int64 account index string, without an address.");
                sourceId = request.SourceId;
            }
            else throw new ProblemException(400, "The venue source identity is unsupported.");
        }
        if (mode == TradingMode.Paper && request.PortfolioId is null)
            throw new ProblemException(400, "Paper accounts require a paper portfolio.");
        var account = new Account(Guid.NewGuid(), owner.OwnerId, request.VenueId, request.Name, mode, startingCapital);
        account.Configure(request.PortfolioId, address, value, sourceId);
        await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(async () =>
        {
            await ValidatePortfolio(request.PortfolioId, ct, mode);
            if (account.SourceId is { } source && await store.SourceExistsAsync(account.VenueId, source, ct))
                throw new ProblemException(409, "An account for this venue and source already exists. Manage or re-enable that account.");
            await store.AddAccountAsync(account, ct);
            return true;
        }, ct), ct);
        return await AccountAsync(account.Id, ct, mode);
    }

    private async Task ValidatePortfolio(Guid? id, CancellationToken ct, string mode = TradingMode.Real)
    {
        if (mode == TradingMode.Paper && id is null)
            throw new ProblemException(400, "Paper accounts require a paper portfolio.");
        if (id.HasValue && !(await store.PortfoliosAsync(ct, mode)).Any(p => p.Id == id))
            throw new ProblemException(404, "Portfolio not found.");
    }

    public async Task<PortfolioDto> UpdatePortfolioAsync(Guid id, UpdatePortfolioRequest request, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        if (request.TradingMode is not null) throw new ProblemException(400, "Trading mode is immutable.");
        ValidateName(request.Name);
        ValidateIcon(request.Icon);
        await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(async () =>
        {
            var portfolio = (await store.PortfoliosAsync(ct, mode)).SingleOrDefault(p => p.Id == id)
                ?? throw new ProblemException(404, "Portfolio not found.");
            portfolio.Rename(request.Name);
            if (request.Icon is not null) portfolio.ChangeIcon(request.Icon);
            await store.SaveAsync(ct);
            return true;
        }, ct), ct);
        return await PortfolioAsync(id, ct, mode);
    }

    public async Task DeletePortfolioAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(async () =>
        {
            await PortfolioAsync(id, ct, mode);
            if (mode == TradingMode.Paper && (await store.AccountsAsync(ct, mode)).Any(a => a.PortfolioId == id))
                throw new ProblemException(409, "Move all accounts to another paper portfolio before deleting this portfolio.");
            await store.DeletePortfolioAsync(id, ct);
            return true;
        }, ct), ct);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid id, UpdateAccountRequest request, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        if (request.StartingCapitalUsd is not null || request.TradingMode is not null)
            throw new ProblemException(400, "Trading mode and paper starting capital are creation-only.");
        ValidateName(request.Name);
        return await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(() => store.WithAccountLockAsync(id, async account =>
        {
            RequireMode(account, mode);
            if (request.ExpectedRevision != account.SettingsRevision)
                throw new ProblemException(409, "Account settings changed. Reload the current settings before saving.");
            await ValidatePortfolio(request.PortfolioId, ct, mode);
            var wasEnabled = account.IsEnabled;
            // Phase 2, item 6: the opt-in reads venue facts, so it is real-only and needs a venue the monitor checks.
            if (request.KeepExecutionHistory == true && !account.KeepExecutionHistory)
            {
                if (account.TradingMode == TradingMode.Paper) throw new ProblemException(409, "Paper accounts have no venue history to keep.");
                if (!KeepsHistory(account)) throw new ProblemException(400, "This account's venue history cannot be kept.");
            }
            account.UpdateSettings(request.Name, request.PortfolioId, request.IsEnabled, request.KeepExecutionHistory);
            await store.SaveAsync(ct);
            if (wasEnabled != account.IsEnabled) await store.AccountVisibilityChangedAsync(id, account.IsEnabled, ct);
            return await AccountAsync(id, ct, mode);
        }, ct), ct), ct);
    }

    public async Task DeleteAccountAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        await unitOfWork.ExecuteAsync(() => store.WithManagementLockAsync(() => store.WithAccountLockAsync(id, async account =>
        {
            RequireMode(account, mode);
            await store.DeleteAccountAsync(account, ct);
            return true;
        }, ct), ct), ct);
    }

    public async Task<IReadOnlyList<AccountDto>> AccountsAsync(CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        var accounts = await store.AccountsAsync(ct, mode);
        var snapshots = mode == TradingMode.Real
            ? (await store.SnapshotsAsync(ct)).ToDictionary(s => s.AccountId) : new Dictionary<Guid, AccountSnapshot>();
        var paper = await PaperBalances(accounts, ct);
        return accounts.Select(a => ToDto(a, snapshots.GetValueOrDefault(a.Id), paper.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<IReadOnlyList<PortfolioDto>> PortfoliosAsync(CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        var accounts = await store.AccountsAsync(ct, mode);
        return MapPortfolios(await store.PortfoliosAsync(ct, mode), accounts,
            mode == TradingMode.Real ? (await store.SnapshotsAsync(ct)).ToDictionary(s => s.AccountId) : new Dictionary<Guid, AccountSnapshot>(),
            await PaperBalances(accounts, ct));
    }

    private Task<IReadOnlyDictionary<Guid, PaperAccountBalance>> PaperBalances(IReadOnlyList<Account> accounts, CancellationToken ct) =>
        accounts.Any(a => a.TradingMode == TradingMode.Paper)
            ? store.PaperBalancesAsync(accounts.Where(a => a.TradingMode == TradingMode.Paper).Select(a => a.Id).ToList(), ct)
            : Task.FromResult<IReadOnlyDictionary<Guid, PaperAccountBalance>>(new Dictionary<Guid, PaperAccountBalance>());

    private List<PortfolioDto> MapPortfolios(IReadOnlyList<Portfolio> portfolios, IReadOnlyList<Account> accounts,
        IReadOnlyDictionary<Guid, AccountSnapshot> snapshots, IReadOnlyDictionary<Guid, PaperAccountBalance> paper)
    {
        var dtos = accounts.Select(a => ToDto(a, snapshots.GetValueOrDefault(a.Id), paper.GetValueOrDefault(a.Id))).ToList();
        return portfolios.Select(p =>
        {
            var members = dtos.Where(a => a.PortfolioId == p.Id && a.TradingMode == p.TradingMode).ToList();
            var enabled = members.Where(a => a.IsEnabled).ToList();
            var values = enabled.Where(a => a.AccountValueUsd is not null).ToList();
            var balances = enabled.Where(a => a.BalanceUsd is not null).ToList();
            var available = enabled.Where(a => a.AvailableBalanceUsd is not null).ToList();
            return new PortfolioDto(p.Id, p.Name, members.Count, Sum(values.Select(a => a.AccountValueUsd)),
                Coverage(values.Count, enabled.Count), Sum(balances.Select(a => a.BalanceUsd)),
                Coverage(balances.Count, enabled.Count), p.Icon, Sum(available.Select(a => a.AvailableBalanceUsd)),
                available.Count, Coverage(available.Count, enabled.Count), enabled.Count, p.TradingMode);
        }).ToList();
    }

    private static string? Sum(IEnumerable<string?> values)
    {
        var known = values.OfType<string>().ToList();
        return known.Count == 0 ? null : known.Aggregate(ExactAmount.From(0), (total, value) => total + ExactAmount.Parse(value)).ToString();
    }

    public async Task<OverviewDto> OverviewAsync(CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var mode = ValidateTradingMode(tradingMode);
        var accounts = await store.AccountsAsync(ct, mode);
        var snapshots = mode == TradingMode.Real
            ? (await store.SnapshotsAsync(ct)).ToDictionary(s => s.AccountId) : new Dictionary<Guid, AccountSnapshot>();
        var paper = await PaperBalances(accounts, ct);
        var dtos = accounts.Select(a => ToDto(a, snapshots.GetValueOrDefault(a.Id), paper.GetValueOrDefault(a.Id))).ToList();
        var portfolios = MapPortfolios(await store.PortfoliosAsync(ct, mode), accounts, snapshots, paper);
        var enabled = dtos.Where(a => a.IsEnabled).ToList();
        var known = enabled.Where(a => a.AccountValueUsd is not null).ToList();
        var wallets = enabled.Where(a => a.TotalStablecoinNominalUsd is not null).ToList();
        var balances = enabled.Where(a => a.BalanceUsd is not null).ToList();
        var available = enabled.Where(a => a.AvailableBalanceUsd is not null).ToList();
        return new(portfolios, dtos, new(portfolios.Count, dtos.Count, Sum(known.Select(a => a.AccountValueUsd)),
            known.Count, dtos.Sum(a => a.PositionCount), mode == TradingMode.Real ? await store.FillCountAsync(ct) : 0,
            Sum(wallets.Select(a => a.AvailableStablecoinNominalUsd)), wallets.Count,
            Sum(balances.Select(a => a.BalanceUsd)), balances.Count, Coverage(balances.Count, enabled.Count),
            Sum(available.Select(a => a.AvailableBalanceUsd)), available.Count, Coverage(available.Count, enabled.Count), enabled.Count),
            mode == TradingMode.Real ? (await store.FillsAsync(null, 100, ct)).Select(ToDto).ToList() : [],
            mode == TradingMode.Paper
                ? "Paper accounts only. Nominal virtual USD, not real funds. Cash includes fixed starting capital and realized simulated P&L; available margin deducts open margin and pending reservations. Equity is unknown without marks for open positions. Unresolved gaps and ambiguity withhold available margin. No real wallet or imported executions."
                : "Enabled real accounts across all real portfolios and unassigned only. Total balance sums known perps/manual values and supported stablecoin wallet totals; unified and portfolio-margin accounts use the wallet alone. Available balance uses wallet total minus held in unified/portfolio-margin modes, otherwise reported available margin first, then withdrawable funds. Manual value, collateral and equity do not imply availability. Coverage counts accounts with a known amount, not full venue valuation. Nominal USD only: no FX, depeg, lending, EVM or risk adjustment. Availability is not guaranteed free trading margin or withdrawal. Imported fills are recent, not complete lifetime history.");
    }

    public async Task<SnapshotDto?> SnapshotAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var account = await RequireAccount(id, ValidateTradingMode(tradingMode), ct);
        if (!account.IsEnabled || account.TradingMode == TradingMode.Paper) return null;
        var s = await store.SnapshotAsync(id, ct);
        return s is null ? null : new(s.ObservedAtUtc, s.ValueScope, ExactDecimal.Format(s.AccountValueUsd),
            ExactDecimal.Format(s.WithdrawableUsd), ExactDecimal.Format(s.MarginUsedUsd),
            s.Positions.OrderBy(p => p.ContractId).Select(p => new PositionDto(p.ContractId, ExactDecimal.Format(p.SignedQuantity),
                ExactDecimal.Format(p.EntryPrice), ExactDecimal.Format(p.UnrealizedPnlUsd), ExactDecimal.Format(p.MarginUsedUsd), p.Leverage,
                ExactDecimal.Format(p.LiquidationPrice), p.MarginMode)).ToList(),
            WalletDto(s), ExactDecimal.Format(s.AvailableMarginUsd));
    }

    public async Task<PortfolioDto> PortfolioAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real) =>
        (await PortfoliosAsync(ct, tradingMode)).SingleOrDefault(p => p.Id == id) ??
        throw new ProblemException(404, "Portfolio not found.");

    public async Task<AccountDto> AccountAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var account = await RequireAccount(id, ValidateTradingMode(tradingMode), ct);
        if (account.TradingMode == TradingMode.Paper)
            return ToDto(account, null, (await store.PaperBalancesAsync([id], ct)).GetValueOrDefault(id));
        return ToDto(account, await store.SnapshotAsync(id, ct));
    }

    public const string FillConflictsNotice = "The venue later reported these fills with different values. Vessel keeps the " +
        "first copy it stored and never overwrites it; only the names of the fields that differed are recorded.";

    public const string HistoryCoverageNotice = "Each import records what it asked the venue for, the span it proves continuous " +
        "and any gap it may have skipped. A gap closes only when a later read proves that span covered.";

    /// <summary>A real venue account whose venue the monitor checks (reads and orders) can keep its history.</summary>
    private bool KeepsHistory(Account account) => account.TradingMode == TradingMode.Real && account.SourceId is not null &&
        venues.Descriptor(account.VenueId) is { Capabilities.Sync: true, Capabilities.Orders: true };

    /// <summary>
    /// Phase 2, item 6: the opt-in and the coverage ledger, newest first, at most 50 of each list. A disabled account
    /// hides its imports, so it shows none.
    /// </summary>
    public async Task<HistoryCoverageDto> HistoryCoverageAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var account = await RequireAccount(id, ValidateTradingMode(tradingMode), ct);
        if (account.TradingMode == TradingMode.Paper) throw new ProblemException(409, "Paper accounts have no venue history.");
        if (!account.IsEnabled) return new(account.KeepExecutionHistory, KeepsHistory(account), 0, [], [], 0, [], HistoryCoverageNotice)
            { BackfillAvailable = false };
        var facts = await store.HistoryCoverageAsync(id, 50, ct);
        static HistoryGapDto Gap(Vessel.Domain.Monitoring.AccountHistoryGap g) => new(g.Id, g.FromUtc, g.ToUtc, g.OpenedAtUtc, g.ClosedAtUtc,
            g.BeyondVenueReach);
        return new(account.KeepExecutionHistory, KeepsHistory(account), facts.OpenGapCount, facts.OpenGaps.Select(Gap).ToList(),
            facts.ClosedGaps.Select(Gap).ToList(), facts.RecordCount, facts.Recent.Select(r => new FillCoverageDto(r.Source,
                r.FirstObservedAtUtc, r.LastObservedAtUtc, r.ImportCount, r.RequestedFromUtc, r.RequestedToUtc, r.Truncated, r.Complete,
                r.CoveredFromUtc, r.CoveredToUtc, r.GapFromUtc, r.GapToUtc)).ToList(), HistoryCoverageNotice)
        {
            BackfillAvailable = KeepsHistory(account) && venues.FillHistory(account.VenueId) is not null,
            Backfills = facts.Backfills.Select(HistoryBackfillDto.Of).ToList()
        };
    }

    /// <summary>Phase 2, item 14c: retained fills reported again with different venue facts, newest first, at most 50.</summary>
    public async Task<FillConflictsDto> FillConflictsAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var account = await RequireAccount(id, ValidateTradingMode(tradingMode), ct);
        if (account.TradingMode == TradingMode.Paper) throw new ProblemException(409, "Paper accounts have simulated executions, not imported fills.");
        // Disabled accounts hide their imported activity, so their conflicts too (Sol round 1 on 14c).
        if (!account.IsEnabled) return new(0, [], FillConflictsNotice);
        var (total, recent) = await store.FillConflictsAsync(id, 50, ct);
        return new(total, recent.Select(c => new FillConflictDto(c.ContractId, c.SourceFillId, c.Fields.Split(','), c.FirstSeenAtUtc,
            c.LastSeenAtUtc, c.Reports)).ToList(), FillConflictsNotice);
    }

    public async Task<IReadOnlyList<FillDto>> FillsAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        var account = await RequireAccount(id, ValidateTradingMode(tradingMode), ct);
        if (account.TradingMode == TradingMode.Paper) throw new ProblemException(409, "Paper accounts have simulated executions, not imported fills.");
        if (!account.IsEnabled) return [];
        return (await store.FillsAsync(id, 100, ct)).Select(ToDto).ToList();
    }

    public async Task<AccountInstrumentsDto> InstrumentsAsync(Guid id, CancellationToken ct, string? tradingMode = TradingMode.Real)
    {
        ct.ThrowIfCancellationRequested();
        var mode = ValidateTradingMode(tradingMode);
        var account = await store.MarketDataAccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        if (account.TradingMode != mode) throw new ProblemException(404, "Account not found.");
        if (!account.IsEnabled)
            throw new ProblemException(409, "Enable the account before reading its instrument catalogue.");
        if (account.VenueId == VenueDescriptor.ManualId)
            return new(account.VenueId, "perpetuals", InstrumentCatalogScopes.Manual, [],
                "Manual catalogue: enter a perpetual contract manually. No venue metadata is available.");
        if (venues.Reader(account.VenueId) is not { } reader)
            throw new ProblemException(502, "The venue instrument read failed. Try again later.");

        IReadOnlyList<VenueInstrument> instruments;
        try
        {
            instruments = await reader.ReadInstrumentsAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is VenueReadException or HttpRequestException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ProblemException(502, "The venue instrument read failed. Try again later.");
        }
        return new(account.VenueId, "perpetuals", InstrumentCatalogScopes.Venue, instruments,
            "Venue perpetual contract metadata only. No orders, balances or execution refresh.");
    }

    /// <summary>"a", "a or b", "a, b or c".</summary>
    private static string OrList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} or {list[^1]}";
    }

    private async Task<Account> RequireAccount(Guid id, string mode, CancellationToken ct)
    {
        var account = await store.AccountAsync(id, ct) ?? throw new ProblemException(404, "Account not found.");
        RequireMode(account, mode);
        return account;
    }
    private AccountDto ToDto(Account a, AccountSnapshot? s, PaperAccountBalance? paper = null)
    {
        if (a.TradingMode == TradingMode.Paper)
            return new(a.Id, a.PortfolioId, a.Name, a.VenueId, null, paper?.EquityUsd, null,
                "paper", null, a.IsEnabled ? paper?.OpenPositionCount ?? 0 : 0,
                "Simulated execution; nominal virtual USD, zero fees and slippage. Open equity is unknown without fresh marks.",
                a.IsEnabled, a.SettingsRevision, BalanceUsd: paper?.CashUsd,
                AvailableBalanceUsd: a.IsEnabled ? paper?.AvailableMarginUsd : null,
                AvailableBalanceSource: "paper", TradingMode: a.TradingMode, StartingCapitalUsd: ExactDecimal.Format(a.StartingCapitalUsd));
        return new(a.Id, a.PortfolioId, a.Name, a.VenueId, a.Address,
        ExactDecimal.Format(a.VenueId == VenueDescriptor.ManualId ? a.ManualAccountValueUsd : s?.AccountValueUsd), a.LastSyncedAtUtc,
        a.SyncStatus, a.LastSyncError, a.IsEnabled ? s?.Positions.Count ?? 0 : 0, a.HistoryNotice, a.IsEnabled, a.SettingsRevision,
        s?.StablecoinsObservedAtUtc is null ? null : ExactDecimal.Sum(s.Stablecoins.Select(balance => balance.Available)),
        s?.StablecoinScope, s?.AccountMode,
        s?.StablecoinsObservedAtUtc is null ? null : ExactDecimal.Sum(s.Stablecoins.Select(balance => balance.Total)),
        Balance(a, s) is { } balance ? ExactDecimal.Sum(balance) : null, a.SourceId,
        AvailableBalance(a, s) is { } available ? ExactDecimal.Sum(available) : null, AvailableSource(a, s), a.TradingMode,
        KeepExecutionHistory: a.KeepExecutionHistory);
    }

    /// <summary>
    /// What the account holds in nominal USD. Unified and portfolio-margin accounts keep every balance in the spot
    /// wallet (their perp state is not meaningful), so the wallet total is the balance. Other modes keep separate perp and
    /// spot ledgers, so perps equity and the wallet total add up. Supported stablecoins only; other assets are excluded.
    /// </summary>
    // Keep individual ledger amounts until the exact display sum; decimal addition can round within one account.
    private static List<decimal>? Balance(Account a, AccountSnapshot? s)
    {
        if (a.VenueId == VenueDescriptor.ManualId) return a.ManualAccountValueUsd is { } manual ? [manual] : null;
        if (s is null) return null;
        List<decimal>? wallet = s.StablecoinsObservedAtUtc is null ? null : s.Stablecoins.Select(balance => balance.Total).ToList();
        if (s.AccountMode is "unifiedAccount" or "portfolioMargin") return wallet;
        // Separate ledgers add up only when the wallet was read with the perps or after them. A wallet left from an earlier
        // read could still hold funds since moved into perps, so the balance is unknown (Phase 2, item 14b, Sol round 1).
        if (wallet is not null && s.StablecoinsObservedAtUtc < s.ObservedAtUtc) return null;
        return s.AccountValueUsd is { } perps ? [perps, ..wallet ?? []] : wallet;
    }

    /// <summary>One reported availability source per enabled account. Never infer it from equity or margin used.</summary>
    private string? AvailableSource(Account a, AccountSnapshot? s)
    {
        if (!a.IsEnabled || a.VenueId == VenueDescriptor.ManualId || s is null) return null;
        if (venues.Descriptor(a.VenueId)?.Capabilities.StablecoinWallet == true &&
            s.AccountMode is "unifiedAccount" or "portfolioMargin")
            return s.StablecoinsObservedAtUtc is null ? null : "wallet";
        return s.AvailableMarginUsd is not null ? "margin" : s.WithdrawableUsd is not null ? "withdrawable" : null;
    }

    private List<decimal>? AvailableBalance(Account a, AccountSnapshot? s) => AvailableSource(a, s) switch
    {
        "wallet" => s!.Stablecoins.Select(balance => balance.Available).ToList(),
        "margin" => [s!.AvailableMarginUsd!.Value],
        "withdrawable" => [s!.WithdrawableUsd!.Value],
        _ => null
    };

    private static string Coverage(int known, int enabled) =>
        known == 0 ? "unavailable" : known == enabled ? "complete" : "partial";
    private static StablecoinWalletDto? WalletDto(AccountSnapshot snapshot) =>
        snapshot.StablecoinsObservedAtUtc is not { } observed ? null :
        new(observed, snapshot.AccountMode!, snapshot.StablecoinScope!,
            ExactDecimal.Sum(snapshot.Stablecoins.Select(balance => balance.Total)),
            ExactDecimal.Sum(snapshot.Stablecoins.Select(balance => balance.Available)),
            snapshot.Stablecoins.OrderBy(balance => balance.TokenIndex).Select(balance => new StablecoinBalanceDto(
                balance.Symbol, balance.TokenIndex, balance.TokenId, ExactDecimal.Format(balance.Total), ExactDecimal.Format(balance.Held),
                ExactDecimal.Format(balance.Available))).ToList(),
            "Venue stablecoin wallet only: supported stablecoin token identities, nominal USD-pegged units, no FX/depeg adjustment. Available is total minus held; it is not guaranteed withdrawal capacity or perpetual free margin. Other assets, EVM wallets and lending/borrow accounting are excluded. Perpetual margin is not added again.");
    private static FillDto ToDto(ImportedFill f) => new(f.Id, f.AccountId, f.ContractId, f.Side, f.Direction, ExactDecimal.Format(f.Price),
        ExactDecimal.Format(f.Quantity), ExactDecimal.Format(f.Fee), f.FeeToken, ExactDecimal.Format(f.ClosedPnlUsd), f.OccurredAtUtc,
        f.ExternalOrderId, f.SourceFillId, f.TransactionHash, f.PositionEffect, f.FeeBasis, f.PnlBasis);
}
