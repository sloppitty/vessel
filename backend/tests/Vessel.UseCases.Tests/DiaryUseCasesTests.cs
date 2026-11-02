using Vessel.Application.Plays;

namespace Vessel.Tests;

public sealed class ManualLedgerUseCasesTests
{

    [Fact]
    public void Reason_bound_has_explicit_omitted_count_instead_of_silent_truncation()
    {
        var reasons = DiaryService.BoundedReasons(Enumerable.Range(0, 202).Select(i => $"Order {i} missing"));
        Assert.Equal(100, reasons.Count);
        Assert.Contains("103 additional reasons omitted", reasons[^1]);
        Assert.Equal(202, reasons.Count + 103 - 1);
    }
}
