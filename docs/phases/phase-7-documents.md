# Phase 7 — Nextcloud documents in the reading pane

**Goal:** a Nextcloud document link in a mail body previews in NightMail's
reading pane exactly as a SharePoint/OneDrive link does for Microsoft 365, and
clicking through still opens the document for editing in the browser.

Depends on: phases 1–5 (merged). Replaces the ADR §43 deferral for `/shares`.

## Decisions
- **Collabora Online (CODE) via Nextcloud Office (`richdocuments`)**, not a
  conversion-only sidecar: the same Collabora instance gives in-browser editing
  when the reader follows the link (Nextcloud's own UI), and its
  `/cool/convert-to/pdf` endpoint gives the server-rendered PDF the preview uses
  for Office formats (Graph `content?format=pdf` parity).
- Links are claimed **only** for hosts the account's NightMail Server advertises
  (`filesBaseUrls` in `/.well-known/nightware`), keeping the client scanner's
  bias against false positives.
- Supported link shapes: `{files}/s/{token}` (public share, file only) and
  `{files}/f/{fileId}` (internal link; the caller must already have access).
  Folder shares, password-protected shares and anything unrecognised fall
  through to the browser.

## Server (Nightware)
- `GET /shares/{u!base64url(url)}/driveItem` and `/driveItem/content[?format=pdf]`
  (contract ops `files.shares.drive-item`, `files.shares.drive-item-content`):
  - `/s/{token}` → Nextcloud public-share WebDAV (`/public.php/dav/files/{token}`),
    `/f/{fileId}` → DAV `SEARCH` by `oc:fileid` with the caller's token.
  - `/content` answers **302** to a short-lived self-authorising URL (ADR §39
    mechanism; the client fetches the `Location` without credentials), which
    streams the bytes — or the Collabora PDF for Office formats when
    `format=pdf`.
- `ISharedFileReader` implemented in `Adapters.Dav/Files`; Collabora conversion
  behind an Application port (e.g. `IDocumentConverter`) with its own adapter.
- `filesBaseUrls` added to the discovery document (optional; absent = feature off).
- Remove the two `/shares` entries from the deferred list; keep threat
  submission deferred.

## Deploy
- `collabora/code` container behind Caddy (`office.<domain>`), `richdocuments`
  installed and pointed at it (WOPI allow-list = the Nextcloud container),
  verified: open a `.docx` from Nextcloud in the browser and edit it.

## Client (NightMail)
- `CloudDriveProvider.nextcloud`; `parseCloudDocumentLink` recognises the two
  shapes only on advertised hosts; routing picks the NightMail Server account
  that advertises the host; reuses the Graph drive datasource against that
  account's Graph base. `cloudDocumentFormatFor` treats Office files as
  server-converted PDF, like Microsoft.
- `docs/claude/body-links.md` updated.

## Acceptance
- Unit/WireMock/endpoint/conformance tests for both operations (fixtures
  updated for Nextcloud-shaped `driveItem`s where Graph-only fields don't apply).
- Live: alice shares a `.docx` and a PDF (public link) and has an internal
  `/f/` link; bob's façade resolves and downloads both; `.docx` with
  `format=pdf` returns a PDF; the `.docx` opens for editing in Collabora via the
  Nextcloud UI.
- Client: tests for scanner, routing and discovery parsing; `flutter analyze lib`
  clean, `flutter test` green, `flutter build macos --debug` succeeds.
