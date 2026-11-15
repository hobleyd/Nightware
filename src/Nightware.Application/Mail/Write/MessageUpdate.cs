using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// The draft fields of a Graph <c>PATCH messages/{id}</c>. A null member was absent from the body and
/// keeps its current value (Graph PATCH semantics); NightMail always sends all five, but importance
/// never, which is why an absent field means a read of the draft rather than a reset.
/// </summary>
public sealed record DraftUpdate
{
    public string? Subject { get; init; }

    public ItemBody? Body { get; init; }

    public IReadOnlyList<EmailAddress>? ToRecipients { get; init; }

    public IReadOnlyList<EmailAddress>? CcRecipients { get; init; }

    public IReadOnlyList<EmailAddress>? BccRecipients { get; init; }

    public Importance? Importance { get; init; }

    /// <summary>The Graph property names that were absent and must be read from the draft.</summary>
    public IReadOnlySet<string> Missing()
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        Add(Subject is null, "subject");
        Add(Body is null, "body");
        Add(ToRecipients is null, "toRecipients");
        Add(CcRecipients is null, "ccRecipients");
        Add(BccRecipients is null, "bccRecipients");
        Add(Importance is null, "importance");
        return missing;

        void Add(bool absent, string property)
        {
            if (absent)
            {
                missing.Add(property);
            }
        }
    }

    /// <summary>The draft's new content: these fields over <paramref name="current"/> (null when nothing was missing).</summary>
    public DraftContent Over(MailMessage? current) => new()
    {
        Subject = Subject ?? current?.Subject,
        Body = Body ?? current?.Body,
        ToRecipients = ToRecipients ?? current?.ToRecipients ?? [],
        CcRecipients = CcRecipients ?? current?.CcRecipients ?? [],
        BccRecipients = BccRecipients ?? current?.BccRecipients ?? [],
        Importance = Importance ?? current?.Importance ?? Domain.Mail.Importance.Normal,
    };
}

/// <summary>A Graph <c>PATCH messages/{id}</c>: message properties, draft content, or both.</summary>
public sealed record MessageUpdate(MessagePatch? Patch, DraftUpdate? Draft)
{
    public bool HasPatch => Patch is { } p && (p.IsRead is not null || p.Flag is not null || p.Categories is not null || p.Importance is not null);
}
