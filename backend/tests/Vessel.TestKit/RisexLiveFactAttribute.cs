
namespace Vessel.Tests;

/// <summary>Opt-in checks against the live RISEx API. Set Vessel_TEST_RISEX_ADDRESS to a public address with trading history.</summary>
public sealed class RisexLiveFactAttribute : FactAttribute
{
    public RisexLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_RISEX_ADDRESS")))
            Skip = "Set Vessel_TEST_RISEX_ADDRESS to run live RISEx reads.";
    }
}
