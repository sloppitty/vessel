using System.Data.Common;
using Vessel.Application.Common;

namespace Vessel.Tests;

public sealed class DatabaseBusyTests
{
    private sealed class FakeDbException(string state) : DbException("database error")
    {
        public override string SqlState => state;
    }

    [Theory]
    [InlineData("55P03", true)] // lock_timeout
    [InlineData("57014", true)] // statement_timeout
    [InlineData("40P01", false)] // deadlock: not a timeout
    [InlineData("23505", false)]
    public void Only_lock_and_statement_timeouts_are_busy_at_any_depth(string state, bool busy)
    {
        Assert.Equal(busy, DatabaseBusy.Is(new FakeDbException(state)));
        // EF and the use cases wrap provider errors.
        Assert.Equal(busy, DatabaseBusy.Is(new InvalidOperationException("wrapped", new Exception("inner", new FakeDbException(state)))));
    }

    [Fact]
    public void A_requested_cancellation_stays_a_cancellation()
    {
        Assert.False(DatabaseBusy.Is(new OperationCanceledException("cancelled", new FakeDbException("57014"))));
        Assert.False(DatabaseBusy.Is(new HttpRequestException("venue")));
    }
}
