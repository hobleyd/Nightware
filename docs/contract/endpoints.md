# NightMail → Graph endpoint inventory

This file and [`endpoints.json`](endpoints.json) describe every Microsoft Graph
request the NightMail client makes. **`endpoints.json` is the source of truth.**
The tables under *Operations* are generated from it by
`tools/inventory/render_endpoints_md.py`. Everything outside the generated
markers is written by hand.

- **Source:** `~/Source/nightmail` at `39ab812` (v1.44.0+255). Line numbers in
  `clientCallSites` point at that commit. The 0.4 client hook moves some of
  them; `tools/inventory/check_inventory.py` re-greps the live tree.
- **Owning phases:** 2 (mail read), 3 (mail write), 4 (calendar and rooms), 5A
  (directory, contacts, people), 5B (tasks, settings, files, security).
- **Scope rule:** an operation that isn't listed here gets a Graph-shaped `501
  notImplemented` (see `docs/architecture.md`). OData the client never emits
  gets `400`; [`odata-subset.md`](odata-subset.md) is the whitelist.

## Conventions used in `endpoints.json`

| Key | Meaning |
|---|---|
| `id` | Stable kebab-case id. It is also the fixture's file name. **An id is not a route.** Where the client uses one route in materially different ways, there is one id per variant, each with its own fixture. These ids share a (method, version, path), so the router and the 501 fallback must key on the route and dispatch on the query or body:<br>• `mail.messages.list` / `mail.messages.list-by-conversation` / `mail.messages.search` (GET `{mailbox}/messages`)<br>• `mail.messages.get` / `mail.messages.get-plain-text-body` (GET `{mailbox}/messages/{id}`)<br>• `mail.messages.update-read-state` / `mail.drafts.update` (PATCH `{mailbox}/messages/{id}`)<br>• `directory.me.get` in its `users` form / `directory.users.get` (GET `/users/{x}`) |
| `phase` | `2`, `3`, `4`, `"5A"` or `"5B"`. |
| `version` | `v1.0` or `beta`. The client's Dio base URL is the v1.0 root. Beta calls are absolute URLs. |
| `path` | Path template relative to the version root. `{mailbox}` is the client's `_base` (see below). `{fooId}` is an opaque id segment. `{folderIdOrWellKnownName}` also accepts `inbox`, `sentitems`, `deleteditems`, `junkemail`, `archive` and `msgfolderroot`. |
| `forms` | `me` gives `/me/...`. `users` gives `/users/{mailbox}/...`, where the shared mailbox's SMTP address sits **raw, with a literal `@`**. `root` means the path has no mailbox prefix (`/users/{id}`, `/places/...`, `/shares/...`). `external` means an absolute URL taken from a response, not a Graph route. |
| `query` | Every query option the client sends. `select` is a list of `$select` variants, one field list each. `filter`/`orderby`/`search`/`expand` hold the exact expressions sent (examples). `top` holds the values sent. `other` holds non-OData parameters (`$skip`, `startDateTime`, `format`). |
| `headers` | Request headers beyond the defaults. Every request also sends `Authorization: Bearer …`, `Accept: application/json` and `Content-Type: application/json`. |
| `requestBody.fields` | JSON fields the client sends. `?` marks a conditional field. |
| `responseFields` | The response fields the client actually reads: dotted paths, with `[]` for arrays. Anything else may be omitted. |
| `successStatus` | The status Graph returns. The client accepts any 2xx (any `< 400` for driveItem content). |
| `paging` | `nextLink` means the client follows `@odata.nextLink`. `deltaLink` means it also stores `@odata.deltaLink`. `none` means it reads one page and ignores links. |
| `clientErrorHandling` | What the client does with a failure on this call. See also the section below. |
| `clientCallSites` | `file:line` in the client at `39ab812`. |

**The mailbox base (`{mailbox}`).** `GraphApiDatasourceImpl._base` is `/me` for
the signed-in account. For a shared mailbox added under it, it is
`/users/{sharedMailboxAddress}`. The bearer token is always the signed-in
owner's. Every `{mailbox}` operation is therefore reached in both forms and must
be routed for both. Delegated access is the backend's decision (see
`IMailboxResolver`). Paths that call `/users/...` directly, such as the
directory lookup and the shared-mailbox probe, encode the address with
`Uri.encodeComponent`, which gives `%40`. The base form does not encode it, so
the server must accept both `users/a@b` and `users/a%40b`.

## Cross-cutting requirements the client imposes on the façade

These came out of reading the client. Each one constrains the server design,
not just one route.

1. **Continuation links must pass the client's host check.** `graphNextLink`
   and `graphDeltaLink` (`contact_bulk_parser.dart`, `graph_message_parser.dart`)
   pull `@odata.nextLink`/`@odata.deltaLink` out of the raw body with a regex and
   unescape `\/` and `&`. They then drop any link that fails `isGraphUrl`.
   - At `39ab812` that check accepts only `https://*.graph.microsoft.com`. A
     link pointing anywhere else silently ends paging: folder, contact,
     directory and conversation listings come back truncated. A delta walk ends
     in `DeltaResyncRequiredException` and re-bootstraps on every cycle.
   - The 0.4 hook (in flight in the client working tree) relaxes this to "same
     scheme, host and port as the account's Graph base URL".
   - **Nightware must emit absolute links on the exact scheme, host and port the
     client was configured with** (`graphBaseUrl`). That includes the
     `/v1.0` prefix.
   - Links are followed verbatim with no query re-applied, so they must carry
     all of their state.
   - `/places` paging reads `data['@odata.nextLink']` without the host check.
2. **There are no immutable ids, and the client tolerates ids that change on
   move.** No request sends `Prefer: IdType="ImmutableId"`. The client expects a
   message move (`POST …/move`) to return **201 with the moved message**, and
   reads the new `id` from it. It reconciles tombstones on the assumption that
   ids may change. Nightware's stable ids are compatible: return the same id.
   - **Draft ids must stay stable across `PATCH`**, because the client keeps
     using the id it created.
   - **Folder ids must survive rename and move.**
   - **`parentFolderId` on messages must compare equal (as a string) to the
     `id` returned by `mailFolders/{wellKnownName}`.** Graph itself breaks this
     (`AQMk…` vs `AAMk…`), which is why the client also pushes
     `parentFolderId ne '<id>'` to the server.
3. **There is no `$batch`, no `$count`, no `$expand` other than one attachments
   form, and no `$skiptoken` built by the client.** The one listing that offset-pages
   is the folder message list, via `$skip`.
4. **Beta is used in exactly two places, both absolute URLs:**
   - `beta/me/findRooms` (always `/me`, even for a shared mailbox).
   - `beta/security/threatSubmission/emailThreats`. Its body names the message
     by an absolute **beta** URL, `messageUrl`, which the server must parse.

   After the 0.4 hook both use the account's beta root. The hook also makes
   threat submission and SharePoint/OneDrive previews (`/shares/...`)
   Microsoft-only, so a Nightware account never sends them. They stay in the
   inventory, marked 5B, as candidates to defer.
5. **`Prefer` headers.** These are the only two values sent:
   - `outlook.timezone="UTC"`, sent on `calendarView`, `events/{id}` and the
     reading-pane `messages/{id}` (eventMessage start/end).
   - `outlook.body-content-type="text"`, sent only on the plain-text body
     probe.

   With UTC, a `dateTimeTimeZone` value is a wall-clock string **without**
   `Z`, with `timeZone: "UTC"`. The client appends `Z` itself. Nothing ever
   sends `outlook.body-content-type="html"`; HTML is the default.
6. **Derived types and casts.**
   - A single-message GET without `$select` must carry `@odata.type`
     (`#microsoft.graph.eventMessageRequest`/`…Response`/`eventMessage`) and the
     derived properties `meetingMessageType`, `startDateTime`, `endDateTime`,
     `location`, `isAllDay` and `proposedNewTime`.
   - The client navigates `messages/{id}/event` under four spellings: the
     `microsoft.graph.eventMessage` cast, the `eventMessageRequest` cast, the
     `eventMessageResponse` cast, and uncast. All four should work.
   - Collection `$select` on attachments must accept only base-`attachment`
     properties, because the client never asks for derived ones there.
7. **Body-field casing quirks.** `events/{id}/forward` sends PascalCase keys
   (`ToRecipients`, `Comment`). Draft create and update send
   `body.contentType` as `Html`/`Text`. Bind case-insensitively.
8. **Time zones.**
   - Event create and update send **IANA** zone names (`Australia/Sydney`) with
     local wall-clock times.
   - Mailbox settings use the mailbox's **Windows** zone name, read back from
     `mailboxSettings.timeZone`. The client round-trips whatever it gets.
   - `getSchedule` and proposal times are UTC.
   - To Do due dates are `YYYY-MM-DDT00:00:00` with `timeZone: "UTC"`. The
     client parses `dateTime` without a `Z` as **local** time.
9. **Recipients' `Name <addr>` are stripped to bare addresses** before sending.
   `emailAddress.name` is only ever sent for room attendees and the migration
   `from`.
10. **Account migration writes MAPI extended properties.** `POST
    mailFolders/{id}/messages` carries `singleValueExtendedProperties`:
    - `SystemTime 0x0E06` and `SystemTime 0x0039` set the received and sent
      times.
    - `Integer 0x0E07` (PR_MESSAGE_FLAGS) is `'1'` for read and `'0'` for
      unread, which also means "not a draft".

    The façade must map these, or migrated mail lands dated today and shows as
    drafts.
11. **Uploads and downloads that bypass the bearer.** Two absolute URLs are
    requested by an interceptor-free Dio with no `Authorization` header, so
    both must be self-authorising (signed, short-lived):
    - the To Do `createUploadSession` `uploadUrl`, which takes one `PUT` of
      the whole file;
    - the `302 Location` from `shares/{id}/driveItem/content`.

## Client error expectations

The façade must emit what the client already handles. The behaviour comes from
`AuthInterceptor`, `RetryInterceptor`, `GraphApiDatasourceImpl._mapDioException`
and the per-call handlers recorded in each operation's `clientErrorHandling`.

**Error envelope.** The client reads only `error.message`, from
`{"error": {"code": "...", "message": "..."}}`, and shows it to the user. It
never branches on `error.code`. Graph's `code` values (`ErrorItemNotFound`,
`ErrorInvalidRequest`, `ErrorAccessDenied` …) should still be emitted for
fidelity. Bodies may be JSON or empty; the client decodes either.

| Status | What the client does | What Nightware should do |
|---|---|---|
| **401** | `AuthInterceptor` refreshes the token and retries once, marking the retry. A 401 on the retry, or with no refresh token, fires `onAuthFailure`, which shows the re-auth banner. Then `AuthException`. | Return 401 only for a bad or expired bearer, never for "forbidden". Otherwise every request does a needless token refresh, and a persistent one flags the account for re-auth. |
| **403** | Final, never retried. Silent fallback in several places: `users/{id}` lookup → no card. Mailbox probe → "no access". Signature profile → nothing to prefill. `/places` → findRooms. Migration create → retry without `from`. `events/{id}/forward` → the client emails the invite instead. Elsewhere it surfaces as `ServerException`. | Use for authorisation failures, such as a shared mailbox without a grant. |
| **404** | Silent fallback: directory lookup → no card. Probe → "not a mailbox". `/places` → findRooms. Message-level RSVP → event-level RSVP. Linked-event cast → next spelling. **Delta: drop the token and re-bootstrap.** | Standard not-found. |
| **400** | Message-level RSVP → fall back. Linked-event cast → next spelling. Conversation `in (...)` filter → one request per conversation. Expansion with `parentFolderId ne` → retry without it. Migration create → retry without `from`. `forward` → email the invite instead. **Delta: drop the token and re-bootstrap.** | Use `400 invalidRequest` for unsupported OData, as the architecture says. The fallbacks mean a 400 degrades gracefully where a 5xx would not. |
| **409** | Child-folder create: list children and use the folder with the same `displayName`. | Duplicate folder name. |
| **410** | **Delta: drop the token and re-bootstrap**, as Graph does with `syncStateNotFound`. | An expired or undecodable delta token. A 400 or 404 is handled the same way. |
| **429 / 503** | `RetryInterceptor` retries at most 5 times. It honours `Retry-After` **only as integer seconds**; otherwise it backs off exponentially from 2 s, capped at 30 s, plus jitter. After that, `ServerException`. | Send `Retry-After: <seconds>`, never an HTTP-date. |
| **Other 5xx** | `ServerException(message, status)`, not retried. Delta counts a strike, and three strikes re-bootstrap. | Graph error envelope. |
| **Transport** | Connect or receive timeout after 30 s (60 s for the upload PUT, 2 min for the share download) → `NetworkException`. | Answer list and delta pages well inside 30 s. |

Delta specifics (`mail_poller_cubit.dart:850`): a status of **400, 404 or 410**,
or a walk that ends without `@odata.deltaLink`, is fatal. The client clears the
stored token, and the next cycle sends an initial delta. Any other failure
counts toward a three-strike streak.

## Non-Graph dependencies

These are not Graph routes, but Nightware has to serve or point at them.

| What | Client code | Notes |
|---|---|---|
| OAuth authorize | `microsoft_auth_service.dart:252` (`$_baseUrl/authorize`) | `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize`. PKCE S256, `state`, `response_mode=query`, `scope` = the Graph scope URIs. 0.4 makes this the discovered `authorizationEndpoint`. |
| OAuth token | `microsoft_auth_service.dart:366, :421` | `POST …/token`, form-encoded `grant_type=authorization_code` or `refresh_token`. The refresh response may omit `refresh_token`. |
| Discovery | 0.4 hook: `/.well-known/nightware` | Returns `{graphBaseUrl, graphBetaBaseUrl?, authority/authorizationEndpoint, tokenEndpoint, clientId, scopes}`. Owned by Phase 1. |
| Upload session PUT | `graph_api_datasource_impl.dart:3040` | Inventoried as `tasks.attachments.upload-session-put` (`external`). |
| Share download redirect | `graph_drive_datasource_impl.dart:144, :168` | The `Location` of `files.shares.drive-item-content`. |

## Where the phase plans and the client disagree

The client wins. These are the differences from `docs/phases/*.md`.

- **Phase 2:** `$skip` is used only on folder message lists. `messages/{id}`
  never uses `internetMessageHeaders` together with `$expand`; the headers come
  from a separate `$select=internetMessageHeaders,body` probe.
  `singleValueExtendedProperties` is only ever *written*, by migration, never
  read.
- **Phase 3:**
  - No `PATCH` of `flag` or `categories` exists. Only `isRead` and draft
    fields are patched. `flag` is read, through the list projection and delta.
  - `DELETE messages/{id}` is only used for drafts. A "delete" is a move to
    `deleteditems`.
  - `POST mailFolders` (a root create) is never used. Root folders are
    `childFolders` of `msgfolderroot`.
  - Reply and forward send `message.body` and **no `comment`**. The body
    already contains the quoted original, so the server must not quote it
    again. That limits how much `ReplyComposer` should add.
- **Phase 4:** The client uses `messages/{id}/microsoft.graph.eventMessage/event`
  and two more casts, plus `events?$filter=iCalUId eq '…'`. `calendarView` is
  never paged by the client.
- **Phase 5A:**
  - `GET /users` has **no** `$filter` or `$search`. It is a full directory
    walk with `$top=999` and nextLink.
  - `GET people` has **no** `$search`.
  - Contacts are read-only.
- **Phase 5B:**
  - To Do: no task or list `DELETE`, no list create, and no `GET` of a single
    task.
  - Mailbox settings read and write only `automaticRepliesSetting` and
    `timeZone`; `language` and `workingHours` are never touched.
  - Threat submission is Microsoft-only once the 0.4 hook lands. `/shares` was
    too, until Phase 7 (below).

### Phase 5B: deferred operations and backend decisions

**Deferred (ADR 0001 §43).** `security.threat-submission.email-threats.create`
is not implemented: the client gates it off for NightMail Server accounts. Its
route is mapped and answers a deliberate `501 notImplemented` (not the
unknown-route `400`). The conformance harness has no "deferred" state, so its
fixture reports as skipped ("no arrangement");
`Arrangements/DeferredOperationsTests.cs` replays it, asserts the 501, and fails
if it is ever arranged without being removed from that list.

**Phase 7: `/shares` for Nextcloud.** `files.shares.drive-item` and
`files.shares.drive-item-content` are implemented for links below the server's
advertised file sites (`filesBaseUrls` in discovery): `{site}/s/{token}` (public
share, read anonymously through public-share WebDAV) and `{site}/f/{fileId}`
(internal link, found by file id in the caller's Files). A link elsewhere is
404, a malformed share id 400, a password-protected share 403, a folder a
`driveItem` with the `folder` facet. `/content[?format=pdf]` answers **302** to
`{graphBaseUrl}/shares/{id}/driveItem/$value[?format=pdf]&authtoken=…`, a
self-authorising URL (ADR §39) valid for 5 minutes (or the token's remaining
life) that streams the file, or the Collabora-rendered PDF of an Office file. A
`format=pdf` the server cannot produce is 400 `notSupported` on the first leg.
The `driveItem` fixture no longer carries `file.hashes` (Nextcloud has no
`quickXorHash`; the client reads only `file.mimeType`).

**Automatic replies → Stalwart `VacationResponse`.** It holds one message and
answers every sender, so Graph's audience and internal/external split cannot be
stored:

- On write, the **external** message is stored (the internal one only when the
  external is empty). It is the text written for people outside the
  organisation, whom Stalwart will answer too; storing the internal one could
  send internal detail to strangers. NightMail sends both equal unless the user
  asked for separate messages.
- On read, both message fields carry the stored text and `externalAudience` is
  `all`, which is what the server actually does.
- `scheduled` maps to `fromDate`/`toDate`; `scheduled` without dates reads back
  as `alwaysEnabled` (both mean "on").
- Schedules are returned as wall clock in the mailbox's zone (the Windows id
  `timeZone` reports), as Graph does.

**Time zone → Keycloak `zoneinfo`.** Accepted as a Windows or IANA id, stored
as IANA (it is also an OIDC claim), returned as the Windows id; UTC when unset or
when the mailbox is not a directory user. The directory is read and written by
the façade's service account, so the mail store (acting as the caller) gates
every mailbox-settings request, and the time zone can only be changed for the
caller's own mailbox (403 otherwise).

**To Do.** Lists are the mailbox's VTODO calendars; the caller's default list
(`tasks/`, VTODO only) is created on first listing when they own none. Due and
completion dates are emitted as that day's midnight labelled `UTC`. Attachments
are files in the owner's Nextcloud Files (`Nightware/Task attachments/{uuid}/`)
referenced from the VTODO by `ATTACH` (RFC 8607 `FILENAME`/`SIZE`).

**Upload URL (`tasks.attachments.upload-session-put`).**
`{graphBaseUrl}/users/{mailbox}/todo/lists/{list}/tasks/{task}/attachmentSessions/{session}/content?authtoken=…`.
`authtoken` is the caller's access token sealed with AES-256-GCM (key derived
with HKDF from `DeltaLinks:SigningKey`), bound to exactly that method and path,
and valid until the earlier of the session's expiry (15 min) and the token's
`exp`. The PUT must carry the whole file; `Content-Range` is optional, and a body
of a different length than the session declared is refused (400/413).

## Fixture format

`tests/Nightware.Conformance.Tests/Fixtures/<operation-id>.json`, one per
operation:

```json
{
  "operationId": "mail.folders.list",
  "provenance": "nightmail test/data/datasources/remote/graph_api_folders_test.dart:20-32 …",
  "request":  { "method": "GET", "path": "/v1.0/me/mailFolders",
                "query": { "$select": "id,displayName,…", "$top": "100" },
                "headers": { "Authorization": "Bearer {token}", "Accept": "application/json" },
                "body": null },
  "response": { "status": 200, "headers": { "Content-Type": "application/json; …" },
                "body": { "@odata.context": "…", "value": [ … ], "@odata.nextLink": "https://graph.microsoft.com/v1.0/…" } }
}
```

- `request.path` includes the version segment (`/v1.0` or `/beta`) and is
  relative to the server root. `query` values are decoded strings.
- A JSON response is in `response.body`. Raw text payloads such as MIME are a
  string in `body`. Binary payloads use `response.bodyBase64`. Empty bodies
  (202/204/302) are `null`.
- Absolute URLs in fixtures (`@odata.nextLink`, `@odata.deltaLink`,
  `uploadUrl`, `messageUrl`, `@odata.context`) use Graph's host. Conformance
  tests should compare them **modulo the configured base URL**, and treat ids,
  etags, tokens and `@odata.context` as opaque.
- All data is synthetic (`example.com`).
- Where a NightMail test exercises the call, the body follows that test's
  shape, and `provenance` cites the test as `file:line 'test name'`.
- Otherwise the body is a realistic Graph v1.0 shape, keeping Graph's casing
  and annotations and trimmed to what the client reads. Its `provenance`
  cites the client code that consumes it.
- None of the bodies is a verbatim copy of Microsoft's documentation. They are
  hand-built and should be checked against a live Graph response if one is
  ever available.

## Operations

<!-- BEGIN GENERATED -->

| Phase | Operations |
|---|---|
| Phase 2 — Mail, read side | 14 |
| Phase 3 — Mail, write side | 21 |
| Phase 4 — Calendar, scheduling and rooms | 21 |
| Phase 5A — Directory, contacts, people | 6 |
| Phase 5B — Tasks, mailbox settings, files, threat submission | 14 |
| Phase 11 — JSON batching (Graph parity) | 2 |
| Phase 12 — Mail (Graph parity) | 19 |
| Phase 13 — Calendar (Graph parity) | 27 |
| Phase 14 — Contacts (Graph parity) | 18 |
| Phase 15 — To Do (Graph parity) | 19 |
| Phase 16 — Change notifications (Graph parity) | 10 |
| Phase 18 — Settings, aliases, single items and $count (Graph parity) | 26 |
| Phase 19 — Photos, MIME, copies, $count segments and more aliases (Graph parity) | 81 |
| Phase 20 — OneDrive over Nextcloud Files (Graph parity, ADR 0008) | 30 |
| Phase 21 — The directory: groups, manager, organization (Graph parity) | 14 |
| Phase 23 — Chats over Nextcloud Talk (Graph parity, ADR 0009) | 22 |
| Phase 24 — Teams and channels over Nextcloud Teams and Talk (Graph parity, ADR 0009) | 34 |
| Phase 25 — Presence over Nextcloud user status (Graph parity, ADR 0009) | 8 |
| Phase 26 — Online meetings and calls over Talk (Graph parity, ADR 0009) | 14 |
| Phase 27 — OneDrive, calendar, directory and settings gaps | 12 |
| **Total** | **413** |

### Phase 2 — Mail, read side

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`mail.folders.list`](#mailfolderslist) | GET | v1.0 | `{mailbox}/mailFolders` | nextLink |
| [`mail.folders.get`](#mailfoldersget) | GET | v1.0 | `{mailbox}/mailFolders/{folderIdOrWellKnownName}` | none |
| [`mail.folders.list-children`](#mailfolderslist-children) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/childFolders` | nextLink |
| [`mail.messages.list-in-folder`](#mailmessageslist-in-folder) | GET | v1.0 | `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages` | none |
| [`mail.messages.list`](#mailmessageslist) | GET | v1.0 | `{mailbox}/messages` | none |
| [`mail.messages.list-by-conversation`](#mailmessageslist-by-conversation) | GET | v1.0 | `{mailbox}/messages` | nextLink |
| [`mail.messages.search`](#mailmessagessearch) | GET | v1.0 | `{mailbox}/messages` | none |
| [`mail.messages.delta`](#mailmessagesdelta) | GET | v1.0 | `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages/delta` | deltaLink |
| [`mail.messages.get`](#mailmessagesget) | GET | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.get-plain-text-body`](#mailmessagesget-plain-text-body) | GET | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.get-mime`](#mailmessagesget-mime) | GET | v1.0 | `{mailbox}/messages/{messageId}/$value` | none |
| [`mail.attachments.list`](#mailattachmentslist) | GET | v1.0 | `{mailbox}/messages/{messageId}/attachments` | none |
| [`mail.attachments.get`](#mailattachmentsget) | GET | v1.0 | `{mailbox}/messages/{messageId}/attachments/{attachmentId}` | none |
| [`mail.attachments.get-value`](#mailattachmentsget-value) | GET | v1.0 | `{mailbox}/messages/{messageId}/attachments/{attachmentId}/$value` | none |

#### `mail.folders.list`

**GET v1.0 `{mailbox}/mailFolders`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `id,displayName,totalItemCount,unreadItemCount,parentFolderId,isHidden,childFolderCount`; `$top`: `100`
- Client reads: `value[].id`, `value[].displayName`, `value[].totalItemCount`, `value[].unreadItemCount`, `value[].parentFolderId`, `value[].isHidden`, `value[].childFolderCount`, `@odata.nextLink`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:750`
- Notes: Top-level folders only (children come from childFolders). Follows @odata.nextLink for up to 25 pages via _fetchAllGraphPages; the link is read by regex from the raw body and must pass isGraphUrl (see endpoints.md, cross-cutting requirement 1) or paging silently stops. totalItemCount/unreadItemCount drive the folder badges.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.list.json`

#### `mail.folders.get`

**GET v1.0 `{mailbox}/mailFolders/{folderIdOrWellKnownName}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id`
- Client reads: `id`
- On error: Well-known lookups: any failure → lookup treated as incomplete and retried next time (no exception). Probe: 403 or 404 → 'no access' (false); anything else → _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:304`, `…/remote/graph_api_datasource_impl.dart:2154`, `…/remote/graph_api_datasource_impl.dart:3489`
- Notes: Resolves well-known names to ids: inbox, sentitems, deleteditems, junkemail, archive (getSpecialFolderIds, _fetchExpansionExcludedIds). Also the shared-mailbox access probe GET /users/{address}/mailFolders/inbox?$select=id (users form, address percent-encoded by Uri.encodeComponent, i.e. '@' → %40). The returned id must compare equal (string) to the parentFolderId the same server stamps on messages in that folder — Graph itself fails this (AQMk vs AAMk), which is why the client also sends parentFolderId ne '<id>' server-side.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.get.json`

#### `mail.folders.list-children`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/childFolders`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `id,displayName,totalItemCount,unreadItemCount,parentFolderId,isHidden,childFolderCount`; `$top`: `100`
- Client reads: `value[].id`, `value[].displayName`, `value[].totalItemCount`, `value[].unreadItemCount`, `value[].parentFolderId`, `value[].isHidden`, `value[].childFolderCount`, `@odata.nextLink`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:771`
- Notes: Same projection and paging as mail.folders.list, one parent at a time (the client walks the tree level by level). Also used after a 409 on folder create to find the existing folder by displayName.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.list-children.json`

#### `mail.messages.list-in-folder`

**GET v1.0 `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$select` (variant 2): `id`; `$filter`: `isRead eq false`; `$orderby`: `receivedDateTime desc`; `$top`: `25`, `10`, `5`, `100`, `1..100`; `$skip`: `0`, `25`, `50`; `$count`: `True`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:145`, `…/remote/graph_api_datasource_impl.dart:2729`
- Notes: Folder page (getEmails): always sends $top, $skip (0 on the first page — offset paging, not nextLink), $select=list projection, $orderby=receivedDateTime desc; $filter only from the AI agent tool (isRead eq false). @odata.nextLink is ignored here. emptyFolder sends only $top=100&$select=id and loops until a page has < 100 rows (it deletes as it goes, so it always re-reads page 1). After each page the client expands every conversationId on it via mail.messages.list-by-conversation. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.list-in-folder.json`

#### `mail.messages.list`

**GET v1.0 `{mailbox}/messages`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$filter`: `isRead eq false`; `$orderby`: `receivedDateTime desc`; `$top`: `25`, `1..100`; `$skip`: `0`, `25`; `$count`: `True`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:146`
- Notes: getEmails with folderId == null (the AI agent tool without a folder): the newest mail of the whole mailbox. Same parameters as mail.messages.list-in-folder. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.list.json`

#### `mail.messages.list-by-conversation`

**GET v1.0 `{mailbox}/messages`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (variant 1): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$select` (variant 2): `id`; `$filter`: `conversationId in ('AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=','AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAKF3YB0e1kBVtXQUq0f7Iks=')`; `$filter`: `conversationId in ('AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=','AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAKF3YB0e1kBVtXQUq0f7Iks=') and parentFolderId ne 'AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEKAAA=' and parentFolderId ne 'AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEiAAA='`; `$filter`: `conversationId eq 'AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI='`; `$filter`: `conversationId eq 'AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=' and parentFolderId ne 'AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEKAAA=' and parentFolderId ne 'AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEiAAA='`; `$top`: `200`, `10`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`, `@odata.nextLink`
- On error: Expansion failures are swallowed (the folder page is returned without cross-folder rows). _conversationMessageIds: 401/403 → AuthException path, others recorded and ignored.
- Call sites: `…/remote/graph_api_datasource_impl.dart:354`, `…/remote/graph_api_datasource_impl.dart:439`, `…/remote/graph_api_datasource_impl.dart:1778`
- Notes: Cross-folder thread expansion. Chunked: up to 15 quoted conversation ids per `in (...)` (the URL is ~2.5 KB); the `in` form follows @odata.nextLink (up to 25 pages), the `eq` forms read one page. If the `in` request fails the client falls back to one `eq` request per id; if a filter with `parentFolderId ne` clauses fails it retries without them. No $orderby, no $skip. The meeting-invite path (_conversationMessageIds) sends $filter=conversationId eq '...'&$select=id&$top=10 and must NOT be asked for eventMessage-only properties. Server must support `in` over conversationId and `ne` over parentFolderId combined with `and`.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.list-by-conversation.json`

#### `mail.messages.search`

**GET v1.0 `{mailbox}/messages`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$search`: `"quarterly numbers"`; `$search`: `"from:alex@example.com"`; `$search`: `"subject:invoice hasAttachments:true"`; `$search`: `"to:sam@example.com budget"`; `$top`: `50`, `1..100`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:474`
- Notes: Always mailbox-wide (never per folder). $search is the user's text wrapped in double quotes; the only rewrite is has:attachment → hasAttachments:true. Embedded double quotes are not escaped. No $orderby/$skip; nextLink ignored. See odata-subset.md §$search.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.search.json`

#### `mail.messages.delta`

**GET v1.0 `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages/delta`** — forms: me, users; success: 200; paging: deltaLink

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$top`: `50`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`, `value[].@removed.reason`, `@odata.nextLink`, `@odata.deltaLink`
- On error: ServerException with 400, 404 or 410 (or DeltaResyncRequiredException) → the stored token is discarded and the next cycle re-bootstraps with an initial delta. Other failures count toward a 3-strike streak.
- Call sites: `…/remote/graph_api_datasource_impl.dart:3135`
- Notes: Initial request: $select=list projection&$top=50, never a $filter (a date filter would be frozen into the token). Subsequent pages and later syncs GET the absolute @odata.nextLink / @odata.deltaLink verbatim (after unescaping \/ and \u0026), so the link must carry its own state, and must pass isGraphUrl (at 39ab812: https on *.graph.microsoft.com; after the 0.4 hook: same scheme/host/port as the account's Graph base URL) or the client drops it. Client follows at most 20 pages per call, then stores the nextLink as the cursor. The folder is always the well-known name 'inbox' (mail_poller_cubit.dart:524, :1399); other folders are re-listed with mail.messages.list-in-folder instead. Item kinds: whole message (has receivedDateTime) → upsert; `@removed: {reason: 'deleted'}` → removed; `@removed: {reason: 'changed'}` → moved out of folder; item WITHOUT receivedDateTime → partial update, only isRead and flag.flagStatus are read from it. Ending without a deltaLink → DeltaResyncRequiredException.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.delta.json`

#### `mail.messages.get`

**GET v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Query: `$expand`: `attachments($select=id,name,contentType,size,isInline)`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `@odata.type`, `id`, `subject`, `from.emailAddress.{address,name}`, `{toRecipients|ccRecipients|bccRecipients}[].emailAddress.address`, `body.content`, `body.contentType`, `bodyPreview`, `isRead`, `flag.flagStatus`, `receivedDateTime`, `sentDateTime`, `importance`, `conversationId`, `hasAttachments`, `parentFolderId`, `meetingMessageType (eventMessage)`, `startDateTime.{dateTime,timeZone} (eventMessage)`, `endDateTime.{dateTime,timeZone} (eventMessage)`, `location.displayName (eventMessage)`, `isAllDay (eventMessage)`, `proposedNewTime.{start,end}.dateTime (eventMessageResponse)`, `attachments[].{id,name,contentType,size,isInline,@odata.type}`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:501`, `…/remote/graph_api_datasource_impl.dart:1541`
- Notes: Reading-pane fetch: NO $select (Graph omits @odata.type on derived types when $select is present, and the client keys meeting UI off @odata.type/meetingMessageType). Must return the derived type's properties for eventMessage/eventMessageRequest/eventMessageResponse. With Prefer outlook.timezone="UTC" the eventMessage startDateTime/endDateTime are UTC wall-clock without 'Z'. Inline attachments (isInline true) are then fetched one by one via mail.attachments.get for contentId/contentBytes; a non-inline text/calendar or *.ics attachment on a non-eventMessage is fetched for a 'published event' banner. The meeting-update path (:1541) GETs the message with no query/headers and reads conversationId, startDateTime and @odata.type.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.get.json`

#### `mail.messages.get-plain-text-body`

**GET v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `internetMessageHeaders,body`
- Headers: `Prefer: outlook.body-content-type="text"`
- Client reads: `internetMessageHeaders[].{name,value}`, `body.content`
- On error: All failures swallowed (falls back to the HTML body).
- Call sites: `…/remote/graph_api_datasource_impl.dart:623`
- Notes: Issued in parallel with mail.messages.get. If the first Content-Type header in internetMessageHeaders starts with text/plain, body.content (rendered as text because of the Prefer header) replaces the HTML body. Any failure → ignored.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.get-plain-text-body.json`

#### `mail.messages.get-mime`

**GET v1.0 `{mailbox}/messages/{messageId}/$value`** — forms: me, users; success: 200; paging: none

- Client reads: `<raw RFC 822 bytes>`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2946`
- Notes: Raw MIME (forward-as-attachment, phishing inbox report, account migration). Read as bytes; Content-Type not inspected.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.get-mime.json`

#### `mail.attachments.list`

**GET v1.0 `{mailbox}/messages/{messageId}/attachments`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,name,contentType`; `$select` (variant 2): `id,name,size`
- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:1650`, `…/remote/graph_api_datasource_impl.dart:2516`, `…/remote/graph_api_datasource_impl.dart:2562`
- Notes: $select uses only base `attachment` properties (selecting isInline/contentId here is a 400 on Graph). Used to find a text/calendar part (name *.ics or contentType text/calendar) and to reconcile draft / forward-draft attachments by name+size.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.list.json`

#### `mail.attachments.get`

**GET v1.0 `{mailbox}/messages/{messageId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `@odata.type`, `id`, `name`, `contentType`, `size`, `isInline`, `contentId`, `contentBytes`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:536`, `…/remote/graph_api_datasource_impl.dart:596`, `…/remote/graph_api_datasource_impl.dart:1672`, `…/remote/graph_api_datasource_impl.dart:2760`
- Notes: Full attachment, no query. fileAttachment must carry contentBytes (base64) and contentId for inline images. For an itemAttachment (attached email) contentBytes is absent and the client falls back to mail.attachments.get-value.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.get.json`

#### `mail.attachments.get-value`

**GET v1.0 `{mailbox}/messages/{messageId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Client reads: `<raw bytes>`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2787`
- Notes: Only reached for attachments whose GET has no contentBytes — an itemAttachment (attached email), served as raw MIME (.eml). Empty body → error.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.get-value.json`

### Phase 3 — Mail, write side

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`mail.messages.update-read-state`](#mailmessagesupdate-read-state) | PATCH | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.drafts.create`](#maildraftscreate) | POST | v1.0 | `{mailbox}/messages` | none |
| [`mail.drafts.update`](#maildraftsupdate) | PATCH | v1.0 | `{mailbox}/messages/{draftId}` | none |
| [`mail.messages.delete`](#mailmessagesdelete) | DELETE | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.move`](#mailmessagesmove) | POST | v1.0 | `{mailbox}/messages/{messageId}/move` | none |
| [`mail.messages.permanent-delete`](#mailmessagespermanent-delete) | POST | v1.0 | `{mailbox}/messages/{messageId}/permanentDelete` | none |
| [`mail.messages.create-in-folder`](#mailmessagescreate-in-folder) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages` | none |
| [`mail.attachments.create`](#mailattachmentscreate) | POST | v1.0 | `{mailbox}/messages/{messageId}/attachments` | none |
| [`mail.attachments.delete`](#mailattachmentsdelete) | DELETE | v1.0 | `{mailbox}/messages/{messageId}/attachments/{attachmentId}` | none |
| [`mail.messages.send-draft`](#mailmessagessend-draft) | POST | v1.0 | `{mailbox}/messages/{messageId}/send` | none |
| [`mail.send-mail`](#mailsend-mail) | POST | v1.0 | `{mailbox}/sendMail` | none |
| [`mail.messages.reply`](#mailmessagesreply) | POST | v1.0 | `{mailbox}/messages/{messageId}/reply` | none |
| [`mail.messages.reply-all`](#mailmessagesreply-all) | POST | v1.0 | `{mailbox}/messages/{messageId}/replyAll` | none |
| [`mail.messages.create-reply`](#mailmessagescreate-reply) | POST | v1.0 | `{mailbox}/messages/{messageId}/createReply` | none |
| [`mail.messages.create-reply-all`](#mailmessagescreate-reply-all) | POST | v1.0 | `{mailbox}/messages/{messageId}/createReplyAll` | none |
| [`mail.messages.forward`](#mailmessagesforward) | POST | v1.0 | `{mailbox}/messages/{messageId}/forward` | none |
| [`mail.messages.create-forward`](#mailmessagescreate-forward) | POST | v1.0 | `{mailbox}/messages/{messageId}/createForward` | none |
| [`mail.folders.create-child`](#mailfolderscreate-child) | POST | v1.0 | `{mailbox}/mailFolders/{parentFolderIdOrWellKnownName}/childFolders` | none |
| [`mail.folders.update`](#mailfoldersupdate) | PATCH | v1.0 | `{mailbox}/mailFolders/{folderId}` | none |
| [`mail.folders.delete`](#mailfoldersdelete) | DELETE | v1.0 | `{mailbox}/mailFolders/{folderId}` | none |
| [`mail.folders.move`](#mailfoldersmove) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/move` | none |

#### `mail.messages.update-read-state`

**PATCH v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`
- Request body: `isRead`
- Client reads: `id`, `subject`, `from.emailAddress.address`, `from.emailAddress.name`, `toRecipients[].emailAddress.{address,name}`, `ccRecipients[].emailAddress.{address,name}`, `bccRecipients[].emailAddress.{address,name}`, `bodyPreview`, `isRead`, `flag.flagStatus`, `receivedDateTime`, `sentDateTime`, `importance`, `conversationId`, `hasAttachments`, `parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:694`
- Notes: Only isRead is ever PATCHed on a received message (no flag or categories PATCH exists in the client). Response must be 200 with the updated message (honouring $select); an empty body is an error.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.update-read-state.json`

#### `mail.drafts.create`

**POST v1.0 `{mailbox}/messages`** — forms: me, users; success: 201; paging: none

- Request body: `subject`; `body.contentType ('Html'|'Text', capitalised)`; `body.content`; `{toRecipients|ccRecipients|bccRecipients}[].emailAddress.address`
- Client reads: `id`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3674`
- Notes: Server-side draft for autosave. contentType is sent capitalised ('Html'/'Text') — accept case-insensitively. cc/bcc omitted when empty. Attachments then added via mail.attachments.create.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.drafts.create.json`

#### `mail.drafts.update`

**PATCH v1.0 `{mailbox}/messages/{draftId}`** — forms: me, users; success: 200; paging: none

- Request body: `subject`; `body.contentType ('Html'|'Text')`; `body.content`; `toRecipients`; `ccRecipients`; `bccRecipients`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3716`
- Notes: Every field always sent (empty arrays clear). Response body ignored; the draft id is assumed unchanged — the server must keep a draft's id stable across PATCH (phase 3 'replacement + destroy' must preserve the Graph id). Followed by mail.attachments.list + create/delete to reconcile attachments.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.drafts.update.json`

#### `mail.messages.delete`

**DELETE v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 204; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3740`
- Notes: Only used to delete a server draft (deleteServerDraft). Deleting received mail is a move to deleteditems (mail.messages.move).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.delete.json`

#### `mail.messages.move`

**POST v1.0 `{mailbox}/messages/{messageId}/move`** — forms: me, users; success: 201; paging: none

- Request body: `destinationId (folder id or well-known name: deleteditems, junkemail, inbox)`
- Client reads: `id`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2606`, `…/remote/graph_api_datasource_impl.dart:2619`, `…/remote/graph_api_datasource_impl.dart:2632`, `…/remote/graph_api_datasource_impl.dart:2712`, `…/remote/graph_api_datasource_impl.dart:2742`
- Notes: Delete = move to 'deleteditems'; report junk = move to 'junkemail'; not junk = move to 'inbox'. Response is 201 with the moved message; the client reads the NEW id from it (Graph assigns a new id on move; Nightware's stable ids may return the same id, which the client accepts).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.move.json`

#### `mail.messages.permanent-delete`

**POST v1.0 `{mailbox}/messages/{messageId}/permanentDelete`** — forms: me, users; success: 204; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2739`
- Notes: emptyFolder(permanentDelete: true), one message at a time. No request body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.permanent-delete.json`

#### `mail.messages.create-in-folder`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages`** — forms: me, users; success: 201; paging: none

- Request body: `subject`; `body.{contentType,content}`; `from.emailAddress.{address,name} (first attempt only)`; `toRecipients[].emailAddress.address`; `ccRecipients[].emailAddress.address`; `singleValueExtendedProperties[] {id:'SystemTime 0x0E06'|'SystemTime 0x0039'|'Integer 0x0E07', value}`; `isRead`
- Client reads: `id`
- On error: 400 or 403 → retried once without `from`; 401 → AuthException; anything else → ServerException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:3567`, `…/remote/graph_api_datasource_impl.dart:3586`
- Notes: Account migration import. receivedDateTime/sentDateTime are conveyed via MAPI extended properties (0x0E06 PR_MESSAGE_DELIVERY_TIME, 0x0039 PR_CLIENT_SUBMIT_TIME, ISO-8601 UTC with ms) and PR_MESSAGE_FLAGS 0x0E07 ('1' read, '0' unread; clears MSGFLAG_UNSENT so it is not a draft). The façade must honour these three ids. body.contentType is 'html' or 'text'.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.create-in-folder.json`

#### `mail.attachments.create`

**POST v1.0 `{mailbox}/messages/{messageId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type ('#microsoft.graph.fileAttachment')`; `name`; `contentType`; `contentBytes (base64)`; `isInline (only when true)`; `contentId (inline only)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2539`, `…/remote/graph_api_datasource_impl.dart:2587`
- Notes: Adds a file to a draft (create-draft, createReply/createForward drafts, migration import). Response body ignored. No upload session is used for mail attachments (large files go inline too).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.create.json`

#### `mail.attachments.delete`

**DELETE v1.0 `{mailbox}/messages/{messageId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2531`, `…/remote/graph_api_datasource_impl.dart:2577`
- Notes: Removes a draft attachment (autosave reconciliation; attachments the user dropped from a createForward draft).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.delete.json`

#### `mail.messages.send-draft`

**POST v1.0 `{mailbox}/messages/{messageId}/send`** — forms: me, users; success: 202; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2421`, `…/remote/graph_api_datasource_impl.dart:2487`
- Notes: Sends a createReply/createReplyAll/createForward draft after attachments were added. No body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.send-draft.json`

#### `mail.send-mail`

**POST v1.0 `{mailbox}/sendMail`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject`; `message.body.contentType ('html'|'text')`; `message.body.content`; `message.toRecipients[].emailAddress.address`; `message.ccRecipients (when non-empty)`; `message.bccRecipients (when non-empty)`; `message.attachments[] {@odata.type fileAttachment, name, contentType, contentBytes, isInline?, contentId?}`; `saveToSentItems (always true)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2333`
- Notes: New message send (no draft). Addresses are bare ("Name <a@b>" stripped to a@b). Inline images carry isInline+contentId and are referenced from the HTML as cid:.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.send-mail.json`

#### `mail.messages.reply`

**POST v1.0 `{mailbox}/messages/{messageId}/reply`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject (optional; Graph builds 'RE: …' when absent)`; `message.body.{contentType,content}`; `message.toRecipients / ccRecipients / bccRecipients (each only when non-empty)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2406`, `…/remote/graph_api_datasource_impl.dart:2405`
- Notes: Sends a reply with no new attachments. The client sends the whole body it composed (quoted original included) in message.body and no `comment`; the server must NOT append the original again. Recipients, when given, replace Graph's defaults. Reply must thread: same conversationId, In-Reply-To/References set.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.reply.json`

#### `mail.messages.reply-all`

**POST v1.0 `{mailbox}/messages/{messageId}/replyAll`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject (optional; Graph builds 'RE: …' when absent)`; `message.body.{contentType,content}`; `message.toRecipients / ccRecipients / bccRecipients (each only when non-empty)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2406`, `…/remote/graph_api_datasource_impl.dart:2404`
- Notes: Sends a reply-all with no new attachments. The client sends the whole body it composed (quoted original included) in message.body and no `comment`; the server must NOT append the original again. Recipients, when given, replace Graph's defaults. Reply must thread: same conversationId, In-Reply-To/References set.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.reply-all.json`

#### `mail.messages.create-reply`

**POST v1.0 `{mailbox}/messages/{messageId}/createReply`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.{to,cc,bcc}Recipients?`
- Client reads: `id`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2412`, `…/remote/graph_api_datasource_impl.dart:2411`
- Notes: Reply that carries new attachments: create the reply draft (body as composed — not to be re-quoted by the server), add attachments, then mail.messages.send-draft. Only `id` is read.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.create-reply.json`

#### `mail.messages.create-reply-all`

**POST v1.0 `{mailbox}/messages/{messageId}/createReplyAll`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.{to,cc,bcc}Recipients?`
- Client reads: `id`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2412`, `…/remote/graph_api_datasource_impl.dart:2410`
- Notes: Reply that carries new attachments: create the reply draft (body as composed — not to be re-quoted by the server), add attachments, then mail.messages.send-draft. Only `id` is read.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.create-reply-all.json`

#### `mail.messages.forward`

**POST v1.0 `{mailbox}/messages/{messageId}/forward`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.ccRecipients?`; `message.bccRecipients?`; `toRecipients[].emailAddress.address (top level, not inside message)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2465`
- Notes: Forward with all original attachments and none added. No `comment`: the composed body already embeds the original, so the server must not append it again. toRecipients is a TOP-LEVEL property; cc/bcc are inside message. The server copies the original's attachments.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.forward.json`

#### `mail.messages.create-forward`

**POST v1.0 `{mailbox}/messages/{messageId}/createForward`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.ccRecipients?`; `message.bccRecipients?`; `toRecipients (top level)`
- Client reads: `id`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2475`
- Notes: Forward that removes some original attachments and/or adds new ones: creates the draft (with copies of every original attachment, matched afterwards by name+size via mail.attachments.list), deletes excluded copies, adds new files, then send-draft.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.create-forward.json`

#### `mail.folders.create-child`

**POST v1.0 `{mailbox}/mailFolders/{parentFolderIdOrWellKnownName}/childFolders`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`
- Client reads: `id`
- On error: 409 (duplicate name) → client lists childFolders and returns the existing folder's id; anything else → ServerException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:3439`
- Notes: Top-level folders are created under the well-known name 'msgfolderroot'. Never uses POST {mailbox}/mailFolders.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.create-child.json`

#### `mail.folders.update`

**PATCH v1.0 `{mailbox}/mailFolders/{folderId}`** — forms: me, users; success: 200; paging: none

- Request body: `displayName`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3612`
- Notes: Rename. Response ignored; the folder id must survive the rename.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.update.json`

#### `mail.folders.delete`

**DELETE v1.0 `{mailbox}/mailFolders/{folderId}`** — forms: me, users; success: 204; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3625`
- Notes: Deletes the folder and its contents.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.delete.json`

#### `mail.folders.move`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/move`** — forms: me, users; success: 201; paging: none

- Request body: `destinationId (folder id or 'msgfolderroot')`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3643`
- Notes: Response ignored; the client assumes the folder id is preserved by the move (it returns the input id).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.move.json`

### Phase 4 — Calendar, scheduling and rooms

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`calendar.calendar-view.list`](#calendarcalendar-viewlist) | GET | v1.0 | `{mailbox}/calendarView` | none |
| [`calendar.events.get`](#calendareventsget) | GET | v1.0 | `{mailbox}/events/{eventId}` | none |
| [`calendar.events.list-by-ical-uid`](#calendareventslist-by-ical-uid) | GET | v1.0 | `{mailbox}/events` | none |
| [`calendar.events.create`](#calendareventscreate) | POST | v1.0 | `{mailbox}/events` | none |
| [`calendar.events.update`](#calendareventsupdate) | PATCH | v1.0 | `{mailbox}/events/{eventId}` | none |
| [`calendar.events.delete`](#calendareventsdelete) | DELETE | v1.0 | `{mailbox}/events/{eventId}` | none |
| [`calendar.events.accept`](#calendareventsaccept) | POST | v1.0 | `{mailbox}/events/{eventId}/accept` | none |
| [`calendar.events.tentatively-accept`](#calendareventstentatively-accept) | POST | v1.0 | `{mailbox}/events/{eventId}/tentativelyAccept` | none |
| [`calendar.events.decline`](#calendareventsdecline) | POST | v1.0 | `{mailbox}/events/{eventId}/decline` | none |
| [`calendar.messages.accept`](#calendarmessagesaccept) | POST | v1.0 | `{mailbox}/messages/{messageId}/accept` | none |
| [`calendar.messages.tentatively-accept`](#calendarmessagestentatively-accept) | POST | v1.0 | `{mailbox}/messages/{messageId}/tentativelyAccept` | none |
| [`calendar.messages.decline`](#calendarmessagesdecline) | POST | v1.0 | `{mailbox}/messages/{messageId}/decline` | none |
| [`calendar.events.cancel`](#calendareventscancel) | POST | v1.0 | `{mailbox}/events/{eventId}/cancel` | none |
| [`calendar.events.forward`](#calendareventsforward) | POST | v1.0 | `{mailbox}/events/{eventId}/forward` | none |
| [`calendar.messages.linked-event`](#calendarmessageslinked-event) | GET | v1.0 | `{mailbox}/messages/{messageId}/microsoft.graph.eventMessage/event` | none |
| [`calendar.messages.linked-event-request-cast`](#calendarmessageslinked-event-request-cast) | GET | v1.0 | `{mailbox}/messages/{messageId}/microsoft.graph.eventMessageRequest/event` | none |
| [`calendar.messages.linked-event-response-cast`](#calendarmessageslinked-event-response-cast) | GET | v1.0 | `{mailbox}/messages/{messageId}/microsoft.graph.eventMessageResponse/event` | none |
| [`calendar.messages.linked-event-uncast`](#calendarmessageslinked-event-uncast) | GET | v1.0 | `{mailbox}/messages/{messageId}/event` | none |
| [`calendar.get-schedule`](#calendarget-schedule) | POST | v1.0 | `{mailbox}/calendar/getSchedule` | none |
| [`rooms.places.list`](#roomsplaceslist) | GET | v1.0 | `/places/microsoft.graph.room` | nextLink |
| [`rooms.find-rooms`](#roomsfind-rooms) | GET | beta | `/me/findRooms` | none |

#### `calendar.calendar-view.list`

**GET v1.0 `{mailbox}/calendarView`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,iCalUId,subject,start,end,isAllDay,location,onlineMeeting,bodyPreview,showAs,isOrganizer,organizer,responseStatus,attendees,recurrence,isReminderOn,reminderMinutesBeforeStart,seriesMasterId`; `$select` (variant 2): `id,isOrganizer,responseStatus`; `$select` (variant 3): `id,isOrganizer`; `$select` (variant 4): `id,start`; `$top`: `100`, `50`, `5`; `startDateTime`: `2026-06-01T00:00:00.000Z`; `endDateTime`: `2026-07-01T00:00:00.000Z`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `value[].id`, `value[].iCalUId`, `value[].subject`, `value[].start.dateTime`, `value[].start.timeZone`, `value[].end.dateTime`, `value[].end.timeZone`, `value[].isAllDay`, `value[].location.displayName`, `value[].onlineMeeting.joinUrl`, `value[].onlineMeetingUrl (fallback, not selected)`, `value[].bodyPreview`, `value[].showAs`, `value[].isOrganizer`, `value[].responseStatus.response`, `value[].organizer.emailAddress.{address,name}`, `value[].attendees[].emailAddress.{address,name}`, `value[].attendees[].status.response`, `value[].attendees[].type`, `value[].recurrence.pattern.{type,interval,daysOfWeek}`, `value[].recurrence.range.{type,endDate,numberOfOccurrences}`, `value[].isReminderOn`, `value[].reminderMinutesBeforeStart`, `value[].seriesMasterId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:794`, `…/remote/graph_api_datasource_impl.dart:1029`, `…/remote/graph_api_datasource_impl.dart:1120`, `…/remote/graph_api_datasource_impl.dart:1199`, `…/remote/graph_api_datasource_impl.dart:1262`, `…/remote/graph_api_datasource_impl.dart:1473`, `…/remote/graph_api_datasource_impl.dart:1745`
- Notes: Expanded occurrences in [startDateTime, endDateTime). Both bounds are Dart toIso8601String() in UTC: 'YYYY-MM-DDTHH:MM:SS.mmmZ' (milliseconds, Z). The client NEVER follows @odata.nextLink here — the server should return every event in the window up to $top (100 for the calendar view) in one page. Occurrences have recurrence null and carry seriesMasterId; the client then GETs the master's recurrence (calendar.events.get). The small-window variants locate a meeting from an invitation (±30 min / +2 h, or ±1 min with $top=5). Windows longer than 3,653 days (ten years) are refused with 400 invalidRequest before any expansion: every occurrence in the window is expanded and sorted per request. The client's windows: cache sync 28 days, reminders and commitments 14, month grid 42, scheduling pane ~1 day, invitation lookups hours; the longest is the out-of-office meeting sweep (the out-of-office dates, pickers spanning ~6 years), which only shows an error if refused. With Prefer UTC the start/end are {dateTime without Z, timeZone:'UTC'}; the client appends Z when timeZone is UTC.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-view.list.json`

#### `calendar.events.get`

**GET v1.0 `{mailbox}/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,iCalUId,subject,start,end,isAllDay,location,onlineMeeting,bodyPreview,showAs,isOrganizer,organizer,responseStatus,attendees,recurrence,isReminderOn,reminderMinutesBeforeStart,seriesMasterId`; `$select` (variant 2): `recurrence`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:840`, `…/remote/graph_api_datasource_impl.dart:875`
- Notes: Full event (with Prefer UTC), or a series master's recurrence only ($select=recurrence, no Prefer). recurrence.pattern.type ∈ daily|weekly|absoluteMonthly|relativeMonthly|absoluteYearly|relativeYearly; range.type ∈ endDate|numbered|noEnd.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.get.json`

#### `calendar.events.list-by-ical-uid`

**GET v1.0 `{mailbox}/events`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id`; `$select` (variant 2): `id,isOrganizer`; `$filter`: `iCalUId eq '040000008200E00074C5B7101A82E00800000000A1B2C3D4E5F6D901000000000000000010000000F1E2D3C4B5A69788'`; `$top`: `1`, `5`; `$count`: `True`
- Client reads: `value[].id`, `value[].isOrganizer`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:1430`, `…/remote/graph_api_datasource_impl.dart:1620`
- Notes: Finds the user's own copy of a meeting by the UID from an invitation's text/calendar part. Single quotes inside the literal are doubled. Must match the series master (not occurrences). $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.list-by-ical-uid.json`

#### `calendar.events.create`

**POST v1.0 `{mailbox}/events`** — forms: me, users; success: 201; paging: none

- Request body: `subject`; `isAllDay`; `isReminderOn`; `reminderMinutesBeforeStart?`; `isOnlineMeeting (only when true)`; `onlineMeetingProvider ('teamsForBusiness', only when online)`; `body {contentType:'text', content}?`; `start {dateTime 'YYYY-MM-DDTHH:MM:SS' local wall-clock, timeZone IANA e.g. 'Australia/Sydney'}`; `end {dateTime, timeZone}`; `locations[] {displayName, locationEmailAddress?, locationType 'conferenceRoom'?} (always sent, may be [])`; `location {displayName, …} (always sent; {displayName:''} when none)`; `attendees[] {emailAddress{address,name?}, type 'required'|'resource'} (always sent)`; `recurrence? {pattern{type daily|weekly|absoluteMonthly|absoluteYearly, interval, daysOfWeek?}, range{type endDate|numbered|noEnd, startDate 'YYYY-MM-DD', endDate?|numberOfOccurrences?}}`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:915`
- Notes: start/end timeZone is an IANA zone name (from the device) — not a Windows name. The response is parsed as a full event (no Prefer header, so start/end come back in the given zone). Rooms are attendees of type 'resource' and also locations[] entries.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.create.json`

#### `calendar.events.update`

**PATCH v1.0 `{mailbox}/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Request body: `subject`; `isAllDay`; `isReminderOn`; `reminderMinutesBeforeStart?`; `isOnlineMeeting (only when true)`; `onlineMeetingProvider ('teamsForBusiness', only when online)`; `body {contentType:'text', content}?`; `start {dateTime 'YYYY-MM-DDTHH:MM:SS' local wall-clock, timeZone IANA e.g. 'Australia/Sydney'}`; `end {dateTime, timeZone}`; `locations[] {displayName, locationEmailAddress?, locationType 'conferenceRoom'?} (always sent, may be [])`; `location {displayName, …} (always sent; {displayName:''} when none)`; `attendees[] {emailAddress{address,name?}, type 'required'|'resource'} (always sent)`; `recurrence? {pattern{type daily|weekly|absoluteMonthly|absoluteYearly, interval, daysOfWeek?}, range{type endDate|numbered|noEnd, startDate 'YYYY-MM-DD', endDate?|numberOfOccurrences?}}`; `— or, accepting a proposed time: start/end only, {dateTime 'YYYY-MM-DDTHH:MM:SS.mmm' (no Z), timeZone:'UTC'}`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:951`, `…/remote/graph_api_datasource_impl.dart:1302`
- Notes: Full edit sends every field (empty location/attendees clear them). Updating start/end on a meeting must notify attendees (Graph does; the client sends no separate update). Response parsed as a full event for the edit path; ignored for accept-proposal.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.update.json`

#### `calendar.events.delete`

**DELETE v1.0 `{mailbox}/events/{eventId}`** — forms: me, users; success: 204; paging: none

- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:1180`, `…/remote/graph_api_datasource_impl.dart:1214`
- Notes: Removes a cancelled meeting from the attendee's calendar ('Remove from calendar' on a cancellation).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.delete.json`

#### `calendar.events.accept`

**POST v1.0 `{mailbox}/events/{eventId}/accept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- On error: Inside the invitation fallback chain: 401/403 → AuthException/ServerException surfaced; other failures fall through to the next locator. Direct calls: _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1007`, `…/remote/graph_api_datasource_impl.dart:1052`
- Notes: RSVP on the user's copy of an event.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.accept.json`

#### `calendar.events.tentatively-accept`

**POST v1.0 `{mailbox}/events/{eventId}/tentativelyAccept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- On error: Inside the invitation fallback chain: 401/403 → AuthException/ServerException surfaced; other failures fall through to the next locator. Direct calls: _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1007`, `…/remote/graph_api_datasource_impl.dart:1052`
- Notes: RSVP on the user's copy of an event.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.tentatively-accept.json`

#### `calendar.events.decline`

**POST v1.0 `{mailbox}/events/{eventId}/decline`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`; `proposedNewTime? {start,end: {dateTime, timeZone}} — propose-new-time is a decline carrying this`
- On error: Inside the invitation fallback chain: 401/403 → AuthException/ServerException surfaced; other failures fall through to the next locator. Direct calls: _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1007`, `…/remote/graph_api_datasource_impl.dart:1052`, `…/remote/graph_api_datasource_impl.dart:1102`, `…/remote/graph_api_datasource_impl.dart:1138`, `…/remote/graph_api_datasource_impl.dart:1869`, `…/remote/graph_api_datasource_impl.dart:1893`
- Notes: RSVP on the user's copy of an event. Propose-new-time is sent as decline + proposedNewTime: from the event screen with local wall-clock + IANA zone, from an invitation with UTC ('YYYY-MM-DDTHH:MM:SS.mmm', timeZone 'UTC').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.decline.json`

#### `calendar.messages.accept`

**POST v1.0 `{mailbox}/messages/{messageId}/accept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (true)`; `comment`
- On error: 400 or 404 → 'action not available on this message type': client falls back to messages/{id}/microsoft.graph.eventMessage/event then events/{id}/accept, then a calendarView search. 401/403 → surfaced. So a façade that cannot RSVP via the message should answer 400/404, never 5xx.
- Call sites: `…/remote/graph_api_datasource_impl.dart:982`
- Notes: First attempt when answering an invitation from the mail (eventMessageRequest). The action segment is interpolated (`$endpoint`).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.accept.json`

#### `calendar.messages.tentatively-accept`

**POST v1.0 `{mailbox}/messages/{messageId}/tentativelyAccept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (true)`; `comment`
- On error: 400 or 404 → 'action not available on this message type': client falls back to messages/{id}/microsoft.graph.eventMessage/event then events/{id}/tentativelyAccept, then a calendarView search. 401/403 → surfaced. So a façade that cannot RSVP via the message should answer 400/404, never 5xx.
- Call sites: `…/remote/graph_api_datasource_impl.dart:982`
- Notes: First attempt when answering an invitation from the mail (eventMessageRequest). The action segment is interpolated (`$endpoint`).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.tentatively-accept.json`

#### `calendar.messages.decline`

**POST v1.0 `{mailbox}/messages/{messageId}/decline`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (true)`; `comment`
- On error: 400 or 404 → 'action not available on this message type': client falls back to messages/{id}/microsoft.graph.eventMessage/event then events/{id}/decline, then a calendarView search. 401/403 → surfaced. So a façade that cannot RSVP via the message should answer 400/404, never 5xx.
- Call sites: `…/remote/graph_api_datasource_impl.dart:982`
- Notes: First attempt when answering an invitation from the mail (eventMessageRequest). The action segment is interpolated (`$endpoint`).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.decline.json`

#### `calendar.events.cancel`

**POST v1.0 `{mailbox}/events/{eventId}/cancel`** — forms: me, users; success: 202; paging: none

- Request body: `comment (always '')`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:1244`, `…/remote/graph_api_datasource_impl.dart:1279`, `…/remote/graph_api_datasource_impl.dart:1831`, `…/remote/graph_api_datasource_impl.dart:1854`
- Notes: Organizer cancels (sends cancellation to attendees). 'Cancel this and following' cancels the whole series master.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.cancel.json`

#### `calendar.events.forward`

**POST v1.0 `{mailbox}/events/{eventId}/forward`** — forms: me, users; success: 202; paging: none

- Request body: `ToRecipients[].emailAddress.address (PascalCase key)`; `Comment (PascalCase key, may be '')`
- On error: 400 or 403 → MeetingForwardUnsupportedException (client then emails the invitation itself); 401 → AuthException; others → ServerException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1321`
- Notes: Body keys are PascalCase ('ToRecipients', 'Comment') — the server's JSON binding must be case-insensitive here.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.forward.json`

#### `calendar.messages.linked-event`

**GET v1.0 `{mailbox}/messages/{messageId}/microsoft.graph.eventMessage/event`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,isOrganizer`; `$select` (variant 2): `id`
- Client reads: `id`, `isOrganizer`
- On error: Any non-auth failure (400 'Resource not found for the segment', 404) → client tries the next spelling, then iCalUId lookup, then calendarView search. 401/403 → surfaced.
- Call sites: `…/remote/graph_api_datasource_impl.dart:52`, `…/remote/graph_api_datasource_impl.dart:994`, `…/remote/graph_api_datasource_impl.dart:1090`, `…/remote/graph_api_datasource_impl.dart:1170`, `…/remote/graph_api_datasource_impl.dart:1229`, `…/remote/graph_api_datasource_impl.dart:1401`, `…/remote/graph_api_datasource_impl.dart:1705`
- Notes: The calendar event an invitation/response message is about. The client tries several spellings in turn because Exchange builds disagree: eventMessageRequest cast → eventMessage cast → uncast (attendee forward), and eventMessageResponse cast → eventMessage cast → uncast (organizer resolving a proposal; this path sends no $select). All spellings should resolve to the same event. isOrganizer is read to refuse organizer-only actions (isOrganizer false → 'not the organizer').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.linked-event.json`

#### `calendar.messages.linked-event-request-cast`

**GET v1.0 `{mailbox}/messages/{messageId}/microsoft.graph.eventMessageRequest/event`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,isOrganizer`; `$select` (variant 2): `id`
- Client reads: `id`, `isOrganizer`
- On error: Any non-auth failure (400 'Resource not found for the segment', 404) → client tries the next spelling, then iCalUId lookup, then calendarView search. 401/403 → surfaced.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1400`
- Notes: The calendar event an invitation/response message is about. The client tries several spellings in turn because Exchange builds disagree: eventMessageRequest cast → eventMessage cast → uncast (attendee forward), and eventMessageResponse cast → eventMessage cast → uncast (organizer resolving a proposal; this path sends no $select). All spellings should resolve to the same event. isOrganizer is read to refuse organizer-only actions (isOrganizer false → 'not the organizer').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.linked-event-request-cast.json`

#### `calendar.messages.linked-event-response-cast`

**GET v1.0 `{mailbox}/messages/{messageId}/microsoft.graph.eventMessageResponse/event`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `isOrganizer`
- On error: Any non-auth failure (400 'Resource not found for the segment', 404) → client tries the next spelling, then iCalUId lookup, then calendarView search. 401/403 → surfaced.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1704`
- Notes: The calendar event an invitation/response message is about. The client tries several spellings in turn because Exchange builds disagree: eventMessageRequest cast → eventMessage cast → uncast (attendee forward), and eventMessageResponse cast → eventMessage cast → uncast (organizer resolving a proposal; this path sends no $select). All spellings should resolve to the same event. isOrganizer is read to refuse organizer-only actions (isOrganizer false → 'not the organizer').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.linked-event-response-cast.json`

#### `calendar.messages.linked-event-uncast`

**GET v1.0 `{mailbox}/messages/{messageId}/event`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,isOrganizer`; `$select` (variant 2): `id`
- Client reads: `id`, `isOrganizer`
- On error: Any non-auth failure (400 'Resource not found for the segment', 404) → client tries the next spelling, then iCalUId lookup, then calendarView search. 401/403 → surfaced.
- Call sites: `…/remote/graph_api_datasource_impl.dart:1402`, `…/remote/graph_api_datasource_impl.dart:1706`
- Notes: The calendar event an invitation/response message is about. The client tries several spellings in turn because Exchange builds disagree: eventMessageRequest cast → eventMessage cast → uncast (attendee forward), and eventMessageResponse cast → eventMessage cast → uncast (organizer resolving a proposal; this path sends no $select). All spellings should resolve to the same event. isOrganizer is read to refuse organizer-only actions (isOrganizer false → 'not the organizer').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.messages.linked-event-uncast.json`

#### `calendar.get-schedule`

**POST v1.0 `{mailbox}/calendar/getSchedule`** — forms: me, users; success: 200; paging: none

- Request body: `schedules[] (email addresses, ≤ 20 per call)`; `startTime {dateTime 'YYYY-MM-DDTHH:MM:SS.mmm' (no Z), timeZone 'UTC'}`; `endTime {…}`
- Client reads: `value[].scheduleId`, `value[].availabilityView`, `value[].scheduleItems[].status`, `value[].scheduleItems[].start.dateTime`, `value[].scheduleItems[].end.dateTime`, `value[].scheduleItems[].subject`, `value[].scheduleItems[].isPrivate`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3214`
- Notes: Free/busy for people and rooms; batches of 20 run concurrently. No availabilityViewInterval is sent (Graph default 30 min). availabilityView digits: 0 free,1 tentative,2 busy,3 oof,4 workingElsewhere. scheduleItems times are read as UTC (client strips fractional seconds and appends Z) — return them in UTC. status ∈ free|tentative|busy|oof|workingElsewhere.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.get-schedule.json`

#### `rooms.places.list`

**GET v1.0 `/places/microsoft.graph.room`** — forms: root; success: 200; paging: nextLink

- Query: `$top`: `500`
- Client reads: `value[].emailAddress`, `value[].displayName`, `value[].capacity`, `value[].building`, `value[].floorNumber`, `value[].isWheelChairAccessible`, `@odata.nextLink`
- On error: 401/403/404 → falls back to rooms.find-rooms (beta). Other errors on page 1 → fallback; on later pages → keep what was read.
- Call sites: `…/remote/graph_api_datasource_impl.dart:3299`
- Notes: Tenant room directory. $top is embedded in the path string ('/places/microsoft.graph.room?$top=500'). Follows @odata.nextLink up to 10 pages — read as data['@odata.nextLink'] WITHOUT the graph.microsoft.com host check that other paging applies. floorNumber is an int; capacity int.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/rooms.places.list.json`

#### `rooms.find-rooms`

**GET beta `/me/findRooms`** — forms: me; success: 200; paging: none

- Client reads: `value[].address`, `value[].name`
- On error: Any failure → empty room list.
- Call sites: `…/remote/graph_api_datasource_impl.dart:3345`
- Notes: Fallback room list. Absolute URL https://graph.microsoft.com/beta/me/findRooms — ALWAYS /me, even for a shared-mailbox account. No paging.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/rooms.find-rooms.json`

### Phase 5A — Directory, contacts, people

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`directory.me.get`](#directorymeget) | GET | v1.0 | `{mailbox}` | none |
| [`directory.users.get`](#directoryusersget) | GET | v1.0 | `/users/{userIdOrUpn}` | none |
| [`directory.users.list`](#directoryuserslist) | GET | v1.0 | `/users` | nextLink |
| [`directory.users.photo-value`](#directoryusersphoto-value) | GET | v1.0 | `/users/{userIdOrUpn}/photo/$value` | none |
| [`contacts.list`](#contactslist) | GET | v1.0 | `{mailbox}/contacts` | nextLink |
| [`people.list`](#peoplelist) | GET | v1.0 | `{mailbox}/people` | nextLink |

#### `directory.me.get`

**GET v1.0 `{mailbox}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `displayName,mail,userPrincipalName`; `$select` (variant 2): `givenName,surname,jobTitle,mobilePhone,businessPhones`; `$select` (variant 3): `id,mail,userPrincipalName`; `$select` (variant 4): `mail,userPrincipalName,proxyAddresses`
- Client reads: `id`, `displayName`, `mail`, `userPrincipalName`, `givenName`, `surname`, `jobTitle`, `mobilePhone`, `businessPhones[]`, `proxyAddresses[] ('SMTP:'/'smtp:' prefixed; only smtp entries used)`
- On error: Signature prefill: 403 → null. Sign-in lookup: any failure → empty list ('could not tell', token accepted). Others → _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2087`, `…/remote/graph_api_datasource_impl.dart:2180`, `…/remote/graph_api_datasource_impl.dart:2672`, `lib/infrastructure/auth/signed_in_mailbox_lookup.dart:87`
- Notes: The signed-in user (or, through _base, the shared mailbox's user object). Variants: account setup profile (displayName,mail,userPrincipalName); signature prefill (givenName,surname,jobTitle,mobilePhone,businessPhones); threat-submission identity (/me only: id,mail,userPrincipalName — id is used as users/{id}); sign-in verification (absolute https://graph.microsoft.com/v1.0/me with an explicit bearer for the token under test: mail,userPrincipalName,proxyAddresses).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.get.json`

#### `directory.users.get`

**GET v1.0 `/users/{userIdOrUpn}`** — forms: root; success: 200; paging: none

- Query: `$select` (always): `displayName,jobTitle,department,companyName,mobilePhone,businessPhones,officeLocation,mail,userPrincipalName`
- Client reads: `displayName`, `jobTitle`, `department`, `companyName`, `mobilePhone`, `businessPhones[]`, `officeLocation`, `mail`, `userPrincipalName`
- On error: 404 (not in directory) or 403 → null, silently. Others → _mapDioException.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2109`
- Notes: Contact card lookup by email address; the address is Uri.encodeComponent-ed ('@' → '%40'). Note the mailbox base (`/users/{address}`, raw '@') resolves to the same route — the server must accept both encodings.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.get.json`

#### `directory.users.list`

**GET v1.0 `/users`** — forms: root; success: 200; paging: nextLink

- Query: `$select` (always): `displayName,mail,userPrincipalName`; `$top`: `999`
- Client reads: `value[].displayName`, `value[].mail`, `value[].userPrincipalName`, `@odata.nextLink`
- On error: Failure recorded as a partial sync ('directory: HTTP 403'), not thrown.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2249`
- Notes: Whole directory for the daily contact-cache refresh. No $filter/$search. Follows nextLink up to 25 pages (host-checked).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.list.json`

#### `directory.users.photo-value`

**GET v1.0 `/users/{userIdOrUpn}/photo/$value`** — forms: root; success: 200; paging: none

- Client reads: `<image bytes>`
- On error: All failures swallowed.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2310`
- Notes: Contact-card avatar; address percent-encoded. Any failure (404 no photo, 403) → no photo.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.photo-value.json`

#### `contacts.list`

**GET v1.0 `{mailbox}/contacts`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `displayName,emailAddresses`; `$top`: `999`; `$count`: `True`
- Client reads: `value[].displayName`, `value[].emailAddresses[].address`, `value[].emailAddresses[].name`, `@odata.nextLink`
- On error: Failure recorded as partial sync.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2245`
- Notes: Personal contacts for the typeahead cache (read-only: the client never creates/updates contacts). Follows nextLink up to 25 pages. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.list.json`

#### `people.list`

**GET v1.0 `{mailbox}/people`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `displayName,scoredEmailAddresses`; `$top`: `999`
- Client reads: `value[].displayName`, `value[].scoredEmailAddresses[].address`, `@odata.nextLink`
- On error: Failure recorded as partial sync.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2253`
- Notes: Relevance-ranked people for the typeahead cache. No $search (the phase-5 plan's `people?$search` is not used by the client). Graph clamps $top itself.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/people.list.json`

### Phase 5B — Tasks, mailbox settings, files, threat submission

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`tasks.lists.list`](#taskslistslist) | GET | v1.0 | `{mailbox}/todo/lists` | none |
| [`tasks.tasks.list`](#taskstaskslist) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks` | none |
| [`tasks.tasks.create`](#taskstaskscreate) | POST | v1.0 | `{mailbox}/todo/lists/{listId}/tasks` | none |
| [`tasks.tasks.update`](#taskstasksupdate) | PATCH | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}` | none |
| [`tasks.attachments.list`](#tasksattachmentslist) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments` | none |
| [`tasks.attachments.create`](#tasksattachmentscreate) | POST | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments` | none |
| [`tasks.attachments.create-upload-session`](#tasksattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/createUploadSession` | none |
| [`tasks.attachments.upload-session-put`](#tasksattachmentsupload-session-put) | PUT | v1.0 | `{uploadUrl}` | none |
| [`tasks.attachments.get-value`](#tasksattachmentsget-value) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}/$value` | none |
| [`mailbox-settings.get`](#mailbox-settingsget) | GET | v1.0 | `{mailbox}/mailboxSettings` | none |
| [`mailbox-settings.update`](#mailbox-settingsupdate) | PATCH | v1.0 | `{mailbox}/mailboxSettings` | none |
| [`files.shares.drive-item`](#filessharesdrive-item) | GET | v1.0 | `/shares/{shareId}/driveItem` | none |
| [`files.shares.drive-item-content`](#filessharesdrive-item-content) | GET | v1.0 | `/shares/{shareId}/driveItem/content` | none |
| [`security.threat-submission.email-threats.create`](#securitythreat-submissionemail-threatscreate) | POST | beta | `/security/threatSubmission/emailThreats` | none |

#### `tasks.lists.list`

**GET v1.0 `{mailbox}/todo/lists`** — forms: me, users; success: 200; paging: none

- Query: `$top`: `100`
- Client reads: `value[].id`, `value[].displayName`, `value[].wellknownListName ('defaultList' marks the default)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2802`
- Notes: No paging followed (first 100 lists only).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.list.json`

#### `tasks.tasks.list`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks`** — forms: me, users; success: 200; paging: none

- Query: `$filter`: `status ne 'completed'`; `$orderby`: `createdDateTime desc`; `$top`: `200`; `$count`: `True`
- Client reads: `value[].id`, `value[].title`, `value[].status`, `value[].importance`, `value[].body.content`, `value[].dueDateTime.dateTime`, `value[].completedDateTime.dateTime`, `value[].isReminderOn`, `value[].reminderDateTime.dateTime`, `value[].createdDateTime`, `value[].lastModifiedDateTime`, `value[].hasAttachments`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2828`
- Notes: $filter present unless completed tasks are included. No paging followed. Task datetimes ({dateTime,timeZone}) are parsed with DateTime.tryParse(dateTime) and converted to local — dateTime without Z is read as LOCAL time, so return dueDateTime in the form Graph does ('YYYY-MM-DDT00:00:00.0000000', timeZone 'UTC'). $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.list.json`

#### `tasks.tasks.create`

**POST v1.0 `{mailbox}/todo/lists/{listId}/tasks`** — forms: me, users; success: 201; paging: none

- Request body: `title`; `importance ('low'|'normal'|'high')`; `body {content, contentType 'text'}?`; `dueDateTime {dateTime 'YYYY-MM-DDT00:00:00', timeZone 'UTC'}?`
- Client reads: `id`, `title`, `status`, `importance`, `body.content`, `dueDateTime.dateTime`, `completedDateTime.dateTime`, `isReminderOn`, `reminderDateTime.dateTime`, `createdDateTime`, `lastModifiedDateTime`, `hasAttachments`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2870`
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.create.json`

#### `tasks.tasks.update`

**PATCH v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}`** — forms: me, users; success: 200; paging: none

- Request body: `status ('notStarted'|'inProgress'|'completed'|'waitingOnOthers'|'deferred')`; `— or — dueDateTime {dateTime, timeZone 'UTC'} | null (null clears)`
- Client reads: `id`, `title`, `status`, `importance`, `body.content`, `dueDateTime.dateTime`, `completedDateTime.dateTime`, `isReminderOn`, `reminderDateTime.dateTime`, `createdDateTime`, `lastModifiedDateTime`, `hasAttachments`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2897`, `…/remote/graph_api_datasource_impl.dart:2926`
- Notes: Two shapes: status only, or dueDateTime only (explicit JSON null clears the due date). Response parsed as the full task; empty body is an error. The client never deletes tasks.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.update.json`

#### `tasks.attachments.list`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3066`
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.list.json`

#### `tasks.attachments.create`

**POST v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type '#microsoft.graph.taskFileAttachment'`; `name`; `contentType 'message/rfc822'`; `contentBytes (base64)`; `size`
- Client reads: `id`, `name`, `contentType`, `size`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2996`
- Notes: Attach an email (.eml) ≤ 3 MB inline. Larger goes through createUploadSession.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.create.json`

#### `tasks.attachments.create-upload-session`

**POST v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `attachmentItem.@odata.type '#microsoft.graph.fileAttachmentUploadProperties'`; `attachmentItem.attachmentType 'file'`; `attachmentItem.contentType`; `attachmentItem.name`; `attachmentItem.size`
- Client reads: `uploadUrl`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3018`
- Notes: Attachments > 3 MB. The returned uploadUrl is PUT to by a separate Dio WITHOUT the bearer token (tasks.attachments.upload-session-put), so it must be pre-authorised (signed/opaque) and must accept the whole file in one PUT.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.create-upload-session.json`

#### `tasks.attachments.upload-session-put`

**PUT v1.0 `{uploadUrl}`** — forms: external; success: 201; paging: none

- Request body: `<raw bytes, whole file in one request>`
- Request headers: `Content-Range: bytes 0-{size-1}/{size}`; `Content-Length`; `Content-Type: message/rfc822`
- Client reads: `id`, `name`, `contentType`, `size`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3040`
- Notes: Not a Graph route: the absolute uploadUrl from createUploadSession, called with NO Authorization header and a 60 s timeout. Single PUT of the entire file; the final response must be the created attachment (JSON) — the client parses it as a taskFileAttachment.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.upload-session-put.json`

#### `tasks.attachments.get-value`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Client reads: `<raw bytes>`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3086`
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.get-value.json`

#### `mailbox-settings.get`

**GET v1.0 `{mailbox}/mailboxSettings`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `automaticRepliesSetting,timeZone`
- Client reads: `timeZone (Windows zone name, e.g. 'AUS Eastern Standard Time')`, `automaticRepliesSetting.status ('disabled'|'scheduled'|'alwaysEnabled')`, `automaticRepliesSetting.externalAudience ('none'|'contactsOnly'|'all')`, `automaticRepliesSetting.internalReplyMessage`, `automaticRepliesSetting.externalReplyMessage`, `automaticRepliesSetting.scheduledStartDateTime.dateTime`, `automaticRepliesSetting.scheduledEndDateTime.dateTime`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3804`
- Notes: Read before every write (the zone the schedule is expressed in is the mailbox's). scheduled*DateTime are read as wall-clock in the mailbox zone with NO conversion; year < 1900 (Graph's 0001-01-01) means unset. language/workingHours are never read.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.get.json`

#### `mailbox-settings.update`

**PATCH v1.0 `{mailbox}/mailboxSettings`** — forms: me, users; success: 200; paging: none

- Request body: `automaticRepliesSetting.status ('scheduled'|'disabled')`; `automaticRepliesSetting.externalAudience ('all'|'contactsOnly'|'none')`; `automaticRepliesSetting.internalReplyMessage (HTML)`; `automaticRepliesSetting.externalReplyMessage (HTML)`; `automaticRepliesSetting.scheduledStartDateTime? {dateTime 'YYYY-MM-DDTHH:MM:SS.0000000', timeZone (mailbox's Windows zone, or 'UTC')}`; `automaticRepliesSetting.scheduledEndDateTime? {…}`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:3778`
- Notes: automaticRepliesSetting is sent whole (Graph replaces complex properties). Response ignored. Needs MailboxSettings.ReadWrite (incremental scope on Microsoft).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.update.json`

#### `files.shares.drive-item`

**GET v1.0 `/shares/{shareId}/driveItem`** — forms: root; success: 200; paging: none

- Query: `$select` (always): `id,name,size,file,folder`
- Client reads: `name`, `size`, `file.mimeType`, `folder (presence → not previewable)`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:64`
- Notes: shareId = 'u!' + unpadded base64url of the sharing URL found in a message body (SharePoint/OneDrive link). Nightware must decode it and map a Nextcloud share link. Microsoft-only in the client today (GraphEndpoints.isMicrosoft) — may be gated off for Nightware accounts.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.shares.drive-item.json`

#### `files.shares.drive-item-content`

**GET v1.0 `/shares/{shareId}/driveItem/content`** — forms: root; success: 302; paging: none

- Query: `format`: `pdf`
- Client reads: `Location header (302) or body bytes (200)`
- On error: Conversion request: any failure → fall back to raw download. Raw: 401 → AuthException; other ≥400 → ServerException.
- Call sites: `…/remote/graph_drive_datasource_impl.dart:135`
- Notes: Requested with followRedirects:false. 302 + Location → the client GETs the Location with a separate Dio and NO Authorization header (so it must be pre-authorised); 200 with the bytes inline is also accepted. ?format=pdf asks for a server-side PDF conversion of Office documents; any failure there falls back to the raw file. Size cap 40 MB.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.shares.drive-item-content.json`

#### `security.threat-submission.email-threats.create`

**POST beta `/security/threatSubmission/emailThreats`** — forms: root; success: 201; paging: none

- Request body: `@odata.type '#microsoft.graph.security.emailUrlThreatSubmission'`; `category 'phishing'`; `recipientEmailAddress (mailbox's mail/UPN)`; `messageUrl 'https://graph.microsoft.com/beta/users/{userIdOrAddress}/messages/{messageId}' (absolute, hardcoded host)`
- On error: _mapDioException; the presentation layer maps a missing grant to PhishingReportAccessNotGranted before calling.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2650`, `…/remote/graph_api_datasource_impl.dart:2692`
- Notes: Absolute URL https://graph.microsoft.com/beta/security/threatSubmission/emailThreats. The message is named by messageUrl, an absolute Graph BETA URL whose host is hardcoded to graph.microsoft.com and whose user segment is the directory id (own mailbox, from GET /me?$select=id,mail,userPrincipalName) or the shared mailbox address. The server must parse users/{id}/messages/{id} out of it. Submitted BEFORE the junk move (move changes the Graph id). Response body ignored. Microsoft-only in the client (needs ThreatSubmission.ReadWrite); likely gated off for Nightware accounts.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/security.threat-submission.email-threats.create.json`

### Phase 11 — JSON batching (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`batch.post`](#batchpost) | POST | v1.0 | `/$batch` | none |
| [`batch.post-depends-on`](#batchpost-depends-on) | POST | v1.0 | `/$batch` | none |

#### `batch.post`

**POST v1.0 `/$batch`** — forms: root; success: 200; paging: none

- Request body: `requests[].id`; `requests[].method`; `requests[].url`; `requests[].headers?`; `requests[].body?`; `requests[].dependsOn?`
- Client reads: `responses[].id`, `responses[].status`, `responses[].headers`, `responses[].body`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/json-batching
- Notes: Up to 20 independent requests, one of them failing. Graph returns the responses in any order; the façade returns request order. Also served under /beta. ADR 0006 D9.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/batch.post.json`

#### `batch.post-depends-on`

**POST v1.0 `/$batch`** — forms: root; success: 200; paging: none

- Request body: `requests[].id`; `requests[].method`; `requests[].url`; `requests[].dependsOn`
- Client reads: `responses[].id`, `responses[].status`, `responses[].body.error.code`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/json-batching
- Notes: dependsOn chain whose first request fails: the dependant is answered 424 FailedDependency without running, with no headers.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/batch.post-depends-on.json`

### Phase 12 — Mail (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`mail.messages.update-flag`](#mailmessagesupdate-flag) | PATCH | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.update-categories`](#mailmessagesupdate-categories) | PATCH | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.update-importance`](#mailmessagesupdate-importance) | PATCH | v1.0 | `{mailbox}/messages/{messageId}` | none |
| [`mail.messages.copy`](#mailmessagescopy) | POST | v1.0 | `{mailbox}/messages/{messageId}/copy` | none |
| [`mail.master-categories.list`](#mailmaster-categorieslist) | GET | v1.0 | `{mailbox}/outlook/masterCategories` | none |
| [`mail.master-categories.get`](#mailmaster-categoriesget) | GET | v1.0 | `{mailbox}/outlook/masterCategories/{categoryId}` | none |
| [`mail.master-categories.create`](#mailmaster-categoriescreate) | POST | v1.0 | `{mailbox}/outlook/masterCategories` | none |
| [`mail.master-categories.update`](#mailmaster-categoriesupdate) | PATCH | v1.0 | `{mailbox}/outlook/masterCategories/{categoryId}` | none |
| [`mail.master-categories.delete`](#mailmaster-categoriesdelete) | DELETE | v1.0 | `{mailbox}/outlook/masterCategories/{categoryId}` | none |
| [`mail.folders.delta`](#mailfoldersdelta) | GET | v1.0 | `{mailbox}/mailFolders/delta` | deltaLink |
| [`mail.folders.delta-changes`](#mailfoldersdelta-changes) | GET | v1.0 | `{mailbox}/mailFolders/delta` | deltaLink |
| [`mail.rules.list`](#mailruleslist) | GET | v1.0 | `{mailbox}/mailFolders/inbox/messageRules` | none |
| [`mail.rules.get`](#mailrulesget) | GET | v1.0 | `{mailbox}/mailFolders/inbox/messageRules/{ruleId}` | none |
| [`mail.rules.create`](#mailrulescreate) | POST | v1.0 | `{mailbox}/mailFolders/inbox/messageRules` | none |
| [`mail.rules.update`](#mailrulesupdate) | PATCH | v1.0 | `{mailbox}/mailFolders/inbox/messageRules/{ruleId}` | none |
| [`mail.rules.delete`](#mailrulesdelete) | DELETE | v1.0 | `{mailbox}/mailFolders/inbox/messageRules/{ruleId}` | none |
| [`mail.attachments.create-upload-session`](#mailattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/messages/{messageId}/attachments/createUploadSession` | none |
| [`mail.attachments.upload-session-put`](#mailattachmentsupload-session-put) | PUT | v1.0 | `{uploadUrl}` | none |
| [`mail.attachments.upload-session-put-final`](#mailattachmentsupload-session-put-final) | PUT | v1.0 | `{uploadUrl}` | none |

#### `mail.messages.update-flag`

**PATCH v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Request body: `flag.flagStatus`; `flag.startDateTime?`; `flag.dueDateTime?`; `flag.completedDateTime?`
- Client reads: `id`, `flag.flagStatus`, `flag.startDateTime?`, `flag.dueDateTime?`, `flag.completedDateTime?`, `categories`, `importance`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-update
- Notes: Any message, not only drafts. flagged is the $flagged keyword; complete and the dates live in the message's JMAP metadata (ADR 0006 D5). The id is kept. Shares its route with mail.messages.update-read-state and mail.drafts.update.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.update-flag.json`

#### `mail.messages.update-categories`

**PATCH v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Request body: `categories`
- Client reads: `id`, `flag.flagStatus`, `flag.startDateTime?`, `flag.dueDateTime?`, `flag.completedDateTime?`, `categories`, `importance`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-update
- Notes: Names need not be master categories. Kept in the message's shared metadata (nightware.ms), up to 8 KB.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.update-categories.json`

#### `mail.messages.update-importance`

**PATCH v1.0 `{mailbox}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Request body: `importance`
- Client reads: `id`, `flag.flagStatus`, `flag.startDateTime?`, `flag.dueDateTime?`, `flag.completedDateTime?`, `categories`, `importance`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-update
- Notes: Overrides the Importance header on read; normal clears the override.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.update-importance.json`

#### `mail.messages.copy`

**POST v1.0 `{mailbox}/messages/{messageId}/copy`** — forms: me, users; success: 201; paging: none

- Request body: `destinationId`
- Client reads: `id`, `flag.flagStatus`, `flag.startDateTime?`, `flag.dueDateTime?`, `flag.completedDateTime?`, `categories`, `importance`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-copy
- Notes: A distinct message (Email/import of the blob) with its own id, carrying read state, flag, categories and importance. Location header as Graph.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.copy.json`

#### `mail.master-categories.list`

**GET v1.0 `{mailbox}/outlook/masterCategories`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].displayName`, `value[].color`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/outlookcategory
- Notes: Kept in the Inbox's JMAP metadata (nightware.ms/masterCategories). A mailbox that never wrote a list has Outlook's six defaults, with ids derived from their names.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.master-categories.list.json`

#### `mail.master-categories.get`

**GET v1.0 `{mailbox}/outlook/masterCategories/{categoryId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `displayName`, `color`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/outlookcategory
- Notes: Id compared ignoring case.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.master-categories.get.json`

#### `mail.master-categories.create`

**POST v1.0 `{mailbox}/outlook/masterCategories`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`; `color?`
- Client reads: `id`, `displayName`, `color`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/outlookcategory
- Notes: Names are unique ignoring case (409 otherwise); colour none or preset0-24. About 80 categories fit Stalwart's 8 KB namespace; beyond that, 400. Location header as Graph.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.master-categories.create.json`

#### `mail.master-categories.update`

**PATCH v1.0 `{mailbox}/outlook/masterCategories/{categoryId}`** — forms: me, users; success: 200; paging: none

- Request body: `color`
- Client reads: `id`, `displayName`, `color`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/outlookcategory
- Notes: Only the colour changes; a different displayName is 400.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.master-categories.update.json`

#### `mail.master-categories.delete`

**DELETE v1.0 `{mailbox}/outlook/masterCategories/{categoryId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/outlookcategory
- Notes: Messages keep the name; it only leaves the list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.master-categories.delete.json`

#### `mail.folders.delta`

**GET v1.0 `{mailbox}/mailFolders/delta`** — forms: me, users; success: 200; paging: deltaLink

- Query: `$select` (always): `displayName,parentFolderId`
- Client reads: `value[].id`, `value[].displayName`, `value[].parentFolderId`, `@odata.nextLink`, `@odata.deltaLink`
- On error: 410 syncStateNotFound: resync from an initial request.
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/mailfolder-delta
- Notes: Initial sync: every folder, parents first. Built on Mailbox/get + Mailbox/changes; $select frozen into the link (ADR 0001 §7).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.delta.json`

#### `mail.folders.delta-changes`

**GET v1.0 `{mailbox}/mailFolders/delta`** — forms: me, users; success: 200; paging: deltaLink

- Query: `$deltatoken`: `<from deltaLink>`
- Client reads: `value[].id`, `value[].displayName`, `value[].parentFolderId`, `value[].@removed`, `@odata.deltaLink`
- On error: 410 syncStateNotFound: resync from an initial request.
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/mailfolder-delta
- Notes: After a change: created/updated folders as upserts (a rename, move or count change), destroyed ones as @removed deleted.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.delta-changes.json`

#### `mail.rules.list`

**GET v1.0 `{mailbox}/mailFolders/inbox/messageRules`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].displayName`, `value[].sequence`, `value[].isEnabled`, `value[].hasError`, `value[].isReadOnly`, `value[].conditions`, `value[].actions`, `value[].exceptions`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/messagerule
- Notes: Rules and automatic replies share one Nightware Sieve script on Stalwart (ADR 0006 D5). In sequence order, numbered 1..n.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.rules.list.json`

#### `mail.rules.get`

**GET v1.0 `{mailbox}/mailFolders/inbox/messageRules/{ruleId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `displayName`, `sequence`, `isEnabled`, `hasError`, `isReadOnly`, `conditions`, `actions`, `exceptions`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/messagerule
- Notes: Rules and automatic replies share one Nightware Sieve script on Stalwart (ADR 0006 D5). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.rules.get.json`

#### `mail.rules.create`

**POST v1.0 `{mailbox}/mailFolders/inbox/messageRules`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`; `sequence?`; `isEnabled?`; `conditions?`; `exceptions?`; `actions`
- Client reads: `id`, `displayName`, `sequence`, `isEnabled`, `hasError`, `isReadOnly`, `conditions`, `actions`, `exceptions`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/messagerule
- Notes: Rules and automatic replies share one Nightware Sieve script on Stalwart (ADR 0006 D5). Supported predicates: subjectContains, bodyContains, bodyOrSubjectContains, senderContains, recipientContains, fromAddresses, sentToAddresses, hasAttachments, importance. Actions: moveToFolder, copyToFolder, delete, permanentDelete, markAsRead, forwardTo/redirectTo (one address in all), stopProcessingRules. Anything else is 501 naming the field. The first rule takes over an enabled VacationResponse.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.rules.create.json`

#### `mail.rules.update`

**PATCH v1.0 `{mailbox}/mailFolders/inbox/messageRules/{ruleId}`** — forms: me, users; success: 200; paging: none

- Request body: `displayName?`; `sequence?`; `isEnabled?`; `conditions?`; `exceptions?`; `actions?`
- Client reads: `id`, `displayName`, `sequence`, `isEnabled`, `hasError`, `isReadOnly`, `conditions`, `actions`, `exceptions`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/messagerule
- Notes: Rules and automatic replies share one Nightware Sieve script on Stalwart (ADR 0006 D5). Members sent replace the stored ones; a changed sequence moves the rule.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.rules.update.json`

#### `mail.rules.delete`

**DELETE v1.0 `{mailbox}/mailFolders/inbox/messageRules/{ruleId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/messagerule
- Notes: Rules and automatic replies share one Nightware Sieve script on Stalwart (ADR 0006 D5). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.rules.delete.json`

#### `mail.attachments.create-upload-session`

**POST v1.0 `{mailbox}/messages/{messageId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `AttachmentItem.attachmentType`; `AttachmentItem.name`; `AttachmentItem.size`; `AttachmentItem.contentType?`; `AttachmentItem.isInline?`; `AttachmentItem.contentId?`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: Drafts only, up to 50,000,000 bytes (Stalwart's maxSizeUpload). The uploadUrl is self-authorising (ADR 0001 §39, its own key purpose) and bound to the draft and session; the session lives in the draft's JMAP metadata (ADR 0006 D5).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.create-upload-session.json`

#### `mail.attachments.upload-session-put`

**PUT v1.0 `{uploadUrl}`** — forms: external; success: 200; paging: none

- Headers: `Content-Range: bytes first-last/total`
- Request body: `<bytes>`
- Client reads: `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: No bearer: the URL is the credential. Chunks must arrive in order; each is a Stalwart blob, merged with Blob/upload up to maxSizeBlobSet. nextExpectedRanges is the next byte, as Exchange answers.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.upload-session-put.json`

#### `mail.attachments.upload-session-put-final`

**PUT v1.0 `{uploadUrl}`** — forms: external; success: 201; paging: none

- Headers: `Content-Range: bytes first-last/total`
- Request body: `<bytes>`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: The last chunk attaches the whole file to the draft (keeping its d. id): 201, Location of the attachment, no body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.attachments.upload-session-put-final.json`

### Phase 13 — Calendar (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`calendar.calendars.list`](#calendarcalendarslist) | GET | v1.0 | `{mailbox}/calendars` | none |
| [`calendar.calendar.get`](#calendarcalendarget) | GET | v1.0 | `{mailbox}/calendar` | none |
| [`calendar.calendars.get`](#calendarcalendarsget) | GET | v1.0 | `{mailbox}/calendars/{calendarId}` | none |
| [`calendar.calendars.create`](#calendarcalendarscreate) | POST | v1.0 | `{mailbox}/calendars` | none |
| [`calendar.calendars.update`](#calendarcalendarsupdate) | PATCH | v1.0 | `{mailbox}/calendars/{calendarId}` | none |
| [`calendar.calendars.delete`](#calendarcalendarsdelete) | DELETE | v1.0 | `{mailbox}/calendars/{calendarId}` | none |
| [`calendar.calendars.events.list`](#calendarcalendarseventslist) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events` | none |
| [`calendar.calendars.events.create`](#calendarcalendarseventscreate) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events` | none |
| [`calendar.calendars.calendar-view`](#calendarcalendarscalendar-view) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/calendarView` | none |
| [`calendar.calendar-groups.list`](#calendarcalendar-groupslist) | GET | v1.0 | `{mailbox}/calendarGroups` | none |
| [`calendar.calendar-groups.calendars`](#calendarcalendar-groupscalendars) | GET | v1.0 | `{mailbox}/calendarGroups/{groupId}/calendars` | none |
| [`calendar.events.list`](#calendareventslist) | GET | v1.0 | `{mailbox}/events` | none |
| [`calendar.events.instances`](#calendareventsinstances) | GET | v1.0 | `{mailbox}/events/{eventId}/instances` | none |
| [`calendar.calendar-view.delta`](#calendarcalendar-viewdelta) | GET | v1.0 | `{mailbox}/calendarView/delta` | deltaLink |
| [`calendar.calendar-view.delta-changes`](#calendarcalendar-viewdelta-changes) | GET | v1.0 | `{mailbox}/calendarView/delta` | deltaLink |
| [`calendar.reminder-view`](#calendarreminder-view) | GET | v1.0 | `{mailbox}/reminderView(startDateTime='{start}',endDateTime='{end}')` | none |
| [`calendar.events.snooze-reminder`](#calendareventssnooze-reminder) | POST | v1.0 | `{mailbox}/events/{eventId}/snoozeReminder` | none |
| [`calendar.events.dismiss-reminder`](#calendareventsdismiss-reminder) | POST | v1.0 | `{mailbox}/events/{eventId}/dismissReminder` | none |
| [`calendar.events.attachments.list`](#calendareventsattachmentslist) | GET | v1.0 | `{mailbox}/events/{eventId}/attachments` | none |
| [`calendar.events.attachments.get`](#calendareventsattachmentsget) | GET | v1.0 | `{mailbox}/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.events.attachments.create`](#calendareventsattachmentscreate) | POST | v1.0 | `{mailbox}/events/{eventId}/attachments` | none |
| [`calendar.events.attachments.delete`](#calendareventsattachmentsdelete) | DELETE | v1.0 | `{mailbox}/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.permissions.list`](#calendarpermissionslist) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/calendarPermissions` | none |
| [`calendar.permissions.create`](#calendarpermissionscreate) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/calendarPermissions` | none |
| [`calendar.permissions.update`](#calendarpermissionsupdate) | PATCH | v1.0 | `{mailbox}/calendars/{calendarId}/calendarPermissions/{permissionId}` | none |
| [`calendar.permissions.delete`](#calendarpermissionsdelete) | DELETE | v1.0 | `{mailbox}/calendars/{calendarId}/calendarPermissions/{permissionId}` | none |
| [`calendar.find-meeting-times`](#calendarfind-meeting-times) | POST | v1.0 | `{mailbox}/findMeetingTimes` | none |

#### `calendar.calendars.list`

**GET v1.0 `{mailbox}/calendars`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].color`, `value[].hexColor`, `value[].isDefaultCalendar`, `value[].canEdit`, `value[].isRemovable`, `value[].owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). The default first; the read-only birthday calendar included.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.list.json`

#### `calendar.calendar.get`

**GET v1.0 `{mailbox}/calendar`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `color`, `hexColor`, `isDefaultCalendar`, `canEdit`, `isRemovable`, `owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). The default (Nextcloud's personal) calendar.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar.get.json`

#### `calendar.calendars.get`

**GET v1.0 `{mailbox}/calendars/{calendarId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `color`, `hexColor`, `isDefaultCalendar`, `canEdit`, `isRemovable`, `owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.get.json`

#### `calendar.calendars.create`

**POST v1.0 `{mailbox}/calendars`** — forms: me, users; success: 201; paging: none

- Request body: `name`; `color?`
- Client reads: `id`, `name`, `color`, `hexColor`, `isDefaultCalendar`, `canEdit`, `isRemovable`, `owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). MKCALENDAR in the caller's own home; Location as Graph.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.create.json`

#### `calendar.calendars.update`

**PATCH v1.0 `{mailbox}/calendars/{calendarId}`** — forms: me, users; success: 200; paging: none

- Request body: `name?`; `color?`
- Client reads: `id`, `name`, `color`, `hexColor`, `isDefaultCalendar`, `canEdit`, `isRemovable`, `owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). PROPPATCH displayname and calendar-color; Graph colour names map to hex, auto removes it.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.update.json`

#### `calendar.calendars.delete`

**DELETE v1.0 `{mailbox}/calendars/{calendarId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). The default calendar is 400 ErrorCannotDeleteDefaultCalendar.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.delete.json`

#### `calendar.calendars.events.list`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events`** — forms: me, users; success: 200; paging: none

- Query: `$count`: `True`
- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). Unexpanded; $orderby start/dateTime, $top, $skip. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.list.json`

#### `calendar.calendars.events.create`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events`** — forms: me, users; success: 201; paging: none

- Request body: `subject`; `start`; `end`; `recurrence?`
- Client reads: `id`, `subject`, `start`, `end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). Refused (403) in a read-only calendar.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.create.json`

#### `calendar.calendars.calendar-view`

**GET v1.0 `{mailbox}/calendars/{calendarId}/calendarView`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). startDateTime/endDateTime required; expanded on the server.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.calendar-view.json`

#### `calendar.calendar-groups.list`

**GET v1.0 `{mailbox}/calendarGroups`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].classId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendargroup
- Notes: One synthetic group, My Calendars (ADR 0006 D6); Exchange's other groups have no Nextcloud counterpart.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-groups.list.json`

#### `calendar.calendar-groups.calendars`

**GET v1.0 `{mailbox}/calendarGroups/{groupId}/calendars`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].color`, `value[].hexColor`, `value[].isDefaultCalendar`, `value[].canEdit`, `value[].isRemovable`, `value[].owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendargroup
- Notes: Every calendar; any other group id is 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-groups.calendars.json`

#### `calendar.events.list`

**GET v1.0 `{mailbox}/events`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-events
- Notes: The default calendar's events, unexpanded; $orderby start/dateTime asc|desc, $select, $top, $skip. Shares its route with calendar.events.list-by-ical-uid.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.list.json`

#### `calendar.events.instances`

**GET v1.0 `{mailbox}/events/{eventId}/instances`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`, `value[].seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-list-instances
- Notes: The series' occurrences and exceptions in the window, from Sabre's expansion of its calendar.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.instances.json`

#### `calendar.calendar-view.delta`

**GET v1.0 `{mailbox}/calendarView/delta`** — forms: me, users; success: 200; paging: deltaLink

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-delta
- Notes: The default calendar's window, expanded; the window and the collection's sync token are frozen into the link (ADR 0006 D6).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-view.delta.json`

#### `calendar.calendar-view.delta-changes`

**GET v1.0 `{mailbox}/calendarView/delta`** — forms: me, users; success: 200; paging: deltaLink

- Client reads: `value[].id`, `value[].@removed`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-delta
- Notes: sync-collection: a changed resource's occurrences in the window are upserts; a removed one is @removed under its series' id (its occurrences are dropped by seriesMasterId). 410 when the token has expired.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-view.delta-changes.json`

#### `calendar.reminder-view`

**GET v1.0 `{mailbox}/reminderView(startDateTime='{start}',endDateTime='{end}')`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].eventId`, `value[].eventSubject`, `value[].eventStartTime`, `value[].eventEndTime`, `value[].reminderFireTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-reminderview
- Notes: Reminders of the default calendar's occurrences starting in the window, not dismissed (RFC 9074 ACKNOWLEDGED), firing at the snooze time when snoozed.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.reminder-view.json`

#### `calendar.events.snooze-reminder`

**POST v1.0 `{mailbox}/events/{eventId}/snoozeReminder`** — forms: me, users; success: 202; paging: none

- Request body: `newReminderTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-snoozereminder
- Notes: Acknowledges the alarms and adds an RFC 9074 snooze alarm at the new time; 202 as Exchange.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.snooze-reminder.json`

#### `calendar.events.dismiss-reminder`

**POST v1.0 `{mailbox}/events/{eventId}/dismissReminder`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-dismissreminder
- Notes: ACKNOWLEDGED up to now, or the occurrence's fire time when later, on every alarm.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.dismiss-reminder.json`

#### `calendar.events.attachments.list`

**GET v1.0 `{mailbox}/events/{eventId}/attachments`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`, `value[].isInline`, `value[].lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/fileattachment
- Notes: Stored in the owner's Nextcloud Files (Nightware/Event attachments) and referenced by ATTACH, as To Do attachments are (ADR 0006 D6). No content in the listing.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.list.json`

#### `calendar.events.attachments.get`

**GET v1.0 `{mailbox}/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`, `contentBytes`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/fileattachment
- Notes: Stored in the owner's Nextcloud Files (Nightware/Event attachments) and referenced by ATTACH, as To Do attachments are (ADR 0006 D6). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.get.json`

#### `calendar.events.attachments.create`

**POST v1.0 `{mailbox}/events/{eventId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type`; `name`; `contentType?`; `contentBytes`
- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/fileattachment
- Notes: Stored in the owner's Nextcloud Files (Nightware/Event attachments) and referenced by ATTACH, as To Do attachments are (ADR 0006 D6). fileAttachment only, up to 3 MB; Location as Graph.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.create.json`

#### `calendar.events.attachments.delete`

**DELETE v1.0 `{mailbox}/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/fileattachment
- Notes: Stored in the owner's Nextcloud Files (Nightware/Event attachments) and referenced by ATTACH, as To Do attachments are (ADR 0006 D6). The file goes too.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.delete.json`

#### `calendar.permissions.list`

**GET v1.0 `{mailbox}/calendars/{calendarId}/calendarPermissions`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].role`, `value[].emailAddress`, `value[].isRemovable`, `value[].isInsideOrganization`, `value[].allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendarpermission
- Notes: Nextcloud CalDAV sharing (oc:invite / oc:share; ADR 0006 D6); only the owner shares. 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.permissions.list.json`

#### `calendar.permissions.create`

**POST v1.0 `{mailbox}/calendars/{calendarId}/calendarPermissions`** — forms: me, users; success: 201; paging: none

- Request body: `emailAddress`; `role`
- Client reads: `id`, `role`, `emailAddress`, `isRemovable`, `isInsideOrganization`, `allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendarpermission
- Notes: Nextcloud CalDAV sharing (oc:invite / oc:share; ADR 0006 D6); only the owner shares. read or write (other roles 501); the sharee must be a user of this server (400 otherwise).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.permissions.create.json`

#### `calendar.permissions.update`

**PATCH v1.0 `{mailbox}/calendars/{calendarId}/calendarPermissions/{permissionId}`** — forms: me, users; success: 200; paging: none

- Request body: `role`
- Client reads: `id`, `role`, `emailAddress`, `isRemovable`, `isInsideOrganization`, `allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendarpermission
- Notes: Nextcloud CalDAV sharing (oc:invite / oc:share; ADR 0006 D6); only the owner shares. 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.permissions.update.json`

#### `calendar.permissions.delete`

**DELETE v1.0 `{mailbox}/calendars/{calendarId}/calendarPermissions/{permissionId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendarpermission
- Notes: Nextcloud CalDAV sharing (oc:invite / oc:share; ADR 0006 D6); only the owner shares. 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.permissions.delete.json`

#### `calendar.find-meeting-times`

**POST v1.0 `{mailbox}/findMeetingTimes`** — forms: me, users; success: 200; paging: none

- Request body: `attendees`; `timeConstraint`; `meetingDuration`; `maxCandidates`; `isOrganizerOptional`; `minimumAttendeePercentage`
- Client reads: `emptySuggestionsReason`, `meetingTimeSuggestions[].confidence`, `meetingTimeSuggestions[].organizerAvailability`, `meetingTimeSuggestions[].attendeeAvailability`, `meetingTimeSuggestions[].locations`, `meetingTimeSuggestions[].meetingTimeSlot`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-findmeetingtimes
- Notes: Computed over CalDAV free/busy and the organizer's working hours (ADR 0006 D6): half-hour starts, confidence is the share of attendees free (tentative/unknown half, optional attendees half weight). locationConstraint is ignored; no locations are suggested.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.find-meeting-times.json`

### Phase 14 — Contacts (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`contacts.create`](#contactscreate) | POST | v1.0 | `{mailbox}/contacts` | none |
| [`contacts.get`](#contactsget) | GET | v1.0 | `{mailbox}/contacts/{contactId}` | none |
| [`contacts.update`](#contactsupdate) | PATCH | v1.0 | `{mailbox}/contacts/{contactId}` | none |
| [`contacts.delete`](#contactsdelete) | DELETE | v1.0 | `{mailbox}/contacts/{contactId}` | none |
| [`contacts.list-full`](#contactslist-full) | GET | v1.0 | `{mailbox}/contacts` | none |
| [`contacts.photo-put`](#contactsphoto-put) | PUT | v1.0 | `{mailbox}/contacts/{contactId}/photo/$value` | none |
| [`contacts.photo-get`](#contactsphoto-get) | GET | v1.0 | `{mailbox}/contacts/{contactId}/photo/$value` | none |
| [`contacts.folders.list`](#contactsfolderslist) | GET | v1.0 | `{mailbox}/contactFolders` | none |
| [`contacts.folders.get`](#contactsfoldersget) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}` | none |
| [`contacts.folders.create`](#contactsfolderscreate) | POST | v1.0 | `{mailbox}/contactFolders` | none |
| [`contacts.folders.update`](#contactsfoldersupdate) | PATCH | v1.0 | `{mailbox}/contactFolders/{folderId}` | none |
| [`contacts.folders.delete`](#contactsfoldersdelete) | DELETE | v1.0 | `{mailbox}/contactFolders/{folderId}` | none |
| [`contacts.folders.child-folders.list`](#contactsfolderschild-folderslist) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/childFolders` | none |
| [`contacts.folders.contacts.list`](#contactsfolderscontactslist) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts` | none |
| [`contacts.folders.contacts.create`](#contactsfolderscontactscreate) | POST | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts` | none |
| [`contacts.delta`](#contactsdelta) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/delta` | none |
| [`contacts.delta-changes`](#contactsdelta-changes) | GET | v1.0 | `{mailbox}/contactFolders('{folderId}')/contacts/delta` | none |
| [`contacts.folders.delta`](#contactsfoldersdelta) | GET | v1.0 | `{mailbox}/contactFolders/delta` | none |

#### `contacts.create`

**POST v1.0 `{mailbox}/contacts`** — forms: me, users; success: 201; paging: none

- Request body: `categories`; `birthday`; `fileAs`; `displayName`; `givenName`; `initials`; `middleName`; `nickName`; `surname`; `title`; `yomiGivenName`; `yomiSurname`; `yomiCompanyName`; `generation`; `imAddresses`; `jobTitle`; `companyName`; `department`; `officeLocation`; `profession`; `businessHomePage`; `assistantName`; `manager`; `homePhones`; `mobilePhone`; `businessPhones`; `spouseName`; `personalNotes`; `children`; `emailAddresses`; `homeAddress`; `businessAddress`; `otherAddress`
- Client reads: `id`, `createdDateTime`, `lastModifiedDateTime`, `changeKey`, `categories`, `parentFolderId`, `birthday`, `fileAs`, `displayName`, `givenName`, `initials`, `middleName`, `nickName`, `surname`, `title`, `yomiGivenName`, `yomiSurname`, `yomiCompanyName`, `generation`, `imAddresses`, `jobTitle`, `companyName`, `department`, `officeLocation`, `profession`, `businessHomePage`, `assistantName`, `manager`, `homePhones`, `mobilePhone`, `businessPhones`, `spouseName`, `personalNotes`, `children`, `emailAddresses`, `primaryEmailAddress`, `homeAddress`, `businessAddress`, `otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). Created in the default address book; a null member clears, an unmapped one is 400.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.create.json`

#### `contacts.get`

**GET v1.0 `{mailbox}/contacts/{contactId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `createdDateTime`, `lastModifiedDateTime`, `changeKey`, `categories`, `parentFolderId`, `birthday`, `fileAs`, `displayName`, `givenName`, `initials`, `middleName`, `nickName`, `surname`, `title`, `yomiGivenName`, `yomiSurname`, `yomiCompanyName`, `generation`, `imAddresses`, `jobTitle`, `companyName`, `department`, `officeLocation`, `profession`, `businessHomePage`, `assistantName`, `manager`, `homePhones`, `mobilePhone`, `businessPhones`, `spouseName`, `personalNotes`, `children`, `emailAddresses`, `primaryEmailAddress`, `homeAddress`, `businessAddress`, `otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.get.json`

#### `contacts.update`

**PATCH v1.0 `{mailbox}/contacts/{contactId}`** — forms: me, users; success: 200; paging: none

- Request body: `categories`; `birthday`; `fileAs`; `displayName`; `givenName`; `initials`; `middleName`; `nickName`; `surname`; `title`; `yomiGivenName`; `yomiSurname`; `yomiCompanyName`; `generation`; `imAddresses`; `jobTitle`; `companyName`; `department`; `officeLocation`; `profession`; `businessHomePage`; `assistantName`; `manager`; `homePhones`; `mobilePhone`; `businessPhones`; `spouseName`; `personalNotes`; `children`; `emailAddresses`; `homeAddress`; `businessAddress`; `otherAddress`
- Client reads: `id`, `createdDateTime`, `lastModifiedDateTime`, `changeKey`, `categories`, `parentFolderId`, `birthday`, `fileAs`, `displayName`, `givenName`, `initials`, `middleName`, `nickName`, `surname`, `title`, `yomiGivenName`, `yomiSurname`, `yomiCompanyName`, `generation`, `imAddresses`, `jobTitle`, `companyName`, `department`, `officeLocation`, `profession`, `businessHomePage`, `assistantName`, `manager`, `homePhones`, `mobilePhone`, `businessPhones`, `spouseName`, `personalNotes`, `children`, `emailAddresses`, `primaryEmailAddress`, `homeAddress`, `businessAddress`, `otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). Conditional on the card's ETag; unmapped vCard properties are kept.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.update.json`

#### `contacts.delete`

**DELETE v1.0 `{mailbox}/contacts/{contactId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.delete.json`

#### `contacts.list-full`

**GET v1.0 `{mailbox}/contacts`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].createdDateTime`, `value[].lastModifiedDateTime`, `value[].changeKey`, `value[].categories`, `value[].parentFolderId`, `value[].birthday`, `value[].fileAs`, `value[].displayName`, `value[].givenName`, `value[].initials`, `value[].middleName`, `value[].nickName`, `value[].surname`, `value[].title`, `value[].yomiGivenName`, `value[].yomiSurname`, `value[].yomiCompanyName`, `value[].generation`, `value[].imAddresses`, `value[].jobTitle`, `value[].companyName`, `value[].department`, `value[].officeLocation`, `value[].profession`, `value[].businessHomePage`, `value[].assistantName`, `value[].manager`, `value[].homePhones`, `value[].mobilePhone`, `value[].businessPhones`, `value[].spouseName`, `value[].personalNotes`, `value[].children`, `value[].emailAddresses`, `value[].primaryEmailAddress`, `value[].homeAddress`, `value[].businessAddress`, `value[].otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). Spans every address book (NightMail's typeahead relies on it); Graph lists only the default folder.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.list-full.json`

#### `contacts.photo-put`

**PUT v1.0 `{mailbox}/contacts/{contactId}/photo/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). vCard PHOTO (v3 ENCODING=b); images up to 4 MB.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.photo-put.json`

#### `contacts.photo-get`

**GET v1.0 `{mailbox}/contacts/{contactId}/photo/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-get
- Notes: CardDAV vCard 3 in Nextcloud (ADR 0006 D7; mapping table in the Dav adapter README). 404 when the contact has no photo.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.photo-get.json`

#### `contacts.folders.list`

**GET v1.0 `{mailbox}/contactFolders`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].parentFolderId`, `value[].displayName`, `value[].wellKnownName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). The books below the default one; generated books (system, recently contacted) are not folders.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.list.json`

#### `contacts.folders.get`

**GET v1.0 `{mailbox}/contactFolders/{folderId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `parentFolderId`, `displayName`, `wellKnownName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). The default book is reachable by id or as 'contacts' (wellKnownName contacts).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.get.json`

#### `contacts.folders.create`

**POST v1.0 `{mailbox}/contactFolders`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`
- Client reads: `id`, `parentFolderId`, `displayName`, `wellKnownName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). Extended MKCOL; a name already taken is 409.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.create.json`

#### `contacts.folders.update`

**PATCH v1.0 `{mailbox}/contactFolders/{folderId}`** — forms: me, users; success: 200; paging: none

- Request body: `displayName`
- Client reads: `id`, `parentFolderId`, `displayName`, `wellKnownName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). PROPPATCH displayname; the default folder cannot be renamed (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.update.json`

#### `contacts.folders.delete`

**DELETE v1.0 `{mailbox}/contactFolders/{folderId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). The default folder cannot be deleted (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.delete.json`

#### `contacts.folders.child-folders.list`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/childFolders`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contactfolder
- Notes: CardDAV address books (ADR 0006 D7). Always empty: CardDAV is flat.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.child-folders.list.json`

#### `contacts.folders.contacts.list`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/contacts`** — forms: me, users; success: 200; paging: none

- Query: `$count`: `True`
- Client reads: `value[]`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV address books (ADR 0006 D7). The contacts of that one book. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.list.json`

#### `contacts.folders.contacts.create`

**POST v1.0 `{mailbox}/contactFolders/{folderId}/contacts`** — forms: me, users; success: 201; paging: none

- Request body: `givenName`; `surname`; `emailAddresses`
- Client reads: `id`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/contact
- Notes: CardDAV address books (ADR 0006 D7). As contacts.create, in the named book.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.create.json`

#### `contacts.delta`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/contacts/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contact-delta
- Notes: CardDAV sync-collection on the folder's book (ADR 0006 D7): the initial sync is every card.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.delta.json`

#### `contacts.delta-changes`

**GET v1.0 `{mailbox}/contactFolders('{folderId}')/contacts/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contact-delta
- Notes: Changed cards (multiget) and @removed; the token names its folder; an expired one is 410. Graph's key form contactFolders('{id}') is routed as well.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.delta-changes.json`

#### `contacts.folders.delta`

**GET v1.0 `{mailbox}/contactFolders/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contactfolder-delta
- Notes: An address-book home has no sync token: the token is a snapshot of folder ids and name hashes, so new, renamed and removed folders are reported.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.delta.json`

### Phase 15 — To Do (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`tasks.lists.create`](#taskslistscreate) | POST | v1.0 | `{mailbox}/todo/lists` | none |
| [`tasks.lists.get`](#taskslistsget) | GET | v1.0 | `{mailbox}/todo/lists/{listId}` | none |
| [`tasks.lists.update`](#taskslistsupdate) | PATCH | v1.0 | `{mailbox}/todo/lists/{listId}` | none |
| [`tasks.lists.delete`](#taskslistsdelete) | DELETE | v1.0 | `{mailbox}/todo/lists/{listId}` | none |
| [`tasks.tasks.get`](#taskstasksget) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}` | none |
| [`tasks.tasks.delete`](#taskstasksdelete) | DELETE | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}` | none |
| [`tasks.attachments.get`](#tasksattachmentsget) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}` | none |
| [`tasks.attachments.delete`](#tasksattachmentsdelete) | DELETE | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}` | none |
| [`tasks.checklist-items.list`](#taskschecklist-itemslist) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems` | none |
| [`tasks.checklist-items.create`](#taskschecklist-itemscreate) | POST | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems` | none |
| [`tasks.checklist-items.update`](#taskschecklist-itemsupdate) | PATCH | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{itemId}` | none |
| [`tasks.checklist-items.delete`](#taskschecklist-itemsdelete) | DELETE | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{itemId}` | none |
| [`tasks.linked-resources.list`](#taskslinked-resourceslist) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources` | none |
| [`tasks.linked-resources.create`](#taskslinked-resourcescreate) | POST | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources` | none |
| [`tasks.linked-resources.update`](#taskslinked-resourcesupdate) | PATCH | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{resourceId}` | none |
| [`tasks.linked-resources.delete`](#taskslinked-resourcesdelete) | DELETE | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{resourceId}` | none |
| [`tasks.lists.delta`](#taskslistsdelta) | GET | v1.0 | `{mailbox}/todo/lists/delta` | none |
| [`tasks.tasks.delta`](#taskstasksdelta) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/delta` | none |
| [`tasks.tasks.delta-changes`](#taskstasksdelta-changes) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/delta` | none |

#### `tasks.lists.create`

**POST v1.0 `{mailbox}/todo/lists`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`
- Client reads: `id`, `displayName`, `isOwner`, `isShared`, `wellknownListName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotasklist
- Notes: VTODO calendar collections (ADR 0006 D8). MKCALENDAR of a VTODO-only collection (Nextcloud limits calendar creation to about ten per hour).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.create.json`

#### `tasks.lists.get`

**GET v1.0 `{mailbox}/todo/lists/{listId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `displayName`, `isOwner`, `isShared`, `wellknownListName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotasklist
- Notes: VTODO calendar collections (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.get.json`

#### `tasks.lists.update`

**PATCH v1.0 `{mailbox}/todo/lists/{listId}`** — forms: me, users; success: 200; paging: none

- Request body: `displayName`
- Client reads: `id`, `displayName`, `isOwner`, `isShared`, `wellknownListName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotasklist
- Notes: VTODO calendar collections (ADR 0006 D8). PROPPATCH displayname; the default list and shared lists cannot be renamed (400/403).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.update.json`

#### `tasks.lists.delete`

**DELETE v1.0 `{mailbox}/todo/lists/{listId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotasklist
- Notes: VTODO calendar collections (ADR 0006 D8). The default list, shared lists and a collection that also holds events are not deleted (400/403/400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.delete.json`

#### `tasks.tasks.get`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `title`, `status`, `importance`, `body`, `dueDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotask
- Notes: VTODO calendar collections (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.get.json`

#### `tasks.tasks.delete`

**DELETE v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/todotask
- Notes: VTODO calendar collections (ADR 0006 D8). Its checklist items (child VTODOs) go with it.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.delete.json`

#### `tasks.attachments.get`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `contentType`, `size`, `lastModifiedDateTime`, `contentBytes`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/taskfileattachment
- Notes: Nextcloud Files, linked by ATTACH; contentBytes inline as To Do answers.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.get.json`

#### `tasks.attachments.delete`

**DELETE v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/taskfileattachment
- Notes: Unlinks the ATTACH, then deletes the file.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.attachments.delete.json`

#### `tasks.checklist-items.list`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].displayName`, `value[].isChecked`, `value[].createdDateTime`, `value[].checkedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/checklistitem
- Notes: Subtasks: VTODOs in the same list with RELATED-TO the task (as Nextcloud Tasks writes them); hidden from the task listing (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.checklist-items.list.json`

#### `tasks.checklist-items.create`

**POST v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`; `isChecked`
- Client reads: `id`, `displayName`, `isChecked`, `createdDateTime`, `checkedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/checklistitem
- Notes: Subtasks: VTODOs in the same list with RELATED-TO the task (as Nextcloud Tasks writes them); hidden from the task listing (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.checklist-items.create.json`

#### `tasks.checklist-items.update`

**PATCH v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{itemId}`** — forms: me, users; success: 200; paging: none

- Request body: `displayName`; `isChecked`
- Client reads: `id`, `displayName`, `isChecked`, `createdDateTime`, `checkedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/checklistitem
- Notes: Subtasks: VTODOs in the same list with RELATED-TO the task (as Nextcloud Tasks writes them); hidden from the task listing (ADR 0006 D8). isChecked is STATUS:COMPLETED with COMPLETED and PERCENT-COMPLETE:100.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.checklist-items.update.json`

#### `tasks.checklist-items.delete`

**DELETE v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{itemId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/checklistitem
- Notes: Subtasks: VTODOs in the same list with RELATED-TO the task (as Nextcloud Tasks writes them); hidden from the task listing (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.checklist-items.delete.json`

#### `tasks.linked-resources.list`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].webUrl`, `value[].applicationName`, `value[].displayName`, `value[].externalId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/linkedresource
- Notes: X-NIGHTWARE-LINKED-RESOURCE properties on the task's VTODO (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.linked-resources.list.json`

#### `tasks.linked-resources.create`

**POST v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources`** — forms: me, users; success: 201; paging: none

- Request body: `webUrl`; `applicationName`; `displayName`; `externalId`
- Client reads: `id`, `webUrl`, `applicationName`, `displayName`, `externalId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/linkedresource
- Notes: X-NIGHTWARE-LINKED-RESOURCE properties on the task's VTODO (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.linked-resources.create.json`

#### `tasks.linked-resources.update`

**PATCH v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{resourceId}`** — forms: me, users; success: 200; paging: none

- Request body: `webUrl`; `applicationName`; `displayName`; `externalId`
- Client reads: `id`, `webUrl`, `applicationName`, `displayName`, `externalId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/linkedresource
- Notes: X-NIGHTWARE-LINKED-RESOURCE properties on the task's VTODO (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.linked-resources.update.json`

#### `tasks.linked-resources.delete`

**DELETE v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{resourceId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/linkedresource
- Notes: X-NIGHTWARE-LINKED-RESOURCE properties on the task's VTODO (ADR 0006 D8). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.linked-resources.delete.json`

#### `tasks.lists.delta`

**GET v1.0 `{mailbox}/todo/lists/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.nextLink`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/todotasklist-delta
- Notes: A calendar home has no sync token for its collections: the token is a snapshot of list ids and what a client shows of them. The initial sync answers with a next page first, as To Do does.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.lists.delta.json`

#### `tasks.tasks.delta`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/todotask-delta
- Notes: CalDAV sync-collection on the list (ADR 0006 D8): the initial sync is every task, as a first page.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.delta.json`

#### `tasks.tasks.delta-changes`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/delta`** — forms: me, users; success: 200; paging: none

- Client reads: `value[]`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/todotask-delta
- Notes: Changed tasks (a changed checklist item stands for its task) and @removed; the token names its list; an expired one is 410. checklistItems are not embedded: read them from checklistItems.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.delta-changes.json`

### Phase 16 — Change notifications (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`subscriptions.create`](#subscriptionscreate) | POST | v1.0 | `subscriptions` | none |
| [`subscriptions.create-events`](#subscriptionscreate-events) | POST | v1.0 | `subscriptions` | none |
| [`subscriptions.create-contacts`](#subscriptionscreate-contacts) | POST | v1.0 | `subscriptions` | none |
| [`subscriptions.create-tasks`](#subscriptionscreate-tasks) | POST | v1.0 | `subscriptions` | none |
| [`subscriptions.list`](#subscriptionslist) | GET | v1.0 | `subscriptions` | none |
| [`subscriptions.get`](#subscriptionsget) | GET | v1.0 | `subscriptions/{id}` | none |
| [`subscriptions.update`](#subscriptionsupdate) | PATCH | v1.0 | `subscriptions/{id}` | none |
| [`subscriptions.delete`](#subscriptionsdelete) | DELETE | v1.0 | `subscriptions/{id}` | none |
| [`subscriptions.validation`](#subscriptionsvalidation) | POST | v1.0 | `{notificationUrl}?validationToken={token}` | none |
| [`subscriptions.notification`](#subscriptionsnotification) | POST | v1.0 | `{notificationUrl}` | none |

#### `subscriptions.create`

**POST v1.0 `subscriptions`** — forms: root; success: 201; paging: none

- Request body: `resource`; `changeType`; `notificationUrl`; `expirationDateTime`; `clientState`; `lifecycleNotificationUrl`
- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). Validates as Graph does (45-minute minimum; 10080 minutes for messages, events and contacts, 4230 for tasks), runs the validationToken handshake (10 s), and keeps a sealed same-session refresh token for the caller. includeResourceData is 400. 501 when the store is not configured. Messages: {mailbox}/mailFolders('{id}')/messages ({mailbox}/messages, every folder, is 501).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.create.json`

#### `subscriptions.create-events`

**POST v1.0 `subscriptions`** — forms: root; success: 201; paging: none

- Request body: `resource`; `changeType`; `notificationUrl`; `expirationDateTime`; `clientState`; `lifecycleNotificationUrl`
- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). Validates as Graph does (45-minute minimum; 10080 minutes for messages, events and contacts, 4230 for tasks), runs the validationToken handshake (10 s), and keeps a sealed same-session refresh token for the caller. includeResourceData is 400. 501 when the store is not configured. {mailbox}/events: the default calendar.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.create-events.json`

#### `subscriptions.create-contacts`

**POST v1.0 `subscriptions`** — forms: root; success: 201; paging: none

- Request body: `resource`; `changeType`; `notificationUrl`; `expirationDateTime`; `clientState`; `lifecycleNotificationUrl`
- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). Validates as Graph does (45-minute minimum; 10080 minutes for messages, events and contacts, 4230 for tasks), runs the validationToken handshake (10 s), and keeps a sealed same-session refresh token for the caller. includeResourceData is 400. 501 when the store is not configured. {mailbox}/contacts: the default address book.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.create-contacts.json`

#### `subscriptions.create-tasks`

**POST v1.0 `subscriptions`** — forms: root; success: 201; paging: none

- Request body: `resource`; `changeType`; `notificationUrl`; `expirationDateTime`; `clientState`; `lifecycleNotificationUrl`
- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). Validates as Graph does (45-minute minimum; 10080 minutes for messages, events and contacts, 4230 for tasks), runs the validationToken handshake (10 s), and keeps a sealed same-session refresh token for the caller. includeResourceData is 400. 501 when the store is not configured. {mailbox}/todo/lists/{id}/tasks.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.create-tasks.json`

#### `subscriptions.list`

**GET v1.0 `subscriptions`** — forms: root; success: 200; paging: none

- Client reads: `value[].id`, `value[].resource`, `value[].applicationId`, `value[].changeType`, `value[].clientState`, `value[].notificationUrl`, `value[].lifecycleNotificationUrl`, `value[].expirationDateTime`, `value[].creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). The caller's own; clientState is left out, as Graph does.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.list.json`

#### `subscriptions.get`

**GET v1.0 `subscriptions/{id}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). 
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.get.json`

#### `subscriptions.update`

**PATCH v1.0 `subscriptions/{id}`** — forms: root; success: 200; paging: none

- Request body: `expirationDateTime`; `notificationUrl`
- Client reads: `id`, `resource`, `applicationId`, `changeType`, `clientState`, `notificationUrl`, `lifecycleNotificationUrl`, `expirationDateTime`, `creatorId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). Renewal; it also exchanges the caller's current token afresh, so it reauthorizes a lapsed subscription.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.update.json`

#### `subscriptions.delete`

**DELETE v1.0 `subscriptions/{id}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/subscription
- Notes: The façade's subscription store (ADR 0006 D10). The sealed credential goes with the row; the Keycloak session is left alone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.delete.json`

#### `subscriptions.validation`

**POST v1.0 `{notificationUrl}?validationToken={token}`** — forms: root; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/change-notifications-delivery-webhooks
- Notes: Outbound (Nightware → the subscriber): the create/renew handshake. The endpoint must answer 200 with the token as text/plain within 10 s. Sent only to a public address (SSRF guard at connect time), never with a bearer token.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.validation.json`

#### `subscriptions.notification`

**POST v1.0 `{notificationUrl}`** — forms: root; success: 202; paging: none

- Client reads: `value[].subscriptionId`, `value[].changeType`, `value[].resource`, `value[].resourceData`, `value[].clientState`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/change-notifications-delivery-webhooks
- Notes: Outbound (Nightware → the subscriber): a changeNotificationCollection. Sent only to a public address, never with a bearer token.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/subscriptions.notification.json`

### Phase 18 — Settings, aliases, single items and $count (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`mailbox-settings.automatic-replies.get`](#mailbox-settingsautomatic-repliesget) | GET | v1.0 | `{mailbox}/mailboxSettings/automaticRepliesSetting` | none |
| [`mailbox-settings.time-zone.get`](#mailbox-settingstime-zoneget) | GET | v1.0 | `{mailbox}/mailboxSettings/timeZone` | none |
| [`mailbox-settings.language.get`](#mailbox-settingslanguageget) | GET | v1.0 | `{mailbox}/mailboxSettings/language` | none |
| [`mailbox-settings.working-hours.get`](#mailbox-settingsworking-hoursget) | GET | v1.0 | `{mailbox}/mailboxSettings/workingHours` | none |
| [`mailbox-settings.date-format.get`](#mailbox-settingsdate-formatget) | GET | v1.0 | `{mailbox}/mailboxSettings/dateFormat` | none |
| [`mailbox-settings.time-format.get`](#mailbox-settingstime-formatget) | GET | v1.0 | `{mailbox}/mailboxSettings/timeFormat` | none |
| [`outlook.supported-time-zones`](#outlooksupported-time-zones) | GET | v1.0 | `{mailbox}/outlook/supportedTimeZones` | none |
| [`outlook.supported-time-zones-standard`](#outlooksupported-time-zones-standard) | GET | v1.0 | `{mailbox}/outlook/supportedTimeZones(TimeZoneStandard={standard})` | none |
| [`outlook.supported-languages`](#outlooksupported-languages) | GET | v1.0 | `{mailbox}/outlook/supportedLanguages` | none |
| [`calendar.default.events.list`](#calendardefaulteventslist) | GET | v1.0 | `{mailbox}/calendar/events` | none |
| [`calendar.default.events.create`](#calendardefaulteventscreate) | POST | v1.0 | `{mailbox}/calendar/events` | none |
| [`calendar.default.calendar-view`](#calendardefaultcalendar-view) | GET | v1.0 | `{mailbox}/calendar/calendarView` | none |
| [`calendar.default.events.get`](#calendardefaulteventsget) | GET | v1.0 | `{mailbox}/calendar/events/{eventId}` | none |
| [`calendar.default.events.update`](#calendardefaulteventsupdate) | PATCH | v1.0 | `{mailbox}/calendar/events/{eventId}` | none |
| [`calendar.default.events.delete`](#calendardefaulteventsdelete) | DELETE | v1.0 | `{mailbox}/calendar/events/{eventId}` | none |
| [`calendar.calendars.events.get`](#calendarcalendarseventsget) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}` | none |
| [`calendar.calendars.events.update`](#calendarcalendarseventsupdate) | PATCH | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}` | none |
| [`calendar.calendars.events.delete`](#calendarcalendarseventsdelete) | DELETE | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}` | none |
| [`mail.folders.messages.get`](#mailfoldersmessagesget) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}` | none |
| [`mail.folders.messages.update`](#mailfoldersmessagesupdate) | PATCH | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}` | none |
| [`mail.folders.messages.delete`](#mailfoldersmessagesdelete) | DELETE | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}` | none |
| [`contacts.folders.contacts.get`](#contactsfolderscontactsget) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/{contactId}` | none |
| [`contacts.folders.contacts.update`](#contactsfolderscontactsupdate) | PATCH | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/{contactId}` | none |
| [`contacts.folders.contacts.delete`](#contactsfolderscontactsdelete) | DELETE | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/{contactId}` | none |
| [`tasks.checklist-items.get`](#taskschecklist-itemsget) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{checklistItemId}` | none |
| [`tasks.linked-resources.get`](#taskslinked-resourcesget) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{linkedResourceId}` | none |

#### `mailbox-settings.automatic-replies.get`

**GET v1.0 `{mailbox}/mailboxSettings/automaticRepliesSetting`** — forms: me, users; success: 200; paging: none

- Client reads: `status`, `externalAudience`, `internalReplyMessage`, `externalReplyMessage`, `scheduledStartDateTime?`, `scheduledEndDateTime?`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: The same setting as mailboxSettings, scheduled times in the mailbox's zone. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.automatic-replies.get.json`

#### `mailbox-settings.time-zone.get`

**GET v1.0 `{mailbox}/mailboxSettings/timeZone`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: A Windows id, as mailboxSettings returns it. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.time-zone.get.json`

#### `mailbox-settings.language.get`

**GET v1.0 `{mailbox}/mailboxSettings/language`** — forms: me, users; success: 200; paging: none

- Client reads: `locale`, `displayName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: The locale stored for the user in the directory, else en-US; its name from outlook/supportedLanguages. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.language.get.json`

#### `mailbox-settings.working-hours.get`

**GET v1.0 `{mailbox}/mailboxSettings/workingHours`** — forms: me, users; success: 200; paging: none

- Client reads: `daysOfWeek`, `startTime`, `endTime`, `timeZone.name`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: The user's own, else Exchange's defaults (Monday to Friday, 08:00–17:00) in the mailbox's zone: what findMeetingTimes plans against. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.working-hours.get.json`

#### `mailbox-settings.date-format.get`

**GET v1.0 `{mailbox}/mailboxSettings/dateFormat`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: The user's own, else the short date pattern of their language. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.date-format.get.json`

#### `mailbox-settings.time-format.get`

**GET v1.0 `{mailbox}/mailboxSettings/timeFormat`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-get-mailboxsettings
- Notes: The user's own, else the short time pattern of their language. Read-only here: PATCH mailboxSettings changes automaticRepliesSetting and timeZone.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mailbox-settings.time-format.get.json`

#### `outlook.supported-time-zones`

**GET v1.0 `{mailbox}/outlook/supportedTimeZones`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].alias`, `value[].displayName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/outlookuser-supportedtimezones
- Notes: A fixed list (tools/outlook-catalog), ordered by standard offset; every alias is accepted by PATCH mailboxSettings timeZone. The fixture keeps the first three. Windows ids.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/outlook.supported-time-zones.json`

#### `outlook.supported-time-zones-standard`

**GET v1.0 `{mailbox}/outlook/supportedTimeZones(TimeZoneStandard={standard})`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].alias`, `value[].displayName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/outlookuser-supportedtimezones
- Notes: A fixed list (tools/outlook-catalog), ordered by standard offset; every alias is accepted by PATCH mailboxSettings timeZone. The fixture keeps the first three. TimeZoneStandard is Windows or Iana (microsoft.graph.timeZoneStandard'Iana'); anything else is 400. IANA ids are their own display name.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/outlook.supported-time-zones-standard.json`

#### `outlook.supported-languages`

**GET v1.0 `{mailbox}/outlook/supportedLanguages`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].locale`, `value[].displayName`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/outlookuser-supportedlanguages
- Notes: A fixed list of locales with their native names (tools/outlook-catalog). The fixture keeps the first three.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/outlook.supported-languages.json`

#### `calendar.default.events.list`

**GET v1.0 `{mailbox}/calendar/events`** — forms: me, users; success: 200; paging: none

- Query: `$count`: `True`
- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-list-events
- Notes: Graph's alias of calendar.calendars.events.list (calendar calendars events list): the same handling. The default calendar's, through the calendars/{id}/… handling. $count=true adds an exact @odata.count (phase 18; odata-subset §10).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.list.json`

#### `calendar.default.events.create`

**POST v1.0 `{mailbox}/calendar/events`** — forms: me, users; success: 201; paging: none

- Request body: `subject`; `start`; `end`; `recurrence?`
- Client reads: `id`, `subject`, `start`, `end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-post-events
- Notes: Graph's alias of calendar.calendars.events.create (calendar calendars events create): the same handling. The default calendar's, through the calendars/{id}/… handling.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.create.json`

#### `calendar.default.calendar-view`

**GET v1.0 `{mailbox}/calendar/calendarView`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-list-calendarview
- Notes: Graph's alias of calendar.calendars.calendar-view (calendar calendars calendar-view): the same handling. The default calendar's, through the calendars/{id}/… handling.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.calendar-view.json`

#### `calendar.default.events.get`

**GET v1.0 `{mailbox}/calendar/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,iCalUId,subject,start,end,isAllDay,location,onlineMeeting,bodyPreview,showAs,isOrganizer,organizer,responseStatus,attendees,recurrence,isReminderOn,reminderMinutesBeforeStart,seriesMasterId`; `$select` (variant 2): `recurrence`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-get
- Notes: Graph's alias of calendar.events.get (calendar events get): the same handling.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.get.json`

#### `calendar.default.events.update`

**PATCH v1.0 `{mailbox}/calendar/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Request body: `subject`; `isAllDay`; `isReminderOn`; `reminderMinutesBeforeStart?`; `isOnlineMeeting (only when true)`; `onlineMeetingProvider ('teamsForBusiness', only when online)`; `body {contentType:'text', content}?`; `start {dateTime 'YYYY-MM-DDTHH:MM:SS' local wall-clock, timeZone IANA e.g. 'Australia/Sydney'}`; `end {dateTime, timeZone}`; `locations[] {displayName, locationEmailAddress?, locationType 'conferenceRoom'?} (always sent, may be [])`; `location {displayName, …} (always sent; {displayName:''} when none)`; `attendees[] {emailAddress{address,name?}, type 'required'|'resource'} (always sent)`; `recurrence? {pattern{type daily|weekly|absoluteMonthly|absoluteYearly, interval, daysOfWeek?}, range{type endDate|numbered|noEnd, startDate 'YYYY-MM-DD', endDate?|numberOfOccurrences?}}`; `— or, accepting a proposed time: start/end only, {dateTime 'YYYY-MM-DDTHH:MM:SS.mmm' (no Z), timeZone:'UTC'}`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-update
- Notes: Graph's alias of calendar.events.update (calendar events update): the same handling.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.update.json`

#### `calendar.default.events.delete`

**DELETE v1.0 `{mailbox}/calendar/events/{eventId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-delete
- Notes: Graph's alias of calendar.events.delete (calendar events delete): the same handling.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.delete.json`

#### `calendar.calendars.events.get`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,iCalUId,subject,start,end,isAllDay,location,onlineMeeting,bodyPreview,showAs,isOrganizer,organizer,responseStatus,attendees,recurrence,isReminderOn,reminderMinutesBeforeStart,seriesMasterId`; `$select` (variant 2): `recurrence`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-get
- Notes: Graph's alias of calendar.events.get (calendar events get): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.get.json`

#### `calendar.calendars.events.update`

**PATCH v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}`** — forms: me, users; success: 200; paging: none

- Request body: `subject`; `isAllDay`; `isReminderOn`; `reminderMinutesBeforeStart?`; `isOnlineMeeting (only when true)`; `onlineMeetingProvider ('teamsForBusiness', only when online)`; `body {contentType:'text', content}?`; `start {dateTime 'YYYY-MM-DDTHH:MM:SS' local wall-clock, timeZone IANA e.g. 'Australia/Sydney'}`; `end {dateTime, timeZone}`; `locations[] {displayName, locationEmailAddress?, locationType 'conferenceRoom'?} (always sent, may be [])`; `location {displayName, …} (always sent; {displayName:''} when none)`; `attendees[] {emailAddress{address,name?}, type 'required'|'resource'} (always sent)`; `recurrence? {pattern{type daily|weekly|absoluteMonthly|absoluteYearly, interval, daysOfWeek?}, range{type endDate|numbered|noEnd, startDate 'YYYY-MM-DD', endDate?|numberOfOccurrences?}}`; `— or, accepting a proposed time: start/end only, {dateTime 'YYYY-MM-DDTHH:MM:SS.mmm' (no Z), timeZone:'UTC'}`
- Client reads: `id`, `iCalUId`, `subject`, `start.dateTime`, `start.timeZone`, `end.dateTime`, `end.timeZone`, `isAllDay`, `location.displayName`, `onlineMeeting.joinUrl`, `onlineMeetingUrl (fallback, not selected)`, `bodyPreview`, `showAs`, `isOrganizer`, `responseStatus.response`, `organizer.emailAddress.{address,name}`, `attendees[].emailAddress.{address,name}`, `attendees[].status.response`, `attendees[].type`, `recurrence.pattern.{type,interval,daysOfWeek}`, `recurrence.range.{type,endDate,numberOfOccurrences}`, `isReminderOn`, `reminderMinutesBeforeStart`, `seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-update
- Notes: Graph's alias of calendar.events.update (calendar events update): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.update.json`

#### `calendar.calendars.events.delete`

**DELETE v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-delete
- Notes: Graph's alias of calendar.events.delete (calendar events delete): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.delete.json`

#### `mail.folders.messages.get`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Query: `$expand`: `attachments($select=id,name,contentType,size,isInline)`
- Headers: `Prefer: outlook.timezone="UTC"`
- Client reads: `@odata.type`, `id`, `subject`, `from.emailAddress.{address,name}`, `{toRecipients|ccRecipients|bccRecipients}[].emailAddress.address`, `body.content`, `body.contentType`, `bodyPreview`, `isRead`, `flag.flagStatus`, `receivedDateTime`, `sentDateTime`, `importance`, `conversationId`, `hasAttachments`, `parentFolderId`, `meetingMessageType (eventMessage)`, `startDateTime.{dateTime,timeZone} (eventMessage)`, `endDateTime.{dateTime,timeZone} (eventMessage)`, `location.displayName (eventMessage)`, `isAllDay (eventMessage)`, `proposedNewTime.{start,end}.dateTime (eventMessageResponse)`, `attachments[].{id,name,contentType,size,isInline,@odata.type}`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-get
- Notes: Graph's alias of mail.messages.get (mail messages get): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.get.json`

#### `mail.folders.messages.update`

**PATCH v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`
- Request body: `isRead`
- Client reads: `id`, `subject`, `from.emailAddress.address`, `from.emailAddress.name`, `toRecipients[].emailAddress.{address,name}`, `ccRecipients[].emailAddress.{address,name}`, `bccRecipients[].emailAddress.{address,name}`, `bodyPreview`, `isRead`, `flag.flagStatus`, `receivedDateTime`, `sentDateTime`, `importance`, `conversationId`, `hasAttachments`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-update
- Notes: Graph's alias of mail.messages.update-read-state (mail messages update-read-state): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.update.json`

#### `mail.folders.messages.delete`

**DELETE v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-delete
- Notes: Graph's alias of mail.messages.delete (mail messages delete): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.delete.json`

#### `contacts.folders.contacts.get`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/contacts/{contactId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `createdDateTime`, `lastModifiedDateTime`, `changeKey`, `categories`, `parentFolderId`, `birthday`, `fileAs`, `displayName`, `givenName`, `initials`, `middleName`, `nickName`, `surname`, `title`, `yomiGivenName`, `yomiSurname`, `yomiCompanyName`, `generation`, `imAddresses`, `jobTitle`, `companyName`, `department`, `officeLocation`, `profession`, `businessHomePage`, `assistantName`, `manager`, `homePhones`, `mobilePhone`, `businessPhones`, `spouseName`, `personalNotes`, `children`, `emailAddresses`, `primaryEmailAddress`, `homeAddress`, `businessAddress`, `otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contact-get
- Notes: Graph's alias of contacts.get (contacts get): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.get.json`

#### `contacts.folders.contacts.update`

**PATCH v1.0 `{mailbox}/contactFolders/{folderId}/contacts/{contactId}`** — forms: me, users; success: 200; paging: none

- Request body: `categories`; `birthday`; `fileAs`; `displayName`; `givenName`; `initials`; `middleName`; `nickName`; `surname`; `title`; `yomiGivenName`; `yomiSurname`; `yomiCompanyName`; `generation`; `imAddresses`; `jobTitle`; `companyName`; `department`; `officeLocation`; `profession`; `businessHomePage`; `assistantName`; `manager`; `homePhones`; `mobilePhone`; `businessPhones`; `spouseName`; `personalNotes`; `children`; `emailAddresses`; `homeAddress`; `businessAddress`; `otherAddress`
- Client reads: `id`, `createdDateTime`, `lastModifiedDateTime`, `changeKey`, `categories`, `parentFolderId`, `birthday`, `fileAs`, `displayName`, `givenName`, `initials`, `middleName`, `nickName`, `surname`, `title`, `yomiGivenName`, `yomiSurname`, `yomiCompanyName`, `generation`, `imAddresses`, `jobTitle`, `companyName`, `department`, `officeLocation`, `profession`, `businessHomePage`, `assistantName`, `manager`, `homePhones`, `mobilePhone`, `businessPhones`, `spouseName`, `personalNotes`, `children`, `emailAddresses`, `primaryEmailAddress`, `homeAddress`, `businessAddress`, `otherAddress`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contact-update
- Notes: Graph's alias of contacts.update (contacts update): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.update.json`

#### `contacts.folders.contacts.delete`

**DELETE v1.0 `{mailbox}/contactFolders/{folderId}/contacts/{contactId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/contact-delete
- Notes: Graph's alias of contacts.delete (contacts delete): the same handling. The parent segment is not checked: ids are unique in the mailbox.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.delete.json`

#### `tasks.checklist-items.get`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/checklistItems/{checklistItemId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `displayName`, `isChecked`, `createdDateTime`, `checkedDateTime?`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/checklistitem-get
- Notes: One step of the task, read from its checklist.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.checklist-items.get.json`

#### `tasks.linked-resources.get`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/{taskId}/linkedResources/{linkedResourceId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `webUrl`, `applicationName`, `displayName`, `externalId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/linkedresource-get
- Notes: One link, read from the task's links. Returned as the entity, as the other single GETs (checklistItem included) are; the reference page's sample wraps it in "value".
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.linked-resources.get.json`

### Phase 19 — Photos, MIME, copies, $count segments and more aliases (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`mail.folders.create`](#mailfolderscreate) | POST | v1.0 | `{mailbox}/mailFolders` | none |
| [`mail.folders.copy`](#mailfolderscopy) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/copy` | none |
| [`mail.messages.count`](#mailmessagescount) | GET | v1.0 | `{mailbox}/messages/$count` | none |
| [`mail.folders.messages.count`](#mailfoldersmessagescount) | GET | v1.0 | `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages/$count` | none |
| [`calendar.events.count`](#calendareventscount) | GET | v1.0 | `{mailbox}/events/$count` | none |
| [`calendar.default.events.count`](#calendardefaulteventscount) | GET | v1.0 | `{mailbox}/calendar/events/$count` | none |
| [`calendar.calendars.events.count`](#calendarcalendarseventscount) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/$count` | none |
| [`contacts.count`](#contactscount) | GET | v1.0 | `{mailbox}/contacts/$count` | none |
| [`contacts.folders.contacts.count`](#contactsfolderscontactscount) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/$count` | none |
| [`tasks.tasks.count`](#taskstaskscount) | GET | v1.0 | `{mailbox}/todo/lists/{listId}/tasks/$count` | none |
| [`directory.me.photo-value`](#directorymephoto-value) | GET | v1.0 | `/me/photo/$value` | none |
| [`directory.me.photo.get`](#directorymephotoget) | GET | v1.0 | `/me/photo` | none |
| [`directory.users.photo.get`](#directoryusersphotoget) | GET | v1.0 | `/users/{userIdOrUpn}/photo` | none |
| [`directory.me.photo.put`](#directorymephotoput) | PUT | v1.0 | `/me/photo/$value` | none |
| [`directory.me.photo.patch`](#directorymephotopatch) | PATCH | v1.0 | `/me/photo/$value` | none |
| [`directory.users.photo.put`](#directoryusersphotoput) | PUT | v1.0 | `/users/{userIdOrUpn}/photo/$value` | none |
| [`directory.users.photo.patch`](#directoryusersphotopatch) | PATCH | v1.0 | `/users/{userIdOrUpn}/photo/$value` | none |
| [`directory.me.photo.delete`](#directorymephotodelete) | DELETE | v1.0 | `/me/photo/$value` | none |
| [`directory.users.photo.delete`](#directoryusersphotodelete) | DELETE | v1.0 | `/users/{userIdOrUpn}/photo/$value` | none |
| [`mail.messages.create-mime`](#mailmessagescreate-mime) | POST | v1.0 | `{mailbox}/messages` | none |
| [`mail.messages.send-mime`](#mailmessagessend-mime) | POST | v1.0 | `{mailbox}/sendMail` | none |
| [`calendar.events.attachments.value`](#calendareventsattachmentsvalue) | GET | v1.0 | `{mailbox}/events/{eventId}/attachments/{attachmentId}/$value` | none |
| [`calendar.events.attachments.create-upload-session`](#calendareventsattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/events/{eventId}/attachments/createUploadSession` | none |
| [`calendar.events.attachments.upload-session-put`](#calendareventsattachmentsupload-session-put) | PUT | v1.0 | `{uploadUrl}` | none |
| [`calendar.events.attachments.upload-session-put-final`](#calendareventsattachmentsupload-session-put-final) | PUT | v1.0 | `{uploadUrl}` | none |
| [`calendar.default.events.accept`](#calendardefaulteventsaccept) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/accept` | none |
| [`calendar.calendars.events.accept`](#calendarcalendarseventsaccept) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/accept` | none |
| [`calendar.default.events.tentatively-accept`](#calendardefaulteventstentatively-accept) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/tentativelyAccept` | none |
| [`calendar.calendars.events.tentatively-accept`](#calendarcalendarseventstentatively-accept) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/tentativelyAccept` | none |
| [`calendar.default.events.decline`](#calendardefaulteventsdecline) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/decline` | none |
| [`calendar.calendars.events.decline`](#calendarcalendarseventsdecline) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/decline` | none |
| [`calendar.default.events.cancel`](#calendardefaulteventscancel) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/cancel` | none |
| [`calendar.calendars.events.cancel`](#calendarcalendarseventscancel) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/cancel` | none |
| [`calendar.default.events.forward`](#calendardefaulteventsforward) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/forward` | none |
| [`calendar.calendars.events.forward`](#calendarcalendarseventsforward) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/forward` | none |
| [`calendar.default.events.instances`](#calendardefaulteventsinstances) | GET | v1.0 | `{mailbox}/calendar/events/{eventId}/instances` | none |
| [`calendar.calendars.events.instances`](#calendarcalendarseventsinstances) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/instances` | none |
| [`calendar.default.events.snooze-reminder`](#calendardefaulteventssnooze-reminder) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/snoozeReminder` | none |
| [`calendar.calendars.events.snooze-reminder`](#calendarcalendarseventssnooze-reminder) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/snoozeReminder` | none |
| [`calendar.default.events.dismiss-reminder`](#calendardefaulteventsdismiss-reminder) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/dismissReminder` | none |
| [`calendar.calendars.events.dismiss-reminder`](#calendarcalendarseventsdismiss-reminder) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/dismissReminder` | none |
| [`calendar.default.events.attachments.list`](#calendardefaulteventsattachmentslist) | GET | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments` | none |
| [`calendar.calendars.events.attachments.list`](#calendarcalendarseventsattachmentslist) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments` | none |
| [`calendar.default.events.attachments.get`](#calendardefaulteventsattachmentsget) | GET | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.calendars.events.attachments.get`](#calendarcalendarseventsattachmentsget) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.default.events.attachments.create`](#calendardefaulteventsattachmentscreate) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments` | none |
| [`calendar.calendars.events.attachments.create`](#calendarcalendarseventsattachmentscreate) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments` | none |
| [`calendar.default.events.attachments.delete`](#calendardefaulteventsattachmentsdelete) | DELETE | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.calendars.events.attachments.delete`](#calendarcalendarseventsattachmentsdelete) | DELETE | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}` | none |
| [`calendar.default.events.attachments.value`](#calendardefaulteventsattachmentsvalue) | GET | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}/$value` | none |
| [`calendar.calendars.events.attachments.value`](#calendarcalendarseventsattachmentsvalue) | GET | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}/$value` | none |
| [`calendar.default.events.attachments.create-upload-session`](#calendardefaulteventsattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/calendar/events/{eventId}/attachments/createUploadSession` | none |
| [`calendar.calendars.events.attachments.create-upload-session`](#calendarcalendarseventsattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/createUploadSession` | none |
| [`mail.folders.messages.get-mime`](#mailfoldersmessagesget-mime) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/$value` | none |
| [`mail.folders.messages.attachments.list`](#mailfoldersmessagesattachmentslist) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments` | none |
| [`mail.folders.messages.attachments.get`](#mailfoldersmessagesattachmentsget) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}` | none |
| [`mail.folders.messages.attachments.get-value`](#mailfoldersmessagesattachmentsget-value) | GET | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}/$value` | none |
| [`mail.folders.messages.attachments.create`](#mailfoldersmessagesattachmentscreate) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments` | none |
| [`mail.folders.messages.attachments.delete`](#mailfoldersmessagesattachmentsdelete) | DELETE | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}` | none |
| [`mail.folders.messages.attachments.create-upload-session`](#mailfoldersmessagesattachmentscreate-upload-session) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/createUploadSession` | none |
| [`mail.folders.messages.move`](#mailfoldersmessagesmove) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/move` | none |
| [`mail.folders.messages.copy`](#mailfoldersmessagescopy) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/copy` | none |
| [`mail.folders.messages.permanent-delete`](#mailfoldersmessagespermanent-delete) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/permanentDelete` | none |
| [`mail.folders.messages.send-draft`](#mailfoldersmessagessend-draft) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/send` | none |
| [`mail.folders.messages.reply`](#mailfoldersmessagesreply) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/reply` | none |
| [`mail.folders.messages.reply-all`](#mailfoldersmessagesreply-all) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/replyAll` | none |
| [`mail.folders.messages.create-reply`](#mailfoldersmessagescreate-reply) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createReply` | none |
| [`mail.folders.messages.create-reply-all`](#mailfoldersmessagescreate-reply-all) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createReplyAll` | none |
| [`mail.folders.messages.forward`](#mailfoldersmessagesforward) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/forward` | none |
| [`mail.folders.messages.create-forward`](#mailfoldersmessagescreate-forward) | POST | v1.0 | `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createForward` | none |
| [`contacts.folders.contacts.photo-get`](#contactsfolderscontactsphoto-get) | GET | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/{contactId}/photo/$value` | none |
| [`contacts.folders.contacts.photo-put`](#contactsfolderscontactsphoto-put) | PUT | v1.0 | `{mailbox}/contactFolders/{folderId}/contacts/{contactId}/photo/$value` | none |
| [`calendar.default.update`](#calendardefaultupdate) | PATCH | v1.0 | `{mailbox}/calendar` | none |
| [`calendar.default.permissions.list`](#calendardefaultpermissionslist) | GET | v1.0 | `{mailbox}/calendar/calendarPermissions` | none |
| [`calendar.default.permissions.create`](#calendardefaultpermissionscreate) | POST | v1.0 | `{mailbox}/calendar/calendarPermissions` | none |
| [`calendar.default.permissions.update`](#calendardefaultpermissionsupdate) | PATCH | v1.0 | `{mailbox}/calendar/calendarPermissions/{permissionId}` | none |
| [`calendar.default.permissions.delete`](#calendardefaultpermissionsdelete) | DELETE | v1.0 | `{mailbox}/calendar/calendarPermissions/{permissionId}` | none |
| [`calendar.calendar-groups.get`](#calendarcalendar-groupsget) | GET | v1.0 | `{mailbox}/calendarGroups/{groupId}` | none |
| [`rooms.room-lists.list`](#roomsroom-listslist) | GET | v1.0 | `/places/microsoft.graph.roomList` | none |
| [`rooms.room-lists.rooms`](#roomsroom-listsrooms) | GET | v1.0 | `/places/{roomListEmail}/microsoft.graph.roomList/rooms` | none |
| [`mail.translate-exchange-ids`](#mailtranslate-exchange-ids) | POST | v1.0 | `{mailbox}/translateExchangeIds` | none |

#### `mail.folders.create`

**POST v1.0 `{mailbox}/mailFolders`** — forms: me, users; success: 201; paging: none

- Request body: `displayName`; `isHidden`
- Client reads: `id`, `displayName`, `parentFolderId`, `childFolderCount`, `unreadItemCount`, `totalItemCount`, `isHidden`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-post-mailfolders
- Notes: A top-level folder (under msgfolderroot), as NightMail's childFolders create of msgfolderroot. A name already used at the top level is 409 ErrorFolderExists. Hidden folders have no counterpart in the mail store, so isHidden: true is refused rather than ignored.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.create.json`

#### `mail.folders.copy`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/copy`** — forms: me, users; success: 200; paging: none

- Request body: `destinationId`
- Client reads: `id`, `displayName`, `parentFolderId`, `childFolderCount`, `unreadItemCount`, `totalItemCount`, `isHidden`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/mailfolder-copy
- Notes: Copies the folder, its subfolders and every message in them under the destination (each message a distinct copy, as messages/{id}/copy). 200 with the new folder, as Graph's reference. A name taken at the destination is 409 ErrorFolderExists; a destination inside the folder is 400. A well-known folder copies as a plain folder; a failure part-way leaves what was copied.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.copy.json`

#### `mail.messages.count`

**GET v1.0 `{mailbox}/messages/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the message list reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as mail.messages.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.count.json`

#### `mail.folders.messages.count`

**GET v1.0 `{mailbox}/mailFolders/{folderIdOrWellKnownName}/messages/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the folder messages reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as mail.messages.list-in-folder.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.count.json`

#### `calendar.events.count`

**GET v1.0 `{mailbox}/events/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the event list reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as calendar.events.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.count.json`

#### `calendar.default.events.count`

**GET v1.0 `{mailbox}/calendar/events/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the calendar events reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as calendar.default.events.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.count.json`

#### `calendar.calendars.events.count`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the calendar events reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as calendar.calendars.events.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.count.json`

#### `contacts.count`

**GET v1.0 `{mailbox}/contacts/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the contact list reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as contacts.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.count.json`

#### `contacts.folders.contacts.count`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/contacts/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the folder contacts reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as contacts.folders.contacts.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.count.json`

#### `tasks.tasks.count`

**GET v1.0 `{mailbox}/todo/lists/{listId}/tasks/$count`** — forms: me, users; success: 200; paging: none

- Client reads: `(text/plain integer)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/query-parameters#count-parameter
- Notes: The /$count segment (OData; Graph's query-parameters page: "the integer total of the collection"; the task list reference page doesn't mention it). Runs the list's own handling with $count=true, so it counts exactly what @odata.count would: every match of $filter/$search, not a page. Answers the integer as text/plain. Same query options as tasks.tasks.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/tasks.tasks.count.json`

#### `directory.me.photo-value`

**GET v1.0 `/me/photo/$value`** — forms: root; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-get
- Notes: The signed-in user's photo, as /users/{x}/photo/$value; 404 ImageNotFound without one.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.photo-value.json`

#### `directory.me.photo.get`

**GET v1.0 `/me/photo`** — forms: root; success: 200; paging: none

- Client reads: `@odata.mediaContentType`, `@odata.mediaEtag`, `id`, `width`, `height`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-get
- Notes: The photo's metadata, read from the image (type, pixel size, a tag that changes with it). Without a photo it is Graph's documented 1x1 image/gif placeholder with an empty mediaEtag, not a 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.photo.get.json`

#### `directory.users.photo.get`

**GET v1.0 `/users/{userIdOrUpn}/photo`** — forms: root; success: 200; paging: none

- Client reads: `@odata.mediaContentType`, `@odata.mediaEtag`, `id`, `width`, `height`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-get
- Notes: As directory.me.photo.get for another user (looked up as /users/{x}/photo/$value is). An unknown user is 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.photo.get.json`

#### `directory.me.photo.put`

**PUT v1.0 `/me/photo/$value`** — forms: root; success: 200; paging: none

- Headers: `Content-Type: image/jpeg`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: Sets the signed-in user's own photo in Nextcloud (their avatar). Any image up to 4 MB: Nextcloud keeps only a square JPEG or PNG, so the façade turns anything else upright (EXIF orientation), crops its centre square and re-encodes it (at most 1024 px a side); an image it can't read or over 50 megapixels is 400, over 4 MB 413. The image's bytes decide its type. 200, no body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.photo.put.json`

#### `directory.me.photo.patch`

**PATCH v1.0 `/me/photo/$value`** — forms: root; success: 200; paging: none

- Headers: `Content-Type: image/jpeg`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: Sets the signed-in user's own photo in Nextcloud (their avatar). Any image up to 4 MB: Nextcloud keeps only a square JPEG or PNG, so the façade turns anything else upright (EXIF orientation), crops its centre square and re-encodes it (at most 1024 px a side); an image it can't read or over 50 megapixels is 400, over 4 MB 413. The image's bytes decide its type. 200, no body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.photo.patch.json`

#### `directory.users.photo.put`

**PUT v1.0 `/users/{userIdOrUpn}/photo/$value`** — forms: root; success: 200; paging: none

- Headers: `Content-Type: image/jpeg`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: Sets the signed-in user's own photo in Nextcloud (their avatar). Any image up to 4 MB: Nextcloud keeps only a square JPEG or PNG, so the façade turns anything else upright (EXIF orientation), crops its centre square and re-encodes it (at most 1024 px a side); an image it can't read or over 50 megapixels is 400, over 4 MB 413. The image's bytes decide its type. 200, no body. On /users/{x} only when x is the caller; anyone else's photo is 403 (Graph's administrator right has no counterpart).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.photo.put.json`

#### `directory.users.photo.patch`

**PATCH v1.0 `/users/{userIdOrUpn}/photo/$value`** — forms: root; success: 200; paging: none

- Headers: `Content-Type: image/jpeg`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: Sets the signed-in user's own photo in Nextcloud (their avatar). Any image up to 4 MB: Nextcloud keeps only a square JPEG or PNG, so the façade turns anything else upright (EXIF orientation), crops its centre square and re-encodes it (at most 1024 px a side); an image it can't read or over 50 megapixels is 400, over 4 MB 413. The image's bytes decide its type. 200, no body. On /users/{x} only when x is the caller; anyone else's photo is 403 (Graph's administrator right has no counterpart).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.photo.patch.json`

#### `directory.me.photo.delete`

**DELETE v1.0 `/me/photo/$value`** — forms: root; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-delete
- Notes: Removes the signed-in user's photo (Nextcloud then draws its letter avatar, which reads as no photo). 200.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.photo.delete.json`

#### `directory.users.photo.delete`

**DELETE v1.0 `/users/{userIdOrUpn}/photo/$value`** — forms: root; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-delete
- Notes: As directory.me.photo.delete. On /users/{x} only when x is the caller; anyone else's photo is 403 (Graph's administrator right has no counterpart).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.photo.delete.json`

#### `mail.messages.create-mime`

**POST v1.0 `{mailbox}/messages`** — forms: me, users; success: 201; paging: none

- Headers: `Content-Type: text/plain`
- Client reads: `id`, `subject`, `isDraft`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-post-messages
- Notes: MIME form (Content-Type: text/plain, the message base64-encoded; line breaks allowed): saves the message as written as a draft in Drafts. Its own endpoint, chosen by content type; the JSON form (mail.drafts.create) is untouched. The Message-ID is always minted (a draft's id is built from it); a message without From gets the mailbox's. Not base64: 400 ErrorMimeContentInvalidBase64String; over 4 MB: 413.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.create-mime.json`

#### `mail.messages.send-mime`

**POST v1.0 `{mailbox}/sendMail`** — forms: me, users; success: 202; paging: none

- Headers: `Content-Type: text/plain`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-sendmail
- Notes: MIME form (Content-Type: text/plain, base64): sends the message as written and saves it to Sent Items. Bcc recipients get it, without the Bcc header. From must be an address the mailbox may send as (403 otherwise). Not base64: 400 ErrorMimeContentInvalidBase64String; over 4 MB: 413.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.send-mime.json`

#### `calendar.events.attachments.value`

**GET v1.0 `{mailbox}/events/{eventId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: The attachment's bytes with its own content type, streamed from Nextcloud Files (any size, unlike the inline contentBytes).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.value.json`

#### `calendar.events.attachments.create-upload-session`

**POST v1.0 `{mailbox}/events/{eventId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `AttachmentItem`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: A large event attachment (up to 150 MB) in chunks of up to 4 MB, as for messages: the upload URL is self-authorising (sent without a bearer) and lives an hour. Chunks are staged in Nextcloud's chunked upload; the last one makes the file an attachment of the event (in the owner's Files, linked by ATTACH). Only file attachments, not inline (501).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.create-upload-session.json`

#### `calendar.events.attachments.upload-session-put`

**PUT v1.0 `{uploadUrl}`** — forms: root; success: 200; paging: none

- Headers: `Content-Range: bytes first-last/total`
- Client reads: `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: One chunk, Content-Range required and starting at the next expected byte (400 otherwise). Answers the next byte wanted, as Outlook's attachment sessions do (no trailing '-').
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.upload-session-put.json`

#### `calendar.events.attachments.upload-session-put-final`

**PUT v1.0 `{uploadUrl}`** — forms: root; success: 201; paging: none

- Headers: `Content-Range: bytes first-last/total`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: The last chunk: the file becomes an attachment of the event. 201 with the attachment's Location and no body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.events.attachments.upload-session-put-final.json`

#### `calendar.default.events.accept`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/accept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-accept
- Notes: Graph's alias of calendar.events.accept on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.accept.json`

#### `calendar.calendars.events.accept`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/accept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-accept
- Notes: Graph's alias of calendar.events.accept on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.accept.json`

#### `calendar.default.events.tentatively-accept`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/tentativelyAccept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-tentativelyaccept
- Notes: Graph's alias of calendar.events.tentatively-accept on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.tentatively-accept.json`

#### `calendar.calendars.events.tentatively-accept`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/tentativelyAccept`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-tentativelyaccept
- Notes: Graph's alias of calendar.events.tentatively-accept on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.tentatively-accept.json`

#### `calendar.default.events.decline`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/decline`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`; `proposedNewTime? {start,end: {dateTime, timeZone}} — propose-new-time is a decline carrying this`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-decline
- Notes: Graph's alias of calendar.events.decline on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.decline.json`

#### `calendar.calendars.events.decline`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/decline`** — forms: me, users; success: 202; paging: none

- Request body: `sendResponse (always true)`; `comment (string, may be '')`; `proposedNewTime? {start,end: {dateTime, timeZone}} — propose-new-time is a decline carrying this`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-decline
- Notes: Graph's alias of calendar.events.decline on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.decline.json`

#### `calendar.default.events.cancel`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/cancel`** — forms: me, users; success: 202; paging: none

- Request body: `comment (always '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-cancel
- Notes: Graph's alias of calendar.events.cancel on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.cancel.json`

#### `calendar.calendars.events.cancel`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/cancel`** — forms: me, users; success: 202; paging: none

- Request body: `comment (always '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-cancel
- Notes: Graph's alias of calendar.events.cancel on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.cancel.json`

#### `calendar.default.events.forward`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/forward`** — forms: me, users; success: 202; paging: none

- Request body: `ToRecipients[].emailAddress.address (PascalCase key)`; `Comment (PascalCase key, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-forward
- Notes: Graph's alias of calendar.events.forward on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.forward.json`

#### `calendar.calendars.events.forward`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/forward`** — forms: me, users; success: 202; paging: none

- Request body: `ToRecipients[].emailAddress.address (PascalCase key)`; `Comment (PascalCase key, may be '')`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-forward
- Notes: Graph's alias of calendar.events.forward on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.forward.json`

#### `calendar.default.events.instances`

**GET v1.0 `{mailbox}/calendar/events/{eventId}/instances`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`, `value[].seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-list-instances
- Notes: Graph's alias of calendar.events.instances on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.instances.json`

#### `calendar.calendars.events.instances`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/instances`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`, `value[].seriesMasterId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-list-instances
- Notes: Graph's alias of calendar.events.instances on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.instances.json`

#### `calendar.default.events.snooze-reminder`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/snoozeReminder`** — forms: me, users; success: 202; paging: none

- Request body: `newReminderTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-snoozereminder
- Notes: Graph's alias of calendar.events.snooze-reminder on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.snooze-reminder.json`

#### `calendar.calendars.events.snooze-reminder`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/snoozeReminder`** — forms: me, users; success: 202; paging: none

- Request body: `newReminderTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-snoozereminder
- Notes: Graph's alias of calendar.events.snooze-reminder on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.snooze-reminder.json`

#### `calendar.default.events.dismiss-reminder`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/dismissReminder`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-dismissreminder
- Notes: Graph's alias of calendar.events.dismiss-reminder on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.dismiss-reminder.json`

#### `calendar.calendars.events.dismiss-reminder`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/dismissReminder`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-dismissreminder
- Notes: Graph's alias of calendar.events.dismiss-reminder on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.dismiss-reminder.json`

#### `calendar.default.events.attachments.list`

**GET v1.0 `{mailbox}/calendar/events/{eventId}/attachments`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`, `value[].isInline`, `value[].lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-list-attachments
- Notes: Graph's alias of calendar.events.attachments.list on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.list.json`

#### `calendar.calendars.events.attachments.list`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`, `value[].isInline`, `value[].lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-list-attachments
- Notes: Graph's alias of calendar.events.attachments.list on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.list.json`

#### `calendar.default.events.attachments.get`

**GET v1.0 `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`, `contentBytes`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of calendar.events.attachments.get on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.get.json`

#### `calendar.calendars.events.attachments.get`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`, `contentBytes`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of calendar.events.attachments.get on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.get.json`

#### `calendar.default.events.attachments.create`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type`; `name`; `contentType?`; `contentBytes`
- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-post-attachments
- Notes: Graph's alias of calendar.events.attachments.create on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.create.json`

#### `calendar.calendars.events.attachments.create`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type`; `name`; `contentType?`; `contentBytes`
- Client reads: `id`, `name`, `contentType`, `size`, `isInline`, `lastModifiedDateTime`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/event-post-attachments
- Notes: Graph's alias of calendar.events.attachments.create on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.create.json`

#### `calendar.default.events.attachments.delete`

**DELETE v1.0 `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-delete
- Notes: Graph's alias of calendar.events.attachments.delete on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.delete.json`

#### `calendar.calendars.events.attachments.delete`

**DELETE v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-delete
- Notes: Graph's alias of calendar.events.attachments.delete on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.delete.json`

#### `calendar.default.events.attachments.value`

**GET v1.0 `{mailbox}/calendar/events/{eventId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of calendar.events.attachments.value on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.value.json`

#### `calendar.calendars.events.attachments.value`

**GET v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of calendar.events.attachments.value on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.value.json`

#### `calendar.default.events.attachments.create-upload-session`

**POST v1.0 `{mailbox}/calendar/events/{eventId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `AttachmentItem`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: Graph's alias of calendar.events.attachments.create-upload-session on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.events.attachments.create-upload-session.json`

#### `calendar.calendars.events.attachments.create-upload-session`

**POST v1.0 `{mailbox}/calendars/{calendarId}/events/{eventId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `AttachmentItem`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: Graph's alias of calendar.events.attachments.create-upload-session on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendars.events.attachments.create-upload-session.json`

#### `mail.folders.messages.get-mime`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/$value`** — forms: me, users; success: 200; paging: none

- Client reads: `<raw RFC 822 bytes>`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-get
- Notes: Graph's alias of mail.messages.get-mime on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.get-mime.json`

#### `mail.folders.messages.attachments.list`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments`** — forms: me, users; success: 200; paging: none

- Query: `$select` (variant 1): `id,name,contentType`; `$select` (variant 2): `id,name,size`
- Client reads: `value[].id`, `value[].name`, `value[].contentType`, `value[].size`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-list-attachments
- Notes: Graph's alias of mail.attachments.list on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.list.json`

#### `mail.folders.messages.attachments.get`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}`** — forms: me, users; success: 200; paging: none

- Client reads: `@odata.type`, `id`, `name`, `contentType`, `size`, `isInline`, `contentId`, `contentBytes`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of mail.attachments.get on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.get.json`

#### `mail.folders.messages.attachments.get-value`

**GET v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}/$value`** — forms: me, users; success: 200; paging: none

- Client reads: `<raw bytes>`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-get
- Notes: Graph's alias of mail.attachments.get-value on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.get-value.json`

#### `mail.folders.messages.attachments.create`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments`** — forms: me, users; success: 201; paging: none

- Request body: `@odata.type ('#microsoft.graph.fileAttachment')`; `name`; `contentType`; `contentBytes (base64)`; `isInline (only when true)`; `contentId (inline only)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-post-attachments
- Notes: Graph's alias of mail.attachments.create on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.create.json`

#### `mail.folders.messages.attachments.delete`

**DELETE v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/{attachmentId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-delete
- Notes: Graph's alias of mail.attachments.delete on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.delete.json`

#### `mail.folders.messages.attachments.create-upload-session`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/attachments/createUploadSession`** — forms: me, users; success: 201; paging: none

- Request body: `AttachmentItem.attachmentType`; `AttachmentItem.name`; `AttachmentItem.size`; `AttachmentItem.contentType?`; `AttachmentItem.isInline?`; `AttachmentItem.contentId?`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/attachment-createuploadsession
- Notes: Graph's alias of mail.attachments.create-upload-session on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.attachments.create-upload-session.json`

#### `mail.folders.messages.move`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/move`** — forms: me, users; success: 201; paging: none

- Request body: `destinationId (folder id or well-known name: deleteditems, junkemail, inbox)`
- Client reads: `id`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-move
- Notes: Graph's alias of mail.messages.move on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.move.json`

#### `mail.folders.messages.copy`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/copy`** — forms: me, users; success: 201; paging: none

- Request body: `destinationId`
- Client reads: `id`, `flag.flagStatus`, `flag.startDateTime?`, `flag.dueDateTime?`, `flag.completedDateTime?`, `categories`, `importance`, `parentFolderId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-copy
- Notes: Graph's alias of mail.messages.copy on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.copy.json`

#### `mail.folders.messages.permanent-delete`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/permanentDelete`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-permanentdelete
- Notes: Graph's alias of mail.messages.permanent-delete on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.permanent-delete.json`

#### `mail.folders.messages.send-draft`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/send`** — forms: me, users; success: 202; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-send
- Notes: Graph's alias of mail.messages.send-draft on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.send-draft.json`

#### `mail.folders.messages.reply`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/reply`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject (optional; Graph builds 'RE: …' when absent)`; `message.body.{contentType,content}`; `message.toRecipients / ccRecipients / bccRecipients (each only when non-empty)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-reply
- Notes: Graph's alias of mail.messages.reply on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.reply.json`

#### `mail.folders.messages.reply-all`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/replyAll`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject (optional; Graph builds 'RE: …' when absent)`; `message.body.{contentType,content}`; `message.toRecipients / ccRecipients / bccRecipients (each only when non-empty)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-replyall
- Notes: Graph's alias of mail.messages.reply-all on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.reply-all.json`

#### `mail.folders.messages.create-reply`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createReply`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.{to,cc,bcc}Recipients?`
- Client reads: `id`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-createreply
- Notes: Graph's alias of mail.messages.create-reply on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.create-reply.json`

#### `mail.folders.messages.create-reply-all`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createReplyAll`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.{to,cc,bcc}Recipients?`
- Client reads: `id`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-createreplyall
- Notes: Graph's alias of mail.messages.create-reply-all on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.create-reply-all.json`

#### `mail.folders.messages.forward`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/forward`** — forms: me, users; success: 202; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.ccRecipients?`; `message.bccRecipients?`; `toRecipients[].emailAddress.address (top level, not inside message)`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-forward
- Notes: Graph's alias of mail.messages.forward on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.forward.json`

#### `mail.folders.messages.create-forward`

**POST v1.0 `{mailbox}/mailFolders/{folderId}/messages/{messageId}/createForward`** — forms: me, users; success: 201; paging: none

- Request body: `message.subject?`; `message.body.{contentType,content}`; `message.ccRecipients?`; `message.bccRecipients?`; `toRecipients (top level)`
- Client reads: `id`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/message-createforward
- Notes: Graph's alias of mail.messages.create-forward on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.folders.messages.create-forward.json`

#### `contacts.folders.contacts.photo-get`

**GET v1.0 `{mailbox}/contactFolders/{folderId}/contacts/{contactId}/photo/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-get
- Notes: Graph's alias of contacts.photo-get on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.photo-get.json`

#### `contacts.folders.contacts.photo-put`

**PUT v1.0 `{mailbox}/contactFolders/{folderId}/contacts/{contactId}/photo/$value`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/profilephoto-update
- Notes: Graph's alias of contacts.photo-put on the parent-scoped route: the same handling (the parent segment is not checked; item ids are unique in the mailbox).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/contacts.folders.contacts.photo-put.json`

#### `calendar.default.update`

**PATCH v1.0 `{mailbox}/calendar`** — forms: me, users; success: 200; paging: none

- Request body: `name?`; `color?`
- Client reads: `id`, `name`, `color`, `hexColor`, `isDefaultCalendar`, `canEdit`, `isRemovable`, `owner`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-update
- Notes: Graph's alias of calendar.calendars.update on the default calendar route: the same handling (the parent segment is not checked; item ids are unique in the mailbox). The default calendar is resolved first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.update.json`

#### `calendar.default.permissions.list`

**GET v1.0 `{mailbox}/calendar/calendarPermissions`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].role`, `value[].emailAddress`, `value[].isRemovable`, `value[].isInsideOrganization`, `value[].allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-list-calendarpermissions
- Notes: Graph's alias of calendar.permissions.list on the default calendar route: the same handling (the parent segment is not checked; item ids are unique in the mailbox). The default calendar is resolved first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.permissions.list.json`

#### `calendar.default.permissions.create`

**POST v1.0 `{mailbox}/calendar/calendarPermissions`** — forms: me, users; success: 201; paging: none

- Request body: `emailAddress`; `role`
- Client reads: `id`, `role`, `emailAddress`, `isRemovable`, `isInsideOrganization`, `allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendar-post-calendarpermissions
- Notes: Graph's alias of calendar.permissions.create on the default calendar route: the same handling (the parent segment is not checked; item ids are unique in the mailbox). The default calendar is resolved first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.permissions.create.json`

#### `calendar.default.permissions.update`

**PATCH v1.0 `{mailbox}/calendar/calendarPermissions/{permissionId}`** — forms: me, users; success: 200; paging: none

- Request body: `role`
- Client reads: `id`, `role`, `emailAddress`, `isRemovable`, `isInsideOrganization`, `allowedRoles`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendarpermission-update
- Notes: Graph's alias of calendar.permissions.update on the default calendar route: the same handling (the parent segment is not checked; item ids are unique in the mailbox). The default calendar is resolved first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.permissions.update.json`

#### `calendar.default.permissions.delete`

**DELETE v1.0 `{mailbox}/calendar/calendarPermissions/{permissionId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendarpermission-delete
- Notes: Graph's alias of calendar.permissions.delete on the default calendar route: the same handling (the parent segment is not checked; item ids are unique in the mailbox). The default calendar is resolved first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.default.permissions.delete.json`

#### `calendar.calendar-groups.get`

**GET v1.0 `{mailbox}/calendarGroups/{groupId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `classId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/calendargroup-get
- Notes: The one calendar group, "My Calendars" (calendargroup-default), which holds every calendar (Nextcloud has no groups). Any other id is 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calendar.calendar-groups.get.json`

#### `rooms.room-lists.list`

**GET v1.0 `/places/microsoft.graph.roomList`** — forms: root; success: 200; paging: none

- Client reads: `value[].id`, `value[].displayName`, `value[].address.street`, `value[].emailAddress`, `value[].placeId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/place-list
- Notes: The buildings as room lists: Nextcloud has no room lists, only each room's building (room-building-name) and address. One list per building, its address as street (the directory keeps one line), and a stable emailAddress {slug}@roomlists.invalid (RFC 2606's reserved domain: it names the list in the next route and is never a mailbox). Rooms with no building are in no list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/rooms.room-lists.list.json`

#### `rooms.room-lists.rooms`

**GET v1.0 `/places/{roomListEmail}/microsoft.graph.roomList/rooms`** — forms: root; success: 200; paging: none

- Client reads: `value[].id`, `value[].emailAddress`, `value[].displayName`, `value[].capacity`, `value[].building`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/place-list
- Notes: The rooms of one building, by the list's emailAddress from rooms.room-lists.list; any other address is 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/rooms.room-lists.rooms.json`

#### `mail.translate-exchange-ids`

**POST v1.0 `{mailbox}/translateExchangeIds`** — forms: me, users; success: 200; paging: none

- Request body: `inputIds`; `sourceIdType`; `targetIdType`
- Client reads: `value[].sourceId`, `value[].targetId`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-translateexchangeids
- Notes: Nightware's ids are already immutable (a message keeps its id across moves), so restId and restImmutableEntryId are the same string and each id translates to itself. entryId, immutableEntryId and ewsId are Exchange store ids with no counterpart: 400 notSupported. At most 1,000 ids. Prefer: IdType="ImmutableId" is accepted everywhere and changes nothing, for the same reason (no Preference-Applied, as Graph documents none).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.translate-exchange-ids.json`

### Phase 20 — OneDrive over Nextcloud Files (Graph parity, ADR 0008)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`files.drive.get`](#filesdriveget) | GET | v1.0 | `{mailbox}/drive` | none |
| [`files.drives.list`](#filesdriveslist) | GET | v1.0 | `{mailbox}/drives` | none |
| [`files.drives.get`](#filesdrivesget) | GET | v1.0 | `/drives/{driveId}` | none |
| [`files.drive.special.get`](#filesdrivespecialget) | GET | v1.0 | `{mailbox}/drive/special/{name}` | none |
| [`files.drive.root.get`](#filesdriverootget) | GET | v1.0 | `{mailbox}/drive/root` | none |
| [`files.drive.items.get`](#filesdriveitemsget) | GET | v1.0 | `{mailbox}/drive/items/{itemId}` | none |
| [`files.drive.items.get-by-path`](#filesdriveitemsget-by-path) | GET | v1.0 | `{mailbox}/drive/root:/{path}:` | none |
| [`files.drive.items.children`](#filesdriveitemschildren) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/children` | nextLink |
| [`files.drive.items.create-folder`](#filesdriveitemscreate-folder) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/children` | none |
| [`files.drive.items.content`](#filesdriveitemscontent) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/content` | none |
| [`files.drive.items.download`](#filesdriveitemsdownload) | GET | v1.0 | `{downloadUrl}` | none |
| [`files.drive.items.upload`](#filesdriveitemsupload) | PUT | v1.0 | `{mailbox}/drive/items/{parentId}:/{fileName}:/content` | none |
| [`files.drive.items.replace-content`](#filesdriveitemsreplace-content) | PUT | v1.0 | `{mailbox}/drive/items/{itemId}/content` | none |
| [`files.drive.items.update`](#filesdriveitemsupdate) | PATCH | v1.0 | `{mailbox}/drive/items/{itemId}` | none |
| [`files.drive.items.delete`](#filesdriveitemsdelete) | DELETE | v1.0 | `{mailbox}/drive/items/{itemId}` | none |
| [`files.drive.items.copy`](#filesdriveitemscopy) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/copy` | none |
| [`files.drive.copy-monitor`](#filesdrivecopy-monitor) | GET | v1.0 | `{monitorUrl}` | none |
| [`files.drive.items.create-upload-session`](#filesdriveitemscreate-upload-session) | POST | v1.0 | `{mailbox}/drive/items/{parentId}:/{fileName}:/createUploadSession` | none |
| [`files.drive.upload-session-put`](#filesdriveupload-session-put) | PUT | v1.0 | `{uploadUrl}` | none |
| [`files.drive.upload-session-put-final`](#filesdriveupload-session-put-final) | PUT | v1.0 | `{uploadUrl}` | none |
| [`files.drive.upload-session-get`](#filesdriveupload-session-get) | GET | v1.0 | `{uploadUrl}` | none |
| [`files.drive.upload-session-delete`](#filesdriveupload-session-delete) | DELETE | v1.0 | `{uploadUrl}` | none |
| [`files.drive.root.delta`](#filesdriverootdelta) | GET | v1.0 | `{mailbox}/drive/root/delta` | deltaLink |
| [`files.drive.root.search`](#filesdriverootsearch) | GET | v1.0 | `{mailbox}/drive/root/search(q='{q}')` | nextLink |
| [`files.drive.items.thumbnails`](#filesdriveitemsthumbnails) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/thumbnails` | none |
| [`files.drive.items.thumbnail-content`](#filesdriveitemsthumbnail-content) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/thumbnails/{thumbId}/{size}/content` | none |
| [`files.drive.items.create-link`](#filesdriveitemscreate-link) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/createLink` | none |
| [`files.drive.items.invite`](#filesdriveitemsinvite) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/invite` | none |
| [`files.drive.items.permissions`](#filesdriveitemspermissions) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/permissions` | none |
| [`files.drive.items.permissions.delete`](#filesdriveitemspermissionsdelete) | DELETE | v1.0 | `{mailbox}/drive/items/{itemId}/permissions/{permId}` | none |

#### `files.drive.get`

**GET v1.0 `{mailbox}/drive`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `driveType`, `owner`, `quota`
- Graph parity (ADR 0006); reference: drive-get
- Notes: The caller's Nextcloud home as a personal drive. /users/{x}/drive only for the caller (403 otherwise). Quota from Nextcloud; total and remaining are left out when unlimited. /users/{x}/drive for a shared mailbox the caller is a member of is that mailbox's drive (the folder it shares with its members, ADR 0008 D12); anyone else's is 403.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.get.json`

#### `files.drives.list`

**GET v1.0 `{mailbox}/drives`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: drive-list
- Notes: The one drive the caller has.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drives.list.json`

#### `files.drives.get`

**GET v1.0 `/drives/{driveId}`** — forms: root; success: 200; paging: none

- Client reads: `id`
- Graph parity (ADR 0006); reference: drive-get
- Notes: Only the caller's own drive id ('nc!' + base64url of the Nextcloud uid); another is 404 itemNotFound.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drives.get.json`

#### `files.drive.special.get`

**GET v1.0 `{mailbox}/drive/special/{name}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`, `folder`
- Graph parity (ADR 0006); reference: drive-get-specialfolder
- Notes: documents, photos, cameraroll (Photos/Camera Roll), music and approot (Apps/{OAuth client}); created on first use.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.special.get.json`

#### `files.drive.root.get`

**GET v1.0 `{mailbox}/drive/root`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `*`; `$expand`: `children`
- Client reads: `id`, `root`, `folder`
- Graph parity (ADR 0006); reference: driveitem-get
- Notes: The home folder, with the root facet. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.root.get.json`

#### `files.drive.items.get`

**GET v1.0 `{mailbox}/drive/items/{itemId}`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `*`; `$expand`: `children`
- Client reads: `id`, `name`, `file`, `folder`, `parentReference`, `@microsoft.graph.downloadUrl`, `file.hashes`
- Graph parity (ADR 0006); reference: driveitem-get
- Notes: An item by its Nextcloud file id. $expand=children carries a folder's first 200 children; a file carries @microsoft.graph.downloadUrl (the five-minute self-authorising URL …/content redirects to) and, when Nextcloud stored one, file.hashes.sha1Hash/sha256Hash. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.get.json`

#### `files.drive.items.get-by-path`

**GET v1.0 `{mailbox}/drive/root:/{path}:`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `name`
- Graph parity (ADR 0006); reference: driveitem-get
- Notes: Path addressing, rewritten before routing; a segment that is empty, '.', '..', or holds a separator is 400. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.get-by-path.json`

#### `files.drive.items.children`

**GET v1.0 `{mailbox}/drive/items/{itemId}/children`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `*`; `$orderby`: `name`; `$orderby`: `lastModifiedDateTime`; `$orderby`: `size`; `$top`: `1-1000`
- Client reads: `value`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: driveitem-list-children
- Notes: A folder's items; $top (default 200, at most 1000), $orderby name|lastModifiedDateTime|size, $select, $skiptoken. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.children.json`

#### `files.drive.items.create-folder`

**POST v1.0 `{mailbox}/drive/items/{itemId}/children`** — forms: me, users; success: 201; paging: none

- Request body: `name`; `folder`; `@microsoft.graph.conflictBehavior`
- Client reads: `id`, `name`, `folder`
- Graph parity (ADR 0006); reference: driveitem-post-children
- Notes: MKCOL. conflictBehavior fail (default, 409 nameAlreadyExists), rename (name 1) or replace. A body without the folder facet is 400. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.create-folder.json`

#### `files.drive.items.content`

**GET v1.0 `{mailbox}/drive/items/{itemId}/content`** — forms: me, users; success: 302; paging: none

- Graph parity (ADR 0006); reference: driveitem-get-content
- Notes: 302 to a self-authorising download URL (five minutes, fetched without a bearer). A folder is 400 notSupported. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.content.json`

#### `files.drive.items.download`

**GET v1.0 `{downloadUrl}`** — forms: external; success: 200; paging: none

- Graph parity (ADR 0006); reference: driveitem-get-content
- Notes: The file's bytes with its own content type, streamed from Nextcloud.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.download.json`

#### `files.drive.items.upload`

**PUT v1.0 `{mailbox}/drive/items/{parentId}:/{fileName}:/content`** — forms: me, users; success: 201; paging: none

- Request body: `(raw bytes)`
- Client reads: `id`, `name`, `size`
- Graph parity (ADR 0006); reference: driveitem-put-content
- Notes: Simple upload, streamed into WebDAV; conflictBehavior (query) replace by default, fail, rename. Up to Graph's 250 MB (Limits:MaxLargeUploadBytes on this route only), with a 15-minute budget. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.upload.json`

#### `files.drive.items.replace-content`

**PUT v1.0 `{mailbox}/drive/items/{itemId}/content`** — forms: me, users; success: 200; paging: none

- Request body: `(raw bytes)`
- Client reads: `id`, `size`
- Graph parity (ADR 0006); reference: driveitem-put-content
- Notes: New content for an existing file; If-Match honoured (412 when stale). Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.replace-content.json`

#### `files.drive.items.update`

**PATCH v1.0 `{mailbox}/drive/items/{itemId}`** — forms: me, users; success: 200; paging: none

- Request body: `name`; `parentReference.id|path`
- Client reads: `id`, `name`, `parentReference`
- Graph parity (ADR 0006); reference: driveitem-update
- Notes: Rename and/or move in one WebDAV MOVE; the id survives. conflictBehavior fail by default; If-Match honoured. Other properties are ignored. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.update.json`

#### `files.drive.items.delete`

**DELETE v1.0 `{mailbox}/drive/items/{itemId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: driveitem-delete
- Notes: To Nextcloud's trash bin. The root can't be deleted (403). If-Match honoured. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.delete.json`

#### `files.drive.items.copy`

**POST v1.0 `{mailbox}/drive/items/{itemId}/copy`** — forms: me, users; success: 202; paging: none

- Request body: `parentReference`; `name`
- Graph parity (ADR 0006); reference: driveitem-copy
- Notes: WebDAV COPY (Depth infinity), done before answering; 202 with a self-authorising monitor Location as Graph's asynchronous copy. A copy beside itself with the same name is renamed. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.copy.json`

#### `files.drive.copy-monitor`

**GET v1.0 `{monitorUrl}`** — forms: external; success: 200; paging: none

- Client reads: `status`, `resourceId`
- Graph parity (ADR 0006); reference: driveitem-copy
- Notes: Always completed, with the copy's id.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.copy-monitor.json`

#### `files.drive.items.create-upload-session`

**POST v1.0 `{mailbox}/drive/items/{parentId}:/{fileName}:/createUploadSession`** — forms: me, users; success: 200; paging: none

- Request body: `item.@microsoft.graph.conflictBehavior`; `item.fileSize`
- Client reads: `uploadUrl`, `expirationDateTime`, `nextExpectedRanges`
- Graph parity (ADR 0006); reference: driveitem-createuploadsession
- Notes: Up to 10 GB in chunks of under 60 MiB, staged in Nextcloud's chunked upload; the upload URL is self-authorising (PUT, GET, DELETE) for 24 hours. items/{id}/createUploadSession replaces an existing file. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.create-upload-session.json`

#### `files.drive.upload-session-put`

**PUT v1.0 `{uploadUrl}`** — forms: external; success: 202; paging: none

- Request body: `(raw bytes)`
- Client reads: `nextExpectedRanges`
- Graph parity (ADR 0006); reference: driveitem-createuploadsession
- Notes: One chunk, Content-Range required and starting at the next expected byte; 202 with the next range.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.upload-session-put.json`

#### `files.drive.upload-session-put-final`

**PUT v1.0 `{uploadUrl}`** — forms: external; success: 201; paging: none

- Request body: `(raw bytes)`
- Client reads: `id`, `name`, `size`
- Graph parity (ADR 0006); reference: driveitem-createuploadsession
- Notes: The last chunk: the joined file is moved into place and answered as a driveItem.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.upload-session-put-final.json`

#### `files.drive.upload-session-get`

**GET v1.0 `{uploadUrl}`** — forms: external; success: 200; paging: none

- Client reads: `nextExpectedRanges`
- Graph parity (ADR 0006); reference: driveitem-createuploadsession
- Notes: The next range the session wants.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.upload-session-get.json`

#### `files.drive.upload-session-delete`

**DELETE v1.0 `{uploadUrl}`** — forms: external; success: 204; paging: none

- Graph parity (ADR 0006); reference: driveitem-createuploadsession
- Notes: Drops the session and its staged chunks.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.upload-session-delete.json`

#### `files.drive.root.delta`

**GET v1.0 `{mailbox}/drive/root/delta`** — forms: me, users; success: 200; paging: deltaLink

- Query: `$select` (always): `*`; `$top`: `1-1000`; `token`: `l`, `a`, `t`, `e`, `s`, `t`
- Client reads: `value`, `@odata.nextLink`, `@odata.deltaLink`
- Graph parity (ADR 0006); reference: driveitem-delta
- Notes: From Nextcloud's activity stream (ADR 0008 D5): the first sync lists every item (root first), later ones what changed, with {id, deleted} for what's gone. token=latest gives only a deltaLink. Without the activity app: 501 notSupported.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.root.delta.json`

#### `files.drive.root.search`

**GET v1.0 `{mailbox}/drive/root/search(q='{q}')`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `*`; `$orderby`: `name`; `$orderby`: `lastModifiedDateTime`; `$orderby`: `size`; `$top`: `1-1000`
- Client reads: `value`, `@odata.nextLink`
- On error: Any non-2xx → _mapException: 401 → AuthException (after AuthInterceptor's one refresh+retry), a connection error or timeout → NetworkException, else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:264`
- Notes: Names containing q, anywhere in the drive (Nextcloud SEARCH); also drive/search(q=…). The cloud picker's search for NightMail Server accounts (up to two pages); Microsoft 365 accounts try /search/query first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.root.search.json`

#### `files.drive.items.thumbnails`

**GET v1.0 `{mailbox}/drive/items/{itemId}/thumbnails`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: driveitem-list-thumbnails
- Notes: One set (small 96, medium 176, large 800) of self-authorising URLs to Nextcloud's preview; none for an item without a preview. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.thumbnails.json`

#### `files.drive.items.thumbnail-content`

**GET v1.0 `{mailbox}/drive/items/{itemId}/thumbnails/{thumbId}/{size}/content`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: driveitem-list-thumbnails
- Notes: The preview image; set 0 only. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.thumbnail-content.json`

#### `files.drive.items.create-link`

**POST v1.0 `{mailbox}/drive/items/{itemId}/createLink`** — forms: me, users; success: 201; paging: none

- Request body: `type`; `scope`; `expirationDateTime`; `password`
- Client reads: `id`, `roles`, `link`
- Graph parity (ADR 0006); reference: driveitem-createlink
- Notes: A Nextcloud public share: view (read), edit, or blocksDownload (read with Nextcloud's hide-download; link.preventsDownload true); scope anonymous only (organization, embed and other types are 400 notSupported). Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.create-link.json`

#### `files.drive.items.invite`

**POST v1.0 `{mailbox}/drive/items/{itemId}/invite`** — forms: me, users; success: 200; paging: none

- Request body: `recipients[].email`; `roles`; `expirationDateTime`
- Client reads: `value`
- Graph parity (ADR 0006); reference: driveitem-invite
- Notes: A Nextcloud user share per recipient (found by address); roles read or write. Nextcloud sends its own notification; sendInvitation and message are not used. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.invite.json`

#### `files.drive.items.permissions`

**GET v1.0 `{mailbox}/drive/items/{itemId}/permissions`** — forms: me, users; success: 200; paging: none

- Client reads: `value`
- Graph parity (ADR 0006); reference: driveitem-list-permissions
- Notes: The item's shares and those inherited from folders above (inheritedFrom). Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.permissions.json`

#### `files.drive.items.permissions.delete`

**DELETE v1.0 `{mailbox}/drive/items/{itemId}/permissions/{permId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: permission-delete
- Notes: Removes one of the item's own shares; an inherited one is 403. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.permissions.delete.json`

### Phase 21 — The directory: groups, manager, organization (Graph parity)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`directory.me.manager.get`](#directorymemanagerget) | GET | v1.0 | `/me/manager` | none |
| [`directory.users.manager.get`](#directoryusersmanagerget) | GET | v1.0 | `/users/{userIdOrUpn}/manager` | none |
| [`directory.me.direct-reports.list`](#directorymedirect-reportslist) | GET | v1.0 | `/me/directReports` | nextLink |
| [`directory.users.direct-reports.list`](#directoryusersdirect-reportslist) | GET | v1.0 | `/users/{userIdOrUpn}/directReports` | nextLink |
| [`directory.me.member-of.list`](#directorymemember-oflist) | GET | v1.0 | `/me/memberOf` | nextLink |
| [`directory.users.member-of.list`](#directoryusersmember-oflist) | GET | v1.0 | `/users/{userIdOrUpn}/memberOf` | nextLink |
| [`directory.me.transitive-member-of.list`](#directorymetransitive-member-oflist) | GET | v1.0 | `/me/transitiveMemberOf` | nextLink |
| [`directory.users.transitive-member-of.list`](#directoryuserstransitive-member-oflist) | GET | v1.0 | `/users/{userIdOrUpn}/transitiveMemberOf` | nextLink |
| [`directory.groups.list`](#directorygroupslist) | GET | v1.0 | `/groups` | nextLink |
| [`directory.groups.get`](#directorygroupsget) | GET | v1.0 | `/groups/{groupId}` | none |
| [`directory.groups.members.list`](#directorygroupsmemberslist) | GET | v1.0 | `/groups/{groupId}/members` | nextLink |
| [`directory.groups.transitive-members.list`](#directorygroupstransitive-memberslist) | GET | v1.0 | `/groups/{groupId}/transitiveMembers` | nextLink |
| [`directory.organization.list`](#directoryorganizationlist) | GET | v1.0 | `/organization` | none |
| [`directory.organization.get`](#directoryorganizationget) | GET | v1.0 | `/organization/{organizationId}` | none |

#### `directory.me.manager.get`

**GET v1.0 `/me/manager`** — forms: root; success: 200; paging: none

- Client reads: `@odata.type`, `id`, `displayName`, `mail`, `userPrincipalName`, `jobTitle`, `businessPhones[]`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-manager
- Notes: The manager the console set (Keycloak attribute nightware_manager); 404 Request_ResourceNotFound when none, as Graph's. $select only.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.manager.get.json`

#### `directory.users.manager.get`

**GET v1.0 `/users/{userIdOrUpn}/manager`** — forms: root; success: 200; paging: none

- Client reads: `@odata.type`, `id`, `displayName`, `mail`, `userPrincipalName`, `jobTitle`, `businessPhones[]`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-manager
- Notes: The manager the console set (Keycloak attribute nightware_manager); 404 Request_ResourceNotFound when none, as Graph's. $select only. The user is looked up as GET /users/{x} is; an unknown user is 404.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.manager.get.json`

#### `directory.me.direct-reports.list`

**GET v1.0 `/me/directReports`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].userPrincipalName`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-directreports
- Notes: Enabled people whose manager is the caller. $select, $top and $skiptoken.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.direct-reports.list.json`

#### `directory.users.direct-reports.list`

**GET v1.0 `/users/{userIdOrUpn}/directReports`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].userPrincipalName`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-directreports
- Notes: As directory.me.direct-reports.list for another user.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.direct-reports.list.json`

#### `directory.me.member-of.list`

**GET v1.0 `/me/memberOf`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].mailEnabled`, `value[].securityEnabled`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-memberof
- Notes: The caller's groups (ADR 0003) in their organizations, each led by @odata.type #microsoft.graph.group. $select, $top and $skiptoken.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.member-of.list.json`

#### `directory.users.member-of.list`

**GET v1.0 `/users/{userIdOrUpn}/memberOf`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].mailEnabled`, `value[].securityEnabled`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-memberof
- Notes: Another user's groups, in the organizations the caller shares with them.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.member-of.list.json`

#### `directory.me.transitive-member-of.list`

**GET v1.0 `/me/transitiveMemberOf`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].mailEnabled`, `value[].securityEnabled`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-transitivememberof
- Notes: Groups don't nest, so the same as directory.me.member-of.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.me.transitive-member-of.list.json`

#### `directory.users.transitive-member-of.list`

**GET v1.0 `/users/{userIdOrUpn}/transitiveMemberOf`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].mailEnabled`, `value[].securityEnabled`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/user-list-transitivememberof
- Notes: Groups don't nest, so the same as directory.users.member-of.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.users.transitive-member-of.list.json`

#### `directory.groups.list`

**GET v1.0 `/groups`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].id`, `value[].displayName`, `value[].mail`, `value[].mailEnabled`, `value[].mailNickname`, `value[].securityEnabled`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/group-list
- Notes: The groups of the caller's organizations: a list makes a group mail-enabled (a distribution group), a files facet security-enabled. $select, $top and $skiptoken.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.groups.list.json`

#### `directory.groups.get`

**GET v1.0 `/groups/{groupId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `mail`, `mailEnabled`, `mailNickname`, `securityEnabled`, `groupTypes[]`, `proxyAddresses[]`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/group-get
- Notes: A group of the caller's organizations; any other id is 404 Request_ResourceNotFound. $select.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.groups.get.json`

#### `directory.groups.members.list`

**GET v1.0 `/groups/{groupId}/members`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].userPrincipalName`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/group-list-members
- Notes: The group's enabled members (people only). $select, $top and $skiptoken.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.groups.members.list.json`

#### `directory.groups.transitive-members.list`

**GET v1.0 `/groups/{groupId}/transitiveMembers`** — forms: root; success: 200; paging: nextLink

- Client reads: `value[].@odata.type`, `value[].id`, `value[].displayName`, `value[].mail`, `value[].userPrincipalName`, `@odata.nextLink`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/group-list-transitivemembers
- Notes: Groups don't nest, so the same as directory.groups.members.list.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.groups.transitive-members.list.json`

#### `directory.organization.list`

**GET v1.0 `/organization`** — forms: root; success: 200; paging: none

- Client reads: `value[].id`, `value[].displayName`, `value[].verifiedDomains[].name`, `value[].verifiedDomains[].isDefault`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/organization-list
- Notes: The caller's organizations (entities), usually one: id, displayName and verifiedDomains (the mail domains, the first in name order the default); the rest null, as Graph gives a User.Read caller. $select.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.organization.list.json`

#### `directory.organization.get`

**GET v1.0 `/organization/{organizationId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `verifiedDomains[].name`, `verifiedDomains[].isDefault`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/organization-get
- Notes: One of the caller's organizations; any other id is 404 Request_ResourceNotFound.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/directory.organization.get.json`

### Phase 23 — Chats over Nextcloud Talk (Graph parity, ADR 0009)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`chats.list`](#chatslist) | GET | v1.0 | `{mailbox}/chats` | nextLink |
| [`chats.get`](#chatsget) | GET | v1.0 | `/chats/{chatId}` | none |
| [`chats.create`](#chatscreate) | POST | v1.0 | `/chats` | none |
| [`chats.update`](#chatsupdate) | PATCH | v1.0 | `/chats/{chatId}` | none |
| [`chats.hide`](#chatshide) | POST | v1.0 | `/chats/{chatId}/hideForUser` | none |
| [`chats.unhide`](#chatsunhide) | POST | v1.0 | `/chats/{chatId}/unhideForUser` | none |
| [`chats.mark-read`](#chatsmark-read) | POST | v1.0 | `/chats/{chatId}/markChatReadForUser` | none |
| [`chats.mark-unread`](#chatsmark-unread) | POST | v1.0 | `/chats/{chatId}/markChatUnreadForUser` | none |
| [`chats.members.list`](#chatsmemberslist) | GET | v1.0 | `/chats/{chatId}/members` | none |
| [`chats.members.get`](#chatsmembersget) | GET | v1.0 | `/chats/{chatId}/members/{membershipId}` | none |
| [`chats.members.add`](#chatsmembersadd) | POST | v1.0 | `/chats/{chatId}/members` | none |
| [`chats.members.remove`](#chatsmembersremove) | DELETE | v1.0 | `/chats/{chatId}/members/{membershipId}` | none |
| [`chats.messages.list`](#chatsmessageslist) | GET | v1.0 | `/chats/{chatId}/messages` | nextLink |
| [`chats.messages.get`](#chatsmessagesget) | GET | v1.0 | `/chats/{chatId}/messages/{messageId}` | none |
| [`chats.messages.send`](#chatsmessagessend) | POST | v1.0 | `/chats/{chatId}/messages` | none |
| [`chats.messages.update`](#chatsmessagesupdate) | PATCH | v1.0 | `/chats/{chatId}/messages/{messageId}` | none |
| [`chats.messages.soft-delete`](#chatsmessagessoft-delete) | POST | v1.0 | `{mailbox}/chats/{chatId}/messages/{messageId}/softDelete` | none |
| [`chats.messages.set-reaction`](#chatsmessagesset-reaction) | POST | v1.0 | `/chats/{chatId}/messages/{messageId}/setReaction` | none |
| [`chats.messages.unset-reaction`](#chatsmessagesunset-reaction) | POST | v1.0 | `/chats/{chatId}/messages/{messageId}/unsetReaction` | none |
| [`chats.pinned.list`](#chatspinnedlist) | GET | v1.0 | `/chats/{chatId}/pinnedMessages` | none |
| [`chats.pinned.add`](#chatspinnedadd) | POST | v1.0 | `/chats/{chatId}/pinnedMessages` | none |
| [`chats.pinned.remove`](#chatspinnedremove) | DELETE | v1.0 | `/chats/{chatId}/pinnedMessages/{pinnedMessageId}` | none |

#### `chats.list`

**GET v1.0 `{mailbox}/chats`** — forms: me, users; success: 200; paging: nextLink

- Client reads: `id`, `topic`, `chatType`, `createdDateTime`, `lastUpdatedDateTime`, `webUrl`, `viewpoint`, `onlineMeetingInfo`, `isHiddenForAllMembers`
- Graph parity (ADR 0006); reference: chat-list
- Notes: The caller's Talk conversations (ADR 0009 D3): one-to-one, group, meeting. Note to self, changelog, sample and channel rooms left out. /users/{x} only for the caller. $top up to 50; $expand=members and lastMessagePreview. Also served as GET /chats.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.list.json`

#### `chats.get`

**GET v1.0 `/chats/{chatId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `topic`, `chatType`, `createdDateTime`, `lastUpdatedDateTime`, `webUrl`, `viewpoint`, `onlineMeetingInfo`, `isHiddenForAllMembers`
- Graph parity (ADR 0006); reference: chat-get
- Notes: One Talk conversation by token. Also served as {mailbox}/chats/{chatId}.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.get.json`

#### `chats.create`

**POST v1.0 `/chats`** — forms: root; success: 201; paging: none

- Request body: `chatType`; `topic?`; `members[] {@odata.type, roles, user@odata.bind}`
- Client reads: `id`, `topic`, `chatType`, `createdDateTime`, `lastUpdatedDateTime`, `webUrl`, `viewpoint`, `onlineMeetingInfo`, `isHiddenForAllMembers`
- Graph parity (ADR 0006); reference: chat-post
- Notes: oneOnOne returns Talk's existing room, as Graph does; group creates a group room named topic.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.create.json`

#### `chats.update`

**PATCH v1.0 `/chats/{chatId}`** — forms: root; success: 200; paging: none

- Request body: `topic`
- Client reads: `id`, `topic`, `chatType`, `createdDateTime`, `lastUpdatedDateTime`, `webUrl`, `viewpoint`, `onlineMeetingInfo`, `isHiddenForAllMembers`
- Graph parity (ADR 0006); reference: chat-patch
- Notes: Renames a group conversation. A one-to-one chat has no topic (400). Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.update.json`

#### `chats.hide`

**POST v1.0 `/chats/{chatId}/hideForUser`** — forms: root; success: 204; paging: none

- Request body: `user {id}`
- Graph parity (ADR 0006); reference: chat-hideforuser
- Notes: Talk archive. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.hide.json`

#### `chats.unhide`

**POST v1.0 `/chats/{chatId}/unhideForUser`** — forms: root; success: 204; paging: none

- Request body: `user {id}`
- Graph parity (ADR 0006); reference: chat-unhideforuser
- Notes: Talk unarchive. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.unhide.json`

#### `chats.mark-read`

**POST v1.0 `/chats/{chatId}/markChatReadForUser`** — forms: root; success: 204; paging: none

- Request body: `user {id}`
- Graph parity (ADR 0006); reference: chat-markchatreadforuser
- Notes: Read marker to the newest message. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.mark-read.json`

#### `chats.mark-unread`

**POST v1.0 `/chats/{chatId}/markChatUnreadForUser`** — forms: root; success: 204; paging: none

- Request body: `user {id}`; `lastMessageReadDateTime?`
- Graph parity (ADR 0006); reference: chat-markchatunreadforuser
- Notes: Talk's mark-unread; lastMessageReadDateTime is ignored. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.mark-unread.json`

#### `chats.members.list`

**GET v1.0 `/chats/{chatId}/members`** — forms: root; success: 200; paging: none

- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: chat-list-members
- Notes: Room participants of type users; owners and moderators carry roles [owner]. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.members.list.json`

#### `chats.members.get`

**GET v1.0 `/chats/{chatId}/members/{membershipId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: chat-get-members
- Notes: Membership id m.<attendeeId> (ADR 0009 D2). Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.members.get.json`

#### `chats.members.add`

**POST v1.0 `/chats/{chatId}/members`** — forms: root; success: 201; paging: none

- Request body: `@odata.type`; `user@odata.bind`; `roles`; `visibleHistoryStartDateTime?`
- Graph parity (ADR 0006); reference: chat-post-members
- Notes: Talk add participant; history is always visible. 201 with Location and no body, as Graph's. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.members.add.json`

#### `chats.members.remove`

**DELETE v1.0 `/chats/{chatId}/members/{membershipId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: chat-delete-members
- Notes: Talk remove attendee, or leaving when it is the caller. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.members.remove.json`

#### `chats.messages.list`

**GET v1.0 `/chats/{chatId}/messages`** — forms: root; success: 200; paging: nextLink

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chat-list-messages
- Notes: Newest first; $top up to 50; $skiptoken is Talk's X-Chat-Last-Given (ADR 0009 D4). Never moves the read marker. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.list.json`

#### `chats.messages.get`

**GET v1.0 `/chats/{chatId}/messages/{messageId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-get
- Notes: One message (Talk chat context of the id). Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.get.json`

#### `chats.messages.send`

**POST v1.0 `/chats/{chatId}/messages`** — forms: root; success: 201; paging: none

- Request body: `body {contentType, content}`; `mentions[]?`; `importance?`; `subject?`
- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chat-post-messages
- Notes: Markdown to Talk; <at id> mentions become @"uid" (ADR 0009 D4). Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.send.json`

#### `chats.messages.update`

**PATCH v1.0 `/chats/{chatId}/messages/{messageId}`** — forms: root; success: 204; paging: none

- Request body: `body`; `mentions[]?`
- Graph parity (ADR 0006); reference: chatmessage-update
- Notes: Talk edit; the sender only. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.update.json`

#### `chats.messages.soft-delete`

**POST v1.0 `{mailbox}/chats/{chatId}/messages/{messageId}/softDelete`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: chatmessage-softdelete
- Notes: Talk delete; the message stays with deletedDateTime. Also served as /chats/{chatId}/messages/{messageId}/softDelete.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.soft-delete.json`

#### `chats.messages.set-reaction`

**POST v1.0 `/chats/{chatId}/messages/{messageId}/setReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-setreaction
- Notes: Talk reaction (an emoji). Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.set-reaction.json`

#### `chats.messages.unset-reaction`

**POST v1.0 `/chats/{chatId}/messages/{messageId}/unsetReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-unsetreaction
- Notes: Removes the caller's reaction. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.messages.unset-reaction.json`

#### `chats.pinned.list`

**GET v1.0 `/chats/{chatId}/pinnedMessages`** — forms: root; success: 200; paging: none

- Client reads: `id`, `message`
- Graph parity (ADR 0006); reference: chat-list-pinnedmessages
- Notes: Talk pinned messages; $expand=message. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.pinned.list.json`

#### `chats.pinned.add`

**POST v1.0 `/chats/{chatId}/pinnedMessages`** — forms: root; success: 201; paging: none

- Request body: `message@odata.bind`
- Client reads: `id`
- Graph parity (ADR 0006); reference: chat-post-pinnedmessages
- Notes: Talk pin (moderators); pinned id is the message id. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.pinned.add.json`

#### `chats.pinned.remove`

**DELETE v1.0 `/chats/{chatId}/pinnedMessages/{pinnedMessageId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: chat-delete-pinnedmessages
- Notes: Talk unpin. Also served under {mailbox}/chats/{chatId}… (Graph's /users/{id}/chats/{chatId}… paths; /users/{x} only for the caller).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/chats.pinned.remove.json`

### Phase 24 — Teams and channels over Nextcloud Teams and Talk (Graph parity, ADR 0009)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`teams.joined`](#teamsjoined) | GET | v1.0 | `{mailbox}/joinedTeams` | none |
| [`teams.get`](#teamsget) | GET | v1.0 | `/teams/{teamId}` | none |
| [`teams.create`](#teamscreate) | POST | v1.0 | `/teams` | none |
| [`teams.operation.get`](#teamsoperationget) | GET | v1.0 | `/teams/{teamId}/operations/{operationId}` | none |
| [`teams.update`](#teamsupdate) | PATCH | v1.0 | `/teams/{teamId}` | none |
| [`teams.delete`](#teamsdelete) | DELETE | v1.0 | `/groups/{teamId}` | none |
| [`teams.members.list`](#teamsmemberslist) | GET | v1.0 | `/teams/{teamId}/members` | none |
| [`teams.members.get`](#teamsmembersget) | GET | v1.0 | `/teams/{teamId}/members/{membershipId}` | none |
| [`teams.members.add`](#teamsmembersadd) | POST | v1.0 | `/teams/{teamId}/members` | none |
| [`teams.members.update`](#teamsmembersupdate) | PATCH | v1.0 | `/teams/{teamId}/members/{membershipId}` | none |
| [`teams.members.remove`](#teamsmembersremove) | DELETE | v1.0 | `/teams/{teamId}/members/{membershipId}` | none |
| [`channels.list`](#channelslist) | GET | v1.0 | `/teams/{teamId}/channels` | none |
| [`channels.get`](#channelsget) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}` | none |
| [`channels.create`](#channelscreate) | POST | v1.0 | `/teams/{teamId}/channels` | none |
| [`channels.update`](#channelsupdate) | PATCH | v1.0 | `/teams/{teamId}/channels/{channelId}` | none |
| [`channels.delete`](#channelsdelete) | DELETE | v1.0 | `/teams/{teamId}/channels/{channelId}` | none |
| [`channels.primary`](#channelsprimary) | GET | v1.0 | `/teams/{teamId}/primaryChannel` | none |
| [`channels.files-folder`](#channelsfiles-folder) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/filesFolder` | none |
| [`channels.members.list`](#channelsmemberslist) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/members` | none |
| [`channels.messages.list`](#channelsmessageslist) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/messages` | nextLink |
| [`channels.messages.get`](#channelsmessagesget) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}` | none |
| [`channels.messages.send`](#channelsmessagessend) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages` | none |
| [`channels.messages.update`](#channelsmessagesupdate) | PATCH | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}` | none |
| [`channels.messages.soft-delete`](#channelsmessagessoft-delete) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/softDelete` | none |
| [`channels.messages.set-reaction`](#channelsmessagesset-reaction) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/setReaction` | none |
| [`channels.messages.unset-reaction`](#channelsmessagesunset-reaction) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/unsetReaction` | none |
| [`channels.messages.delta`](#channelsmessagesdelta) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/delta` | deltaLink |
| [`channels.replies.list`](#channelsreplieslist) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies` | nextLink |
| [`channels.replies.get`](#channelsrepliesget) | GET | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}` | none |
| [`channels.replies.send`](#channelsrepliessend) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies` | none |
| [`channels.replies.update`](#channelsrepliesupdate) | PATCH | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}` | none |
| [`channels.replies.soft-delete`](#channelsrepliessoft-delete) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/softDelete` | none |
| [`channels.replies.set-reaction`](#channelsrepliesset-reaction) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/setReaction` | none |
| [`channels.replies.unset-reaction`](#channelsrepliesunset-reaction) | POST | v1.0 | `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/unsetReaction` | none |

#### `teams.joined`

**GET v1.0 `{mailbox}/joinedTeams`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `displayName`, `description`, `visibility`, `createdDateTime`, `webUrl`, `isArchived`, `internalId`, `classification`, `specialization`, `isMembershipLimitedToOwners`
- Graph parity (ADR 0006); reference: user-list-joinedteams
- Notes: The caller's Nextcloud teams (circles); /users/{x} only for the caller (ADR 0009 D5). tenantId and the settings objects are not emitted (no tenant, no counterpart).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.joined.json`

#### `teams.get`

**GET v1.0 `/teams/{teamId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `description`, `visibility`, `createdDateTime`, `webUrl`, `isArchived`, `internalId`, `classification`, `specialization`, `isMembershipLimitedToOwners`
- Graph parity (ADR 0006); reference: team-get
- Notes: A circle the caller belongs to.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.get.json`

#### `teams.create`

**POST v1.0 `/teams`** — forms: root; success: 202; paging: none

- Request body: `template@odata.bind (standard)`; `displayName`; `description?`; `visibility?`; `members[]?`
- Graph parity (ADR 0006); reference: team-post
- Notes: Circle, members and the General channel; 202 with Location naming a succeeded teamsAsyncOperation (/teams('{id}')/operations('{opId}'), also routed) and Content-Location the team, no body.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.create.json`

#### `teams.operation.get`

**GET v1.0 `/teams/{teamId}/operations/{operationId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `operationType`, `status`, `targetResourceId`, `targetResourceLocation`, `createdDateTime`, `lastActionDateTime`, `attemptsCount`, `error`
- Graph parity (ADR 0006); reference: teamsasyncoperation-get
- Notes: Always succeeded; the id encodes the team.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.operation.get.json`

#### `teams.update`

**PATCH v1.0 `/teams/{teamId}`** — forms: root; success: 204; paging: none

- Request body: `displayName?`; `description?`; `visibility?`
- Graph parity (ADR 0006); reference: team-update
- Notes: Renames and redescribes the circle; visibility sets its visible and open flags.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.update.json`

#### `teams.delete`

**DELETE v1.0 `/groups/{teamId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: group-delete
- Notes: Deletes the circle and its channels' rooms when the id is a team id; other group ids stay read-only (phase 21).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.delete.json`

#### `teams.members.list`

**GET v1.0 `/teams/{teamId}/members`** — forms: root; success: 200; paging: none

- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: team-list-members
- Notes: Circle members; level 8-9 is owner.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.members.list.json`

#### `teams.members.get`

**GET v1.0 `/teams/{teamId}/members/{membershipId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: team-get-members
- Notes: Membership id m.<circle member id>.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.members.get.json`

#### `teams.members.add`

**POST v1.0 `/teams/{teamId}/members`** — forms: root; success: 201; paging: none

- Request body: `@odata.type`; `user@odata.bind`; `roles`
- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: team-post-members
- Notes: Circle member add; owner is level 8.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.members.add.json`

#### `teams.members.update`

**PATCH v1.0 `/teams/{teamId}/members/{membershipId}`** — forms: root; success: 200; paging: none

- Request body: `@odata.type`; `roles`
- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: team-update-members
- Notes: Circle member level; the circle owner can't be demoted (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.members.update.json`

#### `teams.members.remove`

**DELETE v1.0 `/teams/{teamId}/members/{membershipId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: team-delete-members
- Notes: Circle member remove; not the circle owner (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/teams.members.remove.json`

#### `channels.list`

**GET v1.0 `/teams/{teamId}/channels`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `description`, `membershipType`, `createdDateTime`, `webUrl`, `email`, `isArchived`, `isFavoriteByDefault`, `layoutType`
- Graph parity (ADR 0006); reference: channel-list
- Notes: The team's Talk resources (teams/{id}/resources); all standard.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.list.json`

#### `channels.get`

**GET v1.0 `/teams/{teamId}/channels/{channelId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `description`, `membershipType`, `createdDateTime`, `webUrl`, `email`, `isArchived`, `isFavoriteByDefault`, `layoutType`
- Graph parity (ADR 0006); reference: channel-get
- Notes: A team room by token.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.get.json`

#### `channels.create`

**POST v1.0 `/teams/{teamId}/channels`** — forms: root; success: 201; paging: none

- Request body: `displayName`; `description?`; `membershipType? (standard)`
- Client reads: `id`, `displayName`, `description`, `membershipType`, `createdDateTime`, `webUrl`, `email`, `isArchived`, `isFavoriteByDefault`, `layoutType`
- Graph parity (ADR 0006); reference: channel-post
- Notes: Group room with the team as participant; private and shared are 400.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.create.json`

#### `channels.update`

**PATCH v1.0 `/teams/{teamId}/channels/{channelId}`** — forms: root; success: 204; paging: none

- Request body: `displayName?`; `description?`
- Graph parity (ADR 0006); reference: channel-patch
- Notes: Room name and description; General can't be renamed (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.update.json`

#### `channels.delete`

**DELETE v1.0 `/teams/{teamId}/channels/{channelId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: channel-delete
- Notes: Deletes the room; the primary channel can't be deleted (400).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.delete.json`

#### `channels.primary`

**GET v1.0 `/teams/{teamId}/primaryChannel`** — forms: root; success: 200; paging: none

- Client reads: `id`, `displayName`, `description`, `membershipType`, `createdDateTime`, `webUrl`, `email`, `isArchived`, `isFavoriteByDefault`, `layoutType`
- Graph parity (ADR 0006); reference: team-get-primarychannel
- Notes: The team's room with the lowest Talk room id.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.primary.json`

#### `channels.files-folder`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/filesFolder`** — forms: root; success: 200; paging: none

- Client reads: `id`, `name`, `webUrl`, `folder`, `parentReference`
- Graph parity (ADR 0006); reference: channel-get-filesfolder
- Notes: The channel's folder in the team folder, created on first use; a drive item (ADR 0008).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.files-folder.json`

#### `channels.members.list`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/members`** — forms: root; success: 200; paging: none

- Client reads: `id`, `roles`, `displayName`, `userId`, `email`, `visibleHistoryStartDateTime`
- Graph parity (ADR 0006); reference: channel-list-members
- Notes: Standard channel: the team's members.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.members.list.json`

#### `channels.messages.list`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/messages`** — forms: root; success: 200; paging: nextLink

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: channel-list-messages
- Notes: Posts (thread roots and plain top-level messages), newest first; $top up to 50; $expand=replies.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.list.json`

#### `channels.messages.get`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-get
- Notes: One post.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.get.json`

#### `channels.messages.send`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages`** — forms: root; success: 201; paging: none

- Request body: `body`; `subject?`; `mentions[]?`; `importance?`
- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: channel-post-messages
- Notes: A Talk thread; subject is its title (first body line when absent, reported null).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.send.json`

#### `channels.messages.update`

**PATCH v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}`** — forms: root; success: 204; paging: none

- Request body: `body`; `subject?`; `mentions[]?`
- Graph parity (ADR 0006); reference: chatmessage-update
- Notes: Talk edit; subject renames the thread.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.update.json`

#### `channels.messages.soft-delete`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/softDelete`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: chatmessage-softdelete
- Notes: Talk delete.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.soft-delete.json`

#### `channels.messages.set-reaction`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/setReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-setreaction
- Notes: Talk reaction.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.set-reaction.json`

#### `channels.messages.unset-reaction`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/unsetReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-unsetreaction
- Notes: Removes the caller's reaction.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.unset-reaction.json`

#### `channels.messages.delta`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/messages/delta`** — forms: root; success: 200; paging: deltaLink

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-delta
- Notes: First sync: the channel's posts, newest first. Then Talk's chat after the token's last message id: new posts and replies, and the message an edit, delete, rename or reaction is about, re-read (ADR 0009 D5).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.messages.delta.json`

#### `channels.replies.list`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies`** — forms: root; success: 200; paging: nextLink

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-list-replies
- Notes: Messages of the thread, newest first.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.list.json`

#### `channels.replies.get`

**GET v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-get
- Notes: One reply.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.get.json`

#### `channels.replies.send`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies`** — forms: root; success: 201; paging: none

- Request body: `body`; `mentions[]?`; `importance?`
- Client reads: `id`, `replyToId`, `etag`, `messageType`, `createdDateTime`, `lastModifiedDateTime`, `lastEditedDateTime`, `deletedDateTime`, `subject`, `chatId`, `channelIdentity`, `from`, `body`, `attachments`, `mentions`, `reactions`, `importance`, `eventDetail`, `webUrl`
- Graph parity (ADR 0006); reference: chatmessage-post-replies
- Notes: Talk message with threadId.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.send.json`

#### `channels.replies.update`

**PATCH v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}`** — forms: root; success: 204; paging: none

- Request body: `body`; `mentions[]?`
- Graph parity (ADR 0006); reference: chatmessage-update
- Notes: Talk edit.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.update.json`

#### `channels.replies.soft-delete`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/softDelete`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: chatmessage-softdelete
- Notes: Talk delete.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.soft-delete.json`

#### `channels.replies.set-reaction`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/setReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-setreaction
- Notes: Talk reaction.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.set-reaction.json`

#### `channels.replies.unset-reaction`

**POST v1.0 `/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies/{replyId}/unsetReaction`** — forms: root; success: 204; paging: none

- Request body: `reactionType`
- Graph parity (ADR 0006); reference: chatmessage-unsetreaction
- Notes: Removes the caller's reaction.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/channels.replies.unset-reaction.json`

### Phase 25 — Presence over Nextcloud user status (Graph parity, ADR 0009)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`presence.get`](#presenceget) | GET | v1.0 | `{mailbox}/presence` | none |
| [`presence.get-by-id`](#presenceget-by-id) | GET | v1.0 | `/communications/presences/{userId}` | none |
| [`presence.get-by-ids`](#presenceget-by-ids) | POST | v1.0 | `/communications/getPresencesByUserId` | none |
| [`presence.set`](#presenceset) | POST | v1.0 | `{mailbox}/presence/setPresence` | none |
| [`presence.clear`](#presenceclear) | POST | v1.0 | `{mailbox}/presence/clearPresence` | none |
| [`presence.set-preferred`](#presenceset-preferred) | POST | v1.0 | `{mailbox}/presence/setUserPreferredPresence` | none |
| [`presence.clear-preferred`](#presenceclear-preferred) | POST | v1.0 | `{mailbox}/presence/clearUserPreferredPresence` | none |
| [`presence.set-status-message`](#presenceset-status-message) | POST | v1.0 | `{mailbox}/presence/setStatusMessage` | none |

#### `presence.get`

**GET v1.0 `{mailbox}/presence`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `availability`, `activity`, `statusMessage`, `outOfOfficeSettings`
- Graph parity (ADR 0006); reference: presence-get
- Notes: User status mapped per ADR 0009 D6; another user's via statuses/{uid}.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.get.json`

#### `presence.get-by-id`

**GET v1.0 `/communications/presences/{userId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `availability`, `activity`, `statusMessage`, `outOfOfficeSettings`
- Graph parity (ADR 0006); reference: presence-get
- Notes: Same as /users/{id}/presence.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.get-by-id.json`

#### `presence.get-by-ids`

**POST v1.0 `/communications/getPresencesByUserId`** — forms: root; success: 200; paging: none

- Request body: `ids[]`
- Client reads: `id`, `availability`, `activity`, `statusMessage`, `outOfOfficeSettings`
- Graph parity (ADR 0006); reference: cloudcommunications-getpresencesbyuserid
- Notes: One statuses/{uid} read per id; unknown ids are left out.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.get-by-ids.json`

#### `presence.set`

**POST v1.0 `{mailbox}/presence/setPresence`** — forms: me, users; success: 200; paging: none

- Request body: `sessionId`; `availability`; `activity`; `expirationDuration?`
- Graph parity (ADR 0006); reference: presence-setpresence
- Notes: Sets the user-defined status (one status per user, not per application).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.set.json`

#### `presence.clear`

**POST v1.0 `{mailbox}/presence/clearPresence`** — forms: me, users; success: 200; paging: none

- Request body: `sessionId`
- Graph parity (ADR 0006); reference: presence-clearpresence
- Notes: Back to online.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.clear.json`

#### `presence.set-preferred`

**POST v1.0 `{mailbox}/presence/setUserPreferredPresence`** — forms: me, users; success: 200; paging: none

- Request body: `availability`; `activity`; `expirationDuration?`
- Graph parity (ADR 0006); reference: presence-setuserpreferredpresence
- Notes: User-defined status; InACall/InAMeeting set the predefined message.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.set-preferred.json`

#### `presence.clear-preferred`

**POST v1.0 `{mailbox}/presence/clearUserPreferredPresence`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: presence-clearuserpreferredpresence
- Notes: Back to online.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.clear-preferred.json`

#### `presence.set-status-message`

**POST v1.0 `{mailbox}/presence/setStatusMessage`** — forms: me, users; success: 200; paging: none

- Request body: `statusMessage {message {content, contentType}, expiryDateTime?}`
- Graph parity (ADR 0006); reference: presence-setstatusmessage
- Notes: Custom status message with clearAt.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/presence.set-status-message.json`

### Phase 26 — Online meetings and calls over Talk (Graph parity, ADR 0009)

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`online-meetings.create`](#online-meetingscreate) | POST | v1.0 | `{mailbox}/onlineMeetings` | none |
| [`online-meetings.get`](#online-meetingsget) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}` | none |
| [`online-meetings.find`](#online-meetingsfind) | GET | v1.0 | `{mailbox}/onlineMeetings` | none |
| [`online-meetings.update`](#online-meetingsupdate) | PATCH | v1.0 | `{mailbox}/onlineMeetings/{meetingId}` | none |
| [`online-meetings.delete`](#online-meetingsdelete) | DELETE | v1.0 | `{mailbox}/onlineMeetings/{meetingId}` | none |
| [`online-meetings.recordings.list`](#online-meetingsrecordingslist) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/recordings` | none |
| [`online-meetings.recordings.get`](#online-meetingsrecordingsget) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/recordings/{recordingId}` | none |
| [`online-meetings.recordings.content`](#online-meetingsrecordingscontent) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/recordings/{recordingId}/content` | none |
| [`online-meetings.transcripts.list`](#online-meetingstranscriptslist) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/transcripts` | none |
| [`online-meetings.transcripts.get`](#online-meetingstranscriptsget) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/transcripts/{transcriptId}` | none |
| [`online-meetings.transcripts.content`](#online-meetingstranscriptscontent) | GET | v1.0 | `{mailbox}/onlineMeetings/{meetingId}/transcripts/{transcriptId}/content` | none |
| [`calls.create`](#callscreate) | POST | v1.0 | `/communications/calls` | none |
| [`calls.get`](#callsget) | GET | v1.0 | `/communications/calls/{callId}` | none |
| [`calls.delete`](#callsdelete) | DELETE | v1.0 | `/communications/calls/{callId}` | none |

#### `online-meetings.create`

**POST v1.0 `{mailbox}/onlineMeetings`** — forms: me, users; success: 201; paging: none

- Request body: `subject?`; `startDateTime?`; `endDateTime?`; `participants?`
- Client reads: `id`, `subject`, `startDateTime`, `endDateTime`, `creationDateTime`, `joinWebUrl`, `chatInfo`, `participants`, `joinMeetingIdSettings`
- Graph parity (ADR 0006); reference: application-post-onlinemeetings
- Notes: A public Talk room, bound to its span (ADR 0009 D7); attendees who are users become participants.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.create.json`

#### `online-meetings.get`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `subject`, `startDateTime`, `endDateTime`, `creationDateTime`, `joinWebUrl`, `chatInfo`, `participants`, `joinMeetingIdSettings`
- Graph parity (ADR 0006); reference: onlinemeeting-get
- Notes: Id t.<token>.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.get.json`

#### `online-meetings.find`

**GET v1.0 `{mailbox}/onlineMeetings`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `subject`, `startDateTime`, `endDateTime`, `creationDateTime`, `joinWebUrl`, `chatInfo`, `participants`, `joinMeetingIdSettings`
- Graph parity (ADR 0006); reference: onlinemeeting-get
- Notes: Only $filter=JoinWebUrl eq '…' (Graph's rule); finds calendar events' rooms too.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.find.json`

#### `online-meetings.update`

**PATCH v1.0 `{mailbox}/onlineMeetings/{meetingId}`** — forms: me, users; success: 200; paging: none

- Request body: `subject?`; `startDateTime?`; `endDateTime?`
- Client reads: `id`, `subject`, `startDateTime`, `endDateTime`, `creationDateTime`, `joinWebUrl`, `chatInfo`, `participants`, `joinMeetingIdSettings`
- Graph parity (ADR 0006); reference: onlinemeeting-update
- Notes: Renames and re-spans. Talk can neither rename nor re-time a room bound to its span, so either change unbinds it, and the meeting then reports no span (ADR 0009 D7).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.update.json`

#### `online-meetings.delete`

**DELETE v1.0 `{mailbox}/onlineMeetings/{meetingId}`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: onlinemeeting-delete
- Notes: Closes the room.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.delete.json`

#### `online-meetings.recordings.list`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/recordings`** — forms: me, users; success: 200; paging: none

- Query: `$top`: `1..1000`
- Client reads: `id`, `meetingId`, `createdDateTime`, `endDateTime`, `recordingContentUrl`, `contentCorrelationId`, `meetingOrganizer`
- Graph parity (ADR 0006); reference: onlinemeeting-list-recordings
- Notes: Recordings Talk's recording server uploaded to the caller's files for this room ({Talk folder}/Recording/{token}/).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.recordings.list.json`

#### `online-meetings.recordings.get`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/recordings/{recordingId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `meetingId`, `createdDateTime`, `endDateTime`, `recordingContentUrl`, `contentCorrelationId`, `meetingOrganizer`
- Graph parity (ADR 0006); reference: callrecording-get
- Notes: Recording id is the file id.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.recordings.get.json`

#### `online-meetings.recordings.content`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/recordings/{recordingId}/content`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: callrecording-get
- Notes: Streams the file (audio/video as Talk recorded it).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.recordings.content.json`

#### `online-meetings.transcripts.list`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/transcripts`** — forms: me, users; success: 200; paging: none

- Query: `$top`: `1..1000`
- Client reads: `id`, `meetingId`, `createdDateTime`, `endDateTime`, `transcriptContentUrl`, `contentCorrelationId`, `meetingOrganizer`
- Graph parity (ADR 0006); reference: onlinemeeting-list-transcripts
- Notes: Transcripts Talk's transcription wrote next to the recordings.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.transcripts.list.json`

#### `online-meetings.transcripts.get`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/transcripts/{transcriptId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `meetingId`, `createdDateTime`, `endDateTime`, `transcriptContentUrl`, `contentCorrelationId`, `meetingOrganizer`
- Graph parity (ADR 0006); reference: calltranscript-get
- Notes: Transcript id is the file id.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.transcripts.get.json`

#### `online-meetings.transcripts.content`

**GET v1.0 `{mailbox}/onlineMeetings/{meetingId}/transcripts/{transcriptId}/content`** — forms: me, users; success: 200; paging: none

- Query: `$format`: `text/vtt`
- Graph parity (ADR 0006); reference: calltranscript-get
- Notes: text/vtt built from Talk's Markdown transcript (no cue times: one cue per paragraph spanning the recording).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/online-meetings.transcripts.content.json`

#### `calls.create`

**POST v1.0 `/communications/calls`** — forms: root; success: 201; paging: none

- Request body: `callbackUri?`; `targets[] {identity {user {id}}}`; `requestedModalities?`; `mediaConfig?`; `source?`; `subject?`
- Client reads: `id`, `state`, `direction`, `callbackUri`, `source`, `targets`, `requestedModalities`, `chatInfo`, `@nightware.joinWebUrl`
- Graph parity (ADR 0006); reference: application-post-calls
- Notes: Delegated (ADR 0009 D7): starts a Talk call as the caller and rings the targets; callbackUri is never called; media in Talk clients.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calls.create.json`

#### `calls.get`

**GET v1.0 `/communications/calls/{callId}`** — forms: root; success: 200; paging: none

- Client reads: `id`, `state`, `direction`, `callbackUri`, `source`, `targets`, `requestedModalities`, `chatInfo`, `@nightware.joinWebUrl`
- Graph parity (ADR 0006); reference: call-get
- Notes: establishing / established / terminated from the room's call state.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calls.get.json`

#### `calls.delete`

**DELETE v1.0 `/communications/calls/{callId}`** — forms: root; success: 204; paging: none

- Graph parity (ADR 0006); reference: call-delete
- Notes: Leaves the call; ends it for everyone when the caller moderates.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/calls.delete.json`

### Phase 27 — OneDrive, calendar, directory and settings gaps

| Id | Method | Ver | Path | Paging |
|---|---|---|---|---|
| [`files.drive.root.children`](#filesdriverootchildren) | GET | v1.0 | `{mailbox}/drive/root/children` | nextLink |
| [`files.drives.items.get`](#filesdrivesitemsget) | GET | v1.0 | `/drives/{driveId}/items/{itemId}` | none |
| [`files.drive.shared-with-me`](#filesdriveshared-with-me) | GET | v1.0 | `{mailbox}/drive/sharedWithMe` | nextLink |
| [`files.drive.recent`](#filesdriverecent) | GET | v1.0 | `{mailbox}/drive/recent` | none |
| [`search.query`](#searchquery) | POST | v1.0 | `/search/query` | none |
| [`files.drive.items.versions`](#filesdriveitemsversions) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/versions` | none |
| [`files.drive.items.versions.get`](#filesdriveitemsversionsget) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/versions/{versionId}` | none |
| [`files.drive.items.versions.content`](#filesdriveitemsversionscontent) | GET | v1.0 | `{mailbox}/drive/items/{itemId}/versions/{versionId}/content` | none |
| [`files.drive.items.versions.restore`](#filesdriveitemsversionsrestore) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/versions/{versionId}/restoreVersion` | none |
| [`files.drive.items.checkout`](#filesdriveitemscheckout) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/checkout` | none |
| [`files.drive.items.checkin`](#filesdriveitemscheckin) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/checkin` | none |
| [`files.drive.items.preview`](#filesdriveitemspreview) | POST | v1.0 | `{mailbox}/drive/items/{itemId}/preview` | none |

#### `files.drive.root.children`

**GET v1.0 `{mailbox}/drive/root/children`** — forms: me, users; success: 200; paging: nextLink

- Query: `$select` (always): `*`; `$orderby`: `name`; `$orderby`: `lastModifiedDateTime`; `$orderby`: `size`; `$top`: `1-1000`
- Client reads: `value`, `@odata.nextLink`
- On error: Any non-2xx → _mapException: 401 → AuthException (after AuthInterceptor's one refresh+retry), a connection error or timeout → NetworkException, else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:238`
- Notes: The cloud picker's "My files" (NightMail Server accounts use the nextcloud provider): the root folder's items with $top=200, following @odata.nextLink. The same handler as items/{id}/children.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.root.children.json`

#### `files.drives.items.get`

**GET v1.0 `/drives/{driveId}/items/{itemId}`** — forms: root; success: 200; paging: none

- Query: `$select` (always): `*`; `$expand`: `children`
- Client reads: `id`, `name`, `file`, `folder`, `parentReference`, `@microsoft.graph.downloadUrl`, `file.hashes`
- On error: Any non-2xx → _mapException: 401 → AuthException (after AuthInterceptor's one refresh+retry), a connection error or timeout → NetworkException, else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:222`
- Notes: The cloud picker addresses an item through the drive its parentReference.driveId names (_itemPath), and every Nightware item names the caller's own drive, so its children, content and createLink go to /drives/{driveId}/items/{itemId}/… too (the same handlers as /me/drive). Another drive's id is 404 itemNotFound (ADR 0008 D1).
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drives.items.get.json`

#### `files.drive.shared-with-me`

**GET v1.0 `{mailbox}/drive/sharedWithMe`** — forms: me, users; success: 200; paging: nextLink

- Query: `$top`: `200`
- Client reads: `value[].id`, `value[].name`, `value[].remoteItem.id`, `value[].remoteItem.parentReference.driveId`, `value[].remoteItem.file`, `value[].remoteItem.folder`, `value[].remoteItem.size`, `value[].remoteItem.lastModifiedDateTime`, `value[].remoteItem.webUrl`, `@odata.nextLink`
- On error: Any non-2xx → _mapException: 401 → AuthException (after AuthInterceptor's one refresh+retry), a connection error or timeout → NetworkException, else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:236`
- Notes: The cloud picker's "Shared with me" (it asks Microsoft 365 accounts only; NightMail Server accounts use the nextcloud provider, which has no such collection). Nextcloud's OCS shares with the caller (user and group shares), each item once; an entry is the item with remoteItem pointing at it, remoteItem.parentReference.driveId the caller's own drive (Nextcloud mounts shared items in the caller's home with the owner's file id) and remoteItem.shared.owner the owner. Not paged: Nextcloud lists every share at once.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.shared-with-me.json`

#### `files.drive.recent`

**GET v1.0 `{mailbox}/drive/recent`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].remoteItem`
- Graph parity (ADR 0006); reference: drive-recent
- Notes: The caller's 100 most recently modified files (no folders), newest first, from a Nextcloud SEARCH ordered by getlastmodified, as its own Recent view; each entry carries remoteItem pointing at the item, as Graph's do. Microsoft is retiring this API; it is here for clients that still call it.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.recent.json`

#### `search.query`

**POST v1.0 `/search/query`** — forms: root; success: 200; paging: none

- Request body: `requests[].entityTypes`; `requests[].query.queryString`; `requests[].from`; `requests[].size`
- Client reads: `value[].hitsContainers[].hits[].resource`, `value[].hitsContainers[].hits[].resource.id`, `value[].hitsContainers[].hits[].resource.name`, `value[].hitsContainers[].hits[].resource.parentReference.driveId`
- On error: Any non-2xx → _mapException: 401 → AuthException (after AuthInterceptor's one refresh+retry), a connection error or timeout → NetworkException, else ServerException(error.message, status).
- Call sites: `…/remote/graph_drive_datasource_impl.dart:272`
- Notes: Microsoft Search for driveItem: the cloud picker's search for Microsoft 365 accounts (entityTypes [driveItem], from 0, size 50), which falls back to root/search on any error. One request; the caller's drive searched by name as root/search is; each hit's resource is the driveItem with @odata.type. total counts the hits up to this window; moreResultsAvailable says whether more match. chatMessage is search.query.chat-messages; other entity types, and types mixed in one request, are 400 notSupported.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/search.query.json`

#### `files.drive.items.versions`

**GET v1.0 `{mailbox}/drive/items/{itemId}/versions`** — forms: me, users; success: 200; paging: none

- Client reads: `value[].id`, `value[].lastModifiedDateTime`, `value[].size`, `value[].lastModifiedBy`
- Graph parity (ADR 0006); reference: driveitem-list-versions
- Notes: A file's versions from Nextcloud's files_versions (versions/{uid}/versions/{fileid}/), the current version (the one named by the file's modification time, which a restore sets) first, the rest newest first. A version's id is Nextcloud's timestamp; lastModifiedBy.user.id the user who wrote it. A folder is 400 notSupported. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.versions.json`

#### `files.drive.items.versions.get`

**GET v1.0 `{mailbox}/drive/items/{itemId}/versions/{versionId}`** — forms: me, users; success: 200; paging: none

- Client reads: `id`, `lastModifiedDateTime`, `size`
- Graph parity (ADR 0006); reference: driveitemversion-get
- Notes: One version, by id; an unknown id is 404. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.versions.get.json`

#### `files.drive.items.versions.content`

**GET v1.0 `{mailbox}/drive/items/{itemId}/versions/{versionId}/content`** — forms: me, users; success: 200; paging: none

- Graph parity (ADR 0006); reference: driveitemversion-get-contents
- Notes: A version's bytes, streamed with 200 (Graph answers 302 to a download URL; a client following the redirect gets the same bytes). Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.versions.content.json`

#### `files.drive.items.versions.restore`

**POST v1.0 `{mailbox}/drive/items/{itemId}/versions/{versionId}/restoreVersion`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: driveitemversion-restore
- Notes: Makes the version the file's current content (a WebDAV MOVE to versions/{uid}/restore/target); Nextcloud keeps the content it replaces as a version. 204. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.versions.restore.json`

#### `files.drive.items.checkout`

**POST v1.0 `{mailbox}/drive/items/{itemId}/checkout`** — forms: me, users; success: 204; paging: none

- Graph parity (ADR 0006); reference: driveitem-checkout
- Notes: A Nextcloud user lock (files_lock: LOCK with X-User-Lock: 1, no timeout): nobody else can change the file (their writes are 409) until it is checked in. Checking out again is 204; a file someone else has checked out is 409 resourceLocked; a folder 400 notSupported. While checked out, the item carries publication {level: checkout, checkedOutBy}. 204. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.checkout.json`

#### `files.drive.items.checkin`

**POST v1.0 `{mailbox}/drive/items/{itemId}/checkin`** — forms: me, users; success: 204; paging: none

- Request body: `comment`; `checkInAs`
- Graph parity (ADR 0006); reference: driveitem-checkin
- Notes: Releases the caller's lock (UNLOCK with X-User-Lock: 1); comment (at most 250 characters, one line) labels the current version (the one named by the file's modification time) (nc:version-label). checkInAs is accepted and ignored (SharePoint publishing levels). A file that isn't checked out is 400; one someone else has is 403 resourceLocked. 204. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.checkin.json`

#### `files.drive.items.preview`

**POST v1.0 `{mailbox}/drive/items/{itemId}/preview`** — forms: me, users; success: 200; paging: none

- Request body: `viewer`; `chromeless`; `allowEdit`; `page`; `zoom`
- Client reads: `getUrl`, `postUrl`, `postParameters`
- Graph parity (ADR 0006); reference: driveitem-preview
- Notes: getUrl is the item's large (800 px) rendered preview behind a one-hour self-authorising URL (the thumbnail URL): Nextcloud renders images, PDFs, office documents and text. Never the file itself: served inline from the façade's origin an HTML or SVG file would run script there. postUrl and postParameters are null; the body's page, zoom, viewer, chromeless and allowEdit are ignored. An item without a preview, or a folder, is 400 notSupported. Mapped on /me/drive, /users/{caller}/drive and /drives/{driveId}; items/{id}, root and path addressing (root:/a/b:) name the same item.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/files.drive.items.preview.json`

<!-- END GENERATED -->
