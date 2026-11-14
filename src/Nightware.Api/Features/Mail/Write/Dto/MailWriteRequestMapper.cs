using System.Text.Json;
using System.Text.Json.Nodes;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Write.Dto;

/// <summary>Graph request bodies → Application inputs. Malformed input is a 400 naming the property.</summary>
public static class MailWriteRequestMapper
{
    private static readonly string[] DraftProperties = ["subject", "body", "toRecipients", "ccRecipients", "bccRecipients"];

    /// <summary>A message body (draft, sendMail, migration) as <see cref="DraftContent"/>.</summary>
    public static Result<DraftContent> ToContent(MessageDto? message)
    {
        if (message is null)
        {
            return Error.Validation("The request has no message.", "message");
        }

        var body = ToBody(message.Body);
        var from = message.From is null ? Result<EmailAddress?>.Success(null) : ToAddress(message.From, "from").Map<EmailAddress?>(a => a);
        var to = ToAddresses(message.ToRecipients, "toRecipients");
        var cc = ToAddresses(message.CcRecipients, "ccRecipients");
        var bcc = ToAddresses(message.BccRecipients, "bccRecipients");
        var replyTo = ToAddresses(message.ReplyTo, "replyTo");
        var importance = ToImportance(message.Importance);
        var attachments = ToAttachments(message.Attachments);
        if (FirstError(body.Error, from.Error, to.Error, cc.Error, bcc.Error, replyTo.Error, importance.Error, attachments.Error) is { } error)
        {
            return error;
        }

        return new DraftContent
        {
            Subject = message.Subject,
            Body = body.Value,
            From = from.Value,
            ToRecipients = to.Value,
            CcRecipients = cc.Value,
            BccRecipients = bcc.Value,
            ReplyTo = replyTo.Value,
            Importance = importance.Value ?? Importance.Normal,
            Attachments = attachments.Value,
        };
    }

    /// <summary>
    /// What a reply/forward body composed. A <c>comment</c> without a <c>message.body</c> becomes the text
    /// body (Graph would quote the original under it; NightMail never relies on that).
    /// </summary>
    public static Result<ComposedMessage> ToComposed(ReplyRequestDto? request)
    {
        var message = request?.Message ?? new MessageDto();
        var body = ToBody(message.Body);
        var to = ToAddresses(request?.ToRecipients is { Count: > 0 } top ? top : message.ToRecipients, "toRecipients");
        var cc = ToAddresses(message.CcRecipients, "ccRecipients");
        var bcc = ToAddresses(message.BccRecipients, "bccRecipients");
        if (FirstError(body.Error, to.Error, cc.Error, bcc.Error) is { } error)
        {
            return error;
        }

        return new ComposedMessage
        {
            Subject = message.Subject,
            Body = body.Value ?? (request?.Comment is { } comment ? new ItemBody(BodyType.Text, comment) : null),
            ToRecipients = to.Value,
            CcRecipients = cc.Value,
            BccRecipients = bcc.Value,
        };
    }

    /// <summary>
    /// A <c>PATCH messages/{id}</c> body. Any draft field makes it a draft update (with importance riding
    /// along); otherwise it patches message properties. Fields absent from the body stay unchanged.
    /// </summary>
    public static Result<MessageUpdate> ToUpdate(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("The request has no body.");
        }

        MessageDto message;
        try
        {
            message = body.Deserialize<MessageDto>(GraphJson.Options) ?? new MessageDto();
        }
        catch (JsonException ex)
        {
            return Error.Validation($"The message is malformed: {ex.Message}");
        }

        var importance = ToImportance(message.Importance);
        if (!importance.IsSuccess)
        {
            return importance.Error;
        }

        if (DraftProperties.Any(p => Has(body, p)))
        {
            var draft = ToDraftUpdate(body, message, importance.Value);
            return draft.Map(d => new MessageUpdate(null, d));
        }

        var flag = message.Flag is null ? Result<FollowUpFlag?>.Success(null) : ToFlag(message.Flag).Map<FollowUpFlag?>(f => f);
        if (!flag.IsSuccess)
        {
            return flag.Error;
        }

        return new MessageUpdate(
            new MessagePatch { IsRead = message.IsRead, Flag = flag.Value, Categories = message.Categories, Importance = importance.Value },
            null);
    }

    /// <summary>A Graph <c>fileAttachment</c> to add. Item and reference attachments are not supported.</summary>
    public static Result<NewAttachment> ToAttachment(AttachmentDto? attachment)
    {
        if (attachment is null)
        {
            return Error.Validation("The request has no attachment.");
        }

        if (attachment.ODataType is { } type && !string.Equals(type, AttachmentDto.FileAttachmentType, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unsupported($"Only file attachments can be added ('{type}').", "@odata.type");
        }

        if (string.IsNullOrWhiteSpace(attachment.Name))
        {
            return Error.Validation("An attachment needs a name.", "name");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(attachment.ContentBytes ?? string.Empty);
        }
        catch (FormatException)
        {
            return Error.Validation("contentBytes is not valid base64.", "contentBytes");
        }

        return new NewAttachment(attachment.Name, attachment.ContentType ?? "application/octet-stream", content)
        {
            IsInline = attachment.IsInline == true,
            ContentId = attachment.ContentId,
        };
    }

    public static Result<ItemBody?> ToBody(ItemBodyDto? body)
    {
        if (body is null)
        {
            return Result<ItemBody?>.Success(null);
        }

        // Graph's itemBody defaults to text; NightMail sends "Html"/"Text" on drafts and "html"/"text" elsewhere.
        BodyType? type = body.ContentType?.Trim().ToUpperInvariant() switch
        {
            null or "" or "TEXT" => BodyType.Text,
            "HTML" => BodyType.Html,
            _ => null,
        };
        return type is { } t
            ? new ItemBody(t, body.Content ?? string.Empty)
            : Error.Validation($"'{body.ContentType}' is not a body content type (html or text).", "body.contentType");
    }

    private static Result<DraftUpdate> ToDraftUpdate(JsonObject body, MessageDto message, Importance? importance)
    {
        var content = ToBody(message.Body);
        var to = Present(body, "toRecipients", message.ToRecipients);
        var cc = Present(body, "ccRecipients", message.CcRecipients);
        var bcc = Present(body, "bccRecipients", message.BccRecipients);
        if (FirstError(content.Error, to.Error, cc.Error, bcc.Error) is { } error)
        {
            return error;
        }

        return new DraftUpdate
        {
            // An explicit null clears; an absent property keeps the draft's value.
            Subject = Has(body, "subject") ? message.Subject ?? string.Empty : null,
            Body = Has(body, "body") ? content.Value ?? new ItemBody(BodyType.Text, string.Empty) : null,
            ToRecipients = to.Value,
            CcRecipients = cc.Value,
            BccRecipients = bcc.Value,
            Importance = importance,
        };
    }

    private static Result<IReadOnlyList<EmailAddress>?> Present(JsonObject body, string property, IReadOnlyList<RecipientDto>? recipients) =>
        Has(body, property)
            ? ToAddresses(recipients, property).Map<IReadOnlyList<EmailAddress>?>(a => a)
            : Result<IReadOnlyList<EmailAddress>?>.Success(null);

    private static Result<IReadOnlyList<EmailAddress>> ToAddresses(IReadOnlyList<RecipientDto>? recipients, string property)
    {
        var addresses = new List<EmailAddress>();
        foreach (var recipient in recipients ?? [])
        {
            var address = ToAddress(recipient, property);
            if (!address.IsSuccess)
            {
                return address.Error;
            }

            addresses.Add(address.Value);
        }

        return addresses;
    }

    private static Result<EmailAddress> ToAddress(RecipientDto recipient, string property) =>
        recipient.EmailAddress?.Address is { } address && !string.IsNullOrWhiteSpace(address)
            ? new EmailAddress(address.Trim(), string.IsNullOrWhiteSpace(recipient.EmailAddress.Name) ? null : recipient.EmailAddress.Name)
            : Error.Validation($"A recipient in {property} has no address.", property);

    private static Result<Importance?> ToImportance(string? importance) => importance?.Trim().ToUpperInvariant() switch
    {
        null => Result<Importance?>.Success(null),
        "LOW" => Importance.Low,
        "NORMAL" => Importance.Normal,
        "HIGH" => Importance.High,
        _ => Error.Validation($"'{importance}' is not an importance (low, normal or high).", "importance"),
    };

    private static Result<FollowUpFlag> ToFlag(FollowupFlagDto flag) => flag.FlagStatus?.Trim().ToUpperInvariant() switch
    {
        "NOTFLAGGED" => FollowUpFlag.NotFlagged,
        "FLAGGED" => new FollowUpFlag(FlagStatus.Flagged),
        "COMPLETE" => new FollowUpFlag(FlagStatus.Complete),
        _ => Error.Validation($"'{flag.FlagStatus}' is not a flag status.", "flag.flagStatus"),
    };

    private static Result<IReadOnlyList<NewAttachment>> ToAttachments(IReadOnlyList<AttachmentDto>? attachments)
    {
        var result = new List<NewAttachment>();
        foreach (var attachment in attachments ?? [])
        {
            var mapped = ToAttachment(attachment);
            if (!mapped.IsSuccess)
            {
                return mapped.Error;
            }

            result.Add(mapped.Value);
        }

        return result;
    }

    private static bool Has(JsonObject body, string property) =>
        body.Any(p => string.Equals(p.Key, property, StringComparison.OrdinalIgnoreCase));

    private static Error? FirstError(params ReadOnlySpan<Error?> errors)
    {
        foreach (var error in errors)
        {
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }
}
