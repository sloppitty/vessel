using Vessel.Application.Evidence;

namespace Vessel.Tests;

public partial class EvidenceMarkupTestsValues
{
    internal static MarkupShape Pen(string id = "a") => new(id, "pen", "#ff5c5c", 4, [new(1, 2), new(3.5, 4)]);
    internal static ImageMarkup With(params MarkupShape[] shapes) => new(800, 600, shapes);
}
