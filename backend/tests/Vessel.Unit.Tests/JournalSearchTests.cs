using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>Phase 2, item 13: the search rules shared by the projection and the query.</summary>
public sealed class JournalSearchTests
{
    [Fact]
    public void Text_is_composed_folded_and_spaced_once_with_positions_kept()
    {
        // Full-width letters compose, line breaks and tabs become one space, invisible characters go, the ends are trimmed.
        Assert.Equal("BTC breakout retest", JournalSearch.Normalize("  ＢＴＣ\r\n\tbreak​out   retest \n"));
        // An unpaired surrogate cannot be normalized; it becomes the replacement character instead of failing the save.
        Assert.Equal("a�b", JournalSearch.Normalize("a\uD800b"));
        Assert.Equal("", JournalSearch.Normalize(" \n​ "));
        var text = JournalSearch.Normalize("ÉCLAIR Δέλτα 𐐀 Straße");
        var folded = JournalSearch.Fold(text);
        Assert.Equal(text.Length, folded.Length);
        Assert.Equal("éclair δέλτα 𐐨 straße", folded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("​a")]
    public void A_query_needs_two_characters(string? q) =>
        Assert.Equal(400, Assert.Throws<ProblemException>(() => JournalSearch.Query(q)).StatusCode);

    [Fact]
    public void Queries_are_bounded_folded_and_escaped_for_like()
    {
        Assert.Equal(("Ab", "ab"), JournalSearch.Query(" Ab "));
        Assert.Equal(100, JournalSearch.Query(new string('x', 100)).Query.Length);
        Assert.Equal(400, Assert.Throws<ProblemException>(() => JournalSearch.Query(new string('x', 101))).StatusCode);
        Assert.Equal(@"%50\%\_off\\x%", JournalSearch.LikePattern(@"50%_off\x"));
    }

    [Fact]
    public void An_excerpt_holds_the_first_match_with_its_position_and_never_splits_a_character()
    {
        var text = new string('a', 300) + " Needle " + new string('b', 300);
        var (excerpt, start, length) = JournalSearch.Excerpt(text, JournalSearch.Fold(text), "needle");
        Assert.StartsWith("…", excerpt);
        Assert.EndsWith("…", excerpt);
        Assert.InRange(excerpt.Length, JournalSearch.ExcerptLength, JournalSearch.ExcerptLength + 2);
        Assert.Equal("Needle", excerpt.Substring(start, length));
        // Near the start nothing is cut before it; the window still holds the match.
        var (head, at, _) = JournalSearch.Excerpt("Needle " + new string('c', 400), JournalSearch.Fold("Needle " + new string('c', 400)), "needle");
        Assert.Equal((0, false), (at, head.StartsWith('…')));
        // A window edge inside a surrogate pair moves outward.
        var pairs = string.Concat(Enumerable.Repeat("𐐀", 200)) + "needle" + string.Concat(Enumerable.Repeat("𐐀", 200));
        var (around, from, size) = JournalSearch.Excerpt(pairs, JournalSearch.Fold(pairs), "needle");
        Assert.Equal("needle", around.Substring(from, size));
        Assert.All(around.Trim('…').Chunk(2).Where(c => char.IsSurrogate(c[0])), c => Assert.True(char.IsSurrogatePair(c[0], c[1])));
    }

    [Fact]
    public void Plays_without_a_closed_result_have_no_figures_and_unindexed_ones_say_so()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Manual");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        var now = DateTimeOffset.UnixEpoch.AddDays(2);
        Assert.Equal(new PlayOutcomeFacts("active", null, null, null, null), JournalSearch.Outcome(play, null, now));
        play.Cancel(CancelReason.Missed, null, now);
        Assert.Equal(new PlayOutcomeFacts("cancelled", null, null, null, null), JournalSearch.Outcome(play, null, now));
        Assert.Equal(new PlayOutcomeSummaryDto("cancelled", null, null, null, JournalSearch.CancelledReason),
            JournalSearch.Dto(JournalSearch.Outcome(play, null, now)));
        Assert.Equal(new PlayOutcomeSummaryDto("not-indexed", null, null, null, JournalSearch.NotIndexedReason), JournalSearch.Dto(null));
        // Each field keeps its own band of positions, below the next field's.
        Assert.Equal("log", JournalSearch.FieldOf(JournalSearch.Position("log", 499)));
        Assert.True(JournalSearch.Position("nextAction", 0) < JournalSearch.Position("log", 0));
    }
}
