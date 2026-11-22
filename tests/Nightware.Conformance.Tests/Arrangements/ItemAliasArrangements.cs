using Nightware.Application.Calendar.Calendars;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 19 aliases: an item's actions and parts on Graph's parent-scoped routes (<c>calendar/events/{id}/…</c>,
/// <c>calendars/{id}/events/{id}/…</c>, <c>mailFolders/{id}/messages/{id}/…</c>, <c>contactFolders/{id}/contacts/{id}/…</c>)
/// the default calendar's update and permissions, and the one calendar group by id. Each is arranged exactly as its base operation, so a pass shows
/// the alias reaches the same handling.
/// </summary>
public sealed class ItemAliasArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    /// <summary>The fixtures' calendar (calendars/AAMk-id-0004=) as the default, for the calendar/… aliases.</summary>
    private static void DefaultIsTheFixtures(PortFakes fakes) =>
        fakes.For<ICalendarStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success(
            [new EventCalendar(new CalendarId("AAMk-id-0004="), "Nightware ae6347") { IsDefault = true, CanEdit = true }]));

    private static readonly (string Alias, string Base, bool Default)[] Aliases =
    [
        ("calendar.default.events.accept", "calendar.events.accept", false),
        ("calendar.calendars.events.accept", "calendar.events.accept", false),
        ("calendar.default.events.tentatively-accept", "calendar.events.tentatively-accept", false),
        ("calendar.calendars.events.tentatively-accept", "calendar.events.tentatively-accept", false),
        ("calendar.default.events.decline", "calendar.events.decline", false),
        ("calendar.calendars.events.decline", "calendar.events.decline", false),
        ("calendar.default.events.cancel", "calendar.events.cancel", false),
        ("calendar.calendars.events.cancel", "calendar.events.cancel", false),
        ("calendar.default.events.forward", "calendar.events.forward", false),
        ("calendar.calendars.events.forward", "calendar.events.forward", false),
        ("calendar.default.events.instances", "calendar.events.instances", false),
        ("calendar.calendars.events.instances", "calendar.events.instances", false),
        ("calendar.default.events.snooze-reminder", "calendar.events.snooze-reminder", false),
        ("calendar.calendars.events.snooze-reminder", "calendar.events.snooze-reminder", false),
        ("calendar.default.events.dismiss-reminder", "calendar.events.dismiss-reminder", false),
        ("calendar.calendars.events.dismiss-reminder", "calendar.events.dismiss-reminder", false),
        ("calendar.default.events.attachments.list", "calendar.events.attachments.list", false),
        ("calendar.calendars.events.attachments.list", "calendar.events.attachments.list", false),
        ("calendar.default.events.attachments.get", "calendar.events.attachments.get", false),
        ("calendar.calendars.events.attachments.get", "calendar.events.attachments.get", false),
        ("calendar.default.events.attachments.create", "calendar.events.attachments.create", false),
        ("calendar.calendars.events.attachments.create", "calendar.events.attachments.create", false),
        ("calendar.default.events.attachments.delete", "calendar.events.attachments.delete", false),
        ("calendar.calendars.events.attachments.delete", "calendar.events.attachments.delete", false),
        ("calendar.default.events.attachments.value", "calendar.events.attachments.value", false),
        ("calendar.calendars.events.attachments.value", "calendar.events.attachments.value", false),
        ("calendar.default.events.attachments.create-upload-session", "calendar.events.attachments.create-upload-session", false),
        ("calendar.calendars.events.attachments.create-upload-session", "calendar.events.attachments.create-upload-session", false),
        ("mail.folders.messages.get-mime", "mail.messages.get-mime", false),
        ("mail.folders.messages.attachments.list", "mail.attachments.list", false),
        ("mail.folders.messages.attachments.get", "mail.attachments.get", false),
        ("mail.folders.messages.attachments.get-value", "mail.attachments.get-value", false),
        ("mail.folders.messages.attachments.create", "mail.attachments.create", false),
        ("mail.folders.messages.attachments.delete", "mail.attachments.delete", false),
        ("mail.folders.messages.attachments.create-upload-session", "mail.attachments.create-upload-session", false),
        ("mail.folders.messages.move", "mail.messages.move", false),
        ("mail.folders.messages.copy", "mail.messages.copy", false),
        ("mail.folders.messages.permanent-delete", "mail.messages.permanent-delete", false),
        ("mail.folders.messages.send-draft", "mail.messages.send-draft", false),
        ("mail.folders.messages.reply", "mail.messages.reply", false),
        ("mail.folders.messages.reply-all", "mail.messages.reply-all", false),
        ("mail.folders.messages.create-reply", "mail.messages.create-reply", false),
        ("mail.folders.messages.create-reply-all", "mail.messages.create-reply-all", false),
        ("mail.folders.messages.forward", "mail.messages.forward", false),
        ("mail.folders.messages.create-forward", "mail.messages.create-forward", false),
        ("contacts.folders.contacts.photo-get", "contacts.photo-get", false),
        ("contacts.folders.contacts.photo-put", "contacts.photo-put", false),
        ("calendar.default.update", "calendar.calendars.update", true),
        ("calendar.default.permissions.list", "calendar.permissions.list", true),
        ("calendar.default.permissions.create", "calendar.permissions.create", true),
        ("calendar.default.permissions.update", "calendar.permissions.update", true),
        ("calendar.default.permissions.delete", "calendar.permissions.delete", true),
    ];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        // Every other provider, found as the registry finds them; alias providers are left out (they'd ask this one).
        var bases = typeof(ItemAliasArrangements).Assembly.GetTypes()
            .Where(static t => typeof(IConformanceArrangements).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }
                && t != typeof(ItemAliasArrangements) && t != typeof(AliasArrangements))
            .SelectMany(static t => ((IConformanceArrangements)Activator.CreateInstance(t)!).Arrangements())
            .ToDictionary(static a => a.OperationId, StringComparer.Ordinal);
        // The one synthetic group needs no port.
        yield return new ConformanceArrangement("calendar.calendar-groups.get", static _ => { });

        foreach (var (alias, baseId, isDefault) in Aliases)
        {
            var original = bases[baseId];
            yield return original with
            {
                OperationId = alias,
                Arrange = isDefault ? fakes => { original.Arrange(fakes); DefaultIsTheFixtures(fakes); } : original.Arrange,
            };
        }
    }
}
