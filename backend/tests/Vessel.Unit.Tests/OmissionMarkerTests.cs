using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 7: omission markers are durable and deduplicated, and an order they name has known missing executions
/// whatever its status, whichever of the two arrived first.
/// </summary>
public sealed class OmissionMarkerTests
{

    private static ImportedOrder Order(string status, bool omitted = false) => new()
    {
        Id = Guid.NewGuid(), ContractId = "BTC", ExternalOrderId = "o", Side = "sell", OrderType = "Limit", OriginalSize = 2,
        RemainingSize = status == "filled" ? 0 : 1, Status = status, VenueStatus = status, ExecutionsOmitted = omitted
    };

    [Theory]
    [InlineData("filled")]
    [InlineData("canceled")]
    [InlineData("open")]
    public void An_order_with_an_omitted_execution_is_missing_executions_whatever_its_status(string status)
    {
        var fills = new[] { new ImportedFill { Quantity = 2 } };
        Assert.False(ExecutionIntegrity.MissingExecutions(Order(status), fills));
        Assert.True(ExecutionIntegrity.MissingExecutions(Order(status, omitted: true), fills));
    }

    private static ImportedOrder On(string contract, string? native, string id = "o") => new()
    {
        Id = Guid.NewGuid(), ContractId = contract, VenueContractId = native, ExternalOrderId = id, Side = "sell", OrderType = "Limit",
        OriginalSize = 2, RemainingSize = 1, Status = "canceled", VenueStatus = "canceled"
    };

    [Fact]
    public void A_marker_names_the_order_on_its_own_market_and_a_conflicting_native_market_fails_closed()
    {
        // Sol round 1. A recognized market: the canonical contract, with native markets that must agree.
        Assert.True(ExecutionOmission.Names("BTC", null, "o", On("BTC", null)));
        Assert.True(ExecutionOmission.Names("BTC", "1", "o", On("BTC", "1")));
        Assert.True(ExecutionOmission.Names("BTC", "1", "o", On("BTC", null)));
        Assert.Null(ExecutionOmission.Names("BTC", "2", "o", On("BTC", "1")));
        Assert.False(ExecutionOmission.Names("ETH", null, "o", On("BTC", null)));
        Assert.False(ExecutionOmission.Names("BTC", null, "other", On("BTC", null)));
        // A market Vessel does not recognize: its native ID, whichever field the order keeps it in.
        Assert.True(ExecutionOmission.Names(null, "SOL", "o", On("SOL", null)));
        Assert.True(ExecutionOmission.Names(null, "kPEPE", "o", On("1000PEPE", "kPEPE")));
        Assert.True(ExecutionOmission.Names(null, "7", "o", On("XYZ", "7")));
        Assert.False(ExecutionOmission.Names(null, "SOL", "o", On("BTC", null)));
        Assert.False(ExecutionOmission.Names(null, "7", "o", On("BTC", "1")));
        // No market at all: nothing rules an order out.
        Assert.True(ExecutionOmission.Names(null, null, "o", On("BTC", "1")));
    }
}
