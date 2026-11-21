using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using DomainTaskStatus = Nightware.Domain.Tasks.TaskStatus;
using IcalCalendar = Ical.Net.Calendar;

namespace Nightware.Adapters.Dav.Tasks.ICal;

/// <summary>A VTODO parsed from a resource, with its UID (needed to write it back).</summary>
public sealed record ParsedTodo(TodoTask Task, string? Uid)
{
    /// <summary>The parent's UID when this VTODO is a subtask (<c>RELATED-TO</c> with RELTYPE PARENT or none).</summary>
    public string? ParentUid { get; init; }

    /// <summary>When it was completed (COMPLETED), as an instant.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    public IReadOnlyList<LinkedResource> LinkedResources { get; init; } = [];
}

/// <summary>An <c>ATTACH</c> that points at a stored file.</summary>
public sealed record TodoAttachmentLink(Uri Uri, string FileName, string ContentType)
{
    public long? Size { get; init; }
}

/// <summary>
/// VTODO ↔ Domain <see cref="TodoTask"/> (Nextcloud Tasks / RFC 5545 §3.6.2).
/// <list type="bullet">
/// <item>STATUS: NEEDS-ACTION ↔ notStarted, IN-PROCESS ↔ inProgress, COMPLETED ↔ completed,
/// CANCELLED → deferred. Graph's waitingOnOthers/deferred are written as NEEDS-ACTION plus
/// <c>X-NIGHTWARE-TASK-STATUS</c> so they survive a round trip.</item>
/// <item>PRIORITY 1–4 ↔ high, 0/5 ↔ normal (written as no PRIORITY), 6–9 ↔ low (written as 9).</item>
/// <item>DUE/DTSTART at midnight are written as dates (VALUE=DATE), as Graph To Do sends them.</item>
/// <item>The first VALARM is the reminder: absolute triggers as-is, relative ones resolved against DTSTART or DUE.</item>
/// </list>
/// Updates go through <see cref="Apply"/>, which edits the stored VTODO in place and keeps
/// everything the Domain model does not carry.
/// </summary>
public sealed class VTodoCodec
{
    public const string StatusExtension = "X-NIGHTWARE-TASK-STATUS";

    /// <summary>One linked resource per property; the value is base64url JSON (no iCalendar quoting to get wrong).</summary>
    public const string LinkedResourceExtension = "X-NIGHTWARE-LINKED-RESOURCE";

    /// <summary>RFC 8607 §4.2: the attachment's file name.</summary>
    public const string FileNameParameter = "FILENAME";

    /// <summary>RFC 8607 §4.3: the attachment's size in octets.</summary>
    public const string SizeParameter = "SIZE";
    private readonly TimeProvider _timeProvider;

    public VTodoCodec(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <param name="href">Resource href relative to the DAV root; when given, tasks get <c>DavItemId.Resource(href)</c> ids.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered codec.")]
    public Result<IReadOnlyList<ParsedTodo>> Parse(string ics, string? href = null, string floatingTimeZone = GraphTimeZone.Utc)
    {
        if (href is not null && !DavHrefRules.IsSafeRelative(href))
        {
            return Error.Validation("The resource href is not a safe DAV path.", "href");
        }

        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var zones = new IcalZoneContext(loaded.Value, floatingTimeZone);
        var id = href is null ? new TaskId(string.Empty) : DavItemId.Resource(href).ToTaskId();
        IReadOnlyList<ParsedTodo> todos = loaded.Value.Todos
            .Where(t => t.RecurrenceIdentifier?.StartTime is null)
            .Select(t => new ParsedTodo(ToDomain(t, id, zones), t.Uid)
            {
                ParentUid = ParentOf(t),
                CompletedAt = t.Completed is { } completed ? completed.AsUtc : null,
                LinkedResources = ReadLinkedResources(t),
            })
            .ToList();
        return Result<IReadOnlyList<ParsedTodo>>.Success(todos);
    }

    /// <summary>A new VCALENDAR holding one VTODO built from <paramref name="draft"/>.</summary>
    public string Create(TaskDraft draft, string uid)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        var todo = new Todo { Uid = uid, Created = now, Summary = draft.Title ?? string.Empty };
        var calendar = new IcalCalendar { ProductId = IcalNetCalendarCodec.ProductId };
        calendar.Todos.Add(todo);
        ApplyFields(todo, draft with { Status = draft.Status ?? DomainTaskStatus.NotStarted });
        return Write(calendar);
    }

    /// <summary>A new subtask of <paramref name="parentUid"/>, written as Nextcloud Tasks writes one (<c>RELATED-TO</c>, no RELTYPE).</summary>
    public string CreateChild(TaskDraft draft, string uid, string parentUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentUid);
        return Edit(Create(draft, uid), todo => todo.AddProperty("RELATED-TO", parentUid)).Value;
    }

    /// <summary>Replaces the linked resources of the first VTODO (and bumps LAST-MODIFIED).</summary>
    public Result<string> SetLinkedResources(string existingIcs, IReadOnlyList<LinkedResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        return Edit(existingIcs, todo =>
        {
            RemoveProperty(todo, LinkedResourceExtension);
            foreach (var resource in resources)
            {
                todo.AddProperty(LinkedResourceExtension, System.Buffers.Text.Base64Url.EncodeToString(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resource, TodoJsonContext.Default.LinkedResource)));
            }

            todo.DtStamp = now;
            todo.LastModified = now;
        });
    }

    private static string? ParentOf(Todo todo) =>
        todo.Properties.Where(p => string.Equals(p.Name, "RELATED-TO", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Parameters.Get("RELTYPE") is not { Length: > 0 } type || string.Equals(type, "PARENT", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value?.ToString())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static List<LinkedResource> ReadLinkedResources(Todo todo)
    {
        var resources = new List<LinkedResource>();
        foreach (var property in todo.Properties.Where(p => string.Equals(p.Name, LinkedResourceExtension, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (property.Value?.ToString() is { } value
                    && System.Text.Json.JsonSerializer.Deserialize(System.Buffers.Text.Base64Url.DecodeFromChars(value), TodoJsonContext.Default.LinkedResource) is { } resource)
                {
                    resources.Add(resource);
                }
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                // Someone else's edit broke it; it is not a linked resource any more.
            }
        }

        return resources;
    }

    /// <summary>Serialises a whole task (e.g. a copy); prefer <see cref="Apply"/> for updates.</summary>
    public string Serialize(TodoTask task, string uid)
    {
        ArgumentNullException.ThrowIfNull(task);
        var draft = new TaskDraft
        {
            Title = task.Title,
            Status = task.Status,
            Importance = task.Importance,
            BodyText = task.BodyText,
            DueDateTime = task.DueDateTime,
            StartDateTime = task.StartDateTime,
            ReminderDateTime = task.ReminderDateTime,
            IsReminderOn = task.IsReminderOn,
            Recurrence = task.Recurrence,
            Categories = task.Categories,
        };
        var ics = Create(draft, uid);
        if (task.CompletedDateTime is null)
        {
            return ics;
        }

        // Keep the original completion time rather than "now".
        return Edit(ics, todo => todo.Completed = IcalNetCalendarCodec.Utc(GraphTimeZone.ToInstant(task.CompletedDateTime.DateTime, task.CompletedDateTime.TimeZone))).Value;
    }

    /// <summary>Applies the non-null members of <paramref name="patch"/> to the first VTODO of <paramref name="existingIcs"/>.</summary>
    public Result<string> Apply(string existingIcs, TaskDraft patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Edit(existingIcs, todo => ApplyFields(todo, patch));
    }

    /// <summary>
    /// The URI attachments (<c>ATTACH;VALUE=URI</c>) of the first VTODO, with the RFC 8607
    /// <c>FILENAME</c>/<c>SIZE</c> and RFC 5545 <c>FMTTYPE</c> parameters. Inline (BASE64) attachments are skipped.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered codec.")]
    public Result<IReadOnlyList<TodoAttachmentLink>> ReadAttachments(string ics)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var todo = loaded.Value.Todos.FirstOrDefault(t => t.RecurrenceIdentifier?.StartTime is null);
        if (todo is null)
        {
            return Error.NotFound("The resource holds no task.");
        }

        IReadOnlyList<TodoAttachmentLink> links = todo.Attachments
            .Where(a => a.Uri is { IsAbsoluteUri: true })
            .Select(a => new TodoAttachmentLink(
                a.Uri!,
                a.Parameters.Get(FileNameParameter) is { Length: > 0 } name ? name : FileNameOf(a.Uri!),
                string.IsNullOrWhiteSpace(a.FormatType) ? "application/octet-stream" : a.FormatType)
            {
                Size = long.TryParse(a.Parameters.Get(SizeParameter), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null,
            })
            .ToList();
        return Result<IReadOnlyList<TodoAttachmentLink>>.Success(links);
    }

    /// <summary>Adds a URI attachment to the first VTODO, keeping everything else (and bumping LAST-MODIFIED).</summary>
    public Result<string> AddAttachment(string existingIcs, TodoAttachmentLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        return Edit(existingIcs, todo =>
        {
            var attachment = new Attachment { Uri = link.Uri, FormatType = link.ContentType };
            attachment.Parameters.Set(FileNameParameter, link.FileName);
            if (link.Size is { } size)
            {
                attachment.Parameters.Set(SizeParameter, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            todo.Attachments.Add(attachment);
            todo.DtStamp = now;
            todo.LastModified = now;
        });
    }

    /// <summary>Removes the URI attachment <paramref name="uri"/> from the first VTODO (and bumps LAST-MODIFIED).</summary>
    public Result<string> RemoveAttachment(string existingIcs, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        return Edit(existingIcs, todo =>
        {
            foreach (var attachment in todo.Attachments.Where(a => a.Uri == uri).ToList())
            {
                todo.Attachments.Remove(attachment);
            }

            todo.DtStamp = now;
            todo.LastModified = now;
        });
    }

    private static string FileNameOf(Uri uri) =>
        Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]) is { Length: > 0 } last ? last : "attachment";

    private static Result<string> Edit(string ics, Action<Todo> edit)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var todo = loaded.Value.Todos.FirstOrDefault(t => t.RecurrenceIdentifier?.StartTime is null);
        if (todo is null)
        {
            return Error.NotFound("The resource holds no task.");
        }

        edit(todo);
        return Write(loaded.Value);
    }

    private void ApplyFields(Todo todo, TaskDraft patch)
    {
        var now = IcalNetCalendarCodec.Utc(_timeProvider.GetUtcNow());
        todo.DtStamp = now;
        todo.LastModified = now;
        if (patch.Title is not null)
        {
            todo.Summary = patch.Title;
        }

        if (patch.BodyText is not null)
        {
            todo.Description = patch.BodyText.Length == 0 ? null : patch.BodyText;
        }

        if (patch.Importance is { } importance)
        {
            todo.Priority = importance switch
            {
                TaskImportance.High => 1,
                TaskImportance.Low => 9,
                _ => 0,
            };
        }

        if (patch.Status is { } status)
        {
            ApplyStatus(todo, status, now);
        }

        if (patch.ClearDueDateTime)
        {
            todo.Due = null;
        }
        else if (patch.DueDateTime is { } due)
        {
            todo.Due = ToTaskDate(due);
        }

        if (patch.StartDateTime is { } start)
        {
            todo.DtStart = ToTaskDate(start);
        }

        if (patch.Recurrence is { } recurrence)
        {
            var anchor = todo.DtStart ?? todo.Due;
            var allDay = anchor is { HasTime: false };
            var zone = anchor is { HasTime: true, IsUtc: false, IsFloating: false } ? anchor.TzId : GraphTimeZone.Utc;
            todo.RecurrenceRule = IcalRecurrence.ToIcal(recurrence, allDay, anchor is null ? TimeOnly.MinValue : TimeOnly.FromDateTime(anchor.Value), zone);
        }

        if (patch.IsReminderOn == false)
        {
            todo.Alarms.Clear();
        }
        else if (patch.ReminderDateTime is { } reminder)
        {
            todo.Alarms.Clear();
            // Ical.Net writes an empty TRIGGER for absolute times unless VALUE=DATE-TIME is set explicitly.
            var trigger = new Trigger { DateTime = IcalNetCalendarCodec.Utc(GraphTimeZone.ToInstant(reminder.DateTime, reminder.TimeZone)) };
            trigger.Parameters.Set("VALUE", "DATE-TIME");
            todo.Alarms.Add(new Alarm
            {
                Action = AlarmAction.Display,
                Description = todo.Summary ?? "Reminder",
                Trigger = trigger,
            });
        }

        if (patch.Categories is { } categories)
        {
            todo.Categories = categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }
    }

    private static void ApplyStatus(Todo todo, DomainTaskStatus status, CalDateTime now)
    {
        RemoveProperty(todo, StatusExtension);
        switch (status)
        {
            case DomainTaskStatus.Completed:
                // Ical.Net's Status setter stamps COMPLETED with local "now" (floating); set it ourselves afterwards.
                var completedAt = string.Equals(todo.Status, TodoStatus.Completed, StringComparison.OrdinalIgnoreCase) ? todo.Completed : null;
                todo.Status = TodoStatus.Completed;
                todo.Completed = completedAt ?? now;
                todo.PercentComplete = 100;
                return;
            case DomainTaskStatus.InProgress:
                todo.Status = TodoStatus.InProcess;
                break;
            case DomainTaskStatus.WaitingOnOthers:
                todo.Status = TodoStatus.NeedsAction;
                todo.AddProperty(StatusExtension, "waitingOnOthers");
                break;
            case DomainTaskStatus.Deferred:
                todo.Status = TodoStatus.NeedsAction;
                todo.AddProperty(StatusExtension, "deferred");
                break;
            default:
                todo.Status = TodoStatus.NeedsAction;
                break;
        }

        todo.Completed = null;
        if (todo.PercentComplete == 100)
        {
            todo.PercentComplete = 0;
        }
    }

    private static TodoTask ToDomain(Todo todo, TaskId id, IcalZoneContext zones)
    {
        var due = todo.Due is { } d ? zones.ToDateTimeTimeZone(d) : null;
        var start = todo.DtStart is { } s ? zones.ToDateTimeTimeZone(s) : null;
        var anchor = todo.DtStart ?? todo.Due;
        var (reminderOn, reminder) = Reminder(todo, zones);
        return new TodoTask(id, todo.Summary ?? string.Empty)
        {
            Status = StatusOf(todo),
            Importance = todo.Priority switch
            {
                >= 1 and <= 4 => TaskImportance.High,
                >= 6 and <= 9 => TaskImportance.Low,
                _ => TaskImportance.Normal,
            },
            BodyText = todo.Description,
            DueDateTime = due,
            StartDateTime = start,
            CompletedDateTime = todo.Completed is { } completed ? DateTimeTimeZone.Utc(zones.ToInstant(completed)) : null,
            ReminderDateTime = reminder,
            IsReminderOn = reminderOn,
            Recurrence = todo.RecurrenceRule is { } rule && anchor is not null ? IcalRecurrence.ToDomain(rule, anchor, zones) : null,
            Categories = todo.Categories?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [],
            HasAttachments = todo.Attachments.Count > 0,
            CreatedDateTime = todo.Created is { } created ? zones.ToInstant(created) : null,
            LastModifiedDateTime = todo.LastModified is { } modified ? zones.ToInstant(modified) : null,
        };
    }

    private static DomainTaskStatus StatusOf(Todo todo)
    {
        var status = todo.Status?.ToUpperInvariant();
        if (status == TodoStatus.Completed || todo.Completed is not null && status is null)
        {
            return DomainTaskStatus.Completed;
        }

        if (status == TodoStatus.Cancelled)
        {
            return DomainTaskStatus.Deferred;
        }

        var extension = IcalNetCalendarCodec.Text(todo, StatusExtension);
        if (string.Equals(extension, "waitingOnOthers", StringComparison.OrdinalIgnoreCase))
        {
            return DomainTaskStatus.WaitingOnOthers;
        }

        if (string.Equals(extension, "deferred", StringComparison.OrdinalIgnoreCase))
        {
            return DomainTaskStatus.Deferred;
        }

        return status == TodoStatus.InProcess ? DomainTaskStatus.InProgress : DomainTaskStatus.NotStarted;
    }

    private static (bool On, DateTimeTimeZone? At) Reminder(Todo todo, IcalZoneContext zones)
    {
        var trigger = todo.Alarms.Select(a => a.Trigger).FirstOrDefault(t => t is not null);
        if (trigger is null)
        {
            return (false, null);
        }

        if (trigger.DateTime is { } absolute)
        {
            return (true, DateTimeTimeZone.Utc(zones.ToInstant(absolute)));
        }

        var relatedToEnd = string.Equals(trigger.Related, TriggerRelation.End, StringComparison.OrdinalIgnoreCase);
        var anchor = relatedToEnd ? todo.Due ?? todo.DtStart : todo.DtStart ?? todo.Due;
        if (anchor is null || trigger.Duration is not { } offset)
        {
            return (true, null);
        }

        return (true, DateTimeTimeZone.Utc(zones.ToInstant(anchor) + offset.ToTimeSpanUnspecified()));
    }

    /// <summary>Midnight → DATE value; otherwise a zoned (or UTC) date-time.</summary>
    private static CalDateTime ToTaskDate(DateTimeTimeZone value)
    {
        if (value.DateTime.TimeOfDay == TimeSpan.Zero)
        {
            return new CalDateTime(DateOnly.FromDateTime(value.DateTime));
        }

        var iana = ZoneWriter.IanaOf(value.TimeZone);
        return iana is null
            ? IcalNetCalendarCodec.Utc(GraphTimeZone.ToInstant(value.DateTime, value.TimeZone))
            : new CalDateTime(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified), iana, true);
    }

    private static void RemoveProperty(CalendarComponent component, string name)
    {
        foreach (var property in component.Properties.OfType<CalendarProperty>().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            component.Properties.Remove(property);
        }
    }

    private static string Write(IcalCalendar calendar)
    {
        AddTimeZones(calendar);
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    private static void AddTimeZones(IcalCalendar calendar)
    {
        var existing = calendar.TimeZones.Select(t => t.TzId).ToHashSet(StringComparer.Ordinal);
        var used = calendar.Todos
            .SelectMany(t => new[] { t.DtStart, t.Due })
            .Where(d => d is { HasTime: true, IsUtc: false, IsFloating: false })
            .GroupBy(d => d!.TzId!, StringComparer.Ordinal);
        foreach (var zone in used.Where(g => !existing.Contains(g.Key) && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(g.Key) is not null))
        {
            calendar.AddTimeZone(zone.Key, zone.Min(d => d!.Value).AddDays(-1), false);
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(LinkedResource))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TodoJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
