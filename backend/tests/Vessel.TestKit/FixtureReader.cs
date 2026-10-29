using Vessel.Application.Venues;

namespace Vessel.Tests;

internal sealed class FixtureReader : IPerpetualVenueReader
{
    public string VenueId { get; init; } = "hyperliquid";
    public bool Fail { get; set; }
    public int Reads;
    public int InstrumentReads;
    public CancellationToken InstrumentCancellationToken;
    public Exception? InstrumentFailure { get; set; }
    public PerpetualVenueReadResult Result { get; set; } = new(
        new(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), "primary-perpetual-dex", 1234.1234567890123456789012345m,
            1000.0000000000000000000000001m, 20.123456789012345678901234567m,
            [new("BTC", -0.0123456789012345678901234567m, 60000.123456789m, -1.123456789m, 20.123456789m, 5)]),
        [new("BTC", 5, 50, "USDC")],
        [new("source-1", "BTC", "buy", "Open Long", 60000.123456789m, 0.0000000000000000000000000001m,
            0.00123456789m, "USDC", -0.01m, DateTimeOffset.Parse("2026-10-01T09:00:00Z"), "order-1", "hash-1", "secret raw provider payload", "open")],
        "Recent primary perpetual DEX fills only; incomplete history.");
    public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref InstrumentReads);
        InstrumentCancellationToken = ct;
        ct.ThrowIfCancellationRequested();
        var failure = InstrumentFailure ?? (Fail ? new VenueReadException("secret provider failure") : null);
        return failure is not null ? Task.FromException<IReadOnlyList<VenueInstrument>>(failure) : Task.FromResult(Result.Instruments);
    }
    public Task<PerpetualVenueReadResult> ReadAsync(string address, CancellationToken ct)
    {
        Interlocked.Increment(ref Reads);
        ct.ThrowIfCancellationRequested();
        return Fail ? Task.FromException<PerpetualVenueReadResult>(new VenueReadException("secret provider failure")) : Task.FromResult(Result);
    }
}
