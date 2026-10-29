using System.Text.Json;
using Vessel.Application.Events;

namespace Vessel.Tests;

public sealed class VesselEventUnitTests : VesselEventTestsValues
{

    [Fact]
    public void Every_v1_type_has_a_string_cursor_and_exact_string_payload_contract()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var type in VesselEventValues.Types)
        {
            var draft = Draft() with { Type = type };
            var message = new VesselEvent(Guid.CreateVersion7(), type, 1, Guid.NewGuid(), "9007199254740993",
                draft.RunId, draft.DedupKey, draft.Severity, draft.OccurredAt, draft.DetectedAt, draft.Timeliness,
                draft.Relevance, null, draft.Subject, draft.Source, draft.Data);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(message, options));
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("sequence").ValueKind);
            Assert.Equal("9007199254740993", json.RootElement.GetProperty("sequence").GetString());
            Assert.Equal("001.2300", json.RootElement.GetProperty("data").GetProperty("price").GetString());
            Assert.Equal(7, message.Id.Version);
            Assert.Equal(type, json.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal("0.5", VesselEventValues.Exact(0.5000m));
        Assert.Equal("158456325028528675187087900670", VesselEventValues.Sum([decimal.MaxValue, decimal.MaxValue]));
        Assert.Equal("10.5", VesselEventValues.AveragePrice([(10m, 1m), (11m, 1m)]));
        Assert.False(VesselEventCursor.TryParse("-1", out _));
        Assert.False(VesselEventCursor.TryParse("1.0", out _));
        var epoch = Guid.NewGuid();
        Assert.True(VesselEventCursor.TryParse(VesselEventCursor.Format(epoch, 9007199254740993), out var cursor));
        Assert.Equal(new VesselEventCursor(epoch, 9007199254740993), cursor);
        Assert.True(VesselEventCursor.TryParse("0", out var start) && start == default);
    }
}
