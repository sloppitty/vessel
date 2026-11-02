using Vessel.Application.Monitoring;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

public sealed class MonitorAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Admission_is_durable_and_duplicate_commands_cannot_restart_completed_work()
    {
        var row = new MonitorAccount();
        var request = Guid.NewGuid();
        row.Admit(request, "manual", Now);
        Assert.Equal("pending", row.ManualState);
        Assert.True(row.RetryAfter(Now) > 0);
        Assert.True(row.Claim(request, "first", Now, MonitorOptions.Lease));
        Assert.False(row.Claim(request, "second", Now, MonitorOptions.Lease));
        row.Complete(Now.AddSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), true);
        Assert.False(row.Claim(request, "first", Now.AddHours(1), MonitorOptions.Lease));
        Assert.Equal(60, row.RetryAfter(Now.AddSeconds(2)));
        Assert.Equal(1, row.RetryAfter(Now.AddSeconds(61)));
        Assert.Equal(0, row.RetryAfter(Now.AddSeconds(62)));
    }

    [Fact]
    public void Failed_attempts_also_start_cooldown_and_count_toward_stale()
    {
        var row = new MonitorAccount();
        for (var i = 0; i < 3; i++)
        {
            var at = Now.AddMinutes(i * 10);
            row.Admit(Guid.NewGuid(), "monitor", at);
            Assert.True(row.Claim(row.RequestId!.Value, "worker", at, MonitorOptions.Lease));
            row.Complete(at.AddSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(1), false);
            Assert.Equal(60, row.RetryAfter(at.AddSeconds(1)));
        }
        Assert.Equal("stale", row.State);
        Assert.Equal(3, row.ConsecutiveFailures);
        Assert.Null(row.LastSuccessAtUtc);
    }

    [Fact]
    public void Lease_recovery_increments_generation_and_fences_old_result()
    {
        var row = new MonitorAccount();
        var request = Guid.NewGuid();
        row.Admit(request, "manual", Now);
        Assert.True(row.Claim(request, "crashed", Now, MonitorOptions.Lease));
        var generation = row.Generation;
        Assert.True(row.Owns(generation, "crashed", Now.AddMinutes(4)));
        Assert.False(row.Owns(generation, "crashed", Now.AddMinutes(5)));
        Assert.True(row.Claim(request, "recovery", Now.AddMinutes(5), MonitorOptions.Lease));
        Assert.Equal(generation + 1, row.Generation);
        Assert.False(row.Owns(generation, "crashed", Now.AddMinutes(6)));
        Assert.True(row.Owns(row.Generation, "recovery", Now.AddMinutes(6)));
    }

    [Fact]
    public void Delayed_command_with_an_old_request_id_cannot_claim_new_request()
    {
        var row = new MonitorAccount();
        var old = Guid.NewGuid();
        row.Admit(old, "manual", Now);
        row.Claim(old, "worker", Now, MonitorOptions.Lease);
        row.Complete(Now, TimeSpan.Zero, TimeSpan.FromMinutes(10), true);
        row.Admit(Guid.NewGuid(), "manual", Now.AddSeconds(1));
        Assert.False(row.Claim(old, "worker", Now.AddSeconds(1), MonitorOptions.Lease));
    }

    [Fact]
    public void Credential_error_suspends_scheduling_until_revision_changes()
    {
        var row = new MonitorAccount();
        row.Admit(Guid.NewGuid(), "manual", Now);
        row.Claim(row.RequestId!.Value, "worker", Now, MonitorOptions.Lease);
        row.Complete(Now, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), false, credentialError: true);
        Assert.Equal("credential-error", row.State);
        Assert.Null(row.NextRunAtUtc);
        Assert.Equal(60, row.RetryAfter(Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Interval_cannot_bypass_product_bounds(int minutes) =>
        Assert.Throws<InvalidOperationException>(() => new MonitorOptions { Interval = TimeSpan.FromMinutes(minutes) }.Validate());

    [Fact]
    public void Delay_preserves_retry_steps_and_ten_percent_jitter_bounds()
    {
        var options = new MonitorOptions();
        Assert.Equal(TimeSpan.FromMinutes(1), options.NextDelay(1, 1));
        Assert.Equal(TimeSpan.FromMinutes(3), options.NextDelay(2, 0));
        Assert.Equal(TimeSpan.FromMinutes(9), options.NextDelay(0, 0));
        Assert.Equal(TimeSpan.FromMinutes(11), options.NextDelay(3, 1));
    }

    [Fact]
    public void Review_L4_manual_cooldown_longer_than_interval_is_rejected()
    {
        var options = new MonitorOptions { Interval = TimeSpan.FromMinutes(1), ManualCooldown = TimeSpan.FromMinutes(2) };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.ManualCooldown = TimeSpan.FromMinutes(1);
        options.Validate();
    }

    [Fact]
    public void Review_b_state_revision_advances_only_when_the_state_changes()
    {
        var row = new MonitorAccount();
        Assert.False(row.SetState("idle"));
        Assert.True(row.SetState("live"));
        Assert.False(row.SetState("live"));
        Assert.True(row.SetState("degraded"));
        Assert.True(row.SetState("live"));
        Assert.Equal(3, row.StateRevision);
        // Fencing is separate: a claim changes the lease generation, not the state identity.
        row.Admit(Guid.NewGuid(), "manual", DateTimeOffset.UtcNow);
        Assert.True(row.Claim(row.RequestId!.Value, "worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.Equal(3, row.StateRevision);
    }

    [Fact]
    public void Review_L4_scheduled_admission_ignores_the_manual_cooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new MonitorAccount { NextManualAllowedAtUtc = now.AddMinutes(1) };
        Assert.Throws<InvalidOperationException>(() => row.Admit(Guid.NewGuid(), "manual", now));
        row.Admit(Guid.NewGuid(), "monitor", now, scheduled: true);
        Assert.Equal("pending", row.ManualState);
    }
}
