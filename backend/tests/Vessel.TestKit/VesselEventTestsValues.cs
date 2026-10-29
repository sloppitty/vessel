using System.Text.Json;
using Vessel.Application.Events;

namespace Vessel.Tests;

public partial class VesselEventTestsValues
{

    internal static VesselEventDraft Draft(string key = "test", DateTimeOffset? detected = null) => new(
        "monitor.state-changed", Guid.CreateVersion7(), key, "info", detected ?? DateTimeOffset.UtcNow,
        detected ?? DateTimeOffset.UtcNow, new(), new("owner"), JsonSerializer.SerializeToElement(new { price = "001.2300" }));
}
