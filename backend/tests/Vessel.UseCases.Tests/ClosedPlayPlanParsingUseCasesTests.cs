using Vessel.Application.Plays;

namespace Vessel.Tests;

/// <summary>Plans parsed once per stored document give the same answers as parsing every time.</summary>
public sealed class ClosedPlayPlanParsingUseCasesTests : ClosedPlayPlanParsingTestsValues
{

    [Fact]
    public void Play_dto_parses_once_and_serializes_exactly_as_before()
    {
        var play = Facts(30).Play;
        var dto = PlayService.ToDto(play);
        var previous = new PlayDto(PlayService.Summary(play), PlayDocuments.Read(play.Plan), PlayDocuments.ReadDrawings(play.Drawings),
            play.Review, play.Answers);
        Assert.Equal(Json(previous), Json(dto));
        Assert.Equal("long", dto.Summary.Direction);
    }
}
