using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// What every <c>Email/get</c> of this feature fetches. <c>keywords</c> and <c>messageId</c> are always
/// fetched so the right <c>e.</c>/<c>d.</c> id is emitted (ADR 0001 §1); <c>mailboxIds</c> for
/// <c>parentFolderId</c>; <c>bodyStructure</c> (with each part's Content-Type header, which carries the
/// iTIP <c>method</c>) so invitation mail is recognised on list, get and delta alike.
/// </summary>
internal static class EmailProperties
{
    public const string ContentTypeHeader = "Content-Type";

    public static readonly string ImportanceProperty = EmailHeaders.Property("Importance", HeaderForm.Text);
    public static readonly string PriorityProperty = EmailHeaders.Property("X-Priority", HeaderForm.Text);
    public static readonly string PartContentTypeProperty = EmailHeaders.Property(ContentTypeHeader);

    /// <summary>The list projection (folder pages, search, conversation expansion, delta).</summary>
    public static IReadOnlyList<string> List { get; } =
    [
        "id", "blobId", "threadId", "mailboxIds", "keywords", "messageId", "receivedAt", "sentAt", "subject",
        "from", "sender", "to", "cc", "bcc", "replyTo", "preview", "hasAttachment", "bodyStructure",
        ImportanceProperty, PriorityProperty,
    ];

    /// <summary>A single message: the list projection plus bodies and attachments; raw headers only when selected.</summary>
    public static IReadOnlyList<string> Full(MessageFetch fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        string[] full = [.. List, "textBody", "htmlBody", "attachments", "bodyValues"];
        return WantsHeaders(fetch) ? [.. full, "headers"] : full;
    }

    /// <summary>Per-part properties: RFC 8621's defaults plus <c>subParts</c> and the raw Content-Type header.</summary>
    public static IReadOnlyList<string> BodyParts { get; } =
        ["partId", "blobId", "size", "name", "type", "charset", "disposition", "cid", "subParts", PartContentTypeProperty];

    /// <summary>Graph returns <c>internetMessageHeaders</c> only when it is explicitly selected.</summary>
    public static bool WantsHeaders(MessageFetch fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return fetch.Select?.Any(static s => string.Equals(s, "internetMessageHeaders", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static EmailGetRequest ListRequest(string accountId, IReadOnlyList<string>? ids = null) => new(accountId)
    {
        Ids = ids,
        Properties = List,
        BodyProperties = BodyParts,
    };

    public static EmailGetRequest FullRequest(string accountId, string id, MessageFetch fetch) => new(accountId)
    {
        Ids = [id],
        Properties = Full(fetch),
        BodyProperties = BodyParts,
        FetchHtmlBodyValues = true,
        FetchTextBodyValues = true,
    };
}
