using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Directory;

/// <summary>
/// <see cref="IUserPhotoWriter"/> over Nextcloud's own avatar endpoint: <c>POST index.php/avatar/</c> (multipart
/// <c>files[]</c>) and <c>DELETE index.php/avatar/</c>, as the caller.
/// </summary>
/// <remarks>
/// <para>Verified on Nextcloud 35 (phase 19 Step 0): a bearer-token request passes the CSRF check only with
/// <c>OCS-APIRequest: true</c>. The endpoint sets the authenticated user's avatar, never another's, so
/// <c>owner</c> is the caller by construction (the use case checks it).</para>
/// <para>Nextcloud answers 200 for every outcome and tells them apart in the body: <c>{"status":"success"}</c>
/// stored it; <c>{"data":"notsquare",…}</c> stored nothing (its web UI crops in the browser); <c>{"data":{"message"}}</c>
/// is a refusal with a reason. Only the first is success.</para>
/// <para>Nextcloud keeps only a square JPEG or PNG, so <see cref="SquarePhoto"/> makes any other image one first
/// (phase 27).</para>
/// </remarks>
internal sealed partial class NextcloudAvatarWriter(
    HttpClient http,
    IBackendCredentials credentials,
    IOptions<DavOptions> options,
    ILogger<NextcloudAvatarWriter> logger) : IUserPhotoWriter
{
    public Task<Result> SetAsync(MailboxRef owner, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        var square = SquarePhoto.From(image);
        if (!square.IsSuccess)
        {
            return Task.FromResult<Result>(square.Error);
        }

        var (bytes, contentType) = square.Value;
        return SendAsync(HttpMethod.Post, () =>
        {
            var file = new ReadOnlyMemoryContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new MultipartFormDataContent { { file, "files[]", contentType == ImageInfo.Png ? "photo.png" : "photo.jpg" } };
        }, cancellationToken);
    }

    public Task<Result> DeleteAsync(MailboxRef owner, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, null, cancellationToken);

    private async Task<Result> SendAsync(HttpMethod method, Func<HttpContent>? content, CancellationToken cancellationToken)
    {
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

        using var request = new HttpRequestMessage(method, AvatarUri()) { Content = content?.Invoke() };
        request.Options.Set(DavRequestOptions.Credential, allowed.Value);
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Outcome(response.StatusCode, body, method == HttpMethod.Delete);
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

    /// <summary>Nextcloud's answer as a result: see the class remarks for the body shapes.</summary>
    internal static Result Outcome(HttpStatusCode status, string body, bool isDelete)
    {
        switch (status)
        {
            case HttpStatusCode.OK when isDelete:
                return Result.Success();
            case HttpStatusCode.OK or HttpStatusCode.BadRequest:
                break;
            case HttpStatusCode.Forbidden:
                return Error.Forbidden("The groupware server refused to change the photo.");
            case HttpStatusCode.TooManyRequests:
                return new Error(ErrorKind.Throttled, "activityLimitReached", "The groupware server is throttling requests.");
            default:
                // 401 included: the façade's credential problem, never the caller's (see NextcloudAvatarReader).
                return Error.BackendUnavailable("The groupware server could not change the photo.");
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "success")
            {
                return Result.Success();
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.String && data.GetString() == "notsquare")
                {
                    return Error.Validation("The photo must be square; crop it first.");
                }

                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return Error.Validation($"The groupware server refused the photo: {message.GetString()}");
                }
            }
        }
        catch (JsonException)
        {
            // An HTML page (a login redirect target, an error page) is not an outcome.
        }

        return Error.BackendUnavailable("The groupware server gave an unexpected answer to a photo change.");
    }

    private Uri AvatarUri()
    {
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("Dav:BaseUrl is not configured.");
        return new Uri(baseUrl, $"{baseUrl.AbsolutePath.TrimEnd('/')}/index.php/avatar/");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud avatar endpoint unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
