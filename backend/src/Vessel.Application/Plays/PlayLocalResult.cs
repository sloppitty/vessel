using System.Numerics;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays;

/// <summary>
/// The Play-local result: what the Play's own linked fills did, shown beside the scored venue result (Phase 0 item 0.3).
/// Venues that realize P&amp;L at the position's average cost can give a Play another Play's (or an unjournaled position's)
/// entry price, so its venue result can have the wrong sign. This figure uses only the Play's own executions:
/// <code>
/// gross = direction sign × (Σ exit quantity × price − Σ entry quantity × price)    exact products, never rounded
/// net   = gross − Σ trading fees of every linked fill                               both PnL bases, so each fee once
/// </code>
/// It uses no funding by construction and is not total profit. Sizing and Review still score the venue result
/// (owner decision 1); this one is never scored. A manual ledger's average-cost result already is the Play-local result
/// (<see cref="ManualLedger"/>): the same formula over its projection, labelled <see cref="ManualLedger"/>.
/// </summary>
/// <param name="Gross">The local result before fees; null when unknown.</param>
/// <param name="Net">The local result after every trading fee; null when unknown.</param>
/// <param name="Basis"><see cref="Executions"/> for linked venue fills, <see cref="ManualLedger"/> for owner-entered facts.</param>
/// <param name="Reason">Why the result is unknown, in the owner's words; null when known.</param>
public sealed record PlayLocalResult(ExactAmount? Gross, ExactAmount? Net, string Basis, string? Reason)
{
    public const string Executions = "executions", ManualLedger = "manual-ledger";

    private static readonly BigInteger ProductScale = BigInteger.Pow(10, 28);

    /// <summary>
    /// The Play-local result of a Play's facts. Known only when:
    /// <list type="bullet">
    /// <item>the Play is closed and its linked fills settle it (<see cref="ExecutionIntegrity.Settled"/>: balanced, in order,
    /// nothing contradicting and no known missing executions);</item>
    /// <item>every fill is on the Play's account and instrument with a positive price and quantity;</item>
    /// <item>for venue fills, the instrument comes from the venue catalogue and <paramref name="quoteAsset"/>, the venue
    /// descriptor's quote asset, is a listed nominal USD stablecoin. Every registered venue module reads linear perpetuals
    /// quoted and margined in that asset, so quantity × price is the quote cash flow. A manual ledger is a linear
    /// USD-quoted contract by its own definition, so it skips this check;</item>
    /// <item>every fee is verified: a reported or Standard-account fee basis, a non-blank token, and that token a listed
    /// stablecoin or the fee a known zero; for a manual ledger also no unknown manual cost.</item>
    /// </list>
    /// </summary>
    public static PlayLocalResult From(ClosedPlayFacts facts, string? quoteAsset)
    {
        var basis = facts.ManualSource ? ManualLedger : Executions;
        PlayLocalResult Unknown(string reason) => new(null, null, basis, reason);
        if (facts.Play.Status != PlayStatus.Closed) return Unknown("The play is not closed.");
        if (!ExecutionIntegrity.Settled(facts)) return Unknown("Its linked fills do not settle the play.");
        var fills = facts.Fills;
        if (fills.Any(f => f.Fill.AccountId != facts.Play.AccountId || f.Fill.ContractId != facts.Play.ContractId ||
                f.Fill.Price <= 0 || f.Fill.Quantity <= 0))
            return Unknown("A linked fill is on another account or instrument, or lacks a positive price and quantity.");
        if (!facts.ManualSource && UnsupportedUnits(facts.Play, quoteAsset) is { } units) return Unknown(units);
        if (!facts.ManualFeesComplete || fills.Any(f => !VerifiedFee(f.Fill)))
            return Unknown("A fill's fee is unverified or not in a listed USD stablecoin.");
        BigInteger entries = BigInteger.Zero, exits = BigInteger.Zero;
        foreach (var fill in fills)
        {
            var product = ExactAmount.Product(fill.Fill.Quantity, fill.Fill.Price);
            if (fill.Role == OrderLinkRole.Entry) entries += product; else exits += product;
        }
        var units56 = facts.CurrentPlan.Direction == "long" ? exits - entries : entries - exits;
        var whole = BigInteger.DivRem(units56, ProductScale, out var remainder);
        if (!remainder.IsZero) return Unknown("The result needs more than 28 decimal places; it is not rounded.");
        var gross = new ExactAmount(whole);
        var fees = fills.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Fill.Fee));
        return new(gross, gross - fees, basis, null);
    }

    /// <summary>Why a venue Play's quantities and prices are not a nominal USD cash flow, or null when they are.</summary>
    private static string? UnsupportedUnits(Play play, string? quoteAsset) =>
        play.InstrumentSource != InstrumentSource.Venue ? "The instrument was entered by hand, so its quote asset is not known."
        : string.IsNullOrWhiteSpace(quoteAsset) ? "The venue states no quote asset."
        : !NominalStablecoins.Contains(quoteAsset) ? $"Quoted in {quoteAsset}, which is not a listed USD stablecoin."
        : null;

    /// <summary>A reported or Standard-account fee in a listed stablecoin, or a known zero.</summary>
    private static bool VerifiedFee(ImportedFill fill) => ExecutionFacts.IsFeeBasis(fill.FeeBasis) &&
        !string.IsNullOrWhiteSpace(fill.FeeToken) && (NominalStablecoins.Contains(fill.FeeToken) || fill.Fee == 0);

    /// <summary>
    /// Both results are known and their signs differ. Signs compare as negative, zero and positive: scratch is exactly zero,
    /// so a zero beside a nonzero result would change the win/loss/scratch class.
    /// </summary>
    public bool SignDiffers(decimal venueNet) => Net is { } net && net.Units.Sign != Math.Sign(venueNet);

    /// <inheritdoc cref="SignDiffers(decimal)"/>
    public bool SignDiffers(ExactAmount? venueNet) => Net is { } net && venueNet is { } venue && net.Units.Sign != venue.Units.Sign;

    public PlayLocalResultDto ToDto(decimal? venueNet) =>
        new(Net?.ToString(), Basis, Reason, venueNet is { } venue && SignDiffers(venue));

    public PlayLocalResultDto ToDto(ExactAmount? venueNet) => new(Net?.ToString(), Basis, Reason, SignDiffers(venueNet));
}

/// <summary>
/// The Play-local result beside a scored venue result. <paramref name="NetUsd"/> is an exact decimal string, null when
/// unknown with a <paramref name="Reason"/>. <paramref name="Basis"/> is <c>executions</c> or <c>manual-ledger</c>.
/// <paramref name="SignDiffers"/> when both results are known and their signs (negative, zero, positive) differ.
/// </summary>
public sealed record PlayLocalResultDto(string? NetUsd, string Basis, string? Reason, bool SignDiffers);
