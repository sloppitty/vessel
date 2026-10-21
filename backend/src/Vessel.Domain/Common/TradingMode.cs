namespace Vessel.Domain.Common;

public static class TradingMode
{
    public const string Real = "real";
    public const string Paper = "paper";

    public static bool IsKnown(string? mode) => mode is Real or Paper;

    public static string Validate(string mode) => IsKnown(mode)
        ? mode : throw new DomainValidationException("Trading mode must be real or paper.");
}
