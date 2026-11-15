using System.Text.Json.Serialization;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Domain.Tasks;

using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Api.Features.Tasks.Dto;

/// <summary>Graph <c>todoTaskList</c>, trimmed to what NightMail reads plus Graph's ownership flags.</summary>
public sealed record TodoTaskListDto
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public bool IsOwner { get; init; }

    public bool IsShared { get; init; }

    /// <summary><c>defaultList</c> or <c>none</c>.</summary>
    public required string WellknownListName { get; init; }
}

/// <summary>Graph <c>todoTask</c>.</summary>
/// <remarks>
/// <c>createdDateTime</c>/<c>lastModifiedDateTime</c> carry seven fractional digits like Graph's To Do
/// service (not the whole seconds of mail). Members Graph omits when unset are omitted.
/// </remarks>
public sealed record TodoTaskDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public TaskStatus Status { get; init; }

    public TaskImportance Importance { get; init; }

    public bool IsReminderOn { get; init; }

    public bool HasAttachments { get; init; }

    [JsonConverter(typeof(PreciseInstantConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedDateTime { get; init; }

    [JsonConverter(typeof(PreciseInstantConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public required ItemBodyDto Body { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? DueDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? StartDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? CompletedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? ReminderDateTime { get; init; }
}

/// <summary>Graph <c>taskFileAttachment</c> (metadata only; bytes come from <c>$value</c>).</summary>
public sealed record TaskFileAttachmentDto
{
    public const string ODataTypeName = "#microsoft.graph.taskFileAttachment";

    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = ODataTypeName;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ContentType { get; init; }

    public long Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>Graph <c>uploadSession</c>.</summary>
public sealed record UploadSessionDto
{
    public required string UploadUrl { get; init; }

    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? ExpirationDateTime { get; init; }

    public IReadOnlyList<string> NextExpectedRanges { get; init; } = ["0-"];
}
