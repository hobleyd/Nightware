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
| Phase 14 — Contacts (Graph parity) | 7 |
| **Total** | **131** |

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

- Query: `$select` (variant 1): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$select` (variant 2): `id`; `$filter`: `isRead eq false`; `$orderby`: `receivedDateTime desc`; `$top`: `25`, `10`, `5`, `100`, `1..100`; `$skip`: `0`, `25`, `50`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:145`, `…/remote/graph_api_datasource_impl.dart:2729`
- Notes: Folder page (getEmails): always sends $top, $skip (0 on the first page — offset paging, not nextLink), $select=list projection, $orderby=receivedDateTime desc; $filter only from the AI agent tool (isRead eq false). @odata.nextLink is ignored here. emptyFolder sends only $top=100&$select=id and loops until a page has < 100 rows (it deletes as it goes, so it always re-reads page 1). After each page the client expands every conversationId on it via mail.messages.list-by-conversation.
- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/mail.messages.list-in-folder.json`

#### `mail.messages.list`

**GET v1.0 `{mailbox}/messages`** — forms: me, users; success: 200; paging: none

- Query: `$select` (always): `id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId`; `$filter`: `isRead eq false`; `$orderby`: `receivedDateTime desc`; `$top`: `25`, `1..100`; `$skip`: `0`, `25`
- Client reads: `value[].id`, `value[].subject`, `value[].from.emailAddress.address`, `value[].from.emailAddress.name`, `value[].toRecipients[].emailAddress.{address,name}`, `value[].ccRecipients[].emailAddress.{address,name}`, `value[].bccRecipients[].emailAddress.{address,name}`, `value[].bodyPreview`, `value[].isRead`, `value[].flag.flagStatus`, `value[].receivedDateTime`, `value[].sentDateTime`, `value[].importance`, `value[].conversationId`, `value[].hasAttachments`, `value[].parentFolderId`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:146`
- Notes: getEmails with folderId == null (the AI agent tool without a folder): the newest mail of the whole mailbox. Same parameters as mail.messages.list-in-folder.
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

- Query: `$select` (variant 1): `id`; `$select` (variant 2): `id,isOrganizer`; `$filter`: `iCalUId eq '040000008200E00074C5B7101A82E00800000000A1B2C3D4E5F6D901000000000000000010000000F1E2D3C4B5A69788'`; `$top`: `1`, `5`
- Client reads: `value[].id`, `value[].isOrganizer`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:1430`, `…/remote/graph_api_datasource_impl.dart:1620`
- Notes: Finds the user's own copy of a meeting by the UID from an invitation's text/calendar part. Single quotes inside the literal are doubled. Must match the series master (not occurrences).
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

- Query: `$select` (always): `displayName,emailAddresses`; `$top`: `999`
- Client reads: `value[].displayName`, `value[].emailAddresses[].address`, `value[].emailAddresses[].name`, `@odata.nextLink`
- On error: Failure recorded as partial sync.
- Call sites: `…/remote/graph_api_datasource_impl.dart:2245`
- Notes: Personal contacts for the typeahead cache (read-only: the client never creates/updates contacts). Follows nextLink up to 25 pages.
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

- Query: `$filter`: `status ne 'completed'`; `$orderby`: `createdDateTime desc`; `$top`: `200`
- Client reads: `value[].id`, `value[].title`, `value[].status`, `value[].importance`, `value[].body.content`, `value[].dueDateTime.dateTime`, `value[].completedDateTime.dateTime`, `value[].isReminderOn`, `value[].reminderDateTime.dateTime`, `value[].createdDateTime`, `value[].lastModifiedDateTime`, `value[].hasAttachments`
- On error: Any non-2xx → _mapDioException: 401 → AuthException (after AuthInterceptor's one refresh+retry), else ServerException(error.message, status).
- Call sites: `…/remote/graph_api_datasource_impl.dart:2828`
- Notes: $filter present unless completed tasks are included. No paging followed. Task datetimes ({dateTime,timeZone}) are parsed with DateTime.tryParse(dateTime) and converted to local — dateTime without Z is read as LOCAL time, so return dueDateTime in the form Graph does ('YYYY-MM-DDT00:00:00.0000000', timeZone 'UTC').
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

- Client reads: `value[].id`, `value[].subject`, `value[].start`, `value[].end`
- Graph parity (ADR 0006); reference: https://learn.microsoft.com/graph/api/resources/calendar
- Notes: CalDAV collections (ADR 0006 D6). Unexpanded; $orderby start/dateTime, $top, $skip.
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

<!-- END GENERATED -->
