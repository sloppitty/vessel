using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class WorkspaceReviewUnitTests
{

    [Fact]
    public void Domain_RecordSync_never_regresses_or_advances_settings_revision()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");
        var now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        account.RecordSync(now, "History"); account.RecordSync(now.AddHours(-1), "History");
        Assert.Equal(now, account.LastSyncedAtUtc); Assert.Equal(1, account.SettingsRevision);
        account.UpdateSettings("Rename", null, false); Assert.Equal(2, account.SettingsRevision);
        account.RecordSync(now.AddHours(1), "History"); Assert.Equal(2, account.SettingsRevision);
    }

}
