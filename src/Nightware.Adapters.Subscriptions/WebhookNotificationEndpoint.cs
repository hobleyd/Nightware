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

    /// <summary>
    /// A <c>changeNotificationCollection</c> as Graph writes one (capture <c>subscriptions.notification</c>), without
    /// <c>tenantId</c> (Nightware has no tenants) or an etag in <c>resourceData</c>.
    /// </summary>
    public async Task<Result> DeliverAsync(Uri notificationUrl, IReadOnlyList<ChangeNotification> notifications, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notificationUrl);
        ArgumentNullException.ThrowIfNull(notifications);
        if (Refused(notificationUrl) is { } refused)
        {
            return refused;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, notificationUrl)
        {
            Content = new StringContent(Serialize(notifications), Encoding.UTF8, "application/json"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.EndpointTimeout);
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            LogDeliveryRejected(logger, notificationUrl.Host, (int)response.StatusCode);
            return Error.BackendUnavailable($"The notification endpoint answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogDeliveryFailed(logger, notificationUrl.Host, ex);
            return Error.BackendUnavailable("The notification endpoint could not be reached.");
        }
    }

    internal static string Serialize(IReadOnlyList<ChangeNotification> notifications)
    {
        var value = new System.Text.Json.Nodes.JsonArray();
        foreach (var n in notifications)
        {
            var item = new System.Text.Json.Nodes.JsonObject
            {
                ["subscriptionId"] = n.SubscriptionId,
                ["subscriptionExpirationDateTime"] = n.SubscriptionExpirationDateTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
                ["changeType"] = n.ChangeType,
                ["resource"] = n.Resource,
                ["resourceData"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["@odata.type"] = n.ODataType,
                    ["@odata.id"] = n.Resource,
                    ["id"] = n.Id,
                },
            };
            if (n.ClientState is not null)
            {
                item["clientState"] = n.ClientState;
            }

            value.Add(item);
        }

        return new System.Text.Json.Nodes.JsonObject { ["value"] = value }.ToJsonString();
    }

    private Error? Refused(Uri url)
    {
        var exempt = options.Value.TestOnlyAllowedOrigins.Contains(url.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
        return url.Scheme == Uri.UriSchemeHttps || exempt ? null : Error.Validation("notificationUrl must be an HTTPS URL.", "notificationUrl");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Change notification to {Host} answered {Status}")]
    private static partial void LogDeliveryRejected(ILogger logger, string host, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Change notification to {Host} failed")]
    private static partial void LogDeliveryFailed(ILogger logger, string host, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription validation at {Host} answered {Status} or a wrong token")]
    private static partial void LogValidationRejected(ILogger logger, string host, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription validation at {Host} failed")]
    private static partial void LogValidationUnreachable(ILogger logger, string host, Exception exception);
}
