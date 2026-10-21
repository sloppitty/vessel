using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Application.Monitoring;
using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Risk;

namespace Vessel.Application.Risk;

/// <summary>
/// Open risk (heat) across the owner's Planned and Open real Plays, and the owner's soft limits (Phase 2, item 1). It reads
/// retained facts only: no venue call, no estimate of fees, slippage or funding, and nothing is enforced.
/// </summary>
public sealed class OpenRiskService(IOpenRiskStore store, IWorkspaceStore workspaceStore, WorkspaceService workspace,
    IVenueRegistry venues, IJournalOwnerContext owner, TimeProvider time, IUnitOfWork unitOfWork, MonitorOptions? monitor = null)
{
    public const string Notice = "Open risk is the loss if every current stop of each Planned and Open Play fills, at the plan's prices: " +
        "not a venue guarantee. Filled entries count from their average fill, unfilled ones at their planned price. " +
        "Long and short are never netted, and a stop at or beyond its entry adds nothing. A Play that cannot be measured is " +
        "counted as unknown and never partly summed. Paused and unresolved Plays are not included. Nominal USD; no fees, " +
        "slippage or funding. " + NominalStablecoins.Caveat;

    public const int MaxEntries = PlayDocuments.MaxEntries, MaxStops = PlayDocuments.MaxStops, MaxText = 64;

    public async Task<OpenRiskDto> GetAsync(string? scope, CancellationToken ct)
    {
        var listScope = ListScope.Parse(scope);
        var portfolios = await workspaceStore.PortfoliosAsync(ct, TradingMode.Real);
        var accounts = await workspace.AccountsAsync(ct, TradingMode.Real);
        listScope.RequireKnown(id => portfolios.Any(p => p.Id == id), id => accounts.Any(a => a.Id == id));
        var members = accounts.Where(a => listScope.Includes(a.Id, a.PortfolioId, a.IsEnabled)).ToList();
        var memberIds = members.Select(a => a.Id).ToHashSet();
        var facts = (await store.ActivePlaysAsync(null, ct)).Where(f => memberIds.Contains(f.Play.AccountId)).ToList();
        var settings = await store.SettingsAsync(ct);

        var plays = new List<OpenRiskPlayDto>();
        foreach (var fact in facts.Where(f => f.Play.Status is PlayStatus.Planned or PlayStatus.Open)
                     .OrderBy(f => f.Play.CreatedAtUtc).ThenBy(f => f.Play.Id))
        {
            var risk = Risk(fact, PlayDocuments.Read(fact.Play.Plan));
            plays.Add(new(fact.Play.Id, fact.Play.Title, fact.Play.Direction, PlayService.StatusName(fact.Play.Status), fact.Play.AccountId,
                risk.Risk?.ToString(), risk.Reason, risk.Risk is { } value && !value.Terminates, Protection(fact)));
        }
        OpenRiskDirectionDto Direction(string direction)
        {
            var mine = plays.Where(p => p.Direction == direction).ToList();
            var known = mine.Where(p => p.RiskUsd is not null).ToList();
            return new(known.Aggregate(default(ExactAmount), (sum, p) => sum + ExactAmount.Parse(p.RiskUsd!)).ToString(),
                known.Count, mine.Count - known.Count, known.Any(p => p.Rounded));
        }
        var excluded = new List<OpenRiskExcludedDto>();
        if (facts.Count(f => f.Play.Status == PlayStatus.Paused) is > 0 and var paused) excluded.Add(new("paused", paused));
        if (facts.Count(f => f.Play.Status == PlayStatus.Unresolved) is > 0 and var unresolved) excluded.Add(new("unresolved", unresolved));
        var balances = members.Select(a => a.BalanceUsd).OfType<string>().ToList();
        var balance = new OpenRiskBalanceDto(balances.Count == 0 ? null
            : balances.Aggregate(default(ExactAmount), (sum, v) => sum + ExactAmount.Parse(v)).ToString(), balances.Count, members.Count);
        var protection = new OpenRiskProtectionDto(plays.Count(p => p.Protection == NotCovered),
            plays.Count(p => p.Protection is MonitorCoverageStates.Unknown or MonitorCoverageStates.Unsupported or MonitorCoverageStates.Stale or NotChecked));
        return new(time.GetUtcNow(), scope ?? "all", balance, Direction("long"), Direction("short"), excluded,
            protection, plays, Limit(settings), Notice);
    }

    /// <summary>
    /// The edited plan's own risk by the same rule. A saved Play's executions count; a new Draft has none. The request is
    /// bounded like a plan, and blank or unreadable numbers make the result unknown, never an error.
    /// </summary>
    public async Task<OpenRiskPreviewDto> PreviewAsync(OpenRiskPreviewRequest request, CancellationToken ct)
    {
        var plan = Validate(request.Plan);
        var account = await workspaceStore.AccountAsync(request.AccountId, ct);
        if (account is null || account.TradingMode != TradingMode.Real) throw new ProblemException(404, "Account not found.");
        OpenRiskFacts? fact = null;
        if (request.PlayId is { } id)
        {
            // A saved Play must be this owner's real Play on this account; anything else is not found, never a new Draft.
            var play = await store.PlayAsync(id, ct);
            if (play is null || play.AccountId != account.Id || play.TradingMode != TradingMode.Real) throw new ProblemException(404, "Play not found.");
            // A Draft or ended Play has no active executions to count; an active one has its own.
            fact = (await store.ActivePlaysAsync([id], ct)).SingleOrDefault();
        }
        var manual = venues.Descriptor(account.VenueId)?.Capabilities.ManualExecutions == true;
        var risk = fact is null ? OpenRiskCalculator.Calculate(plan, [], manual || UsdQuoted(account.VenueId)) : Risk(fact, plan);
        return new(request.PlayId, plan.Direction, risk.Risk?.ToString(), risk.Reason, risk.Risk is { } value && !value.Terminates);
    }

    public async Task<RiskSettingsDto> SettingsAsync(CancellationToken ct) => new(Limit(await store.SettingsAsync(ct)));

    public async Task<RiskSettingsDto> UpdateSettingsAsync(UpdateRiskSettingsRequest request, CancellationToken ct)
    {
        decimal? limit = null;
        if (request.OpenRiskLimitPercent is { } text)
        {
            if (!ExactDecimal.IsPlanText(text) || !ExactDecimal.TryParseVenue(text, out var value))
                throw new ProblemException(400, "The open-risk limit is a plain decimal percentage, or empty to clear it.");
            limit = value;
        }
        var now = time.GetUtcNow();
        var saved = await unitOfWork.ExecuteAsync(async () =>
        {
            var settings = await store.SettingsForUpdateAsync(new RiskSettings(owner.OwnerId, now), ct);
            settings.SetOpenRiskLimit(limit, now);
            await store.SaveAsync(ct);
            return settings;
        }, ct);
        return new(Limit(saved));
    }

    private PlayRisk Risk(OpenRiskFacts fact, PlayPlanDocument plan)
    {
        if (fact.Problem is { } problem) return new(null, problem);
        var play = fact.Play;
        var manual = fact.ManualLedger || venues.Descriptor(play.VenueId)?.Capabilities.ManualExecutions == true;
        var usd = manual || play.InstrumentSource == InstrumentSource.Venue && UsdQuoted(play.VenueId);
        return OpenRiskCalculator.Calculate(plan, fact.Executions, usd);
    }

    public const string NotCovered = "not-covered", Covered = "covered", NotChecked = "not-checked";

    /// <summary>
    /// The monitor's last recorded stop coverage of an Open venue Play (0.4c), projected stale after two intervals like the
    /// monitor's own read. Only a fresh known "not covered" is confirmed absence; null for Planned and manual Plays.
    /// </summary>
    private string? Protection(OpenRiskFacts fact)
    {
        if (fact.Play.Status != PlayStatus.Open || fact.ManualLedger || venues.Descriptor(fact.Play.VenueId)?.Capabilities.ManualExecutions == true)
            return null;
        if (fact.Coverage is not { } coverage) return NotChecked;
        var interval = (monitor ?? new MonitorOptions()).Interval;
        if (coverage.CheckedAtUtc < time.GetUtcNow() - interval * 2) return MonitorCoverageStates.Stale;
        return coverage.State switch
        {
            MonitorCoverageStates.Known => coverage.Covered == true ? Covered : coverage.Covered == false ? NotCovered : MonitorCoverageStates.Unknown,
            MonitorCoverageStates.Shared => MonitorCoverageStates.Shared,
            MonitorCoverageStates.Unsupported => MonitorCoverageStates.Unsupported,
            _ => MonitorCoverageStates.Unknown,
        };
    }

    private bool UsdQuoted(string venueId) => NominalStablecoins.Contains(venues.Descriptor(venueId)?.QuoteAsset);

    private static string? Limit(RiskSettings? settings) => ExactDecimal.Format(settings?.OpenRiskLimitPercent);

    /// <summary>The preview's plan as a bounded plan document; structure is checked, numbers are read as the editor reads them.</summary>
    private static PlayPlanDocument Validate(RiskPlanDto? plan)
    {
        static bool Text(string? value) => value is not null && value.Length <= MaxText;
        if (plan is null || plan.Direction is not ("long" or "short") || plan.SizingMode is not ("margin" or "quantity") ||
            !Text(plan.Size) || !Text(plan.Leverage) || plan.Entries is not { Count: > 0 and <= MaxEntries } ||
            plan.Entries.Any(e => e is null || !Text(e.Id) || !Text(e.Share) || !Text(e.Price) || e.Stops is not { Count: <= MaxStops } ||
                e.Stops.Any(s => s is null || !Text(s.Id) || s.Unit is not ("price" or "percent") || !Text(s.Value) || !Text(s.Share))))
            throw new ProblemException(400, $"A plan has a direction, sizing, size and leverage, and 1 to {MaxEntries} entries with at most {MaxStops} stops each.");
        // As saved plans require: every entry and stop ID is unique, so no level can stand for another.
        var ids = plan.Entries.Select(e => e.Id).Concat(plan.Entries.SelectMany(e => e.Stops.Select(s => s.Id))).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new ProblemException(400, "Plan items need unique IDs.");
        return new(plan.Direction, plan.SizingMode, plan.Size, plan.Leverage, null,
            plan.Entries.Select(e => new PlanEntry(e.Id, e.Id, "#000000", e.Share, e.Price,
                e.Stops.Select(s => new PlanExit(s.Id, s.Unit, s.Value, s.Share)).ToList(), [])).ToList(),
            new PlanNotes("", "", "", ""));
    }
}
