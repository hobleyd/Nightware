using Nightware.Domain.Kernel;

namespace Nightware.Domain.Mail;

/// <summary>A partial update to a message. Null members are left unchanged.</summary>
public sealed record MessagePatch
{
    public bool? IsRead { get; init; }

    public FollowUpFlag? Flag { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    public Importance? Importance { get; init; }
}

/// <summary>The full content of a draft or an outgoing message.</summary>
public sealed record DraftContent
{
    public string? Subject { get; init; }

    public ItemBody? Body { get; init; }

    public EmailAddress? From { get; init; }

    public IReadOnlyList<EmailAddress> ToRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> CcRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> BccRecipients { get; init; } = [];

    public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];

    public Importance Importance { get; init; } = Importance.Normal;

    /// <summary>Message-ID being replied to (sets <c>In-Reply-To</c>).</summary>
    public string? InReplyTo { get; init; }

    public IReadOnlyList<string> References { get; init; } = [];

    public IReadOnlyList<NewAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// A message whose attachments (inline ones included) are carried over as they are, without
    /// downloading and re-uploading them: Graph <c>forward</c>/<c>createForward</c> copy the original's files.
    /// </summary>
    public MessageId? AttachmentsFrom { get; init; }

    public IReadOnlyList<InternetHeader> ExtraHeaders { get; init; } = [];
}

/// <summary>Someone the mailbox owner recently exchanged mail with (feeds <c>/people</c>).</summary>
public sealed record Correspondent(EmailAddress Address, int MessageCount, DateTimeOffset LastContact);
