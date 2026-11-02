
namespace Vessel.Tests;

public sealed class LighterLiveFactAttribute : FactAttribute
{
    public LighterLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("Vessel_TEST_LIGHTER_PUBLIC") != "1")
            Skip = "Set Vessel_TEST_LIGHTER_PUBLIC=1 for bounded public Lighter reads.";
    }
}
