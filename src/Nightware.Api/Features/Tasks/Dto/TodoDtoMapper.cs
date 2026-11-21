using Nightware.Api.Features.Mail.Dto;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Tasks;

namespace Nightware.Api.Features.Tasks.Dto;

/// <summary>Domain → Graph To Do DTOs.</summary>
public static class TodoDtoMapper
{
    public static TodoTaskListDto ToDto(TaskList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new TodoTaskListDto
        {
            Id = list.Id.Value,
            DisplayName = list.DisplayName,
            IsOwner = list.IsOwner,
            IsShared = list.IsShared,
            WellknownListName = list.WellKnownListName ?? "none",
        };
    }

    /// <remarks>
    /// To Do's due and completion values are dates: Graph sends them as that day's midnight labelled
    /// <c>UTC</c>, and NightMail reads the wall clock as a local date. A value another client stored
    /// with a time or zone is reduced to its date in its own zone, so it lands on the same day.
    /// </remarks>
    public static TodoTaskDto ToDto(TodoTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new TodoTaskDto
        {
            Id = task.Id.Value,
            Title = task.Title,
            Status = task.Status,
            Importance = task.Importance,
            IsReminderOn = task.IsReminderOn,
            HasAttachments = task.HasAttachments,
            Categories = task.Categories.Count > 0 ? task.Categories : null,
            CreatedDateTime = task.CreatedDateTime,
            LastModifiedDateTime = task.LastModifiedDateTime,
            Body = new ItemBodyDto { Content = task.BodyText ?? string.Empty, ContentType = "text" },
            DueDateTime = Date(task.DueDateTime),
            StartDateTime = Date(task.StartDateTime),
            CompletedDateTime = Date(task.CompletedDateTime),
            ReminderDateTime = task.ReminderDateTime is { } reminder
                ? new DateTimeTimeZoneDto { DateTime = GraphTimeZone.ToInstant(reminder.DateTime, reminder.TimeZone).UtcDateTime, TimeZone = GraphTimeZone.Utc }
                : null,
        };
    }

    public static TaskFileAttachmentDto ToDto(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new TaskFileAttachmentDto
        {
            Id = attachment.Id.Value,
            Name = attachment.Name,
            ContentType = attachment.ContentType,
            Size = attachment.Size,
            LastModifiedDateTime = attachment.LastModifiedDateTime,
        };
    }

    private static DateTimeTimeZoneDto? Date(DateTimeTimeZone? value) =>
        value is null ? null : new DateTimeTimeZoneDto { DateTime = value.DateTime.Date, TimeZone = GraphTimeZone.Utc };
}
