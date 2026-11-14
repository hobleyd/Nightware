# Recorded backend exchanges

Captured by `deploy/scripts/record.sh` against the dev stack (Stalwart
v1.1.0, Nextcloud 35.0.3) on 2026-11-14. Test users alice/bob,
domain `nightware.localhost`. Authorization headers are not recorded; every
request was sent with a Keycloak access token obtained by token exchange
(aud `stalwart` for JMAP, `nextcloud` for CalDAV).

- `jmap/NN-*.request.json` — body POSTed to `https://mail.nightware.localhost/jmap/`
  (01-session is `GET /.well-known/jmap` → 307 → `/jmap/session`).
- `jmap/NN-*.response.json` — response body.
- `caldav/NN-*.request.txt` — method, path, Depth and XML body sent to
  `https://cloud.nightware.localhost`; `*.response.xml` is the (pretty-printed) multistatus;
  `*.response-headers.txt` holds status line, content-type, ETag, DAV.

Ids, states and sync-tokens are real values from that run; they are only
meaningful together (e.g. 06-email-changes' `sinceState` is the state
before the two seeded messages arrived).
