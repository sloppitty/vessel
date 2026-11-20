namespace Vessel.Persistence;

/// <summary>
/// The connection pools of a host, built from the one connection string, and the only order in which a borrower may
/// hold a connection of one while it waits for another. Holding a connection while waiting for one from the same pool
/// can exhaust that pool, so a nested borrow always moves down this list and never back up:
/// <list type="number">
/// <item><see cref="Dedicated"/>: an unpooled connection for a session that holds a lock over other work (the paper
/// scheduler's tick). It may borrow from either pool.</item>
/// <item><see cref="Journal"/>: <see cref="VesselDbContext"/> and the units of work. A unit may borrow from the
/// directory pool (owner status, server-wide venue deadlines) while it holds its transaction.</item>
/// <item>The directory pool, the unkeyed <c>NpgsqlDataSource</c>: the identity contexts and the server-wide raw-SQL
/// stores. Its borrowers run their own statements and never borrow another connection while they hold one.</item>
/// </list>
/// A process holds at most the connection string's Maximum Pool Size in each of the two pools.
/// </summary>
public static class DatabasePools
{
    public const string Journal = "vessel-journal";
    public const string Dedicated = "vessel-dedicated";
}
