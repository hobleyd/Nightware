# Nightware.Adapters.Jmap

Stalwart over JMAP (RFC 8620 core, RFC 8621 mail, VacationResponse). This
project holds the shared **core** (`Core/`, `Mail/Ids`, `Mail/MailboxRoles.cs`).
Feature phases add port implementations in `Mail/Read`, `Mail/Write`,
`MailboxSettings` and `Security`, and register them at the end of
`AddJmapAdapter`.

Everything is `internal`. Only `AddJmapAdapter` and `JmapOptions` are public.

## Implementing a port

1. Inject `IJmapClient` (scoped). It is the only way to talk to Stalwart.
   Credentials come from `IBackendCredentials`, sessions are cached, and limits
   and errors are handled for you.
2. Resolve the account from the `MailboxRef` with `ResolveAccountAsync`. This
   gives 404/403 for mailboxes the caller cannot see (ADR 0001 §19).
3. Build a `JmapRequestBuilder` with descriptors from `JmapMethods`, send it,
   and read each call with `response.Get(call)`.

```csharp
internal sealed class JmapMailFolderReader(IJmapClient jmap) : IMailFolderReader
{
    public async Task<Result<MailFolder>> GetAsync(MailboxRef mailbox, FolderRef folder, CancellationToken ct)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, ct);
        if (!account.IsSuccess) return account.Error;

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGet, new GetRequest(account.Value.AccountId)
        {
            Properties = ["id", "name", "parentId", "role", "totalEmails", "unreadEmails"],
        });

        var response = await jmap.SendAsync(request, ct);           // transport/HTTP/request-level errors
        return response.Bind(r => r.Get(get))                        // method-level errors (mapped)
            .Bind(mailboxes => Pick(mailboxes.List, folder));
    }

    private static Result<MailFolder> Pick(IReadOnlyList<Mailbox> list, FolderRef folder) => folder switch
    {
        FolderRef.WellKnown w => MailboxRoles.Find(list, w.Name) is { } m ? Map(m) : Error.NotFound("No such folder."),
        FolderRef.ById b => list.FirstOrDefault(m => m.Id == b.Id.Value) is { } m ? Map(m) : Error.NotFound("No such folder."),
        _ => Error.NotFound("No such folder."),
    };
}
```

### Back-references, writes, blobs

- **Back-reference.** Leave the argument null and bind it when adding the call:
  `request.Add(JmapMethods.EmailGet, new EmailGetRequest(id), query.Ref("ids", JmapPaths.Ids))`.
  The argument goes on the wire as `#ids`.
- **Writes.** Use `SetRequest<T>.Update` with `new Patch().SetEntry("keywords", Keywords.Seen).Build()`.
  Map keys are JSON-pointer-escaped for you. A null value resets the property.
  Read the outcome with `setResponse.Created(creationId)`, `.Updated(id)`,
  `.Destroyed(id)` or `.AllSucceeded()` (`Core/Errors/SetResults`).
- **Implicit responses.** `response.GetAdditional(submitCall, JmapMethods.EmailSet)`
  reads them, e.g. the response to `onSuccessUpdateEmail`.
- **Blobs.** `IJmapBlobService.UploadAsync` streams to the upload URL. Then use
  the `blobId` in `Email/import`. `DownloadAsync` returns a Domain `BlobContent`
  once headers arrive and never buffers the body.
- **Header forms.** Use `EmailHeaders.Property("Message-ID", HeaderForm.MessageIds)`
  in `properties`. Read results back with `EmailHeaders.GetText` and `GetMessageIds`.
- **New methods.** Add a descriptor to `JmapMethods` and the closed generic types
  to `JmapJsonContext`. The JSON is source-generated.

### Limits handled by the client

- A `Foo/get` whose explicit `ids` list exceeds `maxObjectsInGet` is split into
  chunks (`c0`, `c0.1`, …) and merged again in `Get`.
- A **back-referenced** `#ids` is not chunked, because its size is unknown when
  the request is built. Keep `Email/query` `limit` at or below
  `session.Limits.MaxObjectsInGet` when its ids feed an `Email/get`.
- More calls than `maxCallsInRequest` go over several HTTP POSTs. A cut is never
  placed between a call and a call it references. `createdIds` are carried
  across the POSTs.
- `maxObjectsInSet` is exposed in `Limits` but not enforced. Chunk large
  `/set` batches yourself.

### Retries

- Only safe requests are retried: GETs, and POSTs made only of
  `/get`, `/query`, `/changes` and `/queryChanges` calls.
- `/set`, `/import` and uploads are never retried.
- `Retry-After` is honoured.

## Error mapping

| JMAP / HTTP | `ErrorKind` (code) |
|---|---|
| HTTP 401 | `BackendAuthFailed` for a `PassThrough` credential (the client re-authenticates); `BackendUnavailable` for a `TokenExchange`/`ServiceAccount` one, which is façade misconfiguration (ADR 0001 §42). Either way drops the cached session and the scope's credential. |
| HTTP 403 | `Forbidden` |
| HTTP 404 (download) | `NotFound` |
| HTTP 413 | `PayloadTooLarge` (`requestEntityTooLarge`) |
| HTTP 429 | `Throttled` (`activityLimitReached`) + `RetryAfterSeconds` |
| HTTP 5xx, timeout, connection failure | `BackendUnavailable` + `RetryAfterSeconds` |
| problem `limit` (`maxSizeRequest`/`maxSizeUpload`) | `PayloadTooLarge` |
| problem `limit` (`maxConcurrentRequests`/`maxConcurrentUpload`) | `Throttled` |
| problem `limit` (other), `notJSON`, `notRequest` | `Unknown` (`generalException`): our bug |
| problem `unknownCapability` | `Unsupported` |
| method `cannotCalculateChanges`, `tooManyChanges` | `SyncStateGone` (`syncStateNotFound`, HTTP 410) |
| method `accountNotFound`, `fromAccountNotFound` | `NotFound` |
| method `forbidden`, `accountReadOnly` | `Forbidden` |
| method `unknownMethod`, `unsupportedFilter`, `unsupportedSort`, `accountNotSupportedByMethod` | `Unsupported` |
| method `invalidArguments`, `anchorNotFound` | `Validation` |
| method `requestTooLarge` | `PayloadTooLarge` |
| method `stateMismatch` | `PreconditionFailed` |
| method `rateLimit` | `Throttled` |
| method `serverUnavailable` | `BackendUnavailable` |
| method `serverFail`, `serverPartialFail`, `invalidResultReference`, other | `Unknown` |
| SetError `notFound`, `blobNotFound` | `NotFound` |
| SetError `forbidden`, `forbiddenFrom`, `forbiddenMailFrom`, `forbiddenToSend` | `Forbidden` |
| SetError `overQuota` | `PayloadTooLarge` (`ErrorQuotaExceeded`) |
| SetError `tooLarge` | `PayloadTooLarge` |
| SetError `rateLimit` | `Throttled` |
| SetError `invalidProperties` | `Validation`, target = the property names |
| SetError `invalidPatch`, `singleton`, `tooManyKeywords`, `tooManyMailboxes`, `invalidEmail`, `tooManyRecipients`, `noRecipients`, `invalidRecipients` | `Validation` |
| SetError `willDestroy`, `mailboxHasChild`, `mailboxHasEmail`, `alreadyExists`, `cannotUnsend` | `Conflict` |
| account resolution: no account of that name | `NotFound` |
| account resolution: account exists without the capability | `Forbidden` |

## Message id codec (`Mail/Ids/MailItemId.cs`, FROZEN)

The format is frozen because clients store these ids. ADR 0001 §1.

- **`e.<jmapId>`.** An ordinary message. The JMAP id is 1–255 characters of
  `[A-Za-z0-9_-]` and is stable across moves.
- **`d.<base64url(Message-ID)>`.** A message with the `$draft` keyword and
  exactly one valid Message-ID. The Message-ID has no angle brackets, is UTF-8,
  and is encoded as base64url **without padding**. It survives draft
  replacement, which changes the JMAP id.
- **Emit** with `MailItemId.For(email)` or `For(id, keywords, messageIds)`.
  Every `Email/get` must fetch `keywords` and `messageId`. A draft without a
  unique Message-ID falls back to `e.`.
- **Parse.** `MailItemId.Parse(MessageId)` accepts canonical ids only. It
  rejects padding, non-canonical base64 tails, invalid UTF-8, brackets and
  whitespace. Anything else is `Validation` / `ErrorInvalidIdMalformed` (400).
- **Resolve a `d.` id** through `IJmapMessageLocator` (`Mail/Read/JmapMessageLocator`). It lists
  the Drafts mailbox newest first, a page of `maxObjectsInGet` at a time (at most 10 pages), and
  confirms each Email with `id.MatchesDraft(email)`. Stalwart v1.1 cannot search for a Message-ID:
  `Email/query` with `header: ["Message-ID", value]` never matches, with or without angle brackets.
  Header search that does work (Subject) is indexed asynchronously, so it would also miss a draft
  created a moment ago. Verified live on 2026-11-15.
- **New drafts.** Mint the Message-ID with
  `InternetMessageIds.Mint(mailbox)` → `<uuid>@<mailbox domain>`, without
  brackets. Use `ToHeaderValue` to get the header form.

Phase 3 note: Stalwart creates Inbox, Drafts, Sent Items, Junk Mail and
Deleted Items, with roles inbox/drafts/sent/junk/trash. It creates **no
Identity**. Sending needs one, so create it with `Identity/set` when
`Identity/get` returns none.

Folder ids are JMAP mailbox ids, used as-is. `MailboxRoles` maps the Graph
well-known names:

| Graph name | JMAP role |
|---|---|
| inbox | `inbox` |
| sentitems | `sent` |
| drafts | `drafts` |
| deleteditems | `trash` |
| junkemail | `junk` |
| archive | `archive` |
| outbox | `outbox` (not created by Stalwart) |
| msgfolderroot | — (null `parentId`) |

## Mail read side (`Mail/Read`, Phase 2)

`IMailFolderReader`, `IMailMessageReader`, `IMailDeltaReader`, `IRecentCorrespondents` and the
shared `IJmapMessageLocator` are registered by `AddJmapMailRead`.

- **Every `Email/get`** uses `EmailProperties`: `keywords`/`messageId` (for `e.`/`d.` ids),
  `mailboxIds`, `threadId`, `bodyStructure` and `header:Importance`/`header:X-Priority`. Body parts
  are fetched with `header:Content-Type`, because the iTIP `method` lives in its parameters and
  `type` drops them. A `text/calendar` part with a `method` is the message's `CalendarPart`; one
  without is an ordinary file.
- **`parentFolderId`.** The listed folder when the Email is in it, otherwise the ordinally smallest
  mailbox id. Top-level mailboxes report `msgfolderroot`.
- **Lists.** Folder pages and `GET /messages` page by offset; the skip token is the next position
  (`calculateTotal`). `GET /messages` has no `inMailbox`, so it spans Deleted Items and Junk.
  `$search` (KQL → `EmailFilter`) is unpaged. Untranslatable `$filter`s are `Validation` (400),
  which the client's fallbacks expect.
- **Conversations.** `Email/query` has no thread filter. The reader runs `Thread/get`, then
  `Email/get` with explicit ids (so long threads are chunked), drops Emails filed in a
  `parentFolderId ne` folder, sorts newest first and caps at `$top`. There is no nextLink.
- **Bodies.** The type comes from the parts. HTML is used when there is an HTML part; plain-text
  mail is a `text` body. `Prefer: outlook.body-content-type="text"` returns the `text/plain`
  alternative, or `HtmlToText` when there is none.
- **Attachments.** The id is the JMAP `partId`, looked up across the whole `bodyStructure`. An
  `message/rfc822` part is an item attachment with no `contentBytes`; its `$value` is the part blob.
  Inline means "has a Content-ID".
- **Delta.** The initial sync captures the Email state first (`Email/get ids:[]`), then pages
  `Email/query inMailbox` newest first by position. The last page hands over to `Email/changes`
  from that state. Upserts are whole messages (JMAP does not say which properties changed).
  Known limits:
  - `Email/changes` does not say where an updated Email was before. An update to a message
    outside the folder is therefore reported as `@removed: changed`; the client drops that id from
    its cache and re-lists the other folder when it is shown.
  - Position paging can skip a message that is deleted above the cursor while the initial sync is
    still paging.
- **Recent correspondents.** Recipients of the newest 200 Sent Items and senders of the newest 200
  Inbox messages, ranked by count, then recency.

## Delta tokens (`Core/JmapDeltaTokenCodec.cs`)

A token is the unpadded base64url of `{"v":1,"m":mailboxId,"p":"i"|"c","o":position,"s":state,"sel":[…],"top":n}`.

- `p:"i"` is the initial `Email/query` paging phase. `o` is the next
  position, and `s` is the Email state captured when the sync started.
- `p:"c"` is the `Email/changes` phase. `s` is `sinceState`.
- Decoding is tolerant. Garbage, another version, or another folder's token
  (`Decode(token, mailboxId)`) gives `SyncStateGone`, and the client resyncs
  on the resulting 410.

## Authentication

Every request carries the credential from `IBackendCredentials.ForAsync(Backend.Mail)`,
fetched once per DI scope. `JmapAuthenticationHandler` applies it.

- **Bearer.** Sent as-is.
- **Bearer with `ActAs`.** Refused before any request is sent, as `Unknown`
  (500). An example is Adapters.Directory's `ServiceAccount`/`ClientCredentials`
  mode. Stalwart cannot impersonate with a token, so the request would open
  the service account's mailbox instead of the caller's.
- **Basic.** `Parameter` is `base64(user:password)`, as Adapters.Directory
  produces it, or raw `user:password`. A value containing `:` is raw.
- **Basic with `ActAs`.** This is a Stalwart master-user login:
  `"<ActAs>%<user>:<password>"`. The order is *target `%` master*.
  `Jmap:MasterUserSeparator` defaults to `%`. Caveats from Stalwart's docs and
  forum:
  - Impersonation needs an administrator principal.
  - Stalwart reportedly refuses app passwords here.
  - The master account must resolve in the active directory.
  - Group mailboxes cannot be impersonated.
  - Stalwart v1 has no `authentication.master` setting. In the compose stack
    the master is the recovery administrator, verified with `curl -u
    'alice@nightware.localhost%admin:…'`; see `deploy/README.md`
    "Service-account fallback".
- **No credential.** A request without a credential throws, because Stalwart
  answers anonymous session fetches with 200 and no accounts. The only
  deliberately anonymous request is the health check.
- **Redirects.** Auto-redirect is off, because .NET drops `Authorization` on
  redirects. The session fetch follows up to three same-origin redirects
  itself: Stalwart sends `/.well-known/jmap` → 307 → `/jmap/session`.

## Configuration (`Jmap` section)

| Key | Default | Notes |
|---|---|---|
| `SessionUrl` | — (required) | e.g. `http://stalwart:8080/jmap/session` |
| `UseSessionUrlOrigin` | `true` | Stalwart advertises its public `https://mail…` URLs. This moves `apiUrl`/`uploadUrl`/`downloadUrl` onto `SessionUrl`'s origin. |
| `SessionCacheDuration` | 5 min | Per credential. Also dropped on 401 and when `sessionState` changes. |
| `RequestTimeout` / `AttemptTimeout` | 30 s / 10 s | API calls |
| `BlobTimeout` | 5 min | Uploads, and download headers |
| `MaxRetryAttempts` | 2 | Safe requests only |
| `MaxObjectsInGetFallback` / `MaxCallsInRequestFallback` | 500 / 16 | Used when the session omits them |
| `MasterUserSeparator` | `%` | |

Options are validated on first use, not at startup.

The health check `jmap` (tag `backend`) GETs `SessionUrl` anonymously. Any
answer below 500 counts as healthy.
