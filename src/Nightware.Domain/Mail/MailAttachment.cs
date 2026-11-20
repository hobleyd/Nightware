using Nightware.Domain.Kernel;

namespace Nightware.Domain.Mail;

public enum AttachmentKind
{
    /// <summary>Graph <c>fileAttachment</c>.</summary>
    File,
    /// <summary>Graph <c>itemAttachment</c> (an attached message, <c>message/rfc822</c>).</summary>
    Item,
}

public sealed record MailAttachment(AttachmentId Id, string Name, string ContentType, long Size, AttachmentKind Kind)
{
    public bool IsInline { get; init; }

    public string? ContentId { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>Inline bytes, only populated when the request asked for <c>contentBytes</c>.</summary>
    public ReadOnlyMemory<byte>? ContentBytes { get; init; }
}

/// <summary>An attachment being added to a draft.</summary>
public sealed record NewAttachment(string Name, string ContentType, ReadOnlyMemory<byte> Content)
{
    public bool IsInline { get; init; }

    public string? ContentId { get; init; }
}

/// <summary>The file a draft attachment upload session was opened for (Graph <c>attachmentItem</c>).</summary>
public sealed record DraftUploadFile(string Name, string ContentType, long Size)
{
    public bool IsInline { get; init; }

    public string? ContentId { get; init; }
}

/// <summary>An open draft attachment upload: <see cref="Received"/> bytes of <see cref="File"/> have arrived.</summary>
public sealed record DraftUploadState(string SessionId, DraftUploadFile File, long Received)
{
    public bool IsComplete => Received >= File.Size;
}

/// <summary>Where an upload stands after a chunk: the next byte wanted, or the attachment it became.</summary>
public sealed record DraftUploadProgress(long NextExpected, MailAttachment? Completed);
