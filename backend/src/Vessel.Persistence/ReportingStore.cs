using Microsoft.EntityFrameworkCore;
using Vessel.Application.Reporting;
using Vessel.Domain.Reporting;

namespace Vessel.Persistence;

public sealed class ReportingStore(VesselDbContext db, UnitOfWork unitOfWork) : IReportingStore
{
    public Task<ReportingSettings?> SettingsAsync(CancellationToken ct) => db.ReportingSettings.AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task<ReportingSettings> SettingsForUpdateAsync(ReportingSettings initial, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        if (initial.OwnerId != db.CurrentOwnerId) throw new InvalidOperationException("Reporting settings belong to the current owner.");
        // A concurrent first write waits on the conflicting insert, then both read and lock the one committed row.
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO reporting_settings(""OwnerId"", ""TimeZone"", ""WeekStart"", ""UpdatedAtUtc"")
            VALUES({initial.OwnerId}, {initial.TimeZone}, {initial.WeekStart}, {initial.UpdatedAtUtc}) ON CONFLICT DO NOTHING", ct);
        return await db.ReportingSettings.FromSqlInterpolated(
            $@"SELECT * FROM reporting_settings WHERE ""OwnerId"" = {db.CurrentOwnerId} FOR UPDATE").SingleAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => unitOfWork.SaveChangesAsync(ct);
}
