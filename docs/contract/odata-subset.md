# OData subset accepted by the façade

This is the whitelist. These are the exact system query options, and the
literal formats around them, that NightMail sends (as of `nightmail@39ab812`).
The Api's OData parser accepts these and rejects everything else with `400
invalidRequest`, naming the unsupported construct. The one exception is
`$search`; see §6.

Each construct traces back to an operation in [`endpoints.json`](endpoints.json).

## 1. How parameters arrive on the wire

- Dio builds query strings with Dart's `Uri.encodeQueryComponent`. Option
  names therefore usually arrive **percent-encoded**, as `%24select=…` and
  `%24filter=…`. Spaces arrive as `+`, quotes as `%27`, commas as `%2C`,
  parentheses as `%28`/`%29`.
- Decode both `+` and `%20` as a space.
- Accept the `$` name both raw and encoded. `/places/microsoft.graph.room?$top=500`
  is a literal path string, so its `$top` arrives raw.
- `@odata.nextLink` and `@odata.deltaLink` are replayed **verbatim**, with no
  re-encoding and no extra parameters. The server decodes whatever it emitted.
- Option names are lowercase and `$`-prefixed. Case-insensitive matching is
  harmless but not required.
- No request sends `$count`, `$skiptoken`, `$deltatoken`, `$format`, `$levels`,
  `$apply` or `$compute`. The first three appear only inside links the server
  itself produced. Lambda operators and parameter aliases never appear either.

## 2. `$select`

```
select     = property *( "," property )        ; no spaces, no paths, no "*"
property   = 1*( ALPHA / DIGIT )               ; camelCase Graph property name
```

The client sends these properties. Each set is used whole, or as the subsets
listed per operation in `endpoints.json`.

| Resource (route) | Properties |
|---|---|
| message (`messages`, `mailFolders/{id}/messages`, `…/delta`, PATCH `messages/{id}`) | `id subject from toRecipients ccRecipients bccRecipients bodyPreview isRead flag receivedDateTime sentDateTime importance conversationId hasAttachments parentFolderId` |
| message (`messages/{id}`, plain-text probe) | `internetMessageHeaders body` |
| mailFolder (`mailFolders`, `…/childFolders`, `mailFolders/{id}`) | `id displayName totalItemCount unreadItemCount parentFolderId isHidden childFolderCount` |
| attachment (`messages/{id}/attachments`) | `id name contentType size`. These are **base-type** properties only. `isInline` and `contentId` are never selected on the collection. |
| event (`calendarView`, `events/{id}`, `events`) | `id iCalUId subject start end isAllDay location onlineMeeting bodyPreview showAs isOrganizer organizer responseStatus attendees recurrence isReminderOn reminderMinutesBeforeStart seriesMasterId` |
| event (`messages/{id}/…/event`) | `id isOrganizer` |
| user (`me`, `users/{id}`, `users`) | `id displayName mail userPrincipalName givenName surname jobTitle department companyName mobilePhone businessPhones officeLocation proxyAddresses` |
| contact (`contacts`) | `displayName emailAddresses` |
| person (`people`) | `displayName scoredEmailAddresses` |
| mailboxSettings | `automaticRepliesSetting timeZone` |
| driveItem (`shares/{id}/driveItem`) | `id name size file folder` |

Semantics:
- `$select` is a projection. Extra properties in the response are harmless, as
  the architecture says. `id` may always be included.
- `@odata.type` must still be emitted on derived types. In particular, the
  single-message GET deliberately sends **no** `$select` so that `@odata.type`
  is present.

## 3. `$filter`

There are no functions, no `or`, no `not`, no parentheses (other than the `in`
list), no arithmetic, no navigation paths, and no datetime, number or GUID
literals. The complete grammar:

```
filter          = message-filter / event-filter / task-filter

message-filter  = "isRead eq " boolean
                / conversation *( " and " folder-exclusion )

conversation    = "conversationId eq " string
                / "conversationId in (" string *( "," string ) ")"   ; 1..15 items, no spaces
folder-exclusion= "parentFolderId ne " string                        ; 0..2 in practice

event-filter    = "iCalUId eq " string                               ; on {mailbox}/events
task-filter     = "status ne " string                                ; value always 'completed'

boolean         = "false"                                            ; only false is ever sent
string          = "'" *( char-except-quote / "''" ) "'"              ; quote escaped by doubling
```

Where each form is sent:

| Route | Forms |
|---|---|
| `{mailbox}/mailFolders/{id}/messages`, `{mailbox}/messages` | `isRead eq false` (AI agent tool only) |
| `{mailbox}/messages` | `conversationId eq '…'`, `conversationId in ('…','…')`, each optionally followed by `and parentFolderId ne '…'` clauses |
| `{mailbox}/events` | `iCalUId eq '…'` |
| `{mailbox}/todo/lists/{id}/tasks` | `status ne 'completed'` |

Literal notes:
- Conversation and folder ids are Graph-style base64. The client doubles any
  `'` in the `in`, `parentFolderId ne` and `iCalUId eq` forms. It does **not**
  escape the two `conversationId eq` call sites (`:336`, `:1780`). Because ids
  are base64 this never matters, but the parser must accept standard OData
  doubling.
- `conversationId in (…)` with `and parentFolderId ne` is the hot path of every
  folder listing (2 requests per page). It must be supported, not merely
  tolerated. On a 400 the client falls back to per-conversation `eq`, and then
  to no folder exclusion.
- `parentFolderId ne` takes the id the server returned from
  `mailFolders/deleteditems` or `mailFolders/junkemail` (`$select=id`). The
  comparison is the server's, so ids in different encodings must still compare
  equal.
- Asking a `message` collection to filter or select an `eventMessage`-only
  property (`meetingMessageType`) is a 400 on Graph. The client avoids it, so
  this needs no support.

## 4. `$orderby`

```
orderby = ( "receivedDateTime" / "createdDateTime" ) " desc"   ; single key, explicit direction
```

- `receivedDateTime desc` is sent on `mailFolders/{id}/messages` and
  `messages` (the folder page). It is always present there.
- `createdDateTime desc` is sent on `todo/lists/{id}/tasks`.
- Other listings send no `$orderby`. Return calendarView in start order and
  the conversation expansion in any order.

## 5. `$top` and `$skip`

- `$top` is always a positive integer. These values are sent:
  - `1`, `5`, `10`, `25` (page size), `50`, `100`, `200`, `500` and `999`;
  - any value from 1 to 100 from the AI agent tool.
- Collections that page with `@odata.nextLink`: treat `$top` as the page size
  and emit a nextLink when more remain. This covers folders, child folders,
  conversation `in` expansion, contacts, users, people and places.
- Collections the client never pages: return at most `$top` and **no
  nextLink is needed**. The client ignores it. This covers `calendarView`,
  `events`, todo lists and tasks, search, and the folder page.
- On `messages/delta`, `$top` (`50`) is the page size. Graph documents
  `Prefer: odata.maxpagesize` for delta, but the client uses `$top`. Honour it.
- `/users` and `/people` send `999`. Clamp silently rather than reject.
- `$skip` is a non-negative multiple of the page size. It is sent **only** with
  `$top` + `$orderby=receivedDateTime desc` on `mailFolders/{id}/messages` and
  `messages`, for offset paging (Load More and migration). Combine it with
  nothing else.

## 6. `$search` (KQL)

This is sent only on `{mailbox}/messages`, always mailbox-wide, together with
`$top` and `$select`, and never with `$filter`, `$orderby` or `$skip`.

```
search      = DQUOTE kql DQUOTE             ; the outer quotes are always present
kql         = <the user's typed text, verbatim, except:
               "has:attachment" (case-insensitive, word-bounded) → "hasAttachments:true">
```

- The text inside the quotes comes **from the user**, so it is not a fixed
  grammar.
- **Embedded double quotes are not escaped.** Strip exactly the first and
  last character.
- The forms the UI documents, and that the façade must translate to JMAP
  `Email/query` filters, are:

  | Form | Meaning | JMAP |
  |---|---|---|
  | `term term …` | free text, implicit AND | `text` |
  | `from:addr-or-name` | sender | `from` |
  | `to:addr-or-name` | recipient | `to` |
  | `subject:word` | subject | `subject` |
  | `hasAttachments:true` | has attachments | `hasAttachment: true` |

- Unknown `prop:value` tokens should be treated as free text, not rejected,
  because the user typed them. Graph's own KQL accepts much more (`cc:`,
  `received:`, `OR`). This is the one deliberate exception to "400 on
  anything else": a 400 here surfaces as a failed search with Graph's message.
- Results come back without `$orderby`. Graph returns them by relevance, and
  the client re-sorts by date.

## 7. `$expand`

Exactly one form, on `GET {mailbox}/messages/{id}` only:

```
attachments($select=id,name,contentType,size,isInline)
```

The nested `$select` lists base-attachment properties plus `isInline`. Each
expanded item should still carry `@odata.type`
(`#microsoft.graph.fileAttachment` or `#microsoft.graph.itemAttachment`).
`contentBytes` and `contentId` are **not** expected here. The client fetches
each inline image separately.

## 8. Non-OData query parameters

| Parameter | Route | Format |
|---|---|---|
| `startDateTime`, `endDateTime` | `{mailbox}/calendarView` | Dart `DateTime.toUtc().toIso8601String()`: `YYYY-MM-DDTHH:MM:SS.mmmZ`, or `.mmmuuuZ` (6 fractional digits) when microseconds are non-zero. Always UTC with `Z`. |
| `format` | `/shares/{id}/driveItem/content` | `pdf` |

## 9. Date/time literal formats in request bodies

These are not query syntax, but the parser and binder must accept them.

| Where | Format | Zone |
|---|---|---|
| events create/update `start`/`end` | `YYYY-MM-DDTHH:MM:SS` (local wall clock, no fraction, no offset) | `timeZone` = **IANA** id (e.g. `Australia/Sydney`) |
| decline `proposedNewTime` (event screen) | as above | IANA id |
| decline `proposedNewTime` (from an invitation), PATCH start/end when accepting a proposal, `getSchedule` `startTime`/`endTime` | `YYYY-MM-DDTHH:MM:SS.mmm[uuu]` (UTC, `Z` stripped) | `"UTC"` |
| recurrence `range.startDate`/`endDate` | `YYYY-MM-DD` | — |
| todo `dueDateTime` | `YYYY-MM-DDT00:00:00` | `"UTC"` |
| mailboxSettings `scheduledStart/EndDateTime` | `YYYY-MM-DDTHH:MM:SS.0000000` | the mailbox's own `timeZone` (Windows name as returned by GET), or `"UTC"` |
| migration `singleValueExtendedProperties` `SystemTime 0x0E06`/`0x0039` | `YYYY-MM-DDTHH:MM:SS.mmmZ` | UTC |

In responses the client reads `dateTimeTimeZone.dateTime` with or without
fractional seconds (Graph's 7-digit form is fine). It appends `Z` when
`timeZone` is `UTC` (calendar, getSchedule, eventMessage) or when the request
asked for UTC. To Do and mailbox-settings values are read as naive local
wall-clock. Top-level timestamps (`receivedDateTime`, `sentDateTime`,
`createdDateTime`, `lastModifiedDateTime`) are ISO-8601 with `Z`.
