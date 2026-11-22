using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>
/// Phase 19: <c>POST messages</c> and <c>POST sendMail</c> in MIME form, <c>Content-Type: text/plain</c> with the message
/// base64-encoded. These are their own endpoints, chosen by the request's content type, so the JSON forms are untouched.
/// </summary>
internal static class MimeEndpoints
{
    private const string TextPlain = "text/plain";

    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/messages", CreateDraftAsync).Accepts<string>(TextPlain);
            scope.MapPost("/sendMail", SendAsync).Accepts<string>(TextPlain);
        }
    }

    private static async Task<IResult> CreateDraftAsync(Mailbox mailbox, HttpRequest request, CreateDraftFromMime useCase, CancellationToken cancellationToken)
    {
        var mime = await DecodeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!mime.IsSuccess)
        {
            return GraphResults.Error(mime.Error);
        }

        var draft = await useCase.ExecuteAsync(mailbox.Ref, mime.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(draft, m => GraphResults.Created(MessageDtoMapper.ToDto(m), $"{mailbox.ContextSegment}/messages"));
    }

    private static async Task<IResult> SendAsync(Mailbox mailbox, HttpRequest request, SendMime useCase, CancellationToken cancellationToken)
    {
        var mime = await DecodeAsync(request, cancellationToken).ConfigureAwait(false);
        return mime.IsSuccess
            ? GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, mime.Value, cancellationToken).ConfigureAwait(false), GraphResults.Accepted)
            : GraphResults.Error(mime.Error);
    }

    /// <summary>
    /// The body's base64, decoded (line breaks and surrounding white space allowed, as base64 MIME tools write it). Read to
    /// a little past what 4 MB encodes, so an oversize body is a 413 from the use case without buffering all of it.
    /// </summary>
    internal static async Task<Result<byte[]>> DecodeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var limit = ((CreateDraftFromMime.MaxBytes + 2) / 3 * 4) + (CreateDraftFromMime.MaxBytes / 38) + 16;
        var text = new StringBuilder();
        using (var reader = new StreamReader(request.Body, Encoding.ASCII))
        {
            var buffer = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                text.Append(buffer, 0, read);
                if (text.Length > limit)
                {
                    return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "The MIME content is larger than 4 MB.");
                }
            }
        }

        var compact = new string(text.ToString().Where(static c => !char.IsWhiteSpace(c)).ToArray());
        try
        {
            return compact.Length == 0 ? InvalidBase64() : Convert.FromBase64String(compact);
        }
        catch (FormatException)
        {
            return InvalidBase64();
        }
    }

    /// <summary>Graph's answer for a body that isn't base64 (user-post-messages, user-sendmail).</summary>
    private static Error InvalidBase64() => new(ErrorKind.Validation, "ErrorMimeContentInvalidBase64String", "Invalid base64 string for MIME content.");
}
