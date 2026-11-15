using Nightware.Api.Features.Tasks.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class TodoDtoMapperTests
{
    [Theory]
    [InlineData("2026-06-05T17:30:00", "Australia/Sydney")]
    [InlineData("2026-06-05T00:00:00", "UTC")]
    [InlineData("2026-06-05T23:59:00", "Pacific Standard Time")]
    public void Due_dates_are_that_days_midnight_in_utc(string wallClock, string zone)
    {
        var task = new TodoTask(new TaskId("t"), "x") { DueDateTime = new DateTimeTimeZone(DateTime.Parse(wallClock, System.Globalization.CultureInfo.InvariantCulture), zone) };

        var json = GraphJson.ToObject(TodoDtoMapper.ToDto(task));

        json["dueDateTime"]!["dateTime"]!.GetValue<string>().ShouldBe("2026-06-05T00:00:00.0000000");
        json["dueDateTime"]!["timeZone"]!.GetValue<string>().ShouldBe("UTC");
    }

    [Fact]
    public void Reminders_keep_their_time_in_utc()
    {
        var task = new TodoTask(new TaskId("t"), "x") { IsReminderOn = true, ReminderDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 5, 9, 0, 0), "Australia/Sydney") };

        var json = GraphJson.ToObject(TodoDtoMapper.ToDto(task));

        json["reminderDateTime"]!["dateTime"]!.GetValue<string>().ShouldBe("2026-06-04T23:00:00.0000000");
        json["isReminderOn"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Absent_values_are_omitted_and_the_body_is_always_present()
    {
        var json = GraphJson.ToObject(TodoDtoMapper.ToDto(new TodoTask(new TaskId("t"), "x")));

        json.Select(p => p.Key).ShouldBe(["id", "title", "status", "importance", "isReminderOn", "hasAttachments", "body"], ignoreOrder: true);
        json["body"]!["content"]!.GetValue<string>().ShouldBe(string.Empty);
    }

    [Fact]
    public void Lists_without_a_well_known_name_are_none() =>
        TodoDtoMapper.ToDto(new TaskList(new TaskListId("l"), "Shared")).WellknownListName.ShouldBe("none");

    [Fact]
    public void Attachments_carry_their_odata_type()
    {
        var json = GraphJson.ToObject(TodoDtoMapper.ToDto(new TaskAttachment(new AttachmentId("a"), "n", "text/plain", 3)));

        json["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.taskFileAttachment");
        json.ContainsKey("lastModifiedDateTime").ShouldBeFalse();
    }
}
