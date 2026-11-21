using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;

namespace Nightware.Integration.Tests.Calendar;

/// <summary>Phase 13 calendars against the running stack: MKCALENDAR, PROPPATCH, events in a named calendar, DELETE.</summary>
public sealed class CalendarCollectionLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private LiveStack _stack = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    internal static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, string? json = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_second_calendar_is_created_recoloured_given_a_series_and_deleted()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var defaultCalendar = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/calendar");
        defaultCalendar["isDefaultCalendar"]!.GetValue<bool>().ShouldBeTrue();

        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/calendars", $$"""{"name":"Nightware {{_run}}"}""", HttpStatusCode.Created);
        var id = created["id"]!.GetValue<string>();
        try
        {
            var renamed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/calendars/{id}", $$"""{"name":"Nightware {{_run}} renamed","color":"lightGreen"}""");
            renamed["name"]!.GetValue<string>().ShouldBe($"Nightware {_run} renamed");
            renamed["color"]!.GetValue<string>().ShouldBe("lightGreen");

            var start = DateTime.UtcNow.Date.AddDays(1).AddHours(10);
            var series = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/calendars/{id}/events", $$$"""
                {"subject":"Series {{{_run}}}","start":{"dateTime":"{{{start:yyyy-MM-ddTHH:mm:ss}}}","timeZone":"UTC"},
                 "end":{"dateTime":"{{{start.AddMinutes(30):yyyy-MM-ddTHH:mm:ss}}}","timeZone":"UTC"},
                 "recurrence":{"pattern":{"type":"daily","interval":1},"range":{"type":"numbered","startDate":"{{{start:yyyy-MM-dd}}}","numberOfOccurrences":3}}
                """ + "}", HttpStatusCode.Created);
            series["recurrence"].ShouldNotBeNull();

            var listed = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/calendars/{id}/events");
            listed["value"]!.AsArray().Single()!["subject"]!.GetValue<string>().ShouldBe($"Series {_run}");
            var view = await SendAsync(alice, HttpMethod.Get,
                $"/v1.0/me/calendars/{id}/calendarView?startDateTime={start.AddHours(-1):yyyy-MM-ddTHH:mm:ssZ}&endDateTime={start.AddDays(5):yyyy-MM-ddTHH:mm:ssZ}");
            view["value"]!.AsArray().Count.ShouldBe(3);

            (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/calendars"))["value"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == id);
            var groups = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/calendarGroups");
            var groupId = groups["value"]!.AsArray().Single()!["id"]!.GetValue<string>();
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/calendarGroups/{groupId}/calendars"))["value"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == id);
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/calendars/{defaultCalendar["id"]!.GetValue<string>()}", expected: HttpStatusCode.BadRequest);
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/calendars/{id}", expected: HttpStatusCode.NoContent);
        }

        (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/calendars"))["value"]!.AsArray().ShouldNotContain(c => c!["id"]!.GetValue<string>() == id);
    }
}
