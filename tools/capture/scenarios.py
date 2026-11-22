"""Capture scenarios for the Graph-parity phases (ADR 0006).

Each phase is a list of (name, steps). A step is a dict:

  op          fixture id to record (omit for setup steps, which are not recorded)
  method      HTTP method
  path        Graph path starting /v1.0, with ${var} substitution
  url         absolute URL instead of path (a saved deltaLink or uploadUrl)
  record_path path written to the fixture instead of the URL's own ("{uploadUrl}")
  query       query options
  headers     extra request headers
  body        JSON body;  mime: {to, subject, body?} sent in Graph's MIME form (base64, text/plain);  binary: {"range": [first, last]} | {"photo": True}, contentType
  save        {var: "json.path"} read from the response
  follow_to   walk @odata.nextLink and save the final @odata.deltaLink in this var
  poll        {"until": "json.path", "tries": n, "interval": s} repeat until present
  expect      accepted statuses (default 200/201/202/204)
  auth        False for pre-authenticated URLs
  always      cleanup: runs after the scenario even when it failed
  requires    run option the step needs ("colleague", "notification_url")
  wait_notification  wait for a change notification and record it as op

Times (${now}, ${tomorrow10}, ${windowStart} …) and ${run} (a random tag) are
set by capture_graph.time_vars.
"""

SCOPES = {
    "User.Read", "Mail.ReadWrite", "Mail.Send", "MailboxSettings.ReadWrite",
    "Calendars.ReadWrite", "Contacts.ReadWrite", "Tasks.ReadWrite",
    # Phase 21: other users' profiles and reporting lines, groups and their members.
    "User.ReadBasic.All", "GroupMember.Read.All",
}

UTC = {"Prefer": 'outlook.timezone="UTC"'}
UPLOAD_SIZE = 3_670_016          # two chunks; the first a multiple of 320 KiB
FIRST_CHUNK = 327_680 * 6

# An 8x8 JPEG for the contact photo (made with sips).
TINY_JPEG = (
    "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAA"
    "A6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/7QA4UGhvdG9zaG9wIDMu"
    "MAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgACAAIAwEiAAIR"
    "AQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAAB"
    "fQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5"
    "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeo"
    "qaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMB"
    "AQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYS"
    "QVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNU"
    "VVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5"
    "usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMABAQEBAQEBgQEBgkGBgYJ"
    "DAkJCQkMDwwMDAwMDxIPDw8PDw8SEhISEhISEhUVFRUVFRkZGRkZHBwcHBwcHBwcHP/bAEMBBAUF"
    "BwcHDAcHDB0UEBQdHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0d"
    "HR0dHf/dAAQAAf/aAAwDAQACEQMRAD8A8looor9WPgT/2Q=="
)


def step(op, method, path=None, **kw):
    s = {"op": op, "method": method, **kw}
    if path is not None:
        s["path"] = path
    return s


def setup(method, path=None, **kw):
    return step(None, method, path, **kw)


SELF_MAIL = setup("POST", "/v1.0/me/sendMail", body={
    "message": {
        "subject": "Nightware capture ${run}",
        "body": {"contentType": "text", "content": "Nightware capture message ${run}."},
        "toRecipients": [{"emailAddress": {"address": "${me.mail}"}}],
    },
    "saveToSentItems": False,
})

FIND_SELF_MAIL = setup(
    "GET", "/v1.0/me/mailFolders/inbox/messages",
    query={"$filter": "subject eq 'Nightware capture ${run}'", "$select": "id"},
    poll={"until": "value[0].id", "tries": 40, "interval": 3},
    save={"msgId": "value[0].id"})

# ---------------------------------------------------------------- phase 11

BATCH = [
    step("batch.post", "POST", "/v1.0/$batch", body={"requests": [
        {"id": "1", "method": "GET", "url": "/me?$select=displayName,mail"},
        {"id": "2", "method": "GET", "url": "/me/mailFolders/inbox?$select=displayName,totalItemCount"},
        {"id": "3", "method": "GET", "url": "/me/mailFolders/nightware-no-such-folder"},
    ]}),
    step("batch.post-depends-on", "POST", "/v1.0/$batch", body={"requests": [
        {"id": "1", "method": "GET", "url": "/me/mailFolders/nightware-no-such-folder"},
        {"id": "2", "dependsOn": ["1"], "method": "GET", "url": "/me/mailFolders/inbox?$select=displayName"},
    ]}),
]

# ---------------------------------------------------------------- phase 12

MAIL = [
    setup("POST", "/v1.0/me/mailFolders", body={"displayName": "nw-capture-${run}"},
          save={"folderId": "id"}),
    SELF_MAIL,
    FIND_SELF_MAIL,
    step("mail.messages.update-flag", "PATCH", "/v1.0/me/messages/${msgId}",
         body={"flag": {"flagStatus": "flagged"}}),
    step("mail.messages.update-categories", "PATCH", "/v1.0/me/messages/${msgId}",
         body={"categories": ["Nightware ${run}"]}),
    step("mail.messages.update-importance", "PATCH", "/v1.0/me/messages/${msgId}",
         body={"importance": "high"}),
    step("mail.messages.copy", "POST", "/v1.0/me/messages/${msgId}/copy",
         body={"destinationId": "${folderId}"}),

    step("mail.master-categories.list", "GET", "/v1.0/me/outlook/masterCategories"),
    step("mail.master-categories.create", "POST", "/v1.0/me/outlook/masterCategories",
         body={"displayName": "Nightware ${run}", "color": "preset2"}, save={"catId": "id"}),
    step("mail.master-categories.get", "GET", "/v1.0/me/outlook/masterCategories/${catId}"),
    step("mail.master-categories.update", "PATCH", "/v1.0/me/outlook/masterCategories/${catId}",
         body={"color": "preset5"}),

    step("mail.rules.list", "GET", "/v1.0/me/mailFolders/inbox/messageRules"),
    step("mail.rules.create", "POST", "/v1.0/me/mailFolders/inbox/messageRules", body={
        "displayName": "Nightware ${run}", "sequence": 2, "isEnabled": True,
        "conditions": {"subjectContains": ["nw-capture-${run}"]},
        "exceptions": {"hasAttachments": True},
        "actions": {"moveToFolder": "${folderId}", "markAsRead": True, "stopProcessingRules": True},
    }, save={"ruleId": "id"}),
    step("mail.rules.get", "GET", "/v1.0/me/mailFolders/inbox/messageRules/${ruleId}"),
    step("mail.rules.update", "PATCH", "/v1.0/me/mailFolders/inbox/messageRules/${ruleId}",
         body={"displayName": "Nightware ${run} renamed", "isEnabled": False}),

    step("mail.folders.delta", "GET", "/v1.0/me/mailFolders/delta",
         query={"$select": "displayName,parentFolderId"}, follow_to="folderDelta"),
    setup("PATCH", "/v1.0/me/mailFolders/${folderId}", body={"displayName": "nw-capture-${run}-renamed"}),
    step("mail.folders.delta-changes", "GET", url="${folderDelta}"),

    setup("POST", "/v1.0/me/messages", body={
        "subject": "Nightware capture ${run} upload",
        "toRecipients": [{"emailAddress": {"address": "${me.mail}"}}],
    }, save={"draftId": "id"}),
    step("mail.attachments.create-upload-session", "POST",
         "/v1.0/me/messages/${draftId}/attachments/createUploadSession",
         body={"AttachmentItem": {"attachmentType": "file", "name": "capture.bin", "size": UPLOAD_SIZE}},
         save={"uploadUrl": "uploadUrl"}),
    step("mail.attachments.upload-session-put", "PUT", url="${uploadUrl}", record_path="{uploadUrl}",
         auth=False, binary={"range": [0, FIRST_CHUNK - 1]},
         headers={"Content-Range": f"bytes 0-{FIRST_CHUNK - 1}/{UPLOAD_SIZE}"}),
    step("mail.attachments.upload-session-put-final", "PUT", url="${uploadUrl}", record_path="{uploadUrl}",
         auth=False, binary={"range": [FIRST_CHUNK, UPLOAD_SIZE - 1]},
         headers={"Content-Range": f"bytes {FIRST_CHUNK}-{UPLOAD_SIZE - 1}/{UPLOAD_SIZE}"}),

    step("mail.master-categories.delete", "DELETE", "/v1.0/me/outlook/masterCategories/${catId}", always=True),
    step("mail.rules.delete", "DELETE", "/v1.0/me/mailFolders/inbox/messageRules/${ruleId}", always=True),
    setup("DELETE", "/v1.0/me/messages/${draftId}", always=True),
    setup("DELETE", "/v1.0/me/messages/${msgId}", always=True),
    setup("DELETE", "/v1.0/me/mailFolders/${folderId}", always=True),
]

# ---------------------------------------------------------------- phase 13

SERIES = {
    "subject": "Nightware capture ${run} series",
    "start": {"dateTime": "${tomorrow10}", "timeZone": "UTC"},
    "end": {"dateTime": "${tomorrow1030}", "timeZone": "UTC"},
    "isReminderOn": True, "reminderMinutesBeforeStart": 15,
    "recurrence": {
        "pattern": {"type": "weekly", "interval": 1, "daysOfWeek": ["${tomorrowWeekday}"]},
        "range": {"type": "numbered", "startDate": "${tomorrowDate}", "numberOfOccurrences": 4},
    },
}

CALENDAR = [
    step("calendar.calendar.get", "GET", "/v1.0/me/calendar", headers=UTC),
    step("calendar.calendars.list", "GET", "/v1.0/me/calendars", headers=UTC),
    step("calendar.calendars.create", "POST", "/v1.0/me/calendars",
         body={"name": "Nightware ${run}"}, save={"calId": "id"}),
    step("calendar.calendars.get", "GET", "/v1.0/me/calendars/${calId}"),
    step("calendar.calendars.update", "PATCH", "/v1.0/me/calendars/${calId}",
         body={"name": "Nightware ${run} renamed", "color": "lightGreen"}),
    step("calendar.calendars.events.create", "POST", "/v1.0/me/calendars/${calId}/events",
         headers=UTC, body=SERIES, save={"seriesId": "id"}),
    step("calendar.calendars.events.list", "GET", "/v1.0/me/calendars/${calId}/events",
         headers=UTC, query={"$top": "10"}),
    step("calendar.calendars.calendar-view", "GET", "/v1.0/me/calendars/${calId}/calendarView",
         headers=UTC, query={"startDateTime": "${windowStart}", "endDateTime": "${windowEnd}"}),
    step("calendar.events.list", "GET", "/v1.0/me/events", headers=UTC, query={
        "$top": "5", "$select": "subject,start,end,organizer", "$orderby": "start/dateTime desc"}),
    step("calendar.events.instances", "GET", "/v1.0/me/events/${seriesId}/instances",
         headers=UTC, query={"startDateTime": "${windowStart}", "endDateTime": "${windowEnd}"}),

    step("calendar.calendar-view.delta", "GET", "/v1.0/me/calendarView/delta", headers=UTC,
         query={"startDateTime": "${windowStart}", "endDateTime": "${windowEnd}"}, follow_to="calDelta"),
    setup("POST", "/v1.0/me/events", headers=UTC, body={
        "subject": "Nightware capture ${run} single",
        "start": {"dateTime": "${in1h}", "timeZone": "UTC"},
        "end": {"dateTime": "${in90m}", "timeZone": "UTC"},
        "isReminderOn": True, "reminderMinutesBeforeStart": 15,
    }, save={"evId": "id"}),
    step("calendar.calendar-view.delta-changes", "GET", url="${calDelta}", headers=UTC),

    step("calendar.reminder-view", "GET",
         "/v1.0/me/reminderView(startDateTime='${now}',endDateTime='${in1d}')", headers=UTC),
    step("calendar.events.snooze-reminder", "POST", "/v1.0/me/events/${evId}/snoozeReminder",
         body={"newReminderTime": {"dateTime": "${in30m}", "timeZone": "UTC"}}),
    step("calendar.events.dismiss-reminder", "POST", "/v1.0/me/events/${evId}/dismissReminder"),

    step("calendar.events.attachments.create", "POST", "/v1.0/me/events/${evId}/attachments", body={
        "@odata.type": "#microsoft.graph.fileAttachment", "name": "note.txt",
        "contentType": "text/plain", "contentBytes": "TmlnaHR3YXJlIGNhcHR1cmU=",
    }, save={"evAttId": "id"}),
    step("calendar.events.attachments.list", "GET", "/v1.0/me/events/${evId}/attachments"),
    step("calendar.events.attachments.get", "GET", "/v1.0/me/events/${evId}/attachments/${evAttId}"),
    step("calendar.events.attachments.delete", "DELETE", "/v1.0/me/events/${evId}/attachments/${evAttId}"),

    step("calendar.find-meeting-times", "POST", "/v1.0/me/findMeetingTimes", headers=UTC, body={
        "meetingDuration": "PT30M", "maxCandidates": 3, "isOrganizerOptional": False,
        "timeConstraint": {"activityDomain": "work", "timeSlots": [{
            "start": {"dateTime": "${tomorrow10}", "timeZone": "UTC"},
            "end": {"dateTime": "${in4d}", "timeZone": "UTC"},
        }]},
    }),

    step("calendar.calendar-groups.list", "GET", "/v1.0/me/calendarGroups", save={"groupId": "value[0].id"}),
    step("calendar.calendar-groups.calendars", "GET", "/v1.0/me/calendarGroups/${groupId}/calendars"),

    step("calendar.permissions.list", "GET", "/v1.0/me/calendars/${calId}/calendarPermissions"),
    step("calendar.permissions.create", "POST", "/v1.0/me/calendars/${calId}/calendarPermissions",
         requires="colleague", body={
             "emailAddress": {"name": "Colleague", "address": "${colleague}"},
             "isInsideOrganization": True, "isRemovable": True, "role": "read",
         }, save={"permId": "id"}),
    step("calendar.permissions.update", "PATCH",
         "/v1.0/me/calendars/${calId}/calendarPermissions/${permId}", requires="colleague",
         body={"role": "write"}),
    step("calendar.permissions.delete", "DELETE",
         "/v1.0/me/calendars/${calId}/calendarPermissions/${permId}", requires="colleague"),

    setup("DELETE", "/v1.0/me/events/${evId}", always=True),
    step("calendar.calendars.delete", "DELETE", "/v1.0/me/calendars/${calId}", always=True),
]

# ---------------------------------------------------------------- phase 14

FULL_CONTACT = {
    "givenName": "Pat", "surname": "Capture", "middleName": "Q", "nickName": "Patty",
    "title": "Dr", "jobTitle": "Engineer", "department": "R&D", "companyName": "Example Ltd",
    "emailAddresses": [{"name": "Pat Capture", "address": "pat.capture@example.com"}],
    "businessPhones": ["+61 2 5550 0100"], "homePhones": ["+61 2 5550 0101"],
    "mobilePhone": "+61 400 555 010",
    "businessAddress": {"street": "1 Example St", "city": "Sydney", "state": "NSW",
                        "postalCode": "2000", "countryOrRegion": "Australia"},
    "homeAddress": {"street": "2 Sample Rd", "city": "Sydney", "state": "NSW",
                    "postalCode": "2001", "countryOrRegion": "Australia"},
    "birthday": "1990-05-17T00:00:00Z", "personalNotes": "Nightware capture ${run}",
    "imAddresses": ["pat@im.example.com"], "categories": ["Nightware ${run}"],
    "businessHomePage": "https://example.com/pat",
}

CONTACTS = [
    step("contacts.folders.list", "GET", "/v1.0/me/contactFolders"),
    step("contacts.folders.create", "POST", "/v1.0/me/contactFolders",
         body={"displayName": "Nightware ${run}"}, save={"cfId": "id"}),
    step("contacts.folders.get", "GET", "/v1.0/me/contactFolders/${cfId}"),
    step("contacts.folders.update", "PATCH", "/v1.0/me/contactFolders/${cfId}",
         body={"displayName": "Nightware ${run} renamed"}),
    step("contacts.folders.child-folders.list", "GET", "/v1.0/me/contactFolders/${cfId}/childFolders"),
    step("contacts.folders.delta", "GET", "/v1.0/me/contactFolders/delta"),

    step("contacts.delta", "GET", "/v1.0/me/contactFolders/${cfId}/contacts/delta", follow_to="cDelta"),
    step("contacts.folders.contacts.create", "POST", "/v1.0/me/contactFolders/${cfId}/contacts",
         body={"givenName": "Sam", "surname": "Folder",
               "emailAddresses": [{"name": "Sam Folder", "address": "sam.folder@example.com"}]},
         save={"fcId": "id"}),
    step("contacts.folders.contacts.list", "GET", "/v1.0/me/contactFolders/${cfId}/contacts"),
    step("contacts.delta-changes", "GET", url="${cDelta}"),

    step("contacts.create", "POST", "/v1.0/me/contacts", body=FULL_CONTACT, save={"cId": "id"}),
    step("contacts.get", "GET", "/v1.0/me/contacts/${cId}"),
    step("contacts.update", "PATCH", "/v1.0/me/contacts/${cId}",
         body={"companyName": "Example Pty Ltd", "businessPhones": ["+61 2 5550 0199"]}),
    step("contacts.photo-put", "PUT", "/v1.0/me/contacts/${cId}/photo/$value",
         binary={"photo": True, "contentType": "image/jpeg"}),
    step("contacts.photo-get", "GET", "/v1.0/me/contacts/${cId}/photo/$value",
         headers={"Accept": "image/jpeg"}),
    step("contacts.list-full", "GET", "/v1.0/me/contacts", query={"$top": "5"}),

    step("contacts.delete", "DELETE", "/v1.0/me/contacts/${cId}", always=True),
    step("contacts.folders.delete", "DELETE", "/v1.0/me/contactFolders/${cfId}", always=True),
]

# ---------------------------------------------------------------- phase 15

TASKS = "/v1.0/me/todo/lists/${listId}/tasks"

TODO = [
    step("tasks.lists.create", "POST", "/v1.0/me/todo/lists",
         body={"displayName": "Nightware ${run}"}, save={"listId": "id"}),
    step("tasks.lists.get", "GET", "/v1.0/me/todo/lists/${listId}"),
    step("tasks.lists.update", "PATCH", "/v1.0/me/todo/lists/${listId}",
         body={"displayName": "Nightware ${run} renamed"}),
    step("tasks.lists.delta", "GET", "/v1.0/me/todo/lists/delta", follow_to="listDelta"),

    step("tasks.tasks.delta", "GET", TASKS + "/delta", follow_to="taskDelta"),
    setup("POST", TASKS, body={
        "title": "Nightware capture ${run}", "importance": "high",
        "dueDateTime": {"dateTime": "${dueDate}", "timeZone": "UTC"},
    }, save={"taskId": "id"}),
    step("tasks.tasks.get", "GET", TASKS + "/${taskId}"),

    step("tasks.checklist-items.create", "POST", TASKS + "/${taskId}/checklistItems",
         body={"displayName": "Step one"}, save={"ciId": "id"}),
    setup("POST", TASKS + "/${taskId}/checklistItems", body={"displayName": "Step two"}),
    step("tasks.checklist-items.list", "GET", TASKS + "/${taskId}/checklistItems"),
    step("tasks.checklist-items.update", "PATCH", TASKS + "/${taskId}/checklistItems/${ciId}",
         body={"isChecked": True}),
    step("tasks.checklist-items.delete", "DELETE", TASKS + "/${taskId}/checklistItems/${ciId}"),

    step("tasks.linked-resources.create", "POST", TASKS + "/${taskId}/linkedResources", body={
        "webUrl": "https://example.com/items/1", "applicationName": "Nightware capture",
        "displayName": "Item 1", "externalId": "item-1",
    }, save={"lrId": "id"}),
    step("tasks.linked-resources.list", "GET", TASKS + "/${taskId}/linkedResources"),
    step("tasks.linked-resources.update", "PATCH", TASKS + "/${taskId}/linkedResources/${lrId}",
         body={"displayName": "Item 1 renamed"}),
    step("tasks.linked-resources.delete", "DELETE", TASKS + "/${taskId}/linkedResources/${lrId}"),

    setup("POST", TASKS + "/${taskId}/attachments", body={
        "@odata.type": "#microsoft.graph.taskFileAttachment", "name": "note.txt",
        "contentType": "text/plain", "contentBytes": "TmlnaHR3YXJlIGNhcHR1cmU=",
    }, save={"taId": "id"}),
    step("tasks.attachments.get", "GET", TASKS + "/${taskId}/attachments/${taId}"),
    step("tasks.attachments.delete", "DELETE", TASKS + "/${taskId}/attachments/${taId}"),

    setup("PATCH", TASKS + "/${taskId}", body={"status": "completed"}),
    step("tasks.tasks.delta-changes", "GET", url="${taskDelta}"),
    step("tasks.tasks.delete", "DELETE", TASKS + "/${taskId}"),

    step("tasks.lists.delete", "DELETE", "/v1.0/me/todo/lists/${listId}", always=True),
]

# ---------------------------------------------------------------- phase 16

def subscription(op, resource, var):
    return step(op, "POST", "/v1.0/subscriptions", requires="notification_url", body={
        "changeType": "created,updated,deleted",
        "notificationUrl": "${notificationUrl}",
        "resource": resource,
        "expirationDateTime": "${expiry1h}",
        "clientState": "${clientState}",
    }, save={var: "id"})


NOTIFICATIONS = [
    subscription("subscriptions.create", "me/mailFolders('Inbox')/messages", "subId"),
    dict(SELF_MAIL, requires="notification_url"),
    {"op": "subscriptions.notification", "wait_notification": True, "timeout": 180,
     "requires": "notification_url"},
    step("subscriptions.list", "GET", "/v1.0/subscriptions", requires="notification_url"),
    step("subscriptions.get", "GET", "/v1.0/subscriptions/${subId}", requires="notification_url"),
    step("subscriptions.update", "PATCH", "/v1.0/subscriptions/${subId}", requires="notification_url",
         body={"expirationDateTime": "${expiry2h}"}),

    subscription("subscriptions.create-events", "me/events", "subEvId"),
    subscription("subscriptions.create-contacts", "me/contacts", "subCoId"),
    setup("POST", "/v1.0/me/todo/lists", requires="notification_url",
          body={"displayName": "Nightware ${run} subscriptions"}, save={"listId": "id"}),
    subscription("subscriptions.create-tasks", "me/todo/lists/${listId}/tasks", "subTaId"),

    dict(FIND_SELF_MAIL, requires="notification_url"),
    step("subscriptions.delete", "DELETE", "/v1.0/subscriptions/${subId}", always=True),
    setup("DELETE", "/v1.0/subscriptions/${subEvId}", always=True),
    setup("DELETE", "/v1.0/subscriptions/${subCoId}", always=True),
    setup("DELETE", "/v1.0/subscriptions/${subTaId}", always=True),
    setup("DELETE", "/v1.0/me/todo/lists/${listId}", always=True),
    setup("DELETE", "/v1.0/me/messages/${msgId}", always=True),
]

# ---------------------------------------------------------------- phase 18

SETTINGS = [
    step("mailbox-settings.automatic-replies.get", "GET", "/v1.0/me/mailboxSettings/automaticRepliesSetting"),
    step("mailbox-settings.time-zone.get", "GET", "/v1.0/me/mailboxSettings/timeZone"),
    step("mailbox-settings.language.get", "GET", "/v1.0/me/mailboxSettings/language"),
    step("mailbox-settings.working-hours.get", "GET", "/v1.0/me/mailboxSettings/workingHours"),
    step("mailbox-settings.date-format.get", "GET", "/v1.0/me/mailboxSettings/dateFormat"),
    step("mailbox-settings.time-format.get", "GET", "/v1.0/me/mailboxSettings/timeFormat"),
    step("outlook.supported-time-zones", "GET", "/v1.0/me/outlook/supportedTimeZones"),
    step("outlook.supported-time-zones-standard", "GET",
         "/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')"),
    step("outlook.supported-languages", "GET", "/v1.0/me/outlook/supportedLanguages"),
]

EVENT = {
    "subject": "Nightware capture ${run} alias",
    "start": {"dateTime": "${tomorrow10}", "timeZone": "UTC"},
    "end": {"dateTime": "${tomorrow1030}", "timeZone": "UTC"},
}

ALIASES = [
    SELF_MAIL, FIND_SELF_MAIL,
    step("mail.folders.messages.get", "GET", "/v1.0/me/mailFolders/inbox/messages/${msgId}"),
    step("mail.folders.messages.update", "PATCH", "/v1.0/me/mailFolders/inbox/messages/${msgId}", body={"isRead": True}),
    step("mail.folders.messages.delete", "DELETE", "/v1.0/me/mailFolders/inbox/messages/${msgId}"),

    setup("GET", "/v1.0/me/calendar", query={"$select": "id"}, save={"calId": "id"}),
    step("calendar.default.events.create", "POST", "/v1.0/me/calendar/events", headers=UTC, body=EVENT, save={"evId": "id"}),
    step("calendar.default.events.list", "GET", "/v1.0/me/calendar/events", headers=UTC, query={"$top": "5"}),
    step("calendar.default.calendar-view", "GET", "/v1.0/me/calendar/calendarView",
         headers=UTC, query={"startDateTime": "${windowStart}", "endDateTime": "${windowEnd}"}),
    step("calendar.default.events.get", "GET", "/v1.0/me/calendar/events/${evId}", headers=UTC),
    step("calendar.calendars.events.get", "GET", "/v1.0/me/calendars/${calId}/events/${evId}", headers=UTC),
    step("calendar.default.events.update", "PATCH", "/v1.0/me/calendar/events/${evId}", headers=UTC,
         body={"subject": "Nightware capture ${run} alias renamed"}),
    step("calendar.calendars.events.update", "PATCH", "/v1.0/me/calendars/${calId}/events/${evId}", headers=UTC,
         body={"subject": "Nightware capture ${run} alias renamed again"}),
    step("calendar.calendars.events.delete", "DELETE", "/v1.0/me/calendars/${calId}/events/${evId}", always=True),
    setup("POST", "/v1.0/me/calendar/events", headers=UTC, body=EVENT, save={"evId": "id"}),
    step("calendar.default.events.delete", "DELETE", "/v1.0/me/calendar/events/${evId}", always=True),

    setup("POST", "/v1.0/me/contacts", body={"givenName": "Nightware", "surname": "Capture ${run}"},
          save={"cId": "id", "cfId": "parentFolderId"}),
    step("contacts.folders.contacts.get", "GET", "/v1.0/me/contactFolders/${cfId}/contacts/${cId}"),
    step("contacts.folders.contacts.update", "PATCH", "/v1.0/me/contactFolders/${cfId}/contacts/${cId}", body={"jobTitle": "Tester"}),
    step("contacts.folders.contacts.delete", "DELETE", "/v1.0/me/contactFolders/${cfId}/contacts/${cId}", always=True),
]

TODO_ITEMS = [
    setup("POST", "/v1.0/me/todo/lists", body={"displayName": "Nightware ${run} items"}, save={"listId": "id"}),
    setup("POST", TASKS, body={"title": "Nightware capture ${run}"}, save={"taskId": "id"}),
    setup("POST", TASKS + "/${taskId}/checklistItems", body={"displayName": "Step one"}, save={"ciId": "id"}),
    step("tasks.checklist-items.get", "GET", TASKS + "/${taskId}/checklistItems/${ciId}"),
    setup("POST", TASKS + "/${taskId}/linkedResources", body={
        "webUrl": "https://example.com/items/1", "applicationName": "Nightware capture",
        "displayName": "Item 1", "externalId": "item-1",
    }, save={"lrId": "id"}),
    step("tasks.linked-resources.get", "GET", TASKS + "/${taskId}/linkedResources/${lrId}"),
    setup("DELETE", "/v1.0/me/todo/lists/${listId}", always=True),
]

FOLDERS = [
    step("mail.folders.create", "POST", "/v1.0/me/mailFolders", body={"displayName": "Nightware capture ${run}", "isHidden": False},
         save={"folderId": "id"}),
    step("mail.folders.copy", "POST", "/v1.0/me/mailFolders/${folderId}/copy", body={"destinationId": "inbox"}, save={"copyId": "id"}),
    setup("DELETE", "/v1.0/me/mailFolders/${copyId}", always=True),
    setup("DELETE", "/v1.0/me/mailFolders/${folderId}", always=True),
]

MIME = [
    step("mail.messages.create-mime", "POST", "/v1.0/me/messages", mime={"to": "${me.mail}", "subject": "Nightware capture ${run} MIME draft"}, save={"mimeDraft": "id"}),
    setup("DELETE", "/v1.0/me/messages/${mimeDraft}", always=True),
    step("mail.messages.send-mime", "POST", "/v1.0/me/sendMail", mime={"to": "${me.mail}", "subject": "Nightware capture ${run} MIME sent"}),
]

EVENT_UPLOAD = [
    setup("POST", "/v1.0/me/events", headers=UTC, body={
        "subject": "Nightware capture ${run} event upload",
        "start": {"dateTime": "${tomorrow10}", "timeZone": "UTC"},
        "end": {"dateTime": "${tomorrow1030}", "timeZone": "UTC"},
    }, save={"evId": "id"}),
    step("calendar.events.attachments.create-upload-session", "POST",
         "/v1.0/me/events/${evId}/attachments/createUploadSession",
         body={"AttachmentItem": {"attachmentType": "file", "name": "capture.bin", "size": UPLOAD_SIZE}},
         save={"uploadUrl": "uploadUrl"}),
    step("calendar.events.attachments.upload-session-put", "PUT", url="${uploadUrl}", record_path="{uploadUrl}",
         auth=False, binary={"range": [0, FIRST_CHUNK - 1]},
         headers={"Content-Range": f"bytes 0-{FIRST_CHUNK - 1}/{UPLOAD_SIZE}"}),
    step("calendar.events.attachments.upload-session-put-final", "PUT", url="${uploadUrl}", record_path="{uploadUrl}",
         auth=False, binary={"range": [FIRST_CHUNK, UPLOAD_SIZE - 1]},
         headers={"Content-Range": f"bytes {FIRST_CHUNK}-{UPLOAD_SIZE - 1}/{UPLOAD_SIZE}"}),
    setup("GET", "/v1.0/me/events/${evId}/attachments", query={"$select": "id"}, save={"attId": "value[0].id"}),
    step("calendar.events.attachments.value", "GET", "/v1.0/me/events/${evId}/attachments/${attId}/$value"),
    setup("DELETE", "/v1.0/me/events/${evId}", always=True),
]

# Phase 19 aliases: a representative step per family. Every alias's fixture is its base operation's exchange on
# the alias path (same handler), so these captures confirm Graph answers the alias as it does the base.
ITEM_ALIASES = [
    setup("GET", "/v1.0/me/calendar", query={"$select": "id,name"}, save={"calId": "id", "calName": "name"}),
    step("calendar.default.update", "PATCH", "/v1.0/me/calendar", body={"name": "${calName}"}),
    step("calendar.default.permissions.list", "GET", "/v1.0/me/calendar/calendarPermissions"),
    setup("GET", "/v1.0/me/calendarGroups", query={"$top": "1"}, save={"groupId": "value[0].id"}),
    step("calendar.calendar-groups.get", "GET", "/v1.0/me/calendarGroups/${groupId}"),
    setup("POST", "/v1.0/me/events", headers=UTC, body=EVENT, save={"evId": "id"}),
    step("calendar.calendars.events.attachments.create", "POST", "/v1.0/me/calendars/${calId}/events/${evId}/attachments",
         body={"@odata.type": "#microsoft.graph.fileAttachment", "name": "notes.txt", "contentBytes": "aGVsbG8="}, save={"attId": "id"}),
    step("calendar.default.events.attachments.list", "GET", "/v1.0/me/calendar/events/${evId}/attachments"),
    step("calendar.default.events.attachments.value", "GET", "/v1.0/me/calendar/events/${evId}/attachments/${attId}/$value"),
    step("calendar.calendars.events.attachments.delete", "DELETE", "/v1.0/me/calendars/${calId}/events/${evId}/attachments/${attId}"),
    step("calendar.default.events.dismiss-reminder", "POST", "/v1.0/me/calendar/events/${evId}/dismissReminder"),
    setup("DELETE", "/v1.0/me/events/${evId}", always=True),
    SELF_MAIL, FIND_SELF_MAIL,
    step("mail.folders.messages.get-mime", "GET", "/v1.0/me/mailFolders/inbox/messages/${msgId}/$value"),
    step("mail.folders.messages.attachments.list", "GET", "/v1.0/me/mailFolders/inbox/messages/${msgId}/attachments"),
    step("mail.folders.messages.move", "POST", "/v1.0/me/mailFolders/inbox/messages/${msgId}/move",
         body={"destinationId": "deleteditems"}, save={"movedId": "id"}),
    setup("DELETE", "/v1.0/me/messages/${movedId}", always=True),
]

SMALL = [
    step("rooms.room-lists.list", "GET", "/v1.0/places/microsoft.graph.roomList", save={"listMail": "value[0].emailAddress"}),
    step("rooms.room-lists.rooms", "GET", "/v1.0/places/${listMail}/microsoft.graph.roomlist/rooms"),
    SELF_MAIL, FIND_SELF_MAIL,
    step("mail.translate-exchange-ids", "POST", "/v1.0/me/translateExchangeIds",
         body={"inputIds": ["${msgId}"], "sourceIdType": "restId", "targetIdType": "restImmutableEntryId"}),
]

# ---------------------------------------------------------------- phase 21
# The test mailbox needs a manager, a direct report and a group in the tenant for these to record more than 404s.
DIRECTORY = [
    step("directory.me.manager.get", "GET", "/v1.0/me/manager", expect=[200, 404]),
    step("directory.me.direct-reports.list", "GET", "/v1.0/me/directReports"),
    step("directory.me.member-of.list", "GET", "/v1.0/me/memberOf"),
    step("directory.me.transitive-member-of.list", "GET", "/v1.0/me/transitiveMemberOf"),
    step("directory.users.manager.get", "GET", "/v1.0/users/${colleague}/manager", query={"$select": "id,displayName,mail"},
         requires="colleague", expect=[200, 404]),
    step("directory.users.direct-reports.list", "GET", "/v1.0/users/${colleague}/directReports", query={"$top": "1"}, requires="colleague"),
    step("directory.users.member-of.list", "GET", "/v1.0/users/${colleague}/memberOf",
         query={"$select": "displayName,mailEnabled,securityEnabled"}, requires="colleague"),
    step("directory.users.transitive-member-of.list", "GET", "/v1.0/users/${colleague}/transitiveMemberOf",
         query={"$select": "displayName,mailEnabled,securityEnabled"}, requires="colleague"),
    step("directory.groups.list", "GET", "/v1.0/groups", save={"groupId": "value[0].id"}),
    step("directory.groups.get", "GET", "/v1.0/groups/${groupId}"),
    step("directory.groups.members.list", "GET", "/v1.0/groups/${groupId}/members", query={"$select": "id,mail"}),
    step("directory.groups.transitive-members.list", "GET", "/v1.0/groups/${groupId}/transitiveMembers", query={"$select": "id,mail"}),
    step("directory.organization.list", "GET", "/v1.0/organization", save={"orgId": "value[0].id"}),
    step("directory.organization.get", "GET", "/v1.0/organization/${orgId}"),
]

PHOTOS = [
    step("directory.me.photo.get", "GET", "/v1.0/me/photo"),
    step("directory.me.photo.put", "PUT", "/v1.0/me/photo/$value", binary={"photo": True, "contentType": "image/jpeg"}),
    step("directory.me.photo.patch", "PATCH", "/v1.0/me/photo/$value", binary={"photo": True, "contentType": "image/jpeg"}),
    step("directory.me.photo-value", "GET", "/v1.0/me/photo/$value"),
    setup("GET", "/v1.0/me", query={"$select": "userPrincipalName"}, save={"upn": "userPrincipalName"}),
    step("directory.users.photo.get", "GET", "/v1.0/users/${upn}/photo"),
    step("directory.users.photo.put", "PUT", "/v1.0/users/${upn}/photo/$value", binary={"photo": True, "contentType": "image/jpeg"}),
    step("directory.users.photo.patch", "PATCH", "/v1.0/users/${upn}/photo/$value", binary={"photo": True, "contentType": "image/jpeg"}),
    step("directory.users.photo.delete", "DELETE", "/v1.0/users/${upn}/photo/$value"),
    setup("PUT", "/v1.0/me/photo/$value", binary={"photo": True, "contentType": "image/jpeg"}),
    step("directory.me.photo.delete", "DELETE", "/v1.0/me/photo/$value", always=True),
]

COUNTS = [
    step("mail.messages.count", "GET", "/v1.0/me/messages/$count", headers={"Accept": "text/plain"}),
    step("mail.folders.messages.count", "GET", "/v1.0/me/mailFolders/inbox/messages/$count",
         headers={"Accept": "text/plain"}, query={"$filter": "isRead eq false"}),
    step("calendar.events.count", "GET", "/v1.0/me/events/$count", headers={"Accept": "text/plain"}),
    step("calendar.default.events.count", "GET", "/v1.0/me/calendar/events/$count", headers={"Accept": "text/plain"}),
    setup("GET", "/v1.0/me/calendar", query={"$select": "id"}, save={"calId": "id"}),
    step("calendar.calendars.events.count", "GET", "/v1.0/me/calendars/${calId}/events/$count", headers={"Accept": "text/plain"}),
    step("contacts.count", "GET", "/v1.0/me/contacts/$count", headers={"Accept": "text/plain"}),
    setup("GET", "/v1.0/me/contactFolders", query={"$top": "1"}, save={"cfId": "value[0].id"}),
    step("contacts.folders.contacts.count", "GET", "/v1.0/me/contactFolders/${cfId}/contacts/$count", headers={"Accept": "text/plain"}),
    setup("GET", "/v1.0/me/todo/lists", query={"$top": "1"}, save={"listId": "value[0].id"}),
    step("tasks.tasks.count", "GET", "/v1.0/me/todo/lists/${listId}/tasks/$count",
         headers={"Accept": "text/plain"}, query={"$filter": "status ne 'completed'"}),
]

PHASES = {
    11: [("batch", BATCH)],
    12: [("mail", MAIL)],
    13: [("calendar", CALENDAR)],
    14: [("contacts", CONTACTS)],
    15: [("todo", TODO)],
    16: [("notifications", NOTIFICATIONS)],
    18: [("settings", SETTINGS), ("aliases", ALIASES), ("todo items", TODO_ITEMS)],
    19: [("folders", FOLDERS), ("counts", COUNTS), ("photos", PHOTOS), ("mime", MIME), ("event upload", EVENT_UPLOAD), ("item aliases", ITEM_ALIASES), ("small", SMALL)],
    21: [("directory", DIRECTORY)],
}
