using System.Text.RegularExpressions;
using Vessel.Application.Workspace;

namespace Vessel.Tests;

public sealed class NominalStablecoinsTests
{
    [Fact]
    public void The_list_is_exact_and_case_sensitive()
    {
        Assert.Equal(["USDC", "USDG", "USDT", "USDT0", "USDE", "USDe", "USDH"], NominalStablecoins.Symbols);
        Assert.True(NominalStablecoins.Contains("USDG"));
        Assert.True(NominalStablecoins.Contains("USDe"));
        Assert.False(NominalStablecoins.Contains("usdg"));
        Assert.False(NominalStablecoins.Contains("PYUSD"));
        Assert.False(NominalStablecoins.Contains(""));
        Assert.False(NominalStablecoins.Contains(null));
    }

    [Fact]
    public void The_caveat_names_every_listed_stablecoin_and_rules_out_valuation()
    {
        foreach (var symbol in NominalStablecoins.Symbols) Assert.Contains(symbol, NominalStablecoins.Caveat, StringComparison.Ordinal);
        Assert.Contains("nominal 1:1", NominalStablecoins.Caveat, StringComparison.Ordinal);
        Assert.Contains("no FX or depeg valuation", NominalStablecoins.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frontend_list_matches_the_backend_list()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pnpm-workspace.yaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "frontend", "src", "features", "plays", "stablecoins.ts"));
        var list = Regex.Match(source, @"nominalStablecoins\s*=\s*\[(?<items>[^\]]*)\]");
        Assert.True(list.Success);
        var symbols = Regex.Matches(list.Groups["items"].Value, "'([^']*)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(NominalStablecoins.Symbols, symbols);
    }
}
