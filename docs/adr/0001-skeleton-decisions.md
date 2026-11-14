# ADR 0001 — Decisions fixed before the feature phases fan out

Status: accepted (2026-11-15). Source: architecture review of the initial plan
against the NightMail client (`graph_api_datasource_impl.dart`,
`graph_message_parser.dart`, `mail_poller_cubit.dart`). These override
anything contradictory in `architecture.md` or the phase docs.

## Identifiers

1. **Message ids are tagged.** `e.<jmapId>` for ordinary mail; `d.<base64url(Message-ID)>`
   for any message carrying `$draft`. JMAP emails are immutable apart from
   keywords/mailboxes, so a draft PATCH or attachment change is
   create-replacement + destroy, which changes the JMAP id — but the client
   chains `PATCH → attachments → send` on one draft id. The façade mints the
   `Message-ID` header on draft creation, preserves it across replacements and
   resolves `d.` ids with `Email/query {inMailbox: drafts, header: ["Message-ID", v]}`.
   The codec `Adapters.Jmap/Mail/Ids/MailItemId.cs` is skeleton-owned and frozen.
   `Email/get` always fetches `keywords` and `messageId` so the right form is
   emitted everywhere (list, delta, get).
2. **Moves keep the id** (JMAP ids are stable); `POST messages/{id}/move` returns
   the message with the same id. The client reads `response.id` either way.
3. **Folder ids are the JMAP mailbox id, everywhere**, on every route. The client
   compares them with `parentFolderId` and uses them in `$filter`.
4. **Calendar occurrence ids are first-class write targets** (accept/decline/
   cancel/DELETE/PATCH on an occurrence = "this occurrence only": exception
   VEVENT or `EXDATE`). Organizer PATCH of start/end bumps `SEQUENCE` so Sabre
   re-sends the iTIP REQUEST.
5. Attachment ids are the JMAP `partId` within the current blob (the client
   re-lists before deleting).

## Delta

6. Built on `Email/changes` + `Email/get(mailboxIds,…)`, **not** `Email/queryChanges`.
   Created/updated in the folder → upsert; updated and no longer in the folder →
   `@removed {reason: "changed"}`; destroyed → `@removed {reason: "deleted"}`.
   The client distinguishes the two reasons.
7. Initial sync = `Email/query inMailbox`, sorted `receivedAt desc`, `position`
   paging. Token payload `{v, mailboxId, phase: initial|changes, position|state,
   select, top}` — `$select`/`$top` are frozen into the token as Graph does.
   `nextLink` and `deltaLink` are interchangeable inputs (the client stores a
   nextLink as its token when it caps paging).
8. `cannotCalculateChanges` → `ErrorKind.SyncStateGone` → HTTP 410
   `syncStateNotFound`; the client resyncs on 410.

## Layering

9. `$search` KQL is parsed in the Api into the provider-neutral `SearchQuery`.
   Adapters translate a model, never a grammar.
10. `GraphTimeZone` and Windows↔IANA mapping live in `Application/Common/Time`.
    The client sends `Prefer: outlook.timezone="UTC"` on calendar calls and
    nothing on mail (Graph default UTC); v1 may be UTC-only but the type exists.
11. `ReplyComposer` is Application logic producing `DraftContent`; no MIME
    library in Application. MimeKit stays in `Adapters.Jmap`.
12. iCalendar is behind `IICalendarCodec` (Application port, Ical.Net impl in
    `Adapters.Dav/Calendar/ICal`), because invitation-mail → event needs it
    outside the DAV adapter.
13. Invitation detection is Phase 2: `MailMessage` carries `CalendarPart` and
    the message DTO emits `@odata.type` `#microsoft.graph.eventMessageRequest|Response`
    and `meetingMessageType`. Phase 4 owns the `messages/{id}/...event` routes;
    Phase 2 must not claim a catch-all under `messages/{id}/`.
14. `$select` projection is applied to `JsonObject` output; `@odata.type` is
    omitted on `$select` GETs (Graph behaviour the client works around).
15. Domain may use BCL types (`DateTimeOffset`, `TimeZoneInfo`); not ASP.NET/HTTP types.

## Auth

16. Adapters never read the bearer token directly. They ask
    `IBackendCredentials.ForAsync(Backend)`, with strategies
    `PassThrough | TokenExchange | ServiceAccount`. Default is Keycloak standard
    token exchange (RFC 8693) per backend audience, cached by token hash in a
    disposable `IMemoryCache`.
17. Directory lookups (`/users`, `/users/{upn}`) use the `nightware-api` service
    account (`view-users`) as the **primary** design.
18. `mailboxSettings.timeZone/language/workingHours` are stored as Keycloak user
    attributes (`zoneinfo`, `locale`, …) via `IUserPreferencesStore` in
    `Adapters.Directory`; auto-replies are JMAP `VacationResponse`.
19. The shared-mailbox probe `GET /users/{addr}/mailFolders/inbox?$select=id`
    must yield 403/404 when not permitted (map JMAP `accountNotFound`/`forbidden`).

## Scope

20. OData whitelist includes `eq ne and in(...)` on `conversationId`,
    `parentFolderId`, `iCalUId`, `status`; `$orderby receivedDateTime desc` /
    `createdDateTime desc`; `$skip`; `$expand=attachments($select=…)`.
    No `$count`, no `ConsistencyLevel`.
21. **Cut**: `$batch` and Graph `subscriptions` (zero client use).
22. `GET /shares/{id}/driveItem/content?format=pdf` → 501 in v1 (no conversion
    without Collabora).
23. `getSchedule.availabilityView` (bucketed 0–4 string) is Application logic.

## Parallelisation

24. Frozen in the skeleton: `Directory.Packages.props`, `.slnx`, `ErrorKind`,
    `GraphErrorMapper`, the **complete** message DTO, adapter options classes and
    `appsettings.json` sections, all port interfaces, and stub implementations
    returning `ErrorKind.Unsupported` so every worktree builds and unknown
    behaviour is a 501 from day one.
25. Conformance fixtures and `endpoints.json` are split per feature and merged
    by the test host.
26. Cross-phase seams (`IMailMessageReader`, `IMailMessageWriter`,
    `IRecentCorrespondents`, `IICalendarCodec`, `IBackendCredentials`) are
    mocked by consumers; their end-to-end flows are tested after merge.

## Testing

27. ArchUnitNET (maintained) instead of NetArchTest; Testcontainers for the
    opt-in integration suite; `JsonNode.DeepEquals` for fixture comparison.
28. WireMock fixtures are recorded from the real stack (`deploy/recordings/`).
