using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// Nextcloud's OCS API with the admin console's own account (ADR 0002 D8): HTTP Basic,
/// <c>OCS-APIRequest: true</c> (Nextcloud's CSRF exemption), <c>Accept: application/json</c> and
/// <c>?format=json</c>. A typed <see cref="HttpClient"/> of its own — not the per-user
/// <see cref="Application.Common.Identity.IBackendCredentials"/> path — with a cookie-free, redirect-free
/// handler (Nextcloud answers Basic with a session cookie) and retries for GET only.
/// </summary>
internal sealed partial class NextcloudProvisioningClient(
    HttpClient http,
    IOptions<NextcloudProvisioningOptions> options,
    ILogger<NextcloudProvisioningClient> logger)
{
    /// <summary>
    /// Sends one OCS request. The result fails only when Nextcloud could not be asked (unreachable, timed out);
    /// every answer, success or not, comes back as an <see cref="OcsReply"/> for the caller to interpret.
    /// </summary>
    /// <param name="path">Below the base URL, already escaped, e.g. <c>ocs/v1.php/cloud/users/alice</c>.</param>
    public async Task<Result<OcsReply>> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var version = path.StartsWith("ocs/v2.php/", StringComparison.Ordinal) ? OcsVersion.V2 : OcsVersion.V1;

        using var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = BasicCredential();
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reply = OcsReply.Parse(version, response.StatusCode, text, RetryAfterSeconds(response));
            if (!reply.Succeeded)
            {
                LogFailed(logger, method.Method, reply.HttpStatus, reply.IsEnvelope ? reply.StatusCode : null);
            }

            return reply;
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, method.Method, ex.Message);
            return Error.BackendUnavailable("Nextcloud is unreachable.");
        }
        catch (Exception ex) when (ex is TimeoutException or ExecutionRejectedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, method.Method, "timed out");
            return Error.BackendUnavailable("Nextcloud timed out.");
        }
    }

    internal Uri Url(string path)
    {
        var baseUrl = options.Value.BaseUrl ?? throw new InvalidOperationException("NextcloudProvisioning:BaseUrl is not configured.");
        return new Uri(baseUrl, $"{baseUrl.AbsolutePath.TrimEnd('/')}/{path.TrimStart('/')}?format=json");
    }

    private AuthenticationHeaderValue BasicCredential()
    {
        var value = options.Value;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{value.Username}:{value.Password}"));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private static int? RetryAfterSeconds(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Nextcloud provisioning {Method} failed: HTTP {Status}, OCS {OcsStatus}")]
    private static partial void LogFailed(ILogger logger, string method, int status, int? ocsStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud provisioning {Method} could not reach Nextcloud: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string method, string reason);
}
