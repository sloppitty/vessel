using System.Net;
using System.Net.Http.Json;

namespace Vessel.Tests;

public sealed class PaperPlayApiTests : PaperPlayTestsValues
{

    [Fact]
    public async Task Paper_discovery_and_monitor_reject_without_touching_actual_adapters_or_persistence()
    {
        // No persistence connection and no account-discovery adapter: reaching either would produce 5xx, not 409.
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/monitor?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/API/MoNiToR?tradingMode=paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/venues/lighter/accounts?tradingMode=paper&address=0x1111111111111111111111111111111111111111")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/venues/lighter/accounts/credential?tradingMode=paper",
            new { address = "0x1111111111111111111111111111111111111111", token = "not-a-token" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/API/VeNuEs/lighter/AcCoUnTs?tradingMode=paper&address=0x1111111111111111111111111111111111111111")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/API/AcTiViTy/ReTrOsPeCtIvE?tradingMode=paper&accountId=11111111-1111-1111-1111-111111111111&operationId=actual")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/API/PlAyS/ReTrOsPeCtIvE?tradingMode=paper", new { })).StatusCode);
    }
}
