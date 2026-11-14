using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tasks;

public enum TaskStatus
{
    NotStarted,
    InProgress,
    Completed,
    WaitingOnOthers,
    Deferred,
}

public enum TaskImportance
{
    Low,
    Normal,
    High,
}

public sealed record TaskList(TaskListId Id, string DisplayName)
{
    public bool IsOwner { get; init; } = true;

    public bool IsShared { get; init; }

    public string? WellKnownListName { get; init; }
}

public sealed record TodoTask(TaskId Id, string Title)
{
    public TaskStatus Status { get; init; } = TaskStatus.NotStarted;

    public TaskImportance Importance { get; init; } = TaskImportance.Normal;

    public string? BodyText { get; init; }

    public DateTimeTimeZone? DueDateTime { get; init; }

    public DateTimeTimeZone? StartDateTime { get; init; }

    public DateTimeTimeZone? CompletedDateTime { get; init; }

    public DateTimeTimeZone? ReminderDateTime { get; init; }

    public bool IsReminderOn { get; init; }

    public PatternedRecurrence? Recurrence { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public bool HasAttachments { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>Task create/patch payload. On patch, null members are left unchanged.</summary>
public sealed record TaskDraft
{
    public string? Title { get; init; }

    public TaskStatus? Status { get; init; }

    public TaskImportance? Importance { get; init; }

    public string? BodyText { get; init; }

    public DateTimeTimeZone? DueDateTime { get; init; }

    public DateTimeTimeZone? StartDateTime { get; init; }

    public DateTimeTimeZone? ReminderDateTime { get; init; }

    public bool? IsReminderOn { get; init; }

    public PatternedRecurrence? Recurrence { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>Explicitly clear the due date (Graph PATCH with <c>dueDateTime: null</c>).</summary>
    public bool ClearDueDateTime { get; init; }
}

public sealed record TaskAttachment(AttachmentId Id, string Name, string ContentType, long Size)
{
    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>A resumable upload (Graph <c>uploadSession</c>). <paramref name="Token"/> is adapter-opaque.</summary>
public sealed record UploadSession(string Token, DateTimeOffset ExpirationDateTime, long TotalSize);

/// <summary>Result of uploading a chunk: either more ranges are expected, or the attachment is complete.</summary>
public sealed record UploadProgress(IReadOnlyList<string> NextExpectedRanges, TaskAttachment? Completed);
