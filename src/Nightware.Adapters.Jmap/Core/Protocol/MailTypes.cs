using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nightware.Adapters.Jmap.Core.Protocol;

// RFC 8621 data types. Every property is nullable/optional: a Foo/get only returns
// the properties asked for, and a Foo/set create must omit server-set ones.

/// <summary>Well-known JMAP keywords (RFC 8621 §4.1.1).</summary>
internal static class Keywords
{
    public const string Seen = "$seen";
    public const string Flagged = "$flagged";
    public const string Draft = "$draft";
    public const string Answered = "$answered";
    public const string Forwarded = "$forwarded";
    public const string Junk = "$junk";
    public const string NotJunk = "$notjunk";
    public const string Phishing = "$phishing";
}

/// <summary>Mailbox access rights (RFC 8621 §2).</summary>
internal sealed record MailboxRights
{
    public bool MayReadItems { get; init; }

    public bool MayAddItems { get; init; }

    public bool MayRemoveItems { get; init; }

    public bool MaySetSeen { get; init; }

    public bool MaySetKeywords { get; init; }

    public bool MayCreateChild { get; init; }

    public bool MayRename { get; init; }

    public bool MayDelete { get; init; }

    public bool MaySubmit { get; init; }
}

/// <summary>A JMAP Mailbox (RFC 8621 §2).</summary>
internal sealed record Mailbox
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>Null for top-level mailboxes. To move a mailbox to the top level, patch <c>parentId</c> to null.</summary>
    public string? ParentId { get; init; }

    /// <summary>Lower-case role (<c>inbox</c>, <c>sent</c>, …); see <c>Mail/MailboxRoles</c>.</summary>
    public string? Role { get; init; }

    public int? SortOrder { get; init; }

    public int? TotalEmails { get; init; }

    public int? UnreadEmails { get; init; }

    public int? TotalThreads { get; init; }

    public int? UnreadThreads { get; init; }

    public MailboxRights? MyRights { get; init; }

    public bool? IsSubscribed { get; init; }

    /// <summary>Shared per-mailbox JSON by namespace (<c>urn:ietf:params:jmap:metadata</c>).</summary>
    public JsonObject? Metadata { get; init; }
}

/// <summary>Mailbox/query filter condition (RFC 8621 §2.3).</summary>
internal sealed record MailboxFilter
{
    public string? ParentId { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public bool? HasAnyRole { get; init; }

    public bool? IsSubscribed { get; init; }
}

/// <summary>An address in an Email header (RFC 8621 §4.1.2.3).</summary>
internal sealed record EmailAddressValue(string? Name, string Email);

/// <summary>A raw header field (RFC 8621 §4.1.3).</summary>
internal sealed record EmailHeaderValue(string Name, string Value);

/// <summary>A decoded text body part value (RFC 8621 §4.1.4).</summary>
internal sealed record EmailBodyValue(string Value)
{
    public bool IsEncodingProblem { get; init; }

    public bool IsTruncated { get; init; }
}

/// <summary>A MIME part (RFC 8621 §4.1.4). Requested <c>header:…</c> properties land in <see cref="Extra"/>.</summary>
internal sealed record EmailBodyPart
{
    public string? PartId { get; init; }

    public string? BlobId { get; init; }

    public long? Size { get; init; }

    public IReadOnlyList<EmailHeaderValue>? Headers { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? Charset { get; init; }

    public string? Disposition { get; init; }

    public string? Cid { get; init; }

    public IReadOnlyList<string>? Language { get; init; }

    public string? Location { get; init; }

    public IReadOnlyList<EmailBodyPart>? SubParts { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A JMAP Email (RFC 8621 §4). Requested <c>header:…</c> properties land in <see cref="Extra"/>; read them with <see cref="EmailHeaders"/>.</summary>
internal sealed record Email
{
    public string? Id { get; init; }

    public string? BlobId { get; init; }

    public string? ThreadId { get; init; }

    public IReadOnlyDictionary<string, bool>? MailboxIds { get; init; }

    public IReadOnlyDictionary<string, bool>? Keywords { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Message-ID values without angle brackets (<c>header:Message-ID:asMessageIds</c>).</summary>
    public IReadOnlyList<string>? MessageId { get; init; }

    public IReadOnlyList<string>? InReplyTo { get; init; }

    public IReadOnlyList<string>? References { get; init; }

    public IReadOnlyList<EmailAddressValue>? Sender { get; init; }

    public IReadOnlyList<EmailAddressValue>? From { get; init; }

    public IReadOnlyList<EmailAddressValue>? To { get; init; }

    public IReadOnlyList<EmailAddressValue>? Cc { get; init; }

    public IReadOnlyList<EmailAddressValue>? Bcc { get; init; }

    public IReadOnlyList<EmailAddressValue>? ReplyTo { get; init; }

    public string? Subject { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public IReadOnlyList<EmailHeaderValue>? Headers { get; init; }

    /// <summary>Shared per-message JSON by namespace (<c>urn:ietf:params:jmap:metadata</c>).</summary>
    public JsonObject? Metadata { get; init; }

    public EmailBodyPart? BodyStructure { get; init; }

    public IReadOnlyDictionary<string, EmailBodyValue>? BodyValues { get; init; }

    public IReadOnlyList<EmailBodyPart>? TextBody { get; init; }

    public IReadOnlyList<EmailBodyPart>? HtmlBody { get; init; }

    public IReadOnlyList<EmailBodyPart>? Attachments { get; init; }

    public bool? HasAttachment { get; init; }

    public string? Preview { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Email/query filter (RFC 8621 §4.4.1). A single type models both a
/// FilterCondition and a FilterOperator (their JSON members are disjoint);
/// combine with <see cref="And"/>, <see cref="Or"/>, <see cref="Not"/>.
/// </summary>
internal sealed record EmailFilter
{
    public string? InMailbox { get; init; }

    public IReadOnlyList<string>? InMailboxOtherThan { get; init; }

    public DateTimeOffset? Before { get; init; }

    public DateTimeOffset? After { get; init; }

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public string? AllInThreadHaveKeyword { get; init; }

    public string? SomeInThreadHaveKeyword { get; init; }

    public string? NoneInThreadHaveKeyword { get; init; }

    public string? HasKeyword { get; init; }

    public string? NotKeyword { get; init; }

    public bool? HasAttachment { get; init; }

    public string? Text { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Cc { get; init; }

    public string? Bcc { get; init; }

    public string? Subject { get; init; }

    public string? Body { get; init; }

    /// <summary><c>[name]</c> or <c>[name, text]</c>.</summary>
    public IReadOnlyList<string>? Header { get; init; }

    /// <summary>FilterOperator: <c>AND</c>, <c>OR</c> or <c>NOT</c>.</summary>
    public string? Operator { get; init; }

    public IReadOnlyList<EmailFilter>? Conditions { get; init; }

    public static EmailFilter And(params IReadOnlyList<EmailFilter> conditions) => new() { Operator = "AND", Conditions = conditions };

    public static EmailFilter Or(params IReadOnlyList<EmailFilter> conditions) => new() { Operator = "OR", Conditions = conditions };

    /// <summary>JMAP NOT means "none of": not(c1 OR c2 …).</summary>
    public static EmailFilter Not(params IReadOnlyList<EmailFilter> conditions) => new() { Operator = "NOT", Conditions = conditions };
}

/// <summary>A JMAP Thread (RFC 8621 §3).</summary>
internal sealed record EmailThread
{
    public string? Id { get; init; }

    public IReadOnlyList<string>? EmailIds { get; init; }
}

/// <summary>One email for <c>Email/import</c> (RFC 8621 §4.8).</summary>
internal sealed record EmailImport(string BlobId, IReadOnlyDictionary<string, bool> MailboxIds)
{
    public IReadOnlyDictionary<string, bool>? Keywords { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }
}

/// <summary><c>Email/import</c> arguments.</summary>
internal sealed record EmailImportRequest(string AccountId, IReadOnlyDictionary<string, EmailImport> Emails)
{
    public string? IfInState { get; init; }
}

/// <summary><c>Email/import</c> response.</summary>
internal sealed record EmailImportResponse
{
    public string AccountId { get; init; } = string.Empty;

    public string? OldState { get; init; }

    public string NewState { get; init; } = string.Empty;

    public Dictionary<string, Email>? Created { get; init; }

    public Dictionary<string, SetError>? NotCreated { get; init; }
}

/// <summary>A sending identity (RFC 8621 §6).</summary>
internal sealed record Identity
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public IReadOnlyList<EmailAddressValue>? ReplyTo { get; init; }

    public IReadOnlyList<EmailAddressValue>? Bcc { get; init; }

    public string? TextSignature { get; init; }

    public string? HtmlSignature { get; init; }

    public bool? MayDelete { get; init; }
}

/// <summary>An SMTP envelope address with optional ESMTP parameters.</summary>
internal sealed record SubmissionAddress(string Email)
{
    public IReadOnlyDictionary<string, string?>? Parameters { get; init; }
}

internal sealed record Envelope(SubmissionAddress MailFrom, IReadOnlyList<SubmissionAddress> RcptTo);

internal sealed record DeliveryStatus(string Smtp, string Delivered, string Displayed);

/// <summary>A JMAP EmailSubmission (RFC 8621 §7).</summary>
internal sealed record EmailSubmission
{
    public string? Id { get; init; }

    public string? IdentityId { get; init; }

    /// <summary>May be a <c>#creationId</c> of an Email created earlier in the same request.</summary>
    public string? EmailId { get; init; }

    public string? ThreadId { get; init; }

    /// <summary>Null lets the server derive it from the message headers.</summary>
    public Envelope? Envelope { get; init; }

    public DateTimeOffset? SendAt { get; init; }

    /// <summary><c>pending</c>, <c>final</c> or <c>canceled</c>.</summary>
    public string? UndoStatus { get; init; }

    public IReadOnlyDictionary<string, DeliveryStatus>? DeliveryStatus { get; init; }

    public IReadOnlyList<string>? DsnBlobIds { get; init; }

    public IReadOnlyList<string>? MdnBlobIds { get; init; }
}

/// <summary>The singleton VacationResponse (RFC 8621 §8). Its id is always <c>singleton</c>.</summary>
internal sealed record VacationResponse
{
    public const string SingletonId = "singleton";

    public string? Id { get; init; }

    public bool? IsEnabled { get; init; }

    public DateTimeOffset? FromDate { get; init; }

    public DateTimeOffset? ToDate { get; init; }

    public string? Subject { get; init; }

    public string? TextBody { get; init; }

    public string? HtmlBody { get; init; }
}

/// <summary>A Sieve script (RFC 9661 §2). Its content is the blob <see cref="BlobId"/>.</summary>
internal sealed record SieveScript
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? BlobId { get; init; }

    public bool? IsActive { get; init; }
}

/// <summary><c>SieveScript/validate</c> response: <see cref="Error"/> is null when the script compiles.</summary>
internal sealed record SieveScriptValidateResponse
{
    public string AccountId { get; init; } = string.Empty;

    public SetError? Error { get; init; }
}

/// <summary><c>SieveScript/validate</c> arguments.</summary>
internal sealed record SieveScriptValidateRequest(string AccountId, string BlobId);

/// <summary>Response of a blob upload (RFC 8620 §6.1).</summary>
internal sealed record UploadResponse(string AccountId, string BlobId, string Type, long Size);

/// <summary>A request-level error (RFC 7807 problem details, RFC 8620 §3.6.1).</summary>
internal sealed record ProblemDetails
{
    public string? Type { get; init; }

    public int? Status { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>For <c>urn:ietf:params:jmap:error:limit</c>: the limit that was exceeded.</summary>
    public string? Limit { get; init; }
}

/// <summary>A method-level <c>error</c> response's arguments (RFC 8620 §3.6.2).</summary>
internal sealed record MethodError
{
    public string Type { get; init; } = string.Empty;

    public string? Description { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
