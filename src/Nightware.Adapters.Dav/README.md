# Nightware.Adapters.Dav

Nextcloud (Sabre/DAV) adapter. It covers CalDAV (RFC 4791), CalDAV scheduling (RFC 6638), CardDAV (RFC 6352), WebDAV (RFC 4918) with sync-collection (RFC 6578), and the OCS API.

The skeleton (S3) provides the **core** and the **codecs**. Feature phases add port implementations in their own folders: `Calendar/` (but not `Calendar/ICal`, which is skeleton-owned), `Contacts/`, `Tasks/`, `Rooms/`, `Files/` and `Office/` (Collabora, Phase 7). Each phase registers its ports in `DavAdapterServiceCollectionExtensions.AddDavAdapter`.

```
Core/                 options, DavPaths, DavItemId, IDavClient/DavClient, multistatus, errors,
  Discovery/          IDavPrincipalDiscovery (principal, home sets, inbox/outbox; cached)
  Collections/        IDavCollectionCatalog (calendars incl. VEVENT/VTODO, address books)
  Http/               DavAuthenticationHandler, DavResilience (retry policy)
Calendar/ICal/        IcalNetCalendarCodec (IICalendarCodec), IcsEditor, FreeBusyCodec
Tasks/ICal/           VTodoCodec
Contacts/VCard/       VCardCodec (+ VCardReader/VCardWriter)
```

## Implementing a port with the core

Inject `IDavPrincipalDiscovery`, `IDavCollectionCatalog`, `IDavClient` and the codecs. All of them are scoped or transient, apart from the codecs, which are singletons. Every call returns `Result<T>`. Pass errors through unchanged, because they already carry the correct `ErrorKind`.

```csharp
internal sealed class DavTaskStore(IDavPrincipalDiscovery discovery, IDavCollectionCatalog catalog,
    IDavClient dav, DavPaths paths, VTodoCodec codec) : ITaskStore
{
    public async Task<Result<TodoTask>> GetAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken ct)
    {
        var listId = list.ToDavItemId();                 // NotFound for tampered ids
        var taskId = id.ToDavItemId();
        if (!listId.IsSuccess) return listId.Error;
        if (!taskId.IsSuccess) return taskId.Error;
        if (!taskId.Value.IsWithin(listId.Value.Href))   // the task must belong to that list
            return Error.NotFound("The specified object was not found in the store.");

        var resource = await dav.GetAsync(taskId.Value.Href, ct);
        if (!resource.IsSuccess) return resource.Error;

        var parsed = codec.Parse(resource.Value.Body, taskId.Value.Href);
        return parsed.Bind(todos => todos.Count > 0 ? Result<TodoTask>.Success(todos[0].Task)
                                                    : Error.NotFound("No task in resource."));
    }

    public async Task<Result<TodoTask>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId id, TaskDraft patch, CancellationToken ct)
    {
        var href = id.ToDavItemId();
        if (!href.IsSuccess) return href.Error;
        var current = await dav.GetAsync(href.Value.Href, ct);                 // keep the ETag
        if (!current.IsSuccess) return current.Error;
        var edited = codec.Apply(current.Value.Body, patch);                   // edit in place, lossless
        if (!edited.IsSuccess) return edited.Error;
        var put = await dav.PutAsync(href.Value.Href, edited.Value, "text/calendar; charset=utf-8",
            current.Value.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None, ct);
        if (!put.IsSuccess) return put.Error;                                  // stale ETag → PreconditionFailed
        return await GetAsync(mailbox, list, id, ct);
    }
    // ListAsync: discovery.DiscoverAsync(mailbox) → catalog.ListCalendarsAsync(principal)
    //            .Where(c => c.SupportsTasks) → dav.ReportAsync(c.Href, DavDepth.One,
    //            DavRequestBodies.CalendarQuery(new CalendarQuery("VTODO")), ct)
}
```

Typical building blocks:

| Need | Call |
|---|---|
| Mailbox → principal, home sets, inbox/outbox | `discovery.DiscoverAsync(mailbox)` (self or another user/room; cached per credential) |
| Calendars / task lists / address books | `catalog.ListCalendarsAsync(p)` (`SupportsEvents`, `SupportsTasks`, `IsShared`, `IsReadOnly`, `SyncToken`, `Color`), `catalog.ListAddressBooksAsync(p)` |
| calendarView | `ReportAsync(cal, One, DavRequestBodies.CalendarQuery(new("VEVENT") { TimeRange = r, Expand = r }))`, then parse each `CalendarData` with `CalendarParseOptions { Href, Mailbox, Expanded = true }` |
| Find by iCalUId | `CalendarQuery("VEVENT") { Uid = uid }` |
| Fetch many | `DavRequestBodies.CalendarMultiget(hrefs)` / `AddressBookMultiget(hrefs)`. A missing member has `IsFound == false` |
| Delta | `SyncCollectionAsync(collection, token, [DavNames.GetETag], limit)` returns `Changed`, `Removed`, `SyncToken` and `IsTruncated`. An expired token gives `SyncStateGone` |
| Create | `PutAsync(href, ics, "text/calendar; charset=utf-8", DavPrecondition.CreateOnly)`. An existing item gives `Conflict`, and an existing UID gives `Conflict`/`uidConflict` |
| Update | `PutAsync(…, DavPrecondition.Matching(etag))`. A stale item gives `PreconditionFailed`. `ETag` may be null (Sabre rewrote the object), so re-GET if you need it |
| Delete | `DeleteAsync(href, precondition)` |
| RSVP / delete one occurrence / organizer cancel | `IcsEditor.SetParticipation / ExcludeOccurrence / Cancel / BumpSequence` on the stored ICS, then PUT with `If-Match` |
| getSchedule | `FreeBusyCodec.BuildRequest(organizer, attendees, start, end)` → `PostToOutboxAsync(principal.ScheduleOutboxUrl, ics)` → `FreeBusyCodec.ParseReply(item.CalendarData)` per recipient |
| Files / shares | `OpenReadAsync(href)` (streamed, dispose it), `PutAsync(href, stream, …)`, `MkColAsync`, `MoveAsync`, `OcsGetAsync("apps/files_sharing/api/v1/shares", query)` |
| Rooms | `PropFindAsync("principals/calendar-rooms/", One, [DisplayName, CalendarUserAddressSet, DavNames.RoomSeatingCapacity, RoomBuildingAddress, RoomFeatures, RoomType, …])` |

To unit-test a feature, mock `IDavClient`, `IDavPrincipalDiscovery` and `IDavCollectionCatalog`, and use the real codecs. For HTTP-level tests, see `tests/…/DavTestServer.cs`, which runs WireMock and a real `DavClient`.

## URL shapes (Nextcloud 35, from `deploy/recordings/caldav`)

| What | Href |
|---|---|
| DAV root | `{basePath}/remote.php/dav/` |
| Principal | `/remote.php/dav/principals/users/{uid}/` (uid = Keycloak `preferred_username`; **never construct it**, discover it) |
| Calendar home / calendar | `/remote.php/dav/calendars/{uid}/`, `…/{uid}/personal/` |
| Inbox / outbox | `/remote.php/dav/calendars/{uid}/inbox/`, `…/outbox/` |
| Trash bin (excluded from listings) | `/remote.php/dav/calendars/{uid}/trashbin/` (`nc:trash-bin`) |
| Address books | `/remote.php/dav/addressbooks/users/{uid}/contacts/` |
| Files | `/remote.php/dav/files/{uid}/…` |
| Room principals | `/remote.php/dav/principals/calendar-rooms/calendar_resource_management-{id}/` (CUTYPE ROOM; metadata `http://nextcloud.com/ns` `room-*`) |
| OCS | `{basePath}/ocs/v2.php/…?format=json` with `OCS-APIRequest: true` |

Sabre returns server-absolute hrefs, and those can be passed straight back to `IDavClient`. The client also accepts hrefs relative to the DAV root and full URLs on the Nextcloud origin. **Any href that resolves outside the DAV root is refused** with `Validation` and no request is sent. Hrefs are kept percent-encoded exactly as Sabre returned them.

Namespaces are matched by URI, never by prefix. Note that Sabre declares `nc:` for `http://nextcloud.org/ns`, while Nextcloud's own properties (rooms, trash bin, `owner-displayname`, `deleted-at`) live in `http://nextcloud.com/ns` (`DavNs.Nextcloud`).

## Ids (`Core/DavId.cs`, frozen)

`DavItemId` is the only id the DAV adapter emits for events, occurrences, tasks, task lists, contacts and address books.

- **Resource**: `base64url("r:" + href)`
- **Occurrence**: `base64url("o:" + recurrenceId + ":" + href)` (ADR 0001 §4)

Rules:

- `href` is the path **relative to the DAV root**, percent-encoded as Sabre returned it (`paths.ToRelative(response.Href)`). For an occurrence, `href` is the series master's resource.
- `recurrenceId` is a `DavRecurrenceId`: `yyyyMMddTHHmmssZ` (UTC) for timed series, or `yyyyMMdd` for all-day (`VALUE=DATE`) series. These are exactly the forms Sabre's `<C:expand>` emits.
- The encoding is base64url without padding. Only the canonical encoding parses, so each object has exactly one id.
- Parsing rejects all of the following, and `DavItemId.Parse` turns them into `NotFound`:
  - unknown prefixes and invalid UTF-8
  - absolute paths, schemes and authorities
  - `..` and `.` segments, also when percent-encoded
  - empty segments, backslashes, `?`, `#` and control characters
  - ids longer than 4096 characters
- An id only proves that its href is *inside the DAV root*. Feature code must still check that the href belongs to the mailbox or list being addressed, using `IsWithin(collectionHref)` or the home sets. Nextcloud's ACLs are the final authority.
- **Mint ids only from hrefs Sabre returned** (multistatus `href`, PUT target you then re-list). When creating a resource, name it from `[A-Za-z0-9._-]` only (e.g. `{Guid:N}.ics`), never from a UID that may contain `@` or other characters Sabre would encode differently — otherwise one object could end up with two ids.
- Conversions: `ToEventId/ToTaskId/ToTaskListId/ToContactId` and `EventId.ToDavItemId()` (and the equivalents).

## Errors

Sabre's `<d:error>` body is checked before the status code:

| Backend | `ErrorKind` (code) |
|---|---|
| `valid-sync-token` precondition (403/412) | `SyncStateGone` (`syncStateNotFound`) |
| `no-uid-conflict` | `Conflict` (`uidConflict`) |
| `max-resource-size` | `PayloadTooLarge` (`payloadTooLarge`) |
| 400, 415, 422 | `Validation` |
| 401 (also OCS 997, and discovery's `<d:unauthenticated/>`) | `BackendAuthFailed` for a passed-through token; `BackendUnavailable` for a token-exchanged one, which means façade misconfiguration (ADR 0001 §42, `DavErrors.Unauthorized`) |
| 403 | `Forbidden` |
| 404, 410 | `NotFound` |
| 405 (MKCOL on an existing collection), 409, 423 | `Conflict` |
| 412 after `If-None-Match: *` (create) or `MOVE` without overwrite | `Conflict` (`nameAlreadyExists`) |
| 412 after `If-Match` | `PreconditionFailed` (`preconditionFailed`) |
| 413, 507 | `PayloadTooLarge` |
| 429 | `Throttled` (`activityLimitReached`) + `RetryAfterSeconds` |
| 503 | `BackendUnavailable` + `RetryAfterSeconds` (delta-seconds or HTTP-date) |
| other 5xx, timeouts, connection failures, malformed XML | `BackendUnavailable` |

Caller cancellation is still thrown as `OperationCanceledException`.

## Credentials

`DavClient` calls `IBackendCredentials.ForAsync(Backend.Groupware)` once per instance and attaches the result to every request. `DavAuthenticationHandler` writes the `Authorization` header from it.

- The handler holds no scoped services, because `IHttpClientFactory` pools handlers across requests.
- `Bearer` tokens pass through as-is. `Basic` accepts either a raw `user:password` or a pre-encoded value.

Only per-user credentials are supported: token exchange with audience `nextcloud`, or pass-through. **Nextcloud has no DAV impersonation**, so there is no counterpart to Stalwart's master user. A credential with strategy `ServiceAccount`, or with `ActAs` set, is therefore rejected with `Unsupported` before any request is sent. Without this check, Nextcloud would silently act as the service account. Delegated access to another user's calendar works only if that user has shared the calendar with the caller. Discovery finds the other user's principal, and Nextcloud enforces the ACLs.

## Configuration (`Dav` section)

| Key | Default | Notes |
|---|---|---|
| `BaseUrl` | (required) | Nextcloud base URL, including any sub-path. Inside docker, use `http://nextcloud/`. Public hostnames may not resolve, and the Caddy alias needs its root CA |
| `DavRoot` | `/remote.php/dav` | |
| `OcsRoot` | `/ocs/v2.php` | |
| `AttemptTimeout` / `TotalTimeout` | 30 s / 90 s | |
| `MaxRetryAttempts` | 2 | Applies only to GET, HEAD, OPTIONS, PROPFIND and REPORT, with short exponential backoff (Retry-After is not slept on). Unsafe methods (PUT, DELETE, POST, MKCOL, MKCALENDAR, MOVE, PROPPATCH) are never replayed, and 429 is never retried: it is surfaced as `Throttled` with `RetryAfterSeconds` |
| `DiscoveryCacheDuration` | 10 min | Principal cache. The key is a SHA-256 of the credential and never contains the token |
| `HealthCheckTimeout` | 5 s | |

The `dav` health check (tag `backend`) sends an anonymous `PROPFIND Depth: 0` to the DAV root. A 2xx, 207 or 401 response is healthy. Timeouts, 5xx and maintenance-mode 503 are unhealthy. Like `Adapters.Directory`, the project uses `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` for `IHealthCheck`.

## Codecs

### `IcalNetCalendarCodec` (`IICalendarCodec`, Ical.Net 5)

Use `Parse(ics)` for the port and `Parse(ics, CalendarParseOptions { Href, Mailbox, FloatingTimeZone, Expanded })` for real ids and the mailbox's point of view. `Serialize(CalendarDocument)` writes the document back out.

Mapping, iCalendar → Domain:

- **METHOD, UID, SEQUENCE**
- **DTSTART/DTEND/DURATION**:
  - With a TZID: the wall clock plus that zone name (Windows or IANA).
  - In UTC: zone `UTC`.
  - All-day (`VALUE=DATE`): midnight with zone `UTC` and `IsAllDay`.
  - Floating: interpreted in `FloatingTimeZone` (default UTC).
- **TZID resolution** happens in this order:
  1. a Windows or IANA id (`GraphTimeZone`);
  2. a vendor-prefixed id such as `/mozilla.org/…/Europe/Berlin`, or `X-LIC-LOCATION`;
  3. the offsets of the embedded VTIMEZONE observances, with the value then reported in UTC (this handles Outlook's `(UTC+10:00) Canberra, …`);
  4. UTC.
- **Writing zones**: zones are written as IANA TZIDs, with one VTIMEZONE per zone covering the earliest date. Unknown zones are written in UTC.
- **RRULE ↔ `PatternedRecurrence`**:
  - Patterns: daily, weekly (BYDAY, WKST), absolute and relative monthly (both `BYDAY=2TU` and `BYDAY=TU;BYSETPOS=2`), absolute and relative yearly.
  - Ranges: COUNT, UNTIL (read in the series' zone and written as the UTC start of the last occurrence), or no end.
  - Implicit BYDAY or BYMONTHDAY values come from DTSTART. `DAILY;BYDAY=MO..FR` maps to weekly.
- **Exceptions and EXDATE**:
  - A RECURRENCE-ID VEVENT becomes `Exception`. In expanded results it becomes `Occurrence`.
  - Each **EXDATE is surfaced as a cancelled `Exception`** (`IsCancelled`, `OriginalStart`).
  - On write, those cancelled exceptions become EXDATE again if their master is in the same document. Otherwise they are written as a VEVENT with RECURRENCE-ID and `STATUS:CANCELLED`, for example a METHOD:CANCEL of one instance.
- **ORGANIZER/ATTENDEE**:
  - CUTYPE ROOM or RESOURCE → `Resource`.
  - ROLE OPT-PARTICIPANT or NON-PARTICIPANT → `Optional`.
  - PARTSTAT maps to the response type.
  - On write, RSVP=TRUE is set for NEEDS-ACTION. Rooms whose address matches `Location.LocationEmailAddress` get CUTYPE=ROOM, and other resources get CUTYPE=RESOURCE.
- **Locations**: LOCATION → `Location`. The location's email comes from the room attendee whose CN matches, or from the only room attendee. Other rooms go into `Locations`.
- **Body**: DESCRIPTION and `X-ALT-DESC;FMTTYPE=text/html` map to `BodyHtml`/`BodyPreview` (255 characters).
- **ShowAs**: `X-MICROSOFT-CDO-BUSYSTATUS` takes precedence, then TRANSP, then STATUS:TENTATIVE.
- **Reminder**: the first VALARM becomes the reminder (relative, END-related or absolute).
- **Other properties**:
  - CATEGORIES.
  - CLASS ↔ `Sensitivity`, with values normal, private and confidential.
  - STATUS:CANCELLED, or METHOD:CANCEL → `IsCancelled`.
  - `X-MICROSOFT-SKYPETEAMSMEETINGURL`, then `X-GOOGLE-CONFERENCE`, then URL → online meeting.
  - CREATED and LAST-MODIFIED.
- **DTSTAMP** comes from `TimeProvider`, so output is deterministic in tests.

**The Domain model is lossy.** It has no field for SCHEDULE-AGENT/-STATUS, X- properties, ATTACH, attendee COMMENT or DELEGATED, or extra VALARMs. **Never regenerate a stored event from Domain to change one aspect.** Use `IcsEditor` instead:

| Method | Effect |
|---|---|
| `SetParticipation` | Changes PARTSTAT and removes RSVP, for the series or one existing exception, with an optional COMMENT |
| `ExcludeOccurrence` | Adds an EXDATE in the master's own value form and drops a matching exception |
| `Cancel` | Sets STATUS:CANCELLED and increments SEQUENCE |
| `BumpSequence` | Increments SEQUENCE |
| `MaterializeOccurrence` | Gives an unmodified occurrence its exception VEVENT (a deep copy of the master minus RRULE/RDATE/EXDATE, RECURRENCE-ID in the master's DTSTART form), after checking the series produces it; NotFound otherwise (also for an EXDATE'd one) |
| `ApplyPatch` | A Graph PATCH in place, for the series or one occurrence: attendees merged by address (PARTSTAT/SCHEDULE-* kept, organizer's line kept, series edits reach exceptions), unchanged times left in their stored zone, SEQUENCE bumped only when the organizer really reschedules |
| `AddAttendees` | Organizer forward: adds recipients as NEEDS-ACTION/RSVP attendees to the series (and its exceptions) or one occurrence |

`SetParticipation` on an occurrence id creates the exception for an unmodified occurrence, so occurrence ids are write targets everywhere (ADR 0001 §4).

Organizer time changes must also bump SEQUENCE (ADR 0001 §4).

### `VTodoCodec`

Use `Parse(ics, href?)`, `Create(TaskDraft, uid)` for new tasks, `Apply(existingIcs, TaskDraft)` for in-place updates that keep unknown properties, and `Serialize(TodoTask, uid)`.

- **STATUS**:
  - NEEDS-ACTION, IN-PROCESS and COMPLETED map to the matching status.
  - CANCELLED → `deferred`.
  - `waitingOnOthers` and `deferred` are written as NEEDS-ACTION plus `X-NIGHTWARE-TASK-STATUS`.
- **PRIORITY**: 1–4 → high, 0 or 5 → normal (written without PRIORITY), and 6–9 → low (written as 9).
- **Dates**: DUE, DTSTART and COMPLETED are mapped. A midnight DUE or DTSTART is written as a DATE.
- **Other fields**: VALARM ↔ `ReminderDateTime`, plus RRULE, CATEGORIES, DESCRIPTION and ATTACH → `HasAttachments`.

### `VCardCodec` (hand-written)

Use `Parse(vcf, href?)`, `Create(ContactDraft, uid, V3|V4)`, `Apply(existingVcf, ContactDraft)` `Serialize(Contact, uid, version)`, `ReadPhoto(vcf)` and `WritePhoto(vcf, bytes, type)`. `Apply` replaces only the touched properties, so PHOTO, groups and X- properties survive. Version 2.1 input is upgraded to 3.0.

- **Mapping (vCard ↔ Graph `contact`, ADR 0006 D7).** Cards are written as vCard 3, which is how Nextcloud stores them.

  | vCard | Graph |
  |---|---|
  | FN | `displayName`; when none is given it is composed as Exchange does: title, given, middle, surname, generation |
  | N (5 parts) | `surname`; `givenName`; `middleName`; `title` (honorific); `generation` (suffix) |
  | NICKNAME | `nickName` |
  | ORG (2 parts) | `companyName`; `department` |
  | TITLE / ROLE | `jobTitle` / `profession` |
  | EMAIL (preferred first), parameter `X-NAME` | `emailAddresses[]` (`address`, `name`); the first is `primaryEmailAddress` |
  | TEL cell / home / work or untyped | `mobilePhone` (first) / `homePhones` / `businessPhones`; fax and pager are ignored |
  | ADR work / other / home or untyped | `businessAddress` / `otherAddress` / `homeAddress` (first of each) |
  | IMPP | `imAddresses` |
  | URL;TYPE=work | `businessHomePage` |
  | CATEGORIES | `categories` |
  | RELATED;TYPE=spouse / child (VALUE=text) | `spouseName` / `children` |
  | BDAY | `birthday` (Graph shows it at 11:59 UTC, as Exchange does) |
  | NOTE | `personalNotes` |
  | PHOTO (`ENCODING=b`; v4 `data:` read too) | `photo/$value` |
  | REV / X-NIGHTWARE-CREATED | `lastModifiedDateTime` / `createdDateTime` (REV when the card was not made here) |
  | X-ASSISTANT, X-MANAGER | `assistantName`, `manager` |
  | X-PHONETIC-FIRST-NAME, -LAST-NAME, -ORG | `yomiGivenName`, `yomiSurname`, `yomiCompanyName` |
  | X-NIGHTWARE-INITIALS, -OFFICE-LOCATION, -FILE-AS | `initials`, `officeLocation`, `fileAs` |
  | (the resource ETag) / (the address book) | `changeKey` / `parentFolderId` |

- In a patch, a null member is untouched and an empty value (or an address with every part empty) removes the property; `ClearBirthday` removes BDAY.
- **Reader**:
  - Accepts CRLF or LF line ends, and space or tab folding.
  - Handles v2.1 quoted-printable with soft breaks and CHARSET, bare types (`TEL;WORK;VOICE`), v4 quoted `TYPE="work,voice"`, `tel:` URIs and `item1.` groups.
  - Writes are folded at 75 octets without splitting UTF-8, and RFC 6350 escaping is applied.

### `FreeBusyCodec`

`BuildRequest` produces the VFREEBUSY REQUEST. `ParseReply` reads FREEBUSY periods (`start/end` or `start/duration`) and maps FBTYPE as follows:

| FBTYPE | Status |
|---|---|
| BUSY | busy |
| BUSY-TENTATIVE | tentative |
| BUSY-UNAVAILABLE | oof |
| FREE | free |

## Contacts and user photos (phases 5A and 14)

**`DavContactStore` (`IContactStore`, `Contacts/`)** serves `{mailbox}/contacts`.

- Writes (phase 14): create PUTs `{book}/{uuid}.vcf` with `If-None-Match: *` into the default book (`contacts`, else the first of the user's own) or a named folder. Update and photo writes GET the card, edit it with `Apply`/`WritePhoto` and PUT it back with `If-Match` on its ETag, so a concurrent change fails (412) rather than being lost.
- A contact's `parentFolderId` is its address book's `DavItemId`; its `changeKey` is the card's ETag.

- It reads the mailbox's own address books and the ones shared with it. It skips the books Nextcloud generates into every home: `z-server-generated--system` (every account, already served by `/users`) and `z-app-generated--*` (for example Contacts Interaction's "Recently contacted").
- Paging is stateless. Each page sends one `addressbook-query` per book without data, orders the cards by (book href, card href), slices at the offset in the `$skiptoken` (versioned base64url) and fetches only that slice with `addressbook-multiget`. A multiget must name server-absolute hrefs, because Sabre refuses hrefs outside its base URI. The default page size is 10, Graph's default.
- A card that cannot be parsed is logged and left out, but it still takes its place in the paging order. `$filter`, `$search`, `$orderby`, `$skip` and `$expand` are a 400.

**`NextcloudAvatarReader` (`IUserPhotoReader`, `Directory/`)** serves `/users/{addr}/photo/$value`. It is registered from the Contacts hook, because `AddDavAdapter` has no Directory hook.

- The uid comes from `IDavPrincipalDiscovery` (`…/principals/users/{uid}/`). Rooms, resources and unknown addresses have no photo.
- It sends `GET {BaseUrl}index.php/avatar/{uid}/512` with the caller's Groupware credential. This route is outside the DAV root, so it uses its own typed `HttpClient` with the same resilience and auth handlers, and it never follows redirects.
- Nextcloud draws a letter avatar for users who never uploaded one, and the response marks it with `X-NC-IsCustomAvatar: 0`. That counts as **no photo** (`NotFound`), so the client draws its own initials, as it does with Graph.
- The image is buffered, capped at 4 MiB.
- A backend 401 maps to `BackendUnavailable`, never to 401: on a best-effort call, a 401 would make the client refresh its token and could flag the account for re-authentication.

**`NextcloudAvatarWriter` (`IUserPhotoWriter`, `Directory/`)** sets and removes the signed-in user's own photo (phase 19): `POST {BaseUrl}index.php/avatar/` with the image as multipart `files[]`, and `DELETE {BaseUrl}index.php/avatar/`, both with the caller's Groupware credential.

- A bearer-token request passes Nextcloud's CSRF check only with `OCS-APIRequest: true` (verified on Nextcloud 35).
- Nextcloud answers 200 whatever happened and says what in the body: `{"status":"success"}` stored it; `{"data":"notsquare"}` stored nothing (only a square JPEG or PNG is kept, and NC 35 has no crop route); `{"data":{"message"}}` refused it. Only the first is success; the others are 400 with the reason. The use case refuses a non-square image before it gets here.
- No-photo reads now carry Graph's code `ImageNotFound` (`UserPhotos.None()`), so the metadata route can tell "no photo" (a placeholder) from "no such user" (404).

## Calendar and rooms (Phase 4)

`Calendar/` implements `ICalendarReader`, `ICalendarWriter`, `ISchedulingActions` and `IFreeBusyReader`; `Rooms/` implements `IRoomDirectory`.

- **Which calendars.** `/me` is the caller's own event calendars. `/users/{x}` is the calendars `x` shared with the caller (Nextcloud mounts them in the caller's home with `owner-principal` = `x`); `x`'s own home is not readable by others. The **default calendar** (Graph's `/me/calendar`) is the writable `personal*` one, else the first writable by order; the birthday calendar is never the default. `calendarView` and an unfiltered `/events` read the default calendar; the `iCalUId` lookup searches every calendar in scope. Every id is checked to lie inside one of those calendars before any request.
- **calendarView** is one `calendar-query` with `<C:expand>` on the default calendar. Sabre writes every instance with a RECURRENCE-ID, so the recurring resources are multigot unexpanded to type each instance `Exception` (it has a stored exception component) or `Occurrence`. Expanded times are UTC (as Sabre returns them); NightMail always sends `Prefer: outlook.timezone="UTC"`. Paging is an offset `$skiptoken` over the sorted result.
- **Writes are iTIP through Sabre.** Create builds the new resource from Domain (named `{guid}.ics`, ORGANIZER only for meetings); every later change is an `IcsEditor` edit PUT under `If-Match`. Organizer cancel **deletes** the resource (or EXDATEs the occurrence), which is what makes Sabre send METHOD:CANCEL, so the meeting leaves the organizer's calendar as in Graph. Organizer forward adds attendees; an attendee's forward is 403 (NightMail then mails the invitation itself). Rooms (CUTYPE=ROOM) are auto-scheduled by Sabre.
- **getSchedule** posts one VFREEBUSY REQUEST to the **caller's** outbox (Sabre accepts no other), for people and rooms alike; `3.x` request statuses become per-schedule errors. `availabilityView` is computed in Application.
- **Rooms** are the `principals/calendar-rooms/` principals of `calendar_resource_management`: `building` = `room-building-name` (else the address), `floorLabel` = `room-building-story`, `floorNumber` = the number in it, wheelchair access from `room-features`.
- **RSVPs (phase 17).** `sendResponse: false` marks the organizer `SCHEDULE-AGENT=CLIENT` on the attendee's copy, so Sabre sends no REPLY (RFC 6638 §7.1); a later answer clears the mark. Sabre's REPLY carries no comment and it has no COUNTER, so a comment or `proposedNewTime` for an organizer no Nextcloud principal has is also kept from Sabre and returned as an `ItipMessage`: the Jmap adapter's `JmapItipMail` imports it as a draft (`multipart/alternative` with `text/calendar; method=REPLY|COUNTER`) and sends it like any draft. For an organizer inside Nextcloud the comment and proposal stay on the stored copy: Sabre puts the answer straight into their calendar, which a mail would not.
- **Not carried by Sabre's iMIP** (accepted, kept on the stored copy at most): cancel comments and forward comments.
- `VTIMEZONE`s are generated from today when the event is more than a year ahead: Ical.Net throws for a later "earliest date" (`ZoneWriter.SupportedFrom`).

## Tasks and task attachments (phase 5B)

`Tasks/` implements `ITaskListReader`, `ITaskStore` and `ITaskAttachmentStore`.

- **Lists** are the VTODO-capable calendars in the mailbox's calendar home (`DavTaskCollections`). The
  default list is the caller's own `tasks/` collection, else one holding only VTODO, else the first by
  calendar order. When the caller owns no task collection, the first listing creates `tasks/` ("Tasks",
  VTODO only) with MKCALENDAR, as the Nextcloud Tasks app would. Never for another user's home.
- **Ids**: a list id must lie in the mailbox's calendar home; a task id must name a resource directly
  inside that list. Anything else is `NotFound` before any request.
- **Listing** is one `calendar-query` (VTODO) per list; `$filter` (`status eq|ne`), `$orderby`
  (`createdDateTime`, `lastModifiedDateTime`, `title`, `dueDateTime/dateTime`) and paging are applied in
  memory (`TaskQueryEvaluator`, offset `$skiptoken`). Other filters are `Validation` (400).
- **Writes**: create PUTs `{uuid}.ics` with `If-None-Match: *`; updates are `VTodoCodec.Apply` under
  `If-Match`, retried once on 412.
- **Attachments** go to the mailbox owner's Files: `files/{uid}/Nightware/Task attachments/{uuid}/{name}`
  (uid from the discovered principal; the name made safe by `TaskAttachmentFiles.SafeName`). The VTODO
  gets `ATTACH;FMTTYPE=…;FILENAME=…;SIZE=…:{Dav:BaseUrl}remote.php/dav/files/…`. On read only the path
  counts, so a host-name change does not orphan them. Attachment ids are the file's `DavItemId`, and
  only files the task's own `ATTACH` list names are served. A failed task update deletes the uploaded
  folder again.
- **Upload sessions** are stateless: the session token (`TaskUploadToken`) is base64url JSON of mailbox,
  list, task, name, content type, size and expiry (15 min). Every field is re-checked on the PUT, whose
  body is streamed to Nextcloud and must be exactly the declared size (`CountingReadStream` stops at the
  limit: more is 413, less is 400, and the file is removed).

**Event attachments** (`DavEventAttachmentStore`, ADR 0006 D6) live as task attachments do, under
`files/{uid}/Nightware/Event attachments/{uuid}/{name}`, linked by the event's `ATTACH`. Phase 19 adds:

- `$value` streams the file (`GetContentAsync`, `DownloadStream`), so it has no inline size cap.
- **Chunked uploads** (`DavEventAttachmentUploads`) use Nextcloud's chunked upload v1 (no minimum chunk
  size, so Graph clients' 320 KiB multiples fit; v2 wants 5 MB): `MKCOL uploads/{uid}/nw-{uuid}/` when
  the session opens, each chunk `PUT` as its zero-padded first byte (15 digits), and after the last one
  a `MOVE` of the folder's virtual `.file` (the chunks joined in name order) into a fresh attachment
  folder, then the `ATTACH` (`LinkAsync`, shared with the small attachment path). What has arrived is
  the folder's listing, so the session token (`EventUploadToken`: mailbox, event, name, type, size,
  staging and home folders, expiry of one hour) is all the state. Only the next byte is accepted; a chunk
  shorter than its `Content-Range` is deleted again. Nextcloud removes the staging folder on the `MOVE`;
  abandoned ones are left to its own cleanup of stale uploads.

## Documents linked from mail (Phase 7)

`Files/` implements `ISharedFileReader`; `Office/` implements `IDocumentConverter`.

- **Input** is a `ShareLink` whose host the API has already matched to an advertised file site
  (`Discovery:FilesBaseUrls`). Only `SitePath` is read (`NextcloudLink`): `s/{token}` and `f/{fileId}`,
  optionally under `index.php/`, with or without a trailing slash. Anything else is `NotFound` without a
  request. Every request goes to `Dav:BaseUrl`, never to the link.
- **Public shares** (`NextcloudPublicShares`): `PROPFIND`/`GET {BaseUrl}public.php/dav/files/{token}`,
  which serves a file share's one file at its root. **No credential** is sent (the link is the
  authorisation), on a cookie-free, redirect-free client with the DAV resilience pipeline. Nextcloud 35
  answers an unknown token with **503** carrying `Sabre\DAV\Exception\NotFound` (→ `NotFound`, or the client
  retries a 503 five times) and a password-protected share with **401** (→ `Forbidden`: the client reads
  401 as "sign in again"). A folder share is answered as a folder.
- **Internal links** (`NextcloudFileIdSearch`): RFC 5323 `SEARCH` on the DAV root for `oc:fileid`, scoped to
  the caller's own `/files/{uid}` (uid from the discovered principal), with the caller's credential. Shares
  are mounted in the recipient's home with the same file id, so a file shared with the caller is found and
  anything else is `NotFound`. The bytes come from the found href through `IDavClient.OpenReadAsync`.
- **Ids** are Nextcloud's file id (`oc:fileid`), falling back to the token.
- **Conversion** (`CollaboraDocumentConverter`): `POST {Collabora:BaseUrl}cool/convert-to/pdf`, multipart
  field `data` named `document.{ext}` (Collabora detects the format by extension; the user's file name may
  not survive a header). Never retried, streamed both ways. Collabora answers 400 for input it cannot read
  (→ `Validation`/`notSupported`) and 403 to an address outside its `net.post_allow` (→ `BackendUnavailable`:
  a deployment fault). Office and OpenDocument formats only (`OfficeFormats`) — images, text and PDFs are
  previewed by the client as they are. It lives here because Collabora is Nextcloud Office's engine and is
  only used for documents this adapter reads; a project of its own would add a composition seam for one class.
- **Without `Collabora:BaseUrl`** the converter is `UnconfiguredDocumentConverter`: nothing is offered as PDF,
  so `content?format=pdf` is refused on the first leg and NightMail downloads the original instead.

| Key (`Collabora` section) | Default | Notes |
|---|---|---|
| `BaseUrl` | (none: conversion off) | Collabora's internal URL, e.g. `http://collabora:9980/office/` (Collabora's `net.service_root` included). Not the public `/office` URL: Caddy refuses `convert-to` there |
| `Timeout` | 90 s | Whole conversion, upload and download included |
| `MaxSourceBytes` | 50 MB | Larger documents are `PayloadTooLarge` without a request |

## The caller's OneDrive (phase 20, ADR 0008)

`Files/Drive/` serves the Graph drive surface from the caller's Nextcloud home, `files/{uid}/`, always as the caller.

| Class | Port | Over |
|---|---|---|
| `NextcloudDriveStore` | `IDriveStore` | WebDAV (`PROPFIND`, `PUT`, `MKCOL`, `MOVE`, `COPY`, `DELETE`), `SEARCH`, `index.php/core/preview` |
| `NextcloudDriveDelta` | `IDriveDelta` | the activity stream, `ocs/v2.php/apps/activity/api/v2/activity/files` |
| `NextcloudDriveUploads` | `IDriveUploads` | chunked upload v1 in `uploads/{uid}/nw-{uuid}/` |
| `NextcloudDriveSharing` | `IDriveSharing` | OCS `apps/files_sharing/api/v1/shares` and `sharees` |

- **Ids.** A `driveItem` id is the `oc:fileid`, found with a `SEARCH` (`eq`, `or` of several) scoped to `/files/{uid}`; the
  home's own id, which `SEARCH` never returns, is checked with a `PROPFIND`. The drive id is `nc!` + base64url of the uid
  (`DriveHome`). `DriveItemLocator` resolves anchors and paths, maps responses with their parents (each parent looked up
  once per request, eight at a time) and picks OneDrive's free names (`name 1.ext`) for `rename` conflicts.
- **Properties** come from the Files namespace `http://nextcloud.org/ns` (`creation_time`, `has-preview`,
  `contained-*-count`), not the calendar's `http://nextcloud.com/ns` (`DriveItemMapping.FilesNs`).
- **Conflicts.** `fail` is checked before writing (a listing of the folder, case-insensitively as Nextcloud compares) and
  enforced again by `If-None-Match: *` or `Overwrite: F` (412 → `nameAlreadyExists`).
- **Delta.** No `sync-collection` on Files, so the token is an activity id: the initial listing is `SEARCH displayname
  LIKE '%'` ordered by name and paged by offset (the root first), with the activity id taken before it; a follow-up reads
  the activities after the id (304 = nothing new), looks every named file id up, and reports the missing as deleted. A
  404 from the activity route is `501 notSupported`.
- **Thumbnails and the activity stream** lie outside the DAV root, so `NextcloudAppRequests` sends them on the DAV
  client's pipeline (resilience, then the caller's credential; no cookies, no redirects).
- **Sharing.** A link is `shareType=3` (`view` → 1, `edit` → 3 on a file, 15 on a folder), an invitation `shareType=0`
  with the uid the sharee search finds for the address (exact matches only). Permissions are the item's shares
  (`reshares=true`) plus `shares/inherited`; an inherited one can't be removed here.

## Chats over Talk (phase 23, ADR 0009)

`Chats/` serves Graph's chats from the caller's Talk conversations, always as the caller (OCS only; `Talk:PublicBaseUrl`
adds web links). `Chats/Talk/` is conversation-generic, for channels (phase 24) too:

| Class | What |
|---|---|
| `TalkChatClient` | Every Talk call (`apps/spreed/api/v4/room`, `api/v1/chat`, `api/v1/reaction`), token and id checked first. Reads pass `setReadMarker=0`, `markNotificationsAsRead=0`, `noStatusUpdate=1`. `PostAsync` takes `TalkPost` (`ReplyTo`, `ThreadId`, `ThreadTitle`); `HistoryAsync` an optional `threadId`. Talk's 304 (end of history) is a success without data (`OcsExchangeAsync`), so it reads as an empty page. |
| `TalkWire.cs` | `TalkRoom`, `TalkMessage` (with `ThreadId`, `IsThread`, `ThreadTitle`, `Parent`), `TalkParticipant`, `TalkParameter`, `TalkActor`, `TalkReaction`; `TalkIds` (token, message id, `m.{attendeeId}`). PHP's empty map `[]` parses as empty. |
| `TalkMessageCodec` | Pure. `IsListed`, `AccountsIn` (uids to resolve), `ToChatMessage(message, TalkConversation, identify, deletedAt)`, `DeletionTimes`, `ToTalkText(body, mentions)`, `MentionOf(uid)`, `MentionAll`. |
| `TalkMarkdown`, `TalkHtml` | Talk Markdown → Graph html (text encoded first; http/https/mailto links only) and back. |
| `TalkMessageFeed` | `PageAsync(conversation, top, skipToken, threadId)` (fills a page past Talk's bookkeeping, five rounds at most), `GetAsync`, `DecodeOneAsync`, `DecodeAsync` (reads who reacted, resolves people in one batch), `EncodeAsync(draft)`. |
| `TalkIdentities` | uid ↔ directory identity per request over `IUserAccountNames`; `NoDirectory` when the Directory adapter isn't composed. |
| `TalkConversation` | A token, its name (for `@all`), its `WebUrl`, and for a channel its `ChannelIdentity` (messages then carry `channelIdentity`, `replyToId` = the thread, `subject` = the thread title). |
| `TalkLinks`, `TalkChannelRooms` | `{Talk:PublicBaseUrl}call/{token}`; the caller's channel tokens (their teams' `talk` resources). |

The chat ports are `TalkChatStore` (`IChatStore`), `TalkChatMembers`, `TalkChatMessages` and `TalkChatPins`;
`TalkChatRooms` says which rooms are chats. Talk's behaviour behind them is in `docs/phases/phase-23-chats.md`.

## Presence (phase 25, ADR 0009 D6)

`Presence/NextcloudUserStatusStore` implements `IUserStatusStore` over OCS `apps/user_status/api/v1`, as the caller:
`user_status` (own status, with `messageId`), `statuses/{uid}` (anyone's public status: no `messageId`, never
`invisible`), `PUT user_status/status`, `PUT user_status/message/predefined|custom` (`clearAt` in Unix seconds) and
`DELETE user_status/message`. A 404 (no status row: never set, or deleted when `clearAt` passed) reads as
`UserStatus.None`. The mapping to Graph presence is Application logic (`PresenceMapping`).

## Known gaps

- `CalendarDocument` and `CalendarEvent` have no slot for a resource href. The port's `Parse` therefore leaves `EventId` empty, and callers either use `CalendarParseOptions.Href` or fill the id with `with { Id = … }`.
- Floating times and RDATE have no Domain representation. Floating times use `FloatingTimeZone`, and RDATEs are ignored. RRULEs that Graph cannot express (BYHOUR, BYWEEKNO, BYYEARDAY, negative BYMONTHDAY, several BYMONTHs, setpos outside 1–4/-1, sub-daily) give `Recurrence = null` on a `SeriesMaster`.
- Unresolvable TZIDs are reported in UTC, so the original wall-clock zone is lost. Server-side expansion is unaffected, because Sabre has its own Outlook zone table.
- In expanded calendar-query output, every instance is typed `Occurrence`. Telling a modified instance (`Exception`) apart requires comparing it with the master, which is the feature's job.
- Tasks: inline (BASE64) and non-Nextcloud `ATTACH` values count towards `hasAttachments` but are not listed. Attachments of a shared mailbox's task go to that mailbox's Files, which the delegate can write only if Nextcloud allows it.
- vCard: `--MMDD` birthdays, URLs other than the work one, other phone types and addresses beyond the first of each kind are not mapped. `Apply` preserves all of them. A photo given by URL (not inline) reads as no photo.
- There is no Nextcloud service-account or impersonation path (see Credentials).
