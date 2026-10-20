using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Writes and reads Play rows as the schema before the 1.2 <c>ReviewCompletion</c> migration held them, for migration tests
/// that seed an older database: the current EF model also maps columns those schemas do not have yet.
/// </summary>
internal static class LegacyPlayRows
{
    public static Task InsertAsync(DbContext db, Play play) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "InstrumentSource", "Direction", "Status", "Origin", "Title",
            "Plan", "Drawings", "PlanRevision", "Version", "CancelReason", "CreatedAtUtc", "UpdatedAtUtc", "PlannedAtUtc", "EndedAtUtc",
            "Review", "ReviewAdherence", "ReviewMistakeTag", "ReviewLesson", "ReviewNextAction", "ReviewRevision", "ReviewedAtUtc")
        VALUES ({play.Id}, {play.OwnerId}, {play.AccountId}, {play.VenueId}, {play.ContractId}, {play.InstrumentSource.ToString()}, {play.Direction},
            {play.Status.ToString()}, {play.Origin.ToString()}, {play.Title}, {play.Plan}::jsonb, {play.Drawings}::jsonb, {play.PlanRevision},
            {play.Version}, {play.CancelReason?.ToString()}, {play.CreatedAtUtc}, {play.UpdatedAtUtc}, {play.PlannedAtUtc}, {play.EndedAtUtc},
            {play.Review}, {play.ReviewAdherence}, {play.ReviewMistakeTag}, {play.ReviewLesson}, {play.ReviewNextAction}, {play.ReviewRevision},
            {play.ReviewedAtUtc})
        """);
}
