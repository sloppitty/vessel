
namespace Vessel.Tests;

public sealed class VesselEventRabbitFactAttribute : FactAttribute
{
    public VesselEventRabbitFactAttribute()
    {
        if (new VesselEventPostgresFactAttribute().Skip is { } reason) { Skip = reason; return; }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Vessel_TEST_EVENT_RABBIT")))
            Skip = "Requires an explicit disposable Vessel_TEST_EVENT_RABBIT broker.";
    }
}
