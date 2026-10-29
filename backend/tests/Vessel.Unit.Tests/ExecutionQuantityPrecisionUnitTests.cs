using Vessel.Application.Plays.Execution;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ExecutionQuantityPrecisionUnitTests
{

    [Theory]
    [InlineData("filled", false, true)]
    [InlineData("filled", true, false)]
    [InlineData("open", false, false)]
    [InlineData("canceled", false, false)]
    [InlineData("rejected", false, false)]
    public void Missing_executions_keeps_existing_decimal_callers_and_order_status_rules(string status, bool positionTpsl, bool missing)
    {
        var order = new ImportedOrder { Status = status, IsPositionTpsl = positionTpsl, OriginalSize = 2 };
        Assert.Equal(missing, ExecutionIntegrity.MissingExecutions(order, 1m));
        Assert.Equal(missing, ExecutionIntegrity.MissingExecutions(order, [new ImportedFill { Quantity = 1 }]));
        Assert.False(ExecutionIntegrity.MissingExecutions(order, 2m));
        Assert.False(ExecutionIntegrity.MissingExecutions(order, [new ImportedFill { Quantity = 1 }, new ImportedFill { Quantity = 1 }]));
    }
}
