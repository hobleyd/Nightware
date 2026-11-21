using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Graph;
using Nightware.Application.Common.Query;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Subscriptions;

namespace Nightware.Api.Features.Subscriptions;

/// <summary>Graph <c>subscription</c>, in Graph's member order. Members Nightware does not support are always null.</summary>
public sealed record SubscriptionDto
{
    public string? Id { get; init; }

    public string? Resource { get; init; }

    public string? ApplicationId { get; init; }

    public string? ChangeType { get; init; }

    public string? ClientState { get; init; }

    public string? NotificationUrl { get; init; }

    public string? NotificationQueryOptions { get; init; }

    public string? LifecycleNotificationUrl { get; init; }

    public DateTimeOffset? ExpirationDateTime { get; init; }

    public string? CreatorId { get; init; }

    public bool? IncludeResourceData { get; init; }

    public string? LatestSupportedTlsVersion { get; init; }

    public string? EncryptionCertificate { get; init; }

    public string? EncryptionCertificateId { get; init; }

    public string? NotificationUrlAppId { get; init; }

    public string? VapidPublicKey { get; init; }

    public string? WebPushEncryptionP256dhPublicKey { get; init; }

    public string? WebPushEncryptionSecret { get; init; }

    /// <param name="listing">Graph leaves <c>clientState</c> out of a listing.</param>
    public static SubscriptionDto From(Subscription subscription, bool listing = false)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new SubscriptionDto
        {
            Id = subscription.Id,
            Resource = subscription.Resource,
            ApplicationId = subscription.ApplicationId,
            ChangeType = ChangeTypeText(subscription.ChangeType),
            ClientState = listing ? null : subscription.ClientState,
            NotificationUrl = subscription.NotificationUrl.OriginalString,
            LifecycleNotificationUrl = subscription.LifecycleNotificationUrl?.OriginalString,
            ExpirationDateTime = subscription.ExpirationDateTime,
            CreatorId = subscription.CreatorId,
        };
    }

    internal static string ChangeTypeText(SubscriptionChangeType type) =>
        string.Join(',', new[] { SubscriptionChangeType.Created, SubscriptionChangeType.Updated, SubscriptionChangeType.Deleted }
            .Where(t => type.HasFlag(t))
            .Select(t => t.ToString().ToLowerInvariant()));
}

/// <summary>The body of a subscription create or PATCH.</summary>
public sealed record SubscriptionWriteDto
{
    public string? Resource { get; init; }

    public string? ChangeType { get; init; }

    public string? NotificationUrl { get; init; }

    public string? LifecycleNotificationUrl { get; init; }

    public DateTimeOffset? ExpirationDateTime { get; init; }

    public string? ClientState { get; init; }

    public bool? IncludeResourceData { get; init; }
}

/// <summary><c>/subscriptions</c> (ADR 0006 D10): change-notification subscriptions, per caller.</summary>
public static class SubscriptionsEndpoints
{
    private const string Context = "subscriptions";

    public static RouteGroupBuilder MapSubscriptionsEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.MapGet("/subscriptions", ListAsync);
        graph.MapPost("/subscriptions", CreateAsync);
        graph.MapGet("/subscriptions/{id}", GetAsync);
        graph.MapPatch("/subscriptions/{id}", UpdateAsync);
        graph.MapDelete("/subscriptions/{id}", DeleteAsync);
        return graph;
    }

    private static async Task<IResult> ListAsync(SubscriptionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ListAsync(cancellationToken).ConfigureAwait(false),
            list => GraphResults.Collection(list.Select(s => SubscriptionDto.From(s, listing: true)), Context, ItemQuery.Default));

    private static async Task<IResult> CreateAsync(HttpContext http, SubscriptionWriteDto? body, SubscriptionsUseCase useCase, CancellationToken cancellationToken)
    {
        var request = new SubscriptionRequest(body?.Resource, body?.ChangeType, body?.NotificationUrl, body?.ExpirationDateTime)
        {
            ClientState = body?.ClientState,
            LifecycleNotificationUrl = body?.LifecycleNotificationUrl,
            IncludeResourceData = body?.IncludeResourceData == true,
            ApplicationId = http.User.FindFirst("azp")?.Value,
        };
        return GraphResults.Match(
            await useCase.CreateAsync(request, cancellationToken).ConfigureAwait(false),
            s => GraphResults.CreatedAt(SubscriptionDto.From(s), Context, $"subscriptions('{Uri.EscapeDataString(s.Id)}')"));
    }

    private static async Task<IResult> GetAsync(string id, SubscriptionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.GetAsync(id, cancellationToken).ConfigureAwait(false), s => GraphResults.Entity(SubscriptionDto.From(s), Context));

    private static async Task<IResult> UpdateAsync(string id, SubscriptionWriteDto? body, SubscriptionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.UpdateAsync(id, new SubscriptionPatch(body?.ExpirationDateTime, body?.NotificationUrl), cancellationToken).ConfigureAwait(false),
            s => GraphResults.Entity(SubscriptionDto.From(s), Context));

    private static async Task<IResult> DeleteAsync(string id, SubscriptionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.DeleteAsync(id, cancellationToken).ConfigureAwait(false), GraphResults.NoContent);
}
