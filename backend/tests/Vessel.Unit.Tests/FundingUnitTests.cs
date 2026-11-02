using Vessel.Application.Venues;

namespace Vessel.Tests;

public sealed class FundingUnitTests : FundingTestsValues
{

    [Fact]
    public void Adapter_boundary_rejects_conflicting_identities_and_out_of_window_payments()
    {
        var payment = new VenueFundingPayment("source", "BTC", "1", 1, "USDC", At, "{}");
        var result = new VenueFundingReadResult([payment, payment with { Amount = 2 }], At.AddDays(-1), At.AddDays(1), true, "Fixture");
        Assert.Throws<VenueReadException>(() => VenueFundingChecks.Validate(result, result.FromUtc, result.ToUtc));
        result = result with { Payments = [payment with { OccurredAtUtc = At.AddDays(2) }] };
        Assert.Throws<VenueReadException>(() => VenueFundingChecks.Validate(result, result.FromUtc, result.ToUtc));
    }
}
