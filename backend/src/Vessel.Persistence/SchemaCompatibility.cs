using Microsoft.EntityFrameworkCore;
using Vessel.Application.Ownership;

namespace Vessel.Persistence;

public enum SchemaState { Current, Behind, Ahead }

/// <summary>
/// The result of <see cref="SchemaCompatibility.CheckAsync"/>: the first schema that does not match this image, with the
/// migration IDs that differ. <see cref="Reason"/> is for logs only; it names migration IDs, never connection details.
/// </summary>
public sealed record SchemaCheck(SchemaState State, string? Schema = null, IReadOnlyList<string>? Migrations = null)
{
    public static readonly SchemaCheck Current = new(SchemaState.Current);

    public string? Reason => State switch
    {
        SchemaState.Ahead => $"The {Schema} schema is newer than this image: it has applied migration(s) this image does not know " +
            $"({List()}). An older image was started on a newer database. Run the image that applied them, or restore the " +
            "backup taken before that upgrade.",
        SchemaState.Behind => $"The {Schema} schema is behind this image: {Migrations!.Count} migration(s) are not applied " +
            $"({List()}). Run the explicit migrate step.",
        _ => null
    };

    private string List() => Migrations!.Count <= 5 ? string.Join(", ", Migrations)
        : string.Join(", ", Migrations.Take(5)) + $" and {Migrations.Count - 5} more";
}

/// <summary>
/// One check for API readiness and worker start: every schema must have exactly the migrations compiled into this
/// image. Applied migrations this image does not know, in any schema (an older image on a newer schema, such as an
/// image rollback without a restore), are reported before migrations not yet applied. Throws when the database cannot
/// be read.
/// </summary>
public static class SchemaCompatibility
{
    public static async Task<SchemaCheck> CheckAsync(IEnumerable<(string Schema, DbContext Context)> schemas, CancellationToken ct)
    {
        SchemaCheck? behind = null;
        foreach (var (schema, context) in schemas)
        {
            var known = context.Database.GetMigrations().ToList();
            var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
            var unknown = applied.Except(known, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0) return new(SchemaState.Ahead, schema, unknown);
            var pending = known.Where(id => !applied.Contains(id)).ToList();
            if (pending.Count > 0) behind ??= new(SchemaState.Behind, schema, pending);
        }
        return behind ?? SchemaCheck.Current;
    }

    /// <summary>
    /// A journal context for this check alone, outside any owner scope (a worker has none at start): it reads the
    /// migration history and nothing owner-scoped, and any owner-filtered query on it throws.
    /// </summary>
    public static VesselDbContext JournalContext(string connection) =>
        new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(connection).Options, new NoOwner());

    private sealed class NoOwner : IJournalOwnerContext
    {
        public Guid OwnerId => throw new InvalidOperationException("The schema check reads no owner data.");
    }
}
