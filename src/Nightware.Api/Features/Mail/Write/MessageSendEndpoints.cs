using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Features.Mail.Write.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>Sending: a draft, a new message, and the reply/forward actions (sent at once, or saved as drafts).</summary>
internal static class MessageSendEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            foreach (var messages in ItemRoutes.Message)
            {
                scope.MapPost(messages + "/send", SendDraftAsync);
                scope.MapPost(messages + "/reply", (Mailbox m, string messageId, [FromBody] ReplyRequestDto? b, ReplyToMessage u, CancellationToken ct) =>
                    ReplyAsync(m, messageId, b, u, replyAll: false, ct));
                scope.MapPost(messages + "/replyAll", (Mailbox m, string messageId, [FromBody] ReplyRequestDto? b, ReplyToMessage u, CancellationToken ct) =>
                    ReplyAsync(m, messageId, b, u, replyAll: true, ct));
                scope.MapPost(messages + "/forward", ForwardAsync);
                scope.MapPost(messages + "/createReply", (Mailbox m, string messageId, [FromBody] ReplyRequestDto? b, CreateReplyDraft u, CancellationToken ct) =>
                    CreateReplyAsync(m, messageId, b, u, replyAll: false, ct));
                scope.MapPost(messages + "/createReplyAll", (Mailbox m, string messageId, [FromBody] ReplyRequestDto? b, CreateReplyDraft u, CancellationToken ct) =>
                    CreateReplyAsync(m, messageId, b, u, replyAll: true, ct));
                scope.MapPost(messages + "/createForward", CreateForwardAsync);
            }
            scope.MapPost("/sendMail", SendMailAsync);
        }
    }

    private static async Task<IResult> SendDraftAsync(Mailbox mailbox, string messageId, SendDraft useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken), GraphResults.Accepted);

    private static async Task<IResult> SendMailAsync(Mailbox mailbox, [FromBody] SendMailRequestDto? body, SendMail useCase, CancellationToken cancellationToken)
    {
        var content = MailWriteRequestMapper.ToContent(body?.Message);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var sent = await useCase.ExecuteAsync(mailbox.Ref, content.Value, body!.SaveToSentItems ?? true, cancellationToken);
        return GraphResults.Match(sent, GraphResults.Accepted);
    }

    private static async Task<IResult> ReplyAsync(Mailbox mailbox, string messageId, ReplyRequestDto? body, ReplyToMessage useCase, bool replyAll, CancellationToken cancellationToken)
    {
        var composed = MailWriteRequestMapper.ToComposed(body);
        if (!composed.IsSuccess)
        {
            return GraphResults.Error(composed.Error);
        }

        var sent = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), replyAll, composed.Value, cancellationToken);
        return GraphResults.Match(sent, GraphResults.Accepted);
    }

    private static async Task<IResult> ForwardAsync(Mailbox mailbox, string messageId, [FromBody] ReplyRequestDto? body, ForwardMessage useCase, CancellationToken cancellationToken)
    {
        var composed = MailWriteRequestMapper.ToComposed(body);
        if (!composed.IsSuccess)
        {
            return GraphResults.Error(composed.Error);
        }

        var sent = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), composed.Value, cancellationToken);
        return GraphResults.Match(sent, GraphResults.Accepted);
    }

    private static async Task<IResult> CreateReplyAsync(Mailbox mailbox, string messageId, ReplyRequestDto? body, CreateReplyDraft useCase, bool replyAll, CancellationToken cancellationToken)
    {
        var composed = MailWriteRequestMapper.ToComposed(body);
        if (!composed.IsSuccess)
        {
            return GraphResults.Error(composed.Error);
        }

        var draft = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), replyAll, composed.Value, cancellationToken);
        return GraphResults.Match(draft, m => GraphResults.Created(MessageDtoMapper.ToDto(m), $"{mailbox.ContextSegment}/messages"));
    }

    private static async Task<IResult> CreateForwardAsync(Mailbox mailbox, string messageId, [FromBody] ReplyRequestDto? body, CreateForwardDraft useCase, CancellationToken cancellationToken)
    {
        var composed = MailWriteRequestMapper.ToComposed(body);
        if (!composed.IsSuccess)
        {
            return GraphResults.Error(composed.Error);
        }

        var draft = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), composed.Value, cancellationToken);
        return GraphResults.Match(draft, m => GraphResults.Created(MessageDtoMapper.ToDto(m), $"{mailbox.ContextSegment}/messages"));
    }
}
