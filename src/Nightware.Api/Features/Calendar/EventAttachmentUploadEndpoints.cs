using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Nightware.Api.Auth;
using Nightware.Api.Features.Mail.Write.Upload;
using Nightware.Api.Features.Tasks;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using EventId = Nightware.Domain.Kernel.EventId;

namespace Nightware.Api.Features.Calendar;

/// <summary>What an event upload URL is bound to: the mailbox (case-insensitively), the event and the session.</summary>
internal sealed record EventUploadUrlBinding(string Mailbox, string EventId, string SessionId)
{
    public string[] BoundTo => [Mailbox.ToLowerInvariant(), EventId, SessionId];
}

/// <summary>
/// An event attachment upload URL: <c>{version root}/users/{mailbox}/events/{event}/attachmentSessions/{session}/content?authtoken=…</c>.
/// As the mail and To Do ones (ADR 0001 §39), the sealed credential is the only way in and is bound to the path.
/// </summary>
internal static class EventUploadUrl
{
    public const string RoutePattern = "/users/{user}/events/{eventId}/attachmentSessions/{sessionId}/content";

    public static readonly SelfAuthorisingUrl Kind = new("nightware/event-upload-url/v1", HttpMethods.Put, TimeSpan.FromMinutes(60), BindingOf, urlOnly: true);

    public static string Build(string versionRoot, EventUploadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var mailbox = Uri.EscapeDataString(binding.Mailbox).Replace("%40", "@", StringComparison.Ordinal);
        return $"{versionRoot}/users/{mailbox}/events/{Uri.EscapeDataString(binding.EventId)}"
            + $"/attachmentSessions/{Uri.EscapeDataString(binding.SessionId)}/content?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    private static string[]? BindingOf(HttpRequest request) =>
        SelfAuthorisingUrl.RouteValue(request, "user") is { } mailbox
        && SelfAuthorisingUrl.RouteValue(request, "eventId") is { } eventId
        && SelfAuthorisingUrl.RouteValue(request, "sessionId") is { } session
            ? new EventUploadUrlBinding(mailbox, eventId, session).BoundTo
            : null;
}

/// <summary>Phase 19: an event attachment's raw content, and large event attachments in chunks.</summary>
internal static class EventAttachmentUploadEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/events/{eventId}/attachments/{attachmentId}/$value", ContentAsync);
            scope.MapPost("/events/{eventId}/attachments/createUploadSession", BeginAsync);
        }

        // As for mail and To Do uploads: no bearer, only the sealed URL, which always names the mailbox by address.
        graph.MapPut(EventUploadUrl.RoutePattern, UploadAsync).AuthorisedBy(EventUploadUrl.Kind);
    }

    /// <summary>The file's bytes with its own content type (attachment-get: "based on its original content type").</summary>
    private static async Task<IResult> ContentAsync(
        Mailbox mailbox, string eventId, string attachmentId, EventAttachmentsUseCase useCase, CancellationToken cancellationToken)
    {
        var content = await useCase.GetContentAsync(mailbox.Ref, new EventId(eventId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType);
    }

    private static async Task<IResult> BeginAsync(
        Mailbox mailbox, string eventId, JsonObject? body, BeginEventAttachmentUpload useCase, IUserContext caller,
        GraphLinkBuilder links, HttpRequest request, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var file = AttachmentItemReader.Read(body);
        if (!file.IsSuccess)
        {
            return GraphResults.Error(file.Error);
        }

        if (file.Value.IsInline)
        {
            return GraphResults.Error(Error.Unsupported("Event attachments can't be inline.", "AttachmentItem.isInline"));
        }

        var session = await useCase.ExecuteAsync(mailbox.Ref, new EventId(eventId), file.Value.Name, file.Value.ContentType, file.Value.Size, cancellationToken)
            .ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return GraphResults.Error(session.Error);
        }

        var ticket = sealer.TicketFor(EventUploadUrl.Kind, caller.BearerToken, request.HttpContext.User, session.Value.ExpiresAt);
        var binding = new EventUploadUrlBinding(mailbox.Ref.Address, eventId, session.Value.SessionId);
        var uploadUrl = EventUploadUrl.Build(links.VersionRoot(request), binding, sealer.Seal(EventUploadUrl.Kind, ticket, binding.BoundTo));
        if (!SelfAuthorisingUrl.FitsRequestLine(uploadUrl))
        {
            return GraphResults.Error(new Error(ErrorKind.Unknown, "generalException", "The upload URL would exceed the request-line limit."));
        }

        return GraphResults.Created(new MailUploadSessionDto { UploadUrl = uploadUrl, ExpirationDateTime = ticket.ExpiresAt }, "microsoft.graph.uploadSession");
    }

    /// <remarks>Binds no <see cref="Mailbox"/>: the address in the path is the one the sealed ticket is bound to.</remarks>
    private static async Task<IResult> UploadAsync(
        string user, string eventId, string sessionId, HttpRequest request, GraphLinkBuilder links, UploadEventAttachmentChunk useCase, CancellationToken cancellationToken)
    {
        if (request.HttpContext.Features.Get<SealedTicket>() is not { } ticket)
        {
            return Results.Challenge();
        }

        var range = TasksEndpoints.ParseContentRange(request.Headers[HeaderNames.ContentRange].ToString());
        if (!range.IsSuccess)
        {
            return GraphResults.Error(range.Error);
        }

        if (range.Value is not { } bytes)
        {
            return GraphResults.Error(Error.Validation("Each chunk needs a Content-Range.", "Content-Range"));
        }

        var progress = await useCase.ExecuteAsync(
            new MailboxRef(user.ToLowerInvariant()), new EventId(eventId), sessionId,
            new ContentRangeHeader(bytes.First, bytes.Last, bytes.Total), request.Body, cancellationToken).ConfigureAwait(false);
        if (!progress.IsSuccess)
        {
            return GraphResults.Error(progress.Error);
        }

        if (progress.Value.Completed is { } attachment)
        {
            // As the mail upload's last chunk: 201, the attachment's location, no body.
            var location = $"{links.VersionRoot(request)}/users('{Uri.EscapeDataString(user)}')/events('{Uri.EscapeDataString(eventId)}')/attachments('{Uri.EscapeDataString(attachment.Id.Value)}')";
            return Results.Created(location, null);
        }

        return GraphResults.Entity(
            new MailUploadSessionDto { ExpirationDateTime = ticket.ExpiresAt, NextExpectedRanges = [progress.Value.NextExpected.ToString(CultureInfo.InvariantCulture)] },
            "microsoft.graph.uploadSession");
    }
}
