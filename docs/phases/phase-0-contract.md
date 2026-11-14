# Phase 0 — Contract and client hook

**Goal:** pin down exactly what NightMail sends and expects, as machine-checkable
artefacts, and make the client able to point at a Nightware server.

Depends on: nothing. Blocks: nothing hard — later phases start from the draft
inventory and refine it.

## Deliverables

### 0.1 Endpoint inventory — `docs/contract/endpoints.md` + `docs/contract/endpoints.json`
Derived from the NightMail sources (`~/Source/nightmail/lib`, chiefly
`data/datasources/remote/graph_api_datasource_impl.dart`,
`graph_drive_datasource_impl.dart`, `infrastructure/auth/signed_in_mailbox_lookup.dart`,
`infrastructure/auth/microsoft_auth_service.dart`). For every operation:

- method, path template, v1.0 or beta, `/me` and/or `/users/{id}` form
- query options actually sent (`$select` field lists, `$filter` expressions,
  `$orderby`, `$top`, `$search`, `$expand`)
- headers sent (`Prefer: outlook.timezone`, `outlook.body-content-type`, …)
- request body fields
- **response fields the client reads** (from `graph_message_parser.dart`,
  models, and the datasource) and how it pages (`@odata.nextLink`,
  `@odata.deltaLink`)
- the owning phase (2–5)

`endpoints.json` is the source the conformance tests and the 501 fallback use.

### 0.2 OData subset grammar — `docs/contract/odata-subset.md`
The exact `$filter`/`$orderby`/`$search` constructs the client emits (property
names, operators, functions, literal types), written as a small grammar. This is
the whitelist the Api's parser accepts.

### 0.3 Golden fixtures — `tests/Nightware.Conformance.Tests/Fixtures/`
One JSON file per operation: `{ "request": {...}, "response": {"status", "headers", "body"} }`.
Bodies come from NightMail's own test fixtures (`~/Source/nightmail/test/**`)
where they exist, otherwise from Microsoft Graph documentation examples trimmed
to the fields the client reads. Document provenance per fixture.

### 0.4 Client hook (NightMail repo, committed on `main` there)
- Make the Graph base URL (v1.0 and beta, including the hardcoded
  `threatSubmissionEndpoint`) and the OAuth authority configurable per account.
  Today: `lib/infrastructure/http/graph_http_client.dart:16`,
  `lib/infrastructure/auth/microsoft_auth_service.dart:241`.
- Add a "NightMail Server" account option (server URL; discovers issuer via
  `/.well-known/nightware` → `{graphBaseUrl, authority, clientId, scopes}`).
- Defaults must leave existing Microsoft 365 accounts byte-for-byte unchanged.
- Tests for the new configuration paths; `flutter analyze` and `flutter test` clean.

## Acceptance
- Inventory covers every Graph call site in the client (a script in
  `tools/inventory/` re-greps the client and fails on unlisted paths).
- Every inventory entry has at least one fixture.
- NightMail builds and its test suite passes with the hook in place.
