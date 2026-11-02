using Vessel.Application.Common;
using Vessel.Application.Plays;

namespace Vessel.Tests;

public sealed class LossLimitPreflightTests
{

    [Fact]
    public void A_cooldown_note_belongs_to_a_reached_loss_limit_only_and_an_empty_one_is_none()
    {
        var checks = PlayPreflight.Normalize([new("loss-limit", "skipped", "  Walk away until Monday.  "), new("thesis", "passed", " ")]);
        Assert.Equal(["thesis", "loss-limit"], checks!.Select(c => c.Code));
        Assert.Equal("Walk away until Monday.", checks![1].Note);
        Assert.Null(checks[0].Note);
        Assert.DoesNotContain("note", PlayPreflight.Serialize([new("thesis", "passed")]));
        foreach (var invalid in new PreflightCheckDto[]
                 {
                     new("thesis", "passed", "A note"), new("loss-limit", "passed", "A note"), new("loss-limit", "skipped", new string('x', 501)),
                     new("loss-limit", "skipped", "Bell\a"),
                 })
            Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayPreflight.Normalize([invalid])).StatusCode);
    }

}
