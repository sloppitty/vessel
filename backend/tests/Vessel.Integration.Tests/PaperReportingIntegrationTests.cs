using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperReportingIntegrationTests : PaperReportingTestsSupport
{

    [Fact]
    public void Disabled_paper_settings_do_not_erase_valid_history_and_cache_keys_have_mode()
    {
        var paper = Paper(owner, state: "disabled");
        Assert.NotNull(paper.Facts.Paper!.Outcome(At.AddDays(1), out _));
        var real = new ActivitySummaryKey(owner, 1, null, "*");
        Assert.NotEqual(real, real with { TradingMode = "paper" });
    }
}
