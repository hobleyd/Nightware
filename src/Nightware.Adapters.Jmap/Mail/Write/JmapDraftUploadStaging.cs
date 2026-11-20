using System.Text;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Draft attachment uploads staged in Stalwart (ADR 0006 D5): each chunk is uploaded as a blob, and the session
/// (the file and its blobs in order) is kept in the draft's metadata under <see cref="Namespace"/>, so nothing
/// outside the mail store holds it. Past <see cref="MergeAfter"/> chunks, consecutive ones are merged with
/// <c>Blob/upload</c> while the result stays within the account's <c>maxSizeBlobSet</c> (7.5 MB on Stalwart), so
/// even small chunks keep the list inside 8 KB.
/// </summary>
internal sealed class JmapDraftUploadStaging(IJmapClient jmap, IJmapMessageLocator locator, IJmapBlobService blobs) : IDraftUploadStaging
{
    public const string Namespace = "nightware.upload";
    private const string ChunkType = "application/octet-stream";

    /// <summary>Blob entries kept before chunks start to be merged; about 75 bytes each, well inside 8 KB.</summary>
    internal const int MergeAfter = 48;

    public async Task<Result<DraftUploadState>> BeginAsync(MailboxRef mailbox, MessageId draftId, DraftUploadFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var draft = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            return draft.Error;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var sessions = draft.Value.Sessions;
        sessions[sessionId] = new JsonObject
        {
            ["name"] = file.Name,
            ["type"] = file.ContentType,
            ["size"] = file.Size,
            ["inline"] = file.IsInline,
            ["cid"] = file.ContentId,
            ["blobs"] = new JsonArray(),
            ["received"] = 0L,
        };
        var saved = await SaveAsync(draft.Value, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? new DraftUploadState(sessionId, file, 0) : saved.Error;
    }

    public async Task<Result<DraftUploadState>> AppendAsync(
        MailboxRef mailbox, MessageId draftId, string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var draft = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            return draft.Error;
        }

        if (draft.Value.Sessions[sessionId] is not JsonObject session)
        {
            return Error.NotFound("The upload session does not exist or has ended.");
        }

        var state = Read(sessionId, session);
        if (total != state.File.Size)
        {
            return Error.Validation($"The session is for {state.File.Size} bytes, not {total}.", "Content-Range");
        }

        if (first != state.Received || state.Received + length > state.File.Size)
        {
            return Error.Validation($"The next bytes this session wants start at {state.Received}.", "Content-Range");
        }

        var account = draft.Value.Located.Account;
        var uploaded = await blobs.UploadAsync(account.AccountId, chunk, ChunkType, cancellationToken).ConfigureAwait(false);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error;
        }

        if (uploaded.Value.Size != length)
        {
            return Error.Validation($"The chunk had {uploaded.Value.Size} bytes, but Content-Range named {length}.", "Content-Range");
        }

        var list = session["blobs"]!.AsArray();
        var merged = await MergeAsync(account, list, uploaded.Value, cancellationToken).ConfigureAwait(false);
        if (!merged.IsSuccess)
        {
            return merged.Error;
        }

        session["received"] = state.Received + length;
        var saved = await SaveAsync(draft.Value, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? state with { Received = state.Received + length } : saved.Error;
    }

    public async Task<Result<ReadOnlyMemory<byte>>> ReadAsync(MailboxRef mailbox, MessageId draftId, string sessionId, CancellationToken cancellationToken)
    {
        var draft = await DraftAsync(mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            return draft.Error;
        }

        if (draft.Value.Sessions[sessionId] is not JsonObject session)
        {
            return Error.NotFound("The upload session does not exist or has ended.");
        }

        using var content = new MemoryStream();
        foreach (var blob in session["blobs"]!.AsArray().OfType<JsonObject>())
        {
            var download = await blobs.DownloadAsync(draft.Value.Located.AccountId, blob["id"]!.GetValue<string>(), "chunk", ChunkType, cancellationToken).ConfigureAwait(false);
            if (!download.IsSuccess)
            {
                return download.Error;
            }

            await using var stream = await download.Value.Open(cancellationToken).ConfigureAwait(false);
            await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        }

        return new ReadOnlyMemory<byte>(content.ToArray());
    }

    /// <summary>
    /// Appends <paramref name="chunk"/>, merged into the last blob when the account allows a blob of their joint
    /// size; a server without <c>Blob/upload</c> just gets the list.
    /// </summary>
    private async Task<Result> MergeAsync(Core.Session.JmapAccount account, JsonArray list, UploadedBlob chunk, CancellationToken cancellationToken)
    {
        var limit = account.Session.HasAccountCapability(account.AccountId, JmapCapabilities.Blob)
            ? account.Session.AccountCapability(account.AccountId, JmapCapabilities.Blob, JmapJsonContext.Default.BlobAccountCapability)?.MaxSizeBlobSet ?? long.MaxValue
            : 0;
        // Merging re-uploads bytes, and Stalwart counts every blob against the account's upload quota (50 MB
        // per window by default): chunks are only merged once the list would otherwise outgrow the metadata.
        if (list.Count >= MergeAfter && list[^1] is JsonObject last && last["size"]!.GetValue<long>() + chunk.Size <= limit)
        {
            const string created = "merged";
            var request = new JmapRequestBuilder();
            var upload = request.Add(JmapMethods.BlobUpload, new BlobUploadRequest(account.AccountId, new Dictionary<string, BlobUploadObject>(StringComparer.Ordinal)
            {
                [created] = new([new BlobDataSource(last["id"]!.GetValue<string>()), new BlobDataSource(chunk.BlobId)], ChunkType),
            }));
            var response = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(upload));
            if (!response.IsSuccess)
            {
                return response.Error;
            }

            if (response.Value.Created?.GetValueOrDefault(created) is not { } blob)
            {
                return JmapErrors.Unexpected("The mail server did not join the upload's chunks.");
            }

            list[^1] = new JsonObject { ["id"] = blob.Id, ["size"] = blob.Size };
            return Result.Success();
        }

        list.Add(new JsonObject { ["id"] = chunk.BlobId, ["size"] = chunk.Size });
        return Result.Success();
    }

    private sealed record Draft(LocatedMessage Located, JsonObject Sessions, JsonObject Namespace);

    private async Task<Result<Draft>> DraftAsync(MailboxRef mailbox, MessageId draftId, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, draftId, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (!located.Value.Account.SupportsMetadata)
        {
            return Error.Unsupported("This mail server cannot hold an upload session.");
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGetWithMetadata, new EmailGetRequest(located.Value.AccountId) { Ids = [located.Value.JmapId], Properties = ["id", "keywords", "metadata"] });
        var email = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get)).Bind(WriteSupport.Single);
        if (!email.IsSuccess)
        {
            return email.Error;
        }

        if (email.Value.Keywords?.GetValueOrDefault(Keywords.Draft) != true)
        {
            return Error.Validation("Only a draft can take an upload session.", "messageId");
        }

        var ns = email.Value.Metadata?[Namespace]?.DeepClone() as JsonObject ?? [];
        if (ns["sessions"] is not JsonObject sessions)
        {
            sessions = [];
            ns["sessions"] = sessions;
        }

        return new Draft(located.Value, sessions, ns);
    }

    private async Task<Result> SaveAsync(Draft draft, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(draft.Namespace.ToJsonString()) > MessageMetadata.MaxBytes)
        {
            return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "The draft has too many open uploads; finish one first.");
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSetWithMetadata, new SetRequest<Email>(draft.Located.AccountId)
        {
            Update = new Dictionary<string, PatchObject>(StringComparer.Ordinal)
            {
                [draft.Located.JmapId] = new Patch().Set($"metadata/{Namespace}", draft.Namespace.DeepClone()).Build(),
            },
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Updated(draft.Located.JmapId));
    }

    private static DraftUploadState Read(string sessionId, JsonObject session) =>
        new(sessionId,
            new DraftUploadFile(session["name"]!.GetValue<string>(), session["type"]!.GetValue<string>(), session["size"]!.GetValue<long>())
            {
                IsInline = session["inline"]?.GetValue<bool>() == true,
                ContentId = session["cid"]?.GetValue<string>(),
            },
            session["received"]!.GetValue<long>());
}
