using System.Text.Json;

using Nightware.Api.Graph.Json;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Graph.Json;

public sealed class GraphJsonTests
{
    private sealed record Sample(
        DateTimeOffset Instant,
        DateTime WallClock,
        TimeOnly Time,
        FlagStatus Status,
        string? Missing,
        string Html);

    [Fact]
    public void Writes_graph_shapes()
    {
        var sample = new Sample(
            new DateTimeOffset(2026, 1, 1, 20, 0, 0, 123, TimeSpan.FromHours(10)),
            new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified).AddTicks(1234567),
            new TimeOnly(8, 30),
            FlagStatus.NotFlagged,
            null,
            "<p>Café & co</p>");

        var json = JsonSerializer.Serialize(sample, GraphJson.Options);

        json.ShouldBe("{\"instant\":\"2026-01-01T10:00:00Z\",\"wallClock\":\"2026-01-01T10:00:00.1234567\",\"time\":\"08:30:00.0000000\",\"status\":\"notFlagged\",\"missing\":null,\"html\":\"<p>Café & co</p>\"}");
    }

    [Fact]
    public void Reads_case_insensitively_with_graph_casing_quirks()
    {
        var value = JsonSerializer.Deserialize<Sample>(
            "{\"Instant\":\"2026-01-01T10:00:00.000Z\",\"WALLCLOCK\":\"2026-01-01T10:00:00\",\"time\":\"08:30:00.0000000\",\"status\":\"Flagged\",\"html\":\"\"}",
            GraphJson.Options)!;

        value.Instant.ShouldBe(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        value.WallClock.ShouldBe(new DateTime(2026, 1, 1, 10, 0, 0));
        value.WallClock.Kind.ShouldBe(DateTimeKind.Unspecified);
        value.Status.ShouldBe(FlagStatus.Flagged);
    }

    [Fact]
    public void Reads_wall_clock_with_zulu_or_offset_as_unspecified()
    {
        JsonSerializer.Deserialize<DateTime>("\"2026-01-01T10:00:00Z\"", GraphJson.Options).ShouldBe(new DateTime(2026, 1, 1, 10, 0, 0));
        JsonSerializer.Deserialize<DateTime>("\"2026-01-01T10:00:00.123+00:00\"", GraphJson.Options).Kind.ShouldBe(DateTimeKind.Unspecified);
    }

    [Theory]
    [InlineData("\"not a date\"", typeof(DateTimeOffset))]
    [InlineData("\"not a date\"", typeof(DateTime))]
    [InlineData("\"25:99\"", typeof(TimeOnly))]
    [InlineData("1", typeof(FlagStatus))]
    public void Rejects_malformed_values(string json, Type type)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, type, GraphJson.Options));
    }

    [Fact]
    public void ToObject_uses_the_runtime_type()
    {
        object value = new Sample(DateTimeOffset.UnixEpoch, DateTime.UnixEpoch, TimeOnly.MinValue, FlagStatus.Complete, "x", "y");

        GraphJson.ToObject(value)["status"]!.GetValue<string>().ShouldBe("complete");
        Should.Throw<InvalidOperationException>(() => GraphJson.ToObject(42));
    }

    [Fact]
    public void Format_helpers_match_the_converters()
    {
        GraphDateTimeFormat.Instant(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(-5))).ShouldBe("2026-05-01T05:00:00Z");
        GraphDateTimeFormat.WallClock(new DateTime(2026, 5, 1, 9, 0, 0)).ShouldBe("2026-05-01T09:00:00.0000000");
    }
}
