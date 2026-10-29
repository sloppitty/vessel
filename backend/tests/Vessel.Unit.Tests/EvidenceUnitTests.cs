using Vessel.Application.Common;
using Vessel.Application.Evidence;

namespace Vessel.Tests;

public sealed class EvidenceMarkupTests : EvidenceMarkupTestsValues
{
    private static string Rejects(ImageMarkup? markup) => Assert.Throws<ProblemException>(() => EvidenceMarkup.Normalize(markup)).Message;

    [Fact]
    public void Keeps_only_the_fields_each_kind_uses()
    {
        var normalized = EvidenceMarkup.Normalize(With(
            Pen() with { From = new(0, 0), Text = "dropped" },
            new("b", "marker", "#ffd23f", 16, [new(10, 10)]),
            new("c", "arrow", "#4ade80", 3, Points: [new(1, 1)], From: new(0, 0), To: new(800, 600)),
            new("d", "box", "#60a5fa", 3, From: new(5, 5), To: new(50, 40), Size: 9),
            new("e", "text", "#ffffff", Width: 3, At: new(10, 20), Size: 24, Text: "Retest here")));
        Assert.Equal(
            """{"width":800,"height":600,"shapes":[{"id":"a","kind":"pen","color":"#ff5c5c","width":4,"points":[{"x":1,"y":2},{"x":3.5,"y":4}]},{"id":"b","kind":"marker","color":"#ffd23f","width":16,"points":[{"x":10,"y":10}]},{"id":"c","kind":"arrow","color":"#4ade80","width":3,"from":{"x":0,"y":0},"to":{"x":800,"y":600}},{"id":"d","kind":"box","color":"#60a5fa","width":3,"from":{"x":5,"y":5},"to":{"x":50,"y":40}},{"id":"e","kind":"text","color":"#ffffff","at":{"x":10,"y":20},"size":24,"text":"Retest here"}]}""",
            EvidenceMarkup.Serialize(normalized));
        Assert.Equal(normalized.Shapes.Count, EvidenceMarkup.Read(EvidenceMarkup.Serialize(normalized))!.Shapes.Count);
    }

    [Fact]
    public void Rejects_invalid_markup()
    {
        Assert.Contains("required", Rejects(null));
        Assert.Contains("width and height", Rejects(new ImageMarkup(0, 600, [])));
        Assert.Contains("width and height", Rejects(new ImageMarkup(800, 20001, [])));
        Assert.Contains("200 shapes", Rejects(With([.. Enumerable.Range(0, 201).Select(i => Pen($"s{i}"))])));
        Assert.Contains("unique ID", Rejects(With(Pen(), Pen())));
        Assert.Contains("unique ID", Rejects(With(Pen("bad id"))));
        Assert.Contains("#rrggbb", Rejects(With(Pen() with { Color = "#FF5C5C" })));
        Assert.Contains("#rrggbb", Rejects(With(Pen() with { Color = "red" })));
        Assert.Contains("inside the image", Rejects(With(Pen() with { Points = [new(801, 0)] })));
        Assert.Contains("inside the image", Rejects(With(new MarkupShape("a", "box", "#ffffff", 3, From: new(-1, 0), To: new(5, 5)))));
        Assert.Contains("inside the image", Rejects(With(new MarkupShape("a", "arrow", "#ffffff", 3, From: new(1, 1)))));
        Assert.Contains("Stroke width", Rejects(With(Pen() with { Width = 0 })));
        Assert.Contains("Stroke width", Rejects(With(Pen() with { Width = null })));
        Assert.Contains("1 to 2000 points", Rejects(With(Pen() with { Points = [] })));
        Assert.Contains("1 to 2000 points", Rejects(With(Pen() with { Points = [.. Enumerable.Repeat(new MarkupPoint(1, 1), 2001)] })));
        Assert.Contains("Text size", Rejects(With(new MarkupShape("a", "text", "#ffffff", At: new(1, 1), Text: "x"))));
        Assert.Contains("control characters", Rejects(With(new MarkupShape("a", "text", "#ffffff", At: new(1, 1), Size: 20, Text: "line\nbreak"))));
        Assert.Contains("control characters", Rejects(With(new MarkupShape("a", "text", "#ffffff", At: new(1, 1), Size: 20, Text: "   "))));
        Assert.Contains("control characters", Rejects(With(new MarkupShape("a", "text", "#ffffff", At: new(1, 1), Size: 20, Text: new string('x', 281)))));
        Assert.Contains("kinds are", Rejects(With(Pen() with { Kind = "circle" })));
        Assert.Equal("Markup must be valid JSON.", Assert.Throws<ProblemException>(() => EvidenceMarkup.Parse("{")).Message);
    }
}
