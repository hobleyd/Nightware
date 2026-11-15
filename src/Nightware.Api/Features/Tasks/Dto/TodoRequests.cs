using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Api.Features.Tasks.Dto;

/// <summary>
/// Reads To Do request bodies. Bodies are bound as <see cref="JsonObject"/> so a PATCH can tell an
/// explicit <c>null</c> (clear) from an absent property (leave). Properties NightMail Server cannot
/// store are ignored, as Graph ignores read-only ones; a known property with a bad value is a 400.
/// </summary>
public static class TodoRequests
{
    /// <summary>A <c>todoTask</c> from a POST or PATCH body.</summary>
    public static Result<TaskDraft> ReadTask(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A JSON body is required.");
        }

        try
        {
            var draft = new TaskDraft
            {
                Title = Get(body, "title")?.GetValue<string>(),
                Status = Enum<Domain.Tasks.TaskStatus>(body, "status"),
                Importance = Enum<TaskImportance>(body, "importance"),
                BodyText = Get(body, "body")?["content"]?.GetValue<string>(),
                DueDateTime = DateTimeOf(body, "dueDateTime"),
                StartDateTime = DateTimeOf(body, "startDateTime"),
                ReminderDateTime = DateTimeOf(body, "reminderDateTime"),
                IsReminderOn = Get(body, "isReminderOn")?.GetValue<bool>(),
                ClearDueDateTime = Has(body, "dueDateTime") && Get(body, "dueDateTime") is null,
            };
            return draft;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Error.Validation($"The task in the request body is not valid: {e.Message}");
        }
    }

    /// <summary>A <c>taskFileAttachment</c> with inline <c>contentBytes</c>.</summary>
    public static Result<NewAttachment> ReadAttachment(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A JSON body is required.");
        }

        if (Get(body, "@odata.type")?.GetValue<string>() is { } type && !string.Equals(type, TaskFileAttachmentDto.ODataTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation($"Only {TaskFileAttachmentDto.ODataTypeName} attachments are supported.", "@odata.type");
        }

        try
        {
            var name = Get(body, "name")?.GetValue<string>() ?? string.Empty;
            var contentType = Get(body, "contentType")?.GetValue<string>() is { Length: > 0 } ct ? ct : "application/octet-stream";
            var bytes = Get(body, "contentBytes")?.GetValue<string>() is { } base64
                ? Convert.FromBase64String(base64)
                : null;
            return bytes is null
                ? Error.Validation("contentBytes is required.", "contentBytes")
                : new NewAttachment(name, contentType, bytes);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return Error.Validation("contentBytes is not valid base64.", "contentBytes");
        }
    }

    /// <summary>The <c>attachmentItem</c> of a <c>createUploadSession</c> body.</summary>
    public static Result<(string Name, string? ContentType, long Size)> ReadUploadRequest(JsonObject? body)
    {
        if (Get(body, "attachmentItem") is not JsonObject item)
        {
            return Error.Validation("attachmentItem is required.", "attachmentItem");
        }

        if (Get(item, "attachmentType")?.GetValue<string>() is { } kind && !string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Only file attachments can be uploaded.", "attachmentType");
        }

        try
        {
            return (Get(item, "name")?.GetValue<string>() ?? string.Empty, Get(item, "contentType")?.GetValue<string>(), Get(item, "size")?.GetValue<long>() ?? 0);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return Error.Validation("attachmentItem is not valid.", "attachmentItem");
        }
    }

    private static T? Enum<T>(JsonObject body, string name)
        where T : struct, Enum =>
        Get(body, name)?.GetValue<string>() is { } text
            ? System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && text.All(char.IsAsciiLetter)
                ? value
                : throw new FormatException($"'{text}' is not a valid {name}.")
            : null;

    private static DateTimeTimeZone? DateTimeOf(JsonObject body, string name)
    {
        if (Get(body, name) is not JsonObject value)
        {
            return null;
        }

        var dto = value.Deserialize<DateTimeTimeZoneDto>(GraphJson.Options) ?? throw new FormatException($"{name} is not valid.");
        return new DateTimeTimeZone(dto.DateTime, string.IsNullOrWhiteSpace(dto.TimeZone) ? "UTC" : dto.TimeZone);
    }

    private static bool Has(JsonObject body, string name) => body.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Get(JsonObject? body, string name) =>
        body?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
