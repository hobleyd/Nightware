using Nightware.Api.Features.Mail.Dto;

namespace Nightware.Api.Features.Mail.Write.Dto;

// Request bodies of the mail write actions. Messages and attachments themselves bind to the shared
// MessageDto/AttachmentDto (Features/Mail/Dto), which carry the input-only members for this purpose.

/// <summary><c>POST sendMail</c>.</summary>
public sealed record SendMailRequestDto
{
    public MessageDto? Message { get; init; }

    /// <summary>Graph's default is true; NightMail always sends true.</summary>
    public bool? SaveToSentItems { get; init; }
}

/// <summary>
/// <c>reply</c>, <c>replyAll</c>, <c>forward</c> and their <c>create*</c> forms. NightMail sends the whole
/// composed body in <c>message</c> and no <c>comment</c>; a forward's <c>toRecipients</c> is top-level.
/// </summary>
public sealed record ReplyRequestDto
{
    public MessageDto? Message { get; init; }

    public string? Comment { get; init; }

    public IReadOnlyList<RecipientDto>? ToRecipients { get; init; }
}

/// <summary><c>POST messages/{id}/move</c> and <c>POST mailFolders/{id}/move</c>: a folder id or well-known name.</summary>
public sealed record MoveRequestDto
{
    public string? DestinationId { get; init; }
}

/// <summary><c>POST mailFolders/{id}/childFolders</c> and <c>PATCH mailFolders/{id}</c>.</summary>
public sealed record MailFolderRequestDto
{
    public string? DisplayName { get; init; }

    /// <summary>Only on <c>POST mailFolders</c>; true is refused (see FolderWriteEndpoints).</summary>
    public bool? IsHidden { get; init; }
}
