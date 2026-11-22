# Api plumbing — how a feature phase adds a Graph endpoint

This is the guide for phases 2–5. It covers the shared plumbing in
`src/Nightware.Api` that every endpoint uses: routing, query parsing,
projection, links, errors, DTOs and conformance. Read `architecture.md` and
ADR 0001 first. `docs/contract/` defines the scope.

## 1. Where your code goes

| You write | File |
|---|---|
| Routes | `src/Nightware.Api/Features/<Feature>/<Feature>Endpoints.cs`, filling the body of `Map<Feature>Endpoints(this RouteGroupBuilder graph)`. Add further files under `Features/<Feature>/` as needed. |
| DTOs and mappers | `Features/<Feature>/Dto/`. Mail's are already done; see §6. |
| Use cases | `Application/<Feature>/…`, registered in `Add<Feature>UseCases()` |
| Port implementations | `Adapters.*/<Feature>/…`, registered from the adapter's `Add*Adapter` |
| Endpoint tests | `tests/Nightware.Api.Tests/Features/<Feature>/` |
| Conformance arrangements | `tests/Nightware.Conformance.Tests/Arrangements/<Feature>Arrangements.cs` |

Do not edit `Program.cs` or `Composition/`.

- `Map<Feature>Endpoints` is already called for **both** `/v1.0` and `/beta`.
  Each group already requires authentication and runs `GraphBindingFilter`.
- Because every route is mapped twice, do not call `.WithName(...)`. The names
  would collide.
- Feature code must not reference `Nightware.Adapters.*`. An architecture test
  enforces this.
- A route that must work without a bearer token needs `.AllowAnonymous()` and
  must authorise itself. The To Do upload-session `PUT` is one: per
  endpoints.md cross-cutting requirement 11 it arrives with no
  `Authorization` header, so it has to check its own signed, short-lived URL.
- Any port you do not implement resolves to `NotImplementedPort<T>`. Every
  method on it returns `ErrorKind.Unsupported`, which becomes a 501.

## 2. Routing a mailbox-scoped operation

`{mailbox}` operations need two routes: `/me/...` and `/users/{user}/...`.
`MailboxRoutes.MapMailboxGroups` (`Nightware.Api.Graph.Routing`) returns the two
route groups; map each route once per group:

```csharp
foreach (var scope in graph.MapMailboxGroups())
{
    scope.MapGet("/mailFolders/{folderId}/messages", Handler);
}
```

Call the framework's `MapGet`/`MapPost`/`MapPatch`/`MapDelete` directly, with the
handler written at the call site (a method group or lambda). The Request Delegate
Generator (`EnableRequestDelegateGenerator` in `Nightware.Api.csproj`) generates
the request delegate for every call it can see at compile time. A handler passed
through a `Delegate` parameter cannot be generated: the build fails with RDG002,
and at runtime the handler would be compiled from expression trees on every host
start.

Add a `Mailbox mailbox` parameter to get the resolved mailbox. The resolver
works as follows:

- `/me` resolves to the caller's `email` claim. If the claim is missing, the
  response is 403.
- `/users/a@b` and `/users/a%40b` resolve to that address, lower-cased.
  Whether the caller may open it is the backend's decision.
- `/users/{id}` resolves through `IUserDirectory`. If the directory port is
  still the stub, or the id is unknown, the response is 404.

Use these members in your handler:

- `mailbox.Ref` is the `MailboxRef` to pass to ports.
- `mailbox.IsMe` tells you which form was called.
- `mailbox.ContextSegment` is `users('a%40b')`, for `@odata.context`.

If resolution fails, the binding filter returns the Graph error before your
handler runs.

Routes with no mailbox prefix (`/users`, `/places/…`, `/shares/…`) use plain
`graph.MapGet(...)`.

**Several operation ids share one route.** For example, GET
`{mailbox}/messages` serves list, list-by-conversation and search (see
`endpoints.md`, "Conventions"). Map the route once and dispatch inside the
handler on the parsed query (`odata.Query.Search`, `.Filter` and so on). Do not
map a catch-all under `messages/{id}/`; Phase 4 owns `…/event`.

## 3. Query options, `Prefer` and bodies

| Parameter type | What it gives you |
|---|---|
| `ODataQuery odata` | `odata.Query` is the parsed `ItemQuery`, covering `$select $filter $orderby $top $skip $skiptoken $search $expand`. A parse failure is answered `400 invalidRequest`, naming the construct, before your handler runs. |
| `PreferHeader prefer` | `TimeZone` and `TimeZoneLabel` for `outlook.timezone`, `BodyContentType`, `MaxPageSize` and `ReturnMinimal`. When you honour a preference, call `prefer.MarkTimeZoneApplied(http.Response)` or `MarkBodyContentTypeApplied`. Any other preference is ignored, which is right for `IdType="ImmutableId"`: Nightware's ids are already immutable, and Graph documents no `Preference-Applied` for it. |
| A DTO or `JsonObject` body | Bound with `GraphJson.Options`: case-insensitive properties (`ToRecipients`) and case-insensitive enums (`"Html"`). For PATCH, bind `JsonObject` when you must tell an absent property from `null`. |

Parser behaviour:

- **`$filter`.** The parser accepts comparisons
  (`eq ne gt ge lt le`), `in (…)`, `and`/`or`/`not`, parentheses,
  `startswith`/`contains`/`endswith`, and `x/any(a: …)`.
  - Lambda paths are made relative, and the bare variable becomes `"$this"`.
  - Property paths such as `flag/flagStatus` are supported.
  - Literals can be strings (with `''` escapes), numbers, unquoted datetimes,
    `true`/`false`/`null`, or enums (`ns.type'value'`).
  - The parser accepts more than the contract whitelist
    (`odata-subset.md` §3). **Your adapter decides what it can translate.**
    For any other shape it must return `Error.Validation("… not supported …")`,
    which becomes a 400. The client relies on that 400 to fall back, for
    example from `conversationId in (…)` to per-id `eq`.
  - `FilterNode.Conjuncts()` flattens `a and b and c`.
- **`$search`.** The parser strips exactly the outer quotes and parses KQL into
  `SearchQuery`.
  - Single-term clauses go in `Terms`, which are AND-ed. OR-groups go in
    `AnyOf`. `FreeText` holds the plain words.
  - `OR` binds tighter than adjacency, so `from:a OR from:b x` means
    `(from:a OR from:b) AND x`.
  - Unknown `prop:value` tokens and malformed input are treated as free text,
    never a 400.
- **`$top`** is clamped to 1000. `$count`, `$format` and any other unknown
  `$`-option is a 400.
- **`$skip` and `$skiptoken`** both arrive in `ItemQuery`. An adapter that pages
  with its own token should ignore `Skip` whenever `SkipToken` is set.

## 4. Writing the response

Build every response through `GraphResults` (`Nightware.Api.Graph`).

| Need | Call |
|---|---|
| A single entity | `GraphResults.Entity(dto, contextFragment, select, expand)` writes `@odata.context` as `…#fragment(select)/$entity`. |
| A created entity (201), or a move | `GraphResults.Created(dto, contextFragment, select)` |
| A page from a port | `GraphResults.Page(page, toDto, contextFragment, odata.Query)` |
| Any sequence | `GraphResults.Collection(items, contextFragment, query, skipToken)` |
| A delta page | `GraphResults.Delta(deltaPage, toDto, cursor, "message", "#microsoft.graph.message")`. See §5. |
| A `Result<T>` or `Result` | `GraphResults.Match(result, onSuccess)` returns the error envelope on failure. `Match(Result)` defaults to 204. |
| An error | `GraphResults.Error(error)` |
| No content or Accepted | `GraphResults.NoContent()`, `GraphResults.Accepted()` |
| Binary content (`$value`) | `Results.Stream(...)` from the `BlobContent`. No envelope is involved. |

What these calls do for you:

- **`$select` projection** (`GraphProjection`) runs on the serialised DTO. It
  always keeps `id`, `@odata.etag` and `@removed`. At the top level it drops
  `@odata.type` (ADR §14). Inside `$expand(…$select=…)` it keeps
  `@odata.type` (odata-subset §7). Write responses project too: PATCH sends
  `$select`.
- **`@odata.nextLink`** is the request's own query options with
  `$skip`/`$skiptoken` replaced by the adapter's `Page.SkipToken`.
- **Absolute URLs** are built by `GraphLinkBuilder` on the configured public
  `Discovery:GraphBaseUrl`/`GraphBetaBaseUrl`. The client follows links only
  on that scheme, host and port. If no base URL is configured, the request
  origin is used (after `X-Forwarded-Proto/Host/Prefix`).
- **JSON** uses `GraphJson.Options`:
  - camelCase properties, and enums as camelCase strings;
  - instants as `2026-01-01T10:00:00Z`;
  - `dateTimeTimeZone.dateTime` as `2026-01-01T10:00:00.0000000` with **no
    `Z`**;
  - nulls are written. A member Graph omits when empty needs
    `[JsonIgnore(Condition = WhenWritingNull)]`.

## 5. Delta endpoints

The worked example is in
`tests/Nightware.Api.Tests/Endpoints/DeltaEndpointTests.cs`:

```csharp
var cursor = links.ReadCursor(request.Query, odata.Query);      // DeltaLinkCodec links (inject it)
if (!cursor.IsSuccess) return GraphResults.Error(cursor.Error); // bad signature, expired or garbage → 410
var c = cursor.Value;                                           // c.Token null = initial sync
var page = await reader.DeltaAsync(mailbox.Ref, folder, c.Token,
    new DeltaRequest(c.Top ?? DeltaRequest.DefaultPageSize, c.Select), ct);
return GraphResults.Match(page, p => GraphResults.Delta(p, m => MessageDtoMapper.ToDto(m), c, "message", "#microsoft.graph.message"));
```

How the links are built:

- The link token is `base64url(json{v, t: adapterToken, s: select, n: top, i: issuedAt})`,
  then a dot, then `base64url(HMAC)`. The HMAC key is `DeltaLinks:SigningKey`,
  and links live for `DeltaLinks:Lifetime`.
- `$deltatoken` and `$skiptoken` are interchangeable inputs.
- The frozen `$select` and `$top` come out of the token, not the URL.
- An adapter's `Error.SyncStateGone` becomes `410 syncStateNotFound`, and the
  client resyncs.

## 6. Mail DTOs (frozen)

The DTOs live in `Features/Mail/Dto/`:

- `MessageDto` and `EventMessageDto`, where `@odata.type` is
  `eventMessageRequest`, `eventMessageResponse` or `eventMessage`
  (for a cancellation);
- `RecipientDto`, `EmailAddressDto`, `ItemBodyDto`, `FollowupFlagDto`,
  `InternetMessageHeaderDto`, `DateTimeTimeZoneDto`, `TimeSlotDto`,
  `LocationDto`;
- `AttachmentDto`, for file and item attachments;
- `MailFolderDto`;
- `SingleValueExtendedPropertyDto`, which is input only (migration).

`MessageDtoMapper.ToDto(MailMessage, MessageMappingOptions)` maps a message.
The options control the following:

- `Select`: `internetMessageHeaders` is emitted only when it is selected.
- `IncludeAttachments`: set it when `$expand=attachments` is present.
- `Prefer`: the time zone for meeting times.
- `Meeting`: a `MeetingMessageDetails` holding start, end, location, all-day,
  a refined `MeetingMessageType` and the proposed new time.

`MailMessage.CalendarPart` carries only the method. The phase that resolves
the calendar part must therefore supply `Meeting`. Without it, invitations
reach the client with no time.

The mapper also has overloads for `MailAttachment` and `MailFolder`.

## 7. Errors

Return `Result` failures and let the mapper handle them. Do not set status
codes by hand. `GraphErrorMapper` is the only place `ErrorKind` becomes a
status and code:

| Kind | Status | Code |
|---|---|---|
| NotFound | 404 | `ErrorItemNotFound` |
| Validation | 400 | `invalidRequest` |
| Unsupported | 501 | `notImplemented` |
| SyncStateGone | 410 | `syncStateNotFound` |
| Throttled | 429 + `Retry-After` | `TooManyRequests` |
| BackendUnavailable | 503 + `Retry-After` | `serviceNotAvailable` |
| Unauthorized / BackendAuthFailed | 401 | `InvalidAuthenticationToken` |
| Forbidden | 403 | `ErrorAccessDenied` |
| Conflict | 409 | `conflict` |
| PreconditionFailed | 412 | `preconditionFailed` |
| PayloadTooLarge | 413 | `requestEntityTooLarge` |
| Unknown | 500 | `generalException` (message hidden) |

Codes and headers:

- An adapter-specific code passes through for the descriptive kinds, for
  example `new Error(ErrorKind.Conflict, "ErrorFolderExists", …)`.
- The semantic kinds always use the canonical code: auth, sync state,
  throttling, availability and Unknown.
- `Retry-After` is integer seconds: `RetryAfterSeconds`, or 5 by default.
- Use **403, never 401, for "not allowed"**. The client treats 401 as an
  expired token.

The envelope is
`{"error":{"code","message","innerError":{"date","request-id","client-request-id"}}}`.
Every response carries a `request-id` header and echoes `client-request-id`.

Unmatched paths under `/v1.0` or `/beta` return `400 BadRequest`, with the
message "Resource not found for the segment '<last segment>'.". This mirrors
Graph, so the client's 400 fallbacks (casts, RSVP spellings) and its delta
reset behave as they do against Microsoft. A *mapped* route whose port is
still the stub returns 501. Unhandled exceptions and bodiless framework errors
also get the envelope.

## 8. Tests you add

**Endpoint tests.** In `Nightware.Api.Tests`, use `Support/TestHost`. It is the
real composition, with a static JWT key:

```csharp
await using var host = await TestHost.StartAsync(services => services.AddSingleton(fakeReader));
var response = await host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders", ct);
```

Use `TestHost.Token(email: …)` to mint other callers.

**Conformance.** Add an arrangement for each operation id you implement. Until
an operation has one, its fixture is skipped, not failed:

```csharp
public sealed class MailArrangements : IConformanceArrangements
{
    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.folders.list", static fakes =>
            fakes.For<IMailFolderReader>()
                .ListAsync(new MailboxRef("me@example.com"), null, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<MailFolder>([/* domain objects carrying the fixture's ids */], "next")))
        {
            IgnorePaths = [],   // e.g. "value/*/@odata.type" where Graph emits what we deliberately don't
        };
    }
}
```

How the harness runs a fixture:

- It boots the real `Program`. Any `Authorization` header authenticates as
  `me@example.com`, and the public base URL is `https://graph.microsoft.com/v1.0`.
- It replays the request and compares the status, then the body with
  `JsonNode.DeepEquals`.
- Before comparing, it drops `@odata.context` and `@odata.etag`. It masks
  links, after checking that they sit on the base URL, and masks the
  `innerError` ids and date.
- Ports you do not fake stay as the 501 stub, so an unarranged call fails
  loudly.

The worked example is `Arrangements/ExampleArrangements.cs` together with
`Examples/example.mail-folders.list.json`.

## 9. Configuration

| Section | Owner | Keys |
|---|---|---|
| `Auth` | Api | `Authority` (public issuer), `MetadataAddress` (internal JWKS URL, e.g. `http://keycloak:8080/realms/nightware/.well-known/openid-configuration`), `Audience` (`nightware-api`), `RequireHttpsMetadata`, `ValidIssuers[]`, `RequireVerifiedEmail` (default true) |
| `Discovery` | Api | `GraphBaseUrl`, `GraphBetaBaseUrl`, `AuthorizationEndpoint`, `TokenEndpoint`, `ClientId`, `Scopes[]`. All are required, validated on start, and served at `/.well-known/nightware`. |
| `DeltaLinks` | Api | `SigningKey` (shared across instances; if empty, a per-process key is used), `Lifetime` |
| `Directory`, `BackendCredentials` | Directory adapter | validated on start |
| `Jmap`, `Dav` | JMAP and DAV adapters | the adapter owns the options class |

`/healthz` is anonymous, and adapters add their checks to it.

The Docker image is built from the repository root:

```bash
docker build -f src/Nightware.Api/Dockerfile .
```

It listens on 8080 and runs as a non-root user.
