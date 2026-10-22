using System.Globalization;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Infrastructure.Venues.Lighter;

public sealed partial class LighterReader
{
    public async Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Source(account);
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (fromUtc < DateTimeOffset.UnixEpoch || fromUtc > toUtc || toUtc - fromUtc > TimeSpan.FromDays(31) ||
            toUtc > timeProvider.GetUtcNow() ||
            fromUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 || toUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new VenueReadException($"{venue.Name} funding requires an inclusive, millisecond-aligned past window of at most 31 days.");

        var payments = new Dictionary<string, VenueFundingPayment>(StringComparer.Ordinal);
        VenueFundingReadResult Result(bool complete, string detail, bool truncated = false) => new(payments.Values.ToArray(), fromUtc, toUtc,
            complete, $"{(complete ? "" : "Incomplete ")}{venue.Name} settled funding: {detail} " +
            $"{venue.Collateral}; positive received, negative paid. Execution PnL funding inclusion is unverified.",
            FundingFacts.Unknown) { Truncated = truncated };

        var attempt = await credentials.ReadAttemptAsync(account.Id, cancellationToken);
        if (attempt is null)
            return Result(false, "a usable read-only token is required; ordinary account history is unchanged.");
        var credential = attempt.Token;

        var refused = false;
        VenueFundingReadResult result;
        try
        {
            result = await BoundedAsync(async ct =>
            {
                ReadToken? parsed = null;
                try
                {
                    parsed = Token(credential);
                    await BindAsync(source, credential, ct);
                    var catalogue = await CatalogueAsync(ct, refresh: true);
                    // The official client uses seconds. Widen by one second on both sides because the API
                    // does not specify inclusive bounds, then enforce the caller's exact inclusive window.
                    var start = Math.Max(0, fromUtc.ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture);
                    var end = (toUtc.ToUnixTimeSeconds() + 1).ToString(CultureInfo.InvariantCulture);
                    string? cursor = null;
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    for (var page = 0; page < MaxPages; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Token(credential); // A token can expire during the bounded read.
                        using var document = await GetAsync("positionFunding?account_index=" + source +
                            "&limit=100&side=all&start_timestamp=" + start + "&end_timestamp=" + end +
                            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), ct, credential);
                        var root = document.RootElement;
                        var rows = ReadFunding(root, catalogue, fromUtc, toUtc, timeProvider.GetUtcNow(), credential);
                        var next = root.TryGetProperty("next_cursor", out var value) && value.ValueKind == JsonValueKind.Null
                            ? null : Cursor(json, root);
                        if (next is not null && (!cursors.Add(next) || FundingTextContainsToken(next, credential)))
                            throw json.Invalid();
                        // Validate the entire page before retaining any of it.
                        foreach (var payment in rows)
                            if (payments.TryGetValue(payment.SourceId, out var previous) &&
                                previous with { RawJson = payment.RawJson } != payment)
                                throw json.Invalid();
                        foreach (var payment in rows) payments.TryAdd(payment.SourceId, payment);
                        cursor = next;
                        if (cursor is null) return Result(true, "the requested window was exhausted.");
                    }
                    return Result(false, "the five-page limit was reached; the requested window was not exhausted.", truncated: true);
                }
                catch (LighterAuthenticationException)
                {
                    // Local expiry is already represented by credential metadata, not a venue refusal.
                    refused = parsed is not null && parsed.Expiry > timeProvider.GetUtcNow();
                    return Result(false, "the read-only token was refused or expired; verify or replace it. Ordinary account history is unchanged.");
                }
            }, cancellationToken);
        }
        catch (VenueReadException ex) when (ex is not VenueRateLimitException)
        {
            // Optional funding must not roll back an already valid ordinary snapshot/history read.
            // Never surface an upstream body, URL, token or arbitrary exception message.
            result = Result(false, "the funding read could not be verified; coverage is incomplete. Ordinary account history is unchanged.");
        }
        if (refused) await credentials.MarkRefusedAsync(account.Id, attempt.Revision, cancellationToken);
        return result;
    }

    // Official schema and first-party display semantics: docs/architecture/lighter-funding-research.md.
    internal static IReadOnlyList<VenueFundingPayment> ReadFunding(JsonElement root, Catalogue catalogue,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset observed, string? credential = null)
    {
        var exchange = catalogue.Exchange;
        var json = exchange.Json;
        var payments = new List<VenueFundingPayment>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(json, root, "position_fundings", PageSize).EnumerateArray())
        {
            var marketId = Identity(json, json.Property(row, "market_id"));
            // Inactive/retired markets are deliberately retained in the catalogue.
            var market = catalogue.ById(marketId) ?? throw json.Invalid();
            var fundingId = Identity(json, json.Property(row, "funding_id"));
            var sourceId = marketId + ":" + fundingId;
            if (!ids.Add(sourceId)) throw json.Invalid();
            var at = Seconds(json, row, "timestamp", observed.AddMinutes(5));
            var change = Number(json, row, "change");
            _ = Nonnegative(json, row, "discount");
            _ = Number(json, row, "rate");
            _ = Nonnegative(json, row, "position_size");
            if (Text(json, row, "position_side") is not ("long" or "short")) throw json.Invalid();
            if (credential is not null && FundingContainsToken(row, credential)) throw json.Invalid();
            if (at < fromUtc || at > toUtc) continue;
            // change is the reported signed settlement, not a rate-derived estimate or a side-based sign.
            payments.Add(new(sourceId, market.Key, marketId, change, exchange.Collateral, at, row.GetRawText()));
        }
        return payments;
    }

    private static bool FundingContainsToken(JsonElement value, string token) => value.ValueKind switch
    {
        JsonValueKind.String => FundingTextContainsToken(value.GetString()!, token),
        JsonValueKind.Object => value.EnumerateObject().Any(p =>
            FundingTextContainsToken(p.Name, token) || FundingContainsToken(p.Value, token)),
        JsonValueKind.Array => value.EnumerateArray().Any(v => FundingContainsToken(v, token)),
        _ => false
    };

    private static bool FundingTextContainsToken(string text, string token) =>
        text.Contains(token, StringComparison.Ordinal) ||
        text.Contains(Uri.EscapeDataString(token), StringComparison.OrdinalIgnoreCase);
}
