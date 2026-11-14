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
  previews (`/shares`), threat submission (it only moves to Junk), and extra
  consent scopes. `findRooms` (beta) and out-of-office remain in use.

## Operational caveat

The client treats **any** HTTP response to a token refresh — including a proxy's
502/503 while a backend restarts — as bad credentials and forces re-sign-in.
Keep the token endpoint (Keycloak) highly available and never put a
maintenance page in front of it.
