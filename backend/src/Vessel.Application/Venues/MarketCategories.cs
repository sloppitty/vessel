namespace Vessel.Application.Venues;

/// <summary>
/// The one set of market categories every venue maps to, so the selector never reads a venue's own spelling. An unknown
/// label is <see cref="Other"/>, never guessed; a venue that states no category at all leaves it null.
/// </summary>
public static class MarketCategories
{
    public const string Crypto = "crypto", Stocks = "stocks", Commodities = "commodities", Indices = "indices", Fx = "fx",
        Rates = "rates", PreIpo = "pre-ipo", Other = "other";

    public static string? Normalize(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "crypto" => Crypto,
        "stock" or "stocks" => Stocks,
        "commodity" or "commodities" => Commodities,
        "index" or "indices" or "index_etf" => Indices,
        "fx" or "forex" => Fx,
        "rates" => Rates,
        "preipo" or "pre-ipo" => PreIpo,
        _ => Other,
    };
}
