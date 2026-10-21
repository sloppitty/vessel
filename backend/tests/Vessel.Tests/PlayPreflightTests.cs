using Vessel.Application.Common;
using Vessel.Application.Plays;

namespace Vessel.Tests;

/// <summary>Phase 1, 1.5: what the server keeps of the pre-flight, and what counts as recorded.</summary>
public sealed class PlayPreflightTests
{
    [Fact]
    public void An_empty_checklist_records_nothing_so_History_and_Review_agree()
    {
        Assert.Null(PlayPreflight.Normalize([]));
        Assert.Null(PlayPreflight.Normalize(null));
        // Stored before this rule, or with only checks this version does not know: not recorded either.
        Assert.Null(PlayPreflight.Read("[]"));
        Assert.Null(PlayPreflight.Read("[{\"code\":\"later-check\",\"outcome\":\"passed\"}]"));
        var stored = PlayPreflight.Serialize(PlayPreflight.Normalize([new("thesis", "skipped"), new("priced-stops", "passed")]));
        Assert.Equal(["priced-stops", "thesis"], PlayPreflight.Read(stored)!.Select(c => c.Code));
    }

    [Fact]
    public void Unknown_codes_outcomes_and_repeats_are_refused()
    {
        Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayPreflight.Normalize([new("vibes", "passed")])).StatusCode);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayPreflight.Normalize([new("thesis", "maybe")])).StatusCode);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayPreflight.Normalize([new("thesis", "passed"), new("thesis", "skipped")])).StatusCode);
    }
}
