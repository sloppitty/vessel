
namespace Vessel.Tests;

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = new PostgresFactAttribute().Skip;
}
