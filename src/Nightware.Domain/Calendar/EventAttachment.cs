using Nightware.Domain.Kernel;

namespace Nightware.Domain.Calendar;

/// <summary>Graph <c>fileAttachment</c> on an event.</summary>
public sealed record EventAttachment(AttachmentId Id, string Name, string ContentType, long Size)
{
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>Set only when the content was asked for.</summary>
    public ReadOnlyMemory<byte>? Content { get; init; }
}
