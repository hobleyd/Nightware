using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// <see cref="INotificationEndpoint"/> over the guarded HTTP client (<see cref="NotificationAddressGuard"/>): HTTPS only,
/// no redirects, Graph's 10-second limit. Nothing sent carries a bearer token.
/// </summary>
internal sealed partial class WebhookNotificationEndpoint(IHttpClientFactory clients, IOptions<SubscriptionOptions> options, ILogger<WebhookNotificationEndpoint> logger)
    : INotificationEndpoint
{
    public const string ClientName = "Nightware.Subscriptions.Webhook";

    public const string ValidationFailed = "Subscription validation request failed. Notification endpoint must respond with 200 OK to validation request.";

    public async Task<Result> ValidateAsync(Uri notificationUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notificationUrl);
        if (Refused(notificationUrl) is { } refused)
        {
            return refused;
        }

        var token = $"Validation: Testing client application reachability for subscription Request-Id: {Guid.NewGuid()}";
        var separator = string.IsNullOrEmpty(notificationUrl.Query) ? "?" : "&";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{notificationUrl.AbsoluteUri}{separator}validationToken={Uri.EscapeDataString(token)}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.EndpointTimeout);
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK && body.Trim() == token)
            {
                return Result.Success();
            }

            LogValidationRejected(logger, notificationUrl.Host, (int)response.StatusCode);
        }
        catch (HttpRequestException ex) when (NotificationAddressGuard.IsRefusal(ex))
        {
            return Error.Validation("notificationUrl must resolve to a public address.", "notificationUrl");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogValidationUnreachable(logger, notificationUrl.Host, ex);
        }

        return Error.Validation(ValidationFailed, "notificationUrl");
    }

    private Error? Refused(Uri url)
    {
        var exempt = options.Value.TestOnlyAllowedOrigins.Contains(url.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
        return url.Scheme == Uri.UriSchemeHttps || exempt ? null : Error.Validation("notificationUrl must be an HTTPS URL.", "notificationUrl");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription validation at {Host} answered {Status} or a wrong token")]
    private static partial void LogValidationRejected(ILogger logger, string host, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription validation at {Host} failed")]
    private static partial void LogValidationUnreachable(ILogger logger, string host, Exception exception);
}
