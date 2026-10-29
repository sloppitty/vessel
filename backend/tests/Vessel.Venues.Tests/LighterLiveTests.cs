using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

/// <summary>No token is read from the environment. Public reads only (mainnet and Robinhood), with no account writes or history backfill.</summary>
public sealed class LighterLiveTests
{
    private sealed class NoCredential : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) =>
            throw new InvalidOperationException("The public overload must never look up credentials.");
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) =>
            throw new InvalidOperationException("The public overload must never look up credentials.");
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) =>
            throw new InvalidOperationException("The public overload must never change credentials.");
    }

    [LighterLiveFact]
    public async Task Public_catalogue_empty_system_account_and_three_candles_parse()
    {
        using var client = new HttpClient(new LighterAuthenticationHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/"), Timeout = TimeSpan.FromSeconds(20)
        };
        var reader = new LighterReader(client, TimeProvider.System, new NoCredential());
        var catalogue = await reader.ReadInstrumentsAsync(default);
        Assert.Contains(catalogue, i => i.ContractId == "BTC" && i.PriceStep > 0);
        Assert.DoesNotContain(catalogue, i => i.ContractId.Contains('/'));
        // Core concepts identifies 0-2 as system accounts. Account 1 has no recent executions.
        var result = await reader.ReadAsync("1", default);
        Assert.Equal("lighter-perps-account", result.Snapshot.ValueScope);
        Assert.All(result.Fills, f => Assert.True(VenueFactChecks.Valid(f)));
        // Wallet of account 3. Discovery must parse the live shape, including "" balances.
        var accounts = await reader.DiscoverAsync("0x3fbba2b0e07895ae8638d17fd83d72338954d272", default);
        Assert.Equal("main", accounts[0].AccountType);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candles = await reader.ReadCandlesAsync("BTC", "1h", now - 2 * 3600000L, now, default);
        Assert.InRange(candles.Count, 1, 3);
        Assert.All(candles, c => Assert.Null(c.Trades));
    }

    /// <summary>Lighter on Robinhood Chain: same gate, public reads only, the RH host and its USDG collateral.</summary>
    [LighterLiveFact]
    public async Task Robinhood_public_catalogue_system_account_and_candles_parse()
    {
        var exchange = LighterExchange.Robinhood;
        using var client = new HttpClient(new LighterAuthenticationHandler(exchange.Name) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })
        {
            BaseAddress = new Uri("https://api.rh.lighter.xyz/"), Timeout = TimeSpan.FromSeconds(20)
        };
        var reader = new LighterReader(client, TimeProvider.System, new NoCredential(), exchange: exchange);
        var catalogue = await reader.ReadInstrumentsAsync(default);
        Assert.Contains(catalogue, i => i.ContractId == "BTC" && i.PriceStep > 0);
        Assert.Contains(catalogue, i => i.ContractId == "AAPL");
        Assert.All(catalogue, i => Assert.Equal("USDG", i.QuoteAsset));
        Assert.DoesNotContain(catalogue, i => i.ContractId.Contains('/'));
        // System account 1 holds USDG as asset 3 (checked 2026-10-14) and has no recent executions.
        var result = await reader.ReadAsync("1", default);
        Assert.Equal("lighter-rh-perps-account", result.Snapshot.ValueScope);
        Assert.All(result.Fills, f => Assert.True(VenueFactChecks.Valid(f)));
        Assert.StartsWith(exchange.HistoryNotice, result.HistoryNotice);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candles = await reader.ReadCandlesAsync("BTC", "1h", now - 2 * 3600000L, now, default);
        Assert.InRange(candles.Count, 1, 3);
    }
}
