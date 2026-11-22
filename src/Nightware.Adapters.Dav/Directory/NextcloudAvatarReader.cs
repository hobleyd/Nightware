using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Directory;

/// <summary>
/// <see cref="IUserPhotoReader"/> over Nextcloud's avatar endpoint (<c>index.php/avatar/{uid}/{size}</c>).
/// </summary>
/// <remarks>
/// <para>Why here and not in <c>Adapters.Directory</c>: avatars live in Nextcloud, are read with the
/// caller's Groupware credential, and the uid behind an address is found by DAV principal discovery
/// (never constructed) — all of which this adapter already owns.</para>
/// <para>Nextcloud draws a letter avatar for anyone who never uploaded a picture. Graph has no photo for
/// such users and the client then draws initials itself, so a generated avatar (response header
/// <c>X-NC-IsCustomAvatar: 0</c>) is <see cref="ErrorKind.NotFound"/>. The endpoint sits outside the DAV root
/// <see cref="IDavClient"/> is confined to, hence this typed client on the Nextcloud origin.</para>
/// <para>The image is buffered (capped at <see cref="MaxPhotoBytes"/>) so no backend response outlives the
/// request. A 401 is the façade's credential problem, never the caller's: it maps to
/// <see cref="ErrorKind.BackendUnavailable"/>, because a 401 on this best-effort call would make the client
/// refresh its token and could flag the account for re-authentication.</para>
/// </remarks>
internal sealed partial class NextcloudAvatarReader(
    HttpClient http,
    IDavPrincipalDiscovery discovery,
    IBackendCredentials credentials,
    IOptions<DavOptions> options,
    TimeProvider timeProvider,
    ILogger<NextcloudAvatarReader> logger) : IUserPhotoReader
{
    /// <summary>Nextcloud serves 64 or 512; 512 is Graph's typical largest photo for a contact card.</summary>
    public const int Size = 512;

    public const string CustomAvatarHeader = "X-NC-IsCustomAvatar";

    public const int MaxPhotoBytes = 4 * 1024 * 1024;

    private const string UserPrincipalsSegment = "/principals/users/";

    public async Task<Result<BlobContent>> GetAsync(string idOrUpn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrUpn);
        if (!idOrUpn.Contains('@', StringComparison.Ordinal))
        {
            return Error.NotFound("Photos are looked up by mailbox address.");
        }

        var principal = await discovery.DiscoverAsync(new MailboxRef(idOrUpn), cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        if (principal.Value.IsRoomOrResource || UidOf(principal.Value.Href) is not { } uid)
        {
            return NoPhoto();
        }

        var credential = await credentials.ForAsync(Backend.Groupware, cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Error;
        }

        var allowed = DavCredentialPolicy.Check(credential.Value);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, AvatarUri(uid));
        request.Options.Set(DavRequestOptions.Credential, allowed.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, uid, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.Message);
            return Error.BackendUnavailable("The groupware server is unreachable.");
        }
        catch (Exception ex) when (ex is TimeoutException or ExecutionRejectedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, "timed out");
            return Error.BackendUnavailable("The groupware server timed out.");
        }
    }

    /// <summary>The Nextcloud uid of a user principal (<c>…/principals/users/{uid}/</c>); null for anything else.</summary>
    internal static string? UidOf(string principalHref)
    {
        var path = principalHref.TrimEnd('/');
        var index = path.LastIndexOf(UserPrincipalsSegment, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var uid = path[(index + UserPrincipalsSegment.Length)..];
        if (uid.Length == 0 || uid.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        // Sabre percent-encodes the segment; the avatar route takes the raw uid (re-escaped below).
        return Uri.UnescapeDataString(uid);
    }

    private Uri AvatarUri(string uid)
    {
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("Dav:BaseUrl is not configured.");
        var basePath = baseUrl.AbsolutePath.TrimEnd('/');
        return new Uri(baseUrl, $"{basePath}/index.php/avatar/{Uri.EscapeDataString(uid)}/{Size}");
    }

    private async Task<Result<BlobContent>> ReadAsync(HttpResponseMessage response, string uid, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.OK)
        {
            if (!IsCustom(response))
            {
                return NoPhoto();
            }

            if (response.Content.Headers.ContentLength > MaxPhotoBytes)
            {
                LogTooLarge(logger, uid);
                return NoPhoto();
            }

            var bytes = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                LogTooLarge(logger, uid);
                return NoPhoto();
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            return new BlobContent(type, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
        }

        var retryAfter = RetryAfterSeconds(response);
        Error error = status switch
        {
            HttpStatusCode.NotFound => NoPhoto(),
            HttpStatusCode.Forbidden => Error.Forbidden("The groupware server refused the photo."),
            HttpStatusCode.TooManyRequests => new Error(ErrorKind.Throttled, "activityLimitReached", "The groupware server is throttling requests.") { RetryAfterSeconds = retryAfter },
            _ => Error.BackendUnavailable("The groupware server could not serve the photo.", retryAfter),
        };
        LogFailed(logger, (int)status, error.Kind);
        return error;
    }

    private static bool IsCustom(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CustomAvatarHeader, out var values) && values.Any(static v => v.Trim() == "1");

    /// <summary>The body, or null when it exceeds <see cref="MaxPhotoBytes"/>.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPhotoBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private int? RetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        var delay = retryAfter.Delta ?? (retryAfter.Date - timeProvider.GetUtcNow());
        return delay is { } d ? (int)Math.Max(0, Math.Ceiling(d.TotalSeconds)) : null;
    }

    private static Error NoPhoto() => UserPhotos.None();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud avatar request failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud avatar endpoint unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud avatar of {Uid} exceeds the photo size cap; treated as no photo")]
    private static partial void LogTooLarge(ILogger logger, string uid);
}
