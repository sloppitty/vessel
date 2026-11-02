
namespace Vessel.Tests;

public partial class HyperliquidAdapterTestsSupport
{
    // Official spotMeta/spotClearinghouseState shapes, with synthetic balances.
    // Curated public token IDs supplied from the October 1, 2026 metadata check.
    internal const string SpotMeta = """
        {
          "tokens":[
            {"name":"USDC","szDecimals":8,"weiDecimals":8,"index":0,
             "tokenId":"0x6d1e7cde53ba9467b783cb7c530ce054","isCanonical":true,"evmContract":null,"fullName":null},
            {"name":"USDE","szDecimals":2,"weiDecimals":8,"index":235,
             "tokenId":"0x2e6d84f2d7ca82e6581e03523e4389f7","isCanonical":false},
            {"name":"USDT0","szDecimals":2,"weiDecimals":8,"index":268,
             "tokenId":"0x25faedc3f054130dbb4e4203aca63567","isCanonical":false},
            {"name":"USDH","szDecimals":2,"weiDecimals":8,"index":360,
             "tokenId":"0x54e00a5988577cb0b0c9ab0cb6ef7f4b","isCanonical":false},
            {"name":"HYPE","szDecimals":2,"weiDecimals":8,"index":150,
             "tokenId":"0x11111111111111111111111111111111","isCanonical":true},
            {"name":"UBTC","szDecimals":5,"weiDecimals":8,"index":197,
             "tokenId":"0x22222222222222222222222222222222","isCanonical":true},
            {"name":"USDC","szDecimals":8,"weiDecimals":8,"index":999,
             "tokenId":"0x33333333333333333333333333333333","isCanonical":true}
          ],
          "universe":[{"name":"HYPE/USDC","tokens":[150,0],"index":107,"isCanonical":true}]
        }
        """;
}
