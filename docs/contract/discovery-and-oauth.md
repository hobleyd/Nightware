# Discovery and OAuth contract (as implemented by NightMail)

Source: NightMail commit `5d8547d` ("sign a Graph account in to a NightMail
Server"), `lib/data/models/graph_endpoints_model.dart`,
`lib/infrastructure/auth/microsoft_auth_service.dart`.

## Discovery — `GET <server>/.well-known/nightware`

The user types a server address; the client trims it, adds `https://` when no
scheme is given, strips trailing slashes, keeps any sub-path
(`https://host/nw` → `https://host/nw/.well-known/nightware`) and rejects a
query or fragment. Any 2xx is accepted; the body is parsed as JSON regardless
of content type; 404 is reported as "not a NightMail Server". Served
anonymously.

```json
{
  "graphBaseUrl": "https://graph.example.com/v1.0",
  "graphBetaBaseUrl": "https://graph.example.com/beta",
  "authorizationEndpoint": "https://auth.example.com/realms/nightware/protocol/openid-connect/auth",
  "tokenEndpoint": "https://auth.example.com/realms/nightware/protocol/openid-connect/token",
  "clientId": "nightmail",
  "scopes": ["openid", "email", "profile", "offline_access"]
}
```

- All six fields required; strings non-empty; `scopes` a non-empty string list.
  Unknown fields are ignored.
- **Optional `filesBaseUrls`** (Phase 7): a list of the public base URLs of the
  server's file sites (Nextcloud), e.g. `["https://cloud.example.com"]`. Absent
  means no Nextcloud link previews. The client claims a body link as a document
  only when it is `{base}/s/{token}` or `{base}/f/{fileId}` (optionally
  `{base}/index.php/…`, no query or fragment) below one of these, and fetches it
  through this server's `GET /shares/{u!url}/driveItem[/content]`; the server
  resolves sharing links only below the same list. The client drops malformed
  entries rather than refusing the document. Configured as
  `Discovery:FilesBaseUrls` (validated: absolute http(s), no query, fragment or
  credentials).
- **Optional `changesUrl`** (ADR 0007): the change stream, e.g.
  `https://graph.example.com/nightware/v1/changes`. Derived from `graphBaseUrl`
  (`…/v1.0` → `…/nightware/v1/changes`) unless `Discovery:ChangesUrl` is set;
  absent when `ChangeStream:Enabled` is false. The client opens it with the
  account's bearer token, so it uses it only on `graphBaseUrl`'s scheme, host
  and port, and falls back to polling when it is absent or fails.

- URLs must be absolute `https` (`http` only for `localhost`/`127.0.0.1`/`::1`);
  trailing slashes are stripped; `authorizationEndpoint` must not carry a query.

## OAuth (authorization code + PKCE, public client)

- Authorize: `client_id, response_type=code, redirect_uri, scope` (space-joined
  published scopes), `code_challenge` + `code_challenge_method=S256`, `state`,
  `response_mode=query`. Redirect must return `code` and echo `state`.
- Redirect URIs to register: `nightmail://auth-callback` (macOS/iOS/Android),
  exactly `http://localhost:34571` (Windows/Linux), `<web origin>/callback.html`
  (web; needs CORS on discovery, token endpoint and Graph routes).
- Token: form POST, never a `client_secret`. Code exchange sends
  `client_id, grant_type=authorization_code, code, redirect_uri, code_verifier`;
  refresh sends `client_id, grant_type=refresh_token, refresh_token, scope, redirect_uri`.
- Token response: `access_token` required; **`expires_in` must be a JSON
  integer**; `refresh_token` may be omitted on refresh; errors read from
  `error`/`error_description`.

## Graph-side requirements

- `GET <graphBaseUrl>/me` is read for `displayName`, `mail`,
  `userPrincipalName` and `proxyAddresses` (`smtp:` entries → aliases).
- Every `@odata.nextLink`/`@odata.deltaLink` must be absolute and share
  `graphBaseUrl`'s scheme, host and port exactly — otherwise the client ignores
  it (delta polls force a resync; contact paging stops at page 1).
- For a NightMail Server account the client gates off: SharePoint/OneDrive
  previews (Nextcloud links on `filesBaseUrls` hosts do use `/shares`), threat
  submission (it only moves to Junk), and extra consent scopes. `findRooms` (beta) and out-of-office remain in use.

## Operational caveat

The client treats **any** HTTP response to a token refresh — including a proxy's
502/503 while a backend restarts — as bad credentials and forces re-sign-in.
Keep the token endpoint (Keycloak) highly available and never put a
maintenance page in front of it.

## Change stream — `GET <changesUrl>[?mailbox={address}]` (ADR 0007)

Not Graph surface. `Authorization: Bearer` as for Graph; `mailbox` names a
shared mailbox (default: the caller's own). A `text/event-stream` answer:

```
retry: 10000

event: ready
data: {"areas":["mail","calendar"]}

event: changed
data: {"areas":["mail"]}

: keepalive

event: end
data: {"reason":"expired"}
```

- `ready` once the baseline is read: the client syncs once then, since a change
  between its last sync and `ready` is not signalled.
- `changed` names the areas whose backend state moved in the last round
  (`ChangeStream:PollInterval`, 10 s); it carries no ids, the client fetches
  over its delta routes.
- A comment line at least every `ChangeStream:KeepAlive` (25 s).
- `end` with `expired` (30 s before the token's `exp`, or after
  `ChangeStream:MaxDuration`, 30 min) or `credentials` (a backend refused the
  token): the server closes; reconnect with a current token.
- Before the stream opens: 401 (token), 404 (nothing to watch, or a mailbox the
  caller may not open), 429 with `Retry-After` past
  `ChangeStream:MaxStreamsPerUser` (8) on one instance.
