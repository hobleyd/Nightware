using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Rules;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Application.Administration.AutoReplies;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// <see cref="IMailboxAutoReplies"/> with the console's own Stalwart credential: the façade's
/// <see cref="JmapAutoReplyStore"/> and <see cref="NightwareSieve"/> run unchanged against the target account through a
/// JMAP client and blob service bound to it. Stalwart lets a principal with the Admin role act on any account by its id
/// (as the console's account has where global admins administer the servers); with a narrower role it refuses, and that
/// is what the admin sees.
/// </summary>
internal sealed class StalwartMailboxAutoReplies(IStalwartManagementClient management) : IMailboxAutoReplies
{
    public async Task<Result<AutoReplySetting>> GetAsync(string mailAccountId, MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var store = await StoreAsync(mailAccountId, address, cancellationToken).ConfigureAwait(false);
        return store.IsSuccess ? await store.Value.GetAsync(new MailboxRef(address.Value), cancellationToken).ConfigureAwait(false) : store.Error;
    }

    public async Task<Result<AutoReplySetting>> SetAsync(
        string mailAccountId, MailAddress address, AutoReplySetting setting, IReadOnlyList<string> internalDomains, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var store = await StoreAsync(mailAccountId, address, cancellationToken).ConfigureAwait(false);
        return store.IsSuccess ? await store.Value.SetAsync(new MailboxRef(address.Value), setting, internalDomains, cancellationToken).ConfigureAwait(false) : store.Error;
    }

    private async Task<Result<JmapAutoReplyStore>> StoreAsync(string mailAccountId, MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailAccountId);
        var session = await management.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return session.Error;
        }

        var account = ManagedAccount.For(session.Value, mailAccountId, address.Value);
        var jmap = new ManagedAccountJmapClient(management, account);
        return new JmapAutoReplyStore(jmap, new NightwareSieve(jmap, new ManagedAccountBlobs(management, account.Session)));
    }
}

/// <summary>A session in which <paramref name="AccountId"/> is the primary account, with the server's mail, Sieve and vacation capabilities.</summary>
internal static class ManagedAccount
{
    private static readonly string[] Capabilities = [JmapCapabilities.Mail, JmapCapabilities.Sieve, JmapCapabilities.VacationResponse, JmapCapabilities.Submission];

    public static JmapAccount For(JmapSession console, string accountId, string name)
    {
        var document = console.Document;
        var own = document.Accounts.Values.FirstOrDefault(static a => a.AccountCapabilities.Count > 0)?.AccountCapabilities ?? [];
        var capabilities = new Dictionary<string, JsonElement>(own, StringComparer.Ordinal);
        foreach (var capability in Capabilities.Where(c => document.Capabilities.ContainsKey(c) && !capabilities.ContainsKey(c)))
        {
            capabilities[capability] = JsonElement.Parse("{}");
        }

        var accounts = new Dictionary<string, JmapAccountInfo>(document.Accounts, StringComparer.Ordinal)
        {
            [accountId] = new JmapAccountInfo { Name = name, IsPersonal = true, IsReadOnly = false, AccountCapabilities = capabilities },
        };
        var primary = Capabilities.ToDictionary(static c => c, _ => accountId, StringComparer.Ordinal);
        var session = console.WithDocument(document with { Accounts = accounts, PrimaryAccounts = primary });
        return new JmapAccount(accountId, name, IsPrimary: true, IsReadOnly: false, session);
    }
}

/// <summary><see cref="IJmapClient"/> for one account, sending as the console.</summary>
internal sealed class ManagedAccountJmapClient(IStalwartManagementClient management, JmapAccount account) : IJmapClient
{
    public Task<Result<JmapSession>> GetSessionAsync(CancellationToken cancellationToken) => Task.FromResult(Result<JmapSession>.Success(account.Session));

    public Task<Result<JmapAccount>> ResolveAccountAsync(MailboxRef mailbox, CancellationToken cancellationToken, string capability = JmapCapabilities.Mail) =>
        Task.FromResult(Result<JmapAccount>.Success(account));

    public Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request, CancellationToken cancellationToken) => management.SendAsync(request, cancellationToken);
}

/// <summary><see cref="IJmapBlobService"/> on the session's upload and download URLs, as the console.</summary>
internal sealed class ManagedAccountBlobs(IStalwartManagementClient management, JmapSession session) : IJmapBlobService
{
    public async Task<Result<UploadedBlob>> UploadAsync(string accountId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, session.UploadUrl(accountId)) { Content = new StreamContent(content) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var sent = await management.SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.Forbidden
                ? Error.Forbidden("The mail server does not let the admin console change this mailbox.")
                : Error.BackendUnavailable($"The mail server refused the upload (HTTP {(int)response.StatusCode}).");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var uploaded = JsonSerializer.Deserialize(bytes, JmapJsonContext.Default.UploadResponse);
        return uploaded is null
            ? Error.BackendUnavailable("The mail server sent an unreadable upload answer.")
            : new UploadedBlob(uploaded.AccountId, uploaded.BlobId, uploaded.Type, uploaded.Size);
    }

    public async Task<Result<BlobContent>> DownloadAsync(string accountId, string blobId, string? name, string contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, session.DownloadUrl(accountId, blobId, name ?? "blob", contentType));
        var sent = await management.SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.NotFound
                ? Error.NotFound("The blob was not found.")
                : Error.BackendUnavailable($"The mail server refused the download (HTTP {(int)response.StatusCode}).");
        }

        // Scripts are small: read now, so nothing holds the connection.
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new BlobContent(contentType, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))) { FileName = name };
    }
}
