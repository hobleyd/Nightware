using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// Requests to Nextcloud outside the DAV root, as the caller: the preview renderer (<c>index.php/core/preview</c>) and the
/// activity stream (<c>ocs/v2.php/apps/activity/…</c>), whose answers (an image; 304 and paging headers) <see cref="IDavClient"/>
/// does not surface. Same pipeline as the DAV client: resilience, then the caller's credential; no cookies, no redirects.
/// </summary>
internal sealed partial class NextcloudAppRequests(
    HttpClient http,
    IOptions<DavOptions> options,
    IBackendCredentials credentials,
    ILogger<NextcloudAppRequests> logger)
{
    /// <summary>
    /// Sends a GET to <paramref name="pathAndQuery"/> below Nextcloud's base URL (no leading slash). Any HTTP status comes
    /// back as a response for the caller to read and dispose; only an unreachable server or a credential failure is an error.
    /// </summary>
    public async Task<Result<HttpResponseMessage>> GetAsync(string pathAndQuery, bool ocs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        var credential = await credentials.ForAsync(Backend.Groupware, cancellationToken).ConfigureAwait(false);
        var allowed = credential.IsSuccess ? DavCredentialPolicy.Check(credential.Value) : credential;
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("Dav:BaseUrl is not configured.");
        var uri = new Uri(baseUrl, $"{baseUrl.AbsolutePath.TrimEnd('/')}/{pathAndQuery}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Options.Set(DavRequestOptions.Credential, allowed.Value);
        if (ocs)
        {
            request.Headers.Add("OCS-APIRequest", "true");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud unreachable outside the DAV root: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
