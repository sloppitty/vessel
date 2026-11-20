using System.Globalization;
using Vessel.Application.Common;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Activity;

/// <summary>
/// The Activity filters, evaluated after aggregation. Dates use an operation's latest execution; rolling periods
/// count back from the response's <c>asOfUtc</c>; custom days are inclusive UTC calendar days. <c>partial</c> is an
/// open operation that has already exited some quantity, and <c>open</c> still includes it.
/// </summary>
public sealed record ActivityFilter(Guid? Account, string? Portfolio, string? Contract, string? Direction, string? Status,
    DateTimeOffset? From, DateTimeOffset? To)
{
    public const int MaxContractLength = 128;

    internal static ActivityFilter Parse(ActivityQuery query, DateTimeOffset asOf)
    {
        var account = Value(query.Account) is { } accountText ? Id(accountText, "Choose a valid account filter.") : (Guid?)null;
        var portfolio = Value(query.Portfolio) is { } portfolioText
            ? portfolioText == "unassigned" ? portfolioText : Id(portfolioText, "Choose unassigned or a valid portfolio filter.").ToString("D")
            : null;
        var contract = Value(query.Contract);
        if (contract?.Length > MaxContractLength) throw new ProblemException(400, $"Instrument filters have at most {MaxContractLength} characters.");
        var direction = Value(query.Direction);
        if (direction is not (null or "long" or "short")) throw new ProblemException(400, "Direction must be long or short.");
        var status = Value(query.Status);
        if (status is not (null or "open" or "partial" or "closed" or "incomplete"))
            throw new ProblemException(400, "Status must be open, partial, closed or incomplete.");
        var period = Value(query.Period) ?? "all";
        if (period is not ("all" or "7" or "30" or "90" or "custom"))
            throw new ProblemException(400, "Choose all retained history, 7, 30, 90 days or a custom period.");
        var (fromText, toText) = (Value(query.From), Value(query.To));
        if (period != "custom" && (fromText is not null || toText is not null))
            throw new ProblemException(400, "From and to dates need the custom period.");
        DateTimeOffset? from = period switch
        {
            "custom" => fromText is null ? null : Start(Day(fromText)),
            "all" => null,
            _ => asOf.AddDays(-int.Parse(period, CultureInfo.InvariantCulture))
        };
        // The end day is inclusive; the last representable day has no later bound.
        var end = toText is null ? (DateOnly?)null : Day(toText);
        DateTimeOffset? to = end is null || end == DateOnly.MaxValue ? null : Start(end.Value.AddDays(1));
        return new(account, portfolio, contract, direction, status, from, to);
    }

    /// <summary>Issue #108: whether an account passes the account and portfolio filters, which the read model applies as an account set.</summary>
    public bool MatchesAccount(Guid accountId, Guid? portfolioId) =>
        (Account is null || accountId == Account) &&
        (Portfolio is null || (Portfolio == "unassigned" ? portfolioId is null : portfolioId?.ToString("D") == Portfolio));

    internal bool Matches(ActivityOperationDto operation, Guid? portfolioId) =>
        (Account is null || operation.AccountId == Account) &&
        (Portfolio is null || (Portfolio == "unassigned" ? portfolioId is null : portfolioId?.ToString("D") == Portfolio)) &&
        (Contract is null || operation.ContractId == Contract) &&
        (Direction is null || operation.Direction == Direction) &&
        (Status is null || (Status == "partial" ? IsPartial(operation) : operation.Status == Status)) &&
        (From is null || operation.LastOccurredAtUtc >= From) && (To is null || operation.LastOccurredAtUtc < To);

    /// <summary>An open operation with positive exited and remaining quantities: display progress, not an API status.</summary>
    internal static bool IsPartial(ActivityOperationDto operation) => operation.Status == "open" &&
        Positive(operation.TotalExitedQuantity) && Positive(operation.OpenQuantity);

    private static bool Positive(string? value) => value is not null && ExactAmount.Parse(value).Units.Sign > 0;
    // An empty query value means no filter, as an omitted one does.
    private static string? Value(string? text) => string.IsNullOrEmpty(text) ? null : text;
    private static Guid Id(string text, string message) =>
        Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : throw new ProblemException(400, message);
    private static DateOnly Day(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new ProblemException(400, "Dates use yyyy-MM-dd.");
    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
