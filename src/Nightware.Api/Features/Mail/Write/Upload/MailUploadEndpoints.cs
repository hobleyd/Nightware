using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Nightware.Api.Auth;
using Nightware.Api.Features.Tasks;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write.Upload;

/// <summary>Large draft attachments in chunks (ADR 0006 D5, phase 12).</summary>
internal static class MailUploadEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/messages/{messageId}/attachments/createUploadSession", BeginAsync);
        }

        // As for To Do uploads: no bearer, only the sealed URL, which always names the mailbox by address.
        graph.MapPut(MailUploadUrl.RoutePattern, UploadAsync).AuthorisedBy(MailUploadUrl.Kind);
    }

    private static async Task<IResult> BeginAsync(
        Mailbox mailbox, string messageId, JsonObject? body, BeginDraftAttachmentUpload useCase, IUserContext caller,
        GraphLinkBuilder links, HttpRequest request, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var file = AttachmentItemReader.Read(body);
        if (!file.IsSuccess)
        {
            return GraphResults.Error(file.Error);
        }

        var session = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), file.Value, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return GraphResults.Error(session.Error);
        }

        var ticket = sealer.TicketFor(MailUploadUrl.Kind, caller.BearerToken, request.HttpContext.User);
        var binding = new MailUploadUrlBinding(mailbox.Ref.Address, messageId, session.Value.SessionId);
        var uploadUrl = MailUploadUrl.Build(links.VersionRoot(request), binding, sealer.Seal(MailUploadUrl.Kind, ticket, binding.BoundTo));
        if (!SelfAuthorisingUrl.FitsRequestLine(uploadUrl))
        {
            return GraphResults.Error(new Error(ErrorKind.Unknown, "generalException", "The upload URL would exceed the request-line limit."));
        }

        return GraphResults.Created(new MailUploadSessionDto { UploadUrl = uploadUrl, ExpirationDateTime = ticket.ExpiresAt }, "microsoft.graph.uploadSession");
    }

    /// <remarks>Binds no <see cref="Mailbox"/>: the address in the path is the one the sealed ticket is bound to.</remarks>
    private static async Task<IResult> UploadAsync(
        string user, string messageId, string sessionId, HttpRequest request, GraphLinkBuilder links, UploadDraftAttachmentChunk useCase, CancellationToken cancellationToken)
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
            new MailboxRef(user.ToLowerInvariant()), new MessageId(messageId), sessionId,
            new ContentRangeHeader(bytes.First, bytes.Last, bytes.Total), request.Body, cancellationToken).ConfigureAwait(false);
        if (!progress.IsSuccess)
        {
            return GraphResults.Error(progress.Error);
        }

        if (progress.Value.Completed is { } attachment)
        {
            // As Exchange answers the last chunk: 201, the attachment's location, no body.
            var location = $"{links.VersionRoot(request)}/users('{Uri.EscapeDataString(user)}')/messages('{Uri.EscapeDataString(messageId)}')/attachments('{Uri.EscapeDataString(attachment.Id.Value)}')";
            return Results.Created(location, null);
        }

        return GraphResults.Entity(
            new MailUploadSessionDto { ExpirationDateTime = ticket.ExpiresAt, NextExpectedRanges = [progress.Value.NextExpected.ToString(System.Globalization.CultureInfo.InvariantCulture)] },
            "microsoft.graph.uploadSession");
    }
}
