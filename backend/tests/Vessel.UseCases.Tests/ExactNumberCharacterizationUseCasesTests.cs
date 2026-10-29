
namespace Vessel.Tests;

/// <summary>
/// Characterization of every exact-number entry point (backend review S1): the text each one accepts and the
/// strings it prints, quirks included. The tables are the contract. When an implementation moves, only the
/// bindings in the first section may change; a changed expectation is a behaviour change.
/// </summary>
public sealed class ExactNumberCharacterizationUseCasesTests : ExactNumberCharacterizationTestsSupport
{

    [Fact]
    public async Task Missing_text_is_rejected_where_it_can_arrive()
    {
        Assert.Equal("x", PlanText(null));
        Assert.Equal("x", ManualFact(null!, positive: true));
        Assert.Equal("x", ManualFact(null!, positive: false));
        Assert.Equal("x", await RiskPercent(null));
    }
}
