using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Infrastructure.Venues.Risex;

public sealed partial class RisexReader
{
    private const int MaxFundingPages = 10;
    private const string FundingNotice =
        "RISEx settled funding only, in USDC; unsettled accruals and dividend adjustments are excluded. " +
        "Whether RISEx execution PnL includes funding is unverified; funding must not be added to it.";

    public Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(account);
        if (account.VenueId != Id || !account.IsEnabled)
            throw new VenueReadException("An enabled RISEx account is required for funding history.");
        var address = Address(account.SourceId ?? account.Address);
        if (account.Address is not null && Address(account.Address) != address)
            throw new VenueReadException("The RISEx account source and public address must match.");
        if (fromUtc < DateTimeOffset.UnixEpoch || toUtc < fromUtc || toUtc > timeProvider.GetUtcNow() ||
            toUtc - fromUtc > TimeSpan.FromDays(31) ||
            fromUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 || toUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            toUtc.ToUnixTimeMilliseconds() > long.MaxValue / 1_000_000)
            throw new VenueReadException("RISEx funding requires an inclusive past window of at most 31 days with exact millisecond bounds.");
        var fromNs = fromUtc.ToUnixTimeMilliseconds() * 1_000_000;
        var toNs = toUtc.ToUnixTimeMilliseconds() * 1_000_000;
        // The docs do not specify endpoint inclusivity. Read one ns either side and filter the exact window locally.
        var requestFrom = Math.Max(0, fromNs - 1);
        var requestTo = toNs + 1;

        return Http.ReadBoundedAsync(async ct =>
        {
            var catalogue = await CatalogueAsync(ct);
            var payments = new List<VenueFundingPayment>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var unknownMarkets = false;
            long previousTime = -1;
            for (var page = 1; page <= MaxFundingPages; page++)
            {
                // Explicit bounds override the API's default seven-day window. No rate/position arithmetic.
                using var document = await GetAsync(FormattableString.Invariant(
                    $"/v1/account/funding-payments?account={address}&start_time={requestFrom}&end_time={requestTo}&page={page}&limit={PageSize}&sorted_by=time"), ct);
                var data = Data(document);
                if (Address(Json.Text(Json.Property(data, "account"), 42)) != address ||
                    Json.Integer(Json.Property(data, "page")) != page)
                    throw Json.Invalid();
                var rows = Json.Array(Json.Property(data, "records"));
                var hasNext = Json.Boolean(Json.Property(data, "has_next_page"));
                if (rows.GetArrayLength() > PageSize || (hasNext && rows.GetArrayLength() == 0))
                    throw Json.Invalid();
                foreach (var row in rows.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var kind = Json.Text(Json.Property(row, "entry_type"), 16);
                    if (kind is not ("funding" or "dividend")) throw Json.Invalid();
                    var settled = Json.Boolean(Json.Property(row, "is_settled"));
                    if (!settled || kind == "dividend") continue;
                    var time = FundingUnsigned(Json.Property(row, "block_time"));
                    if (time < requestFrom || time > requestTo || time < previousTime) throw Json.Invalid();
                    previousTime = time;
                    if (time < fromNs || time > toNs) continue;
                    var marketId = Json.Text(Json.Property(row, "market_id"), 20);
                    if (FundingUnsigned(Json.Property(row, "market_id")) == 0) throw Json.Invalid();
                    var block = FundingUnsigned(Json.Property(row, "block_number"));
                    if (block == 0) throw Json.Invalid();
                    var transaction = Json.Text(Json.Property(row, "tx_hash"), 66);
                    if (transaction.Length != 66 || !transaction.StartsWith("0x", StringComparison.Ordinal) ||
                        !transaction[2..].All(char.IsAsciiHexDigit))
                        throw Json.Invalid();
                    var amount = Json.Number(Json.Property(row, "funding_payment"));
                    // REST has no payment ID/log index. Fail closed if two rows share these native coordinates;
                    // neither content hashes nor page ordinals can prove that they are distinct settlements.
                    var identity = "risex-funding:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                        FormattableString.Invariant($"{address}|{marketId}|{block}|{time}|{transaction.ToLowerInvariant()}"))));
                    if (!identities.Add(identity)) throw Json.Invalid();
                    if (catalogue.ById(marketId) is not { } market)
                    {
                        unknownMarkets = true;
                        continue;
                    }
                    // RISEx reports positive PAID / negative RECEIVED, already decimal USDC (not wei).
                    payments.Add(new(identity, market.Key, marketId, -amount, QuoteAsset,
                        DateTimeOffset.UnixEpoch.AddTicks((time / 1_000) * 10), row.GetRawText())
                    {
                        // PostgreSQL timestamps have microsecond precision; keep the original native instant separately.
                        OccurredAtUnixNanoseconds = time
                    });
                }
                if (!hasNext) return Result(complete: !unknownMarkets, truncated: false);
            }
            return Result(complete: false, truncated: true);

            VenueFundingReadResult Result(bool complete, bool truncated) => new(payments, fromUtc, toUtc, complete,
                FundingNotice + (truncated ? " Incomplete history: the 10-page / 10,000-row funding limit was reached." : "") +
                (unknownMarkets ? " Incomplete history: payments for unrecognized native markets were omitted." : ""),
                FundingFacts.Unknown) { Truncated = truncated && !unknownMarkets };
        }, cancellationToken);
    }

    private static long FundingUnsigned(JsonElement value)
    {
        var text = Json.Text(value, 20);
        return text.All(char.IsAsciiDigit) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : throw Json.Invalid();
    }
}
