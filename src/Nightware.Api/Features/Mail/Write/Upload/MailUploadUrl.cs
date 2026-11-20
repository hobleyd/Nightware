using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Nightware.Api.Auth;
using Nightware.Api.Features.Tasks.Dto;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Write.Upload;

/// <summary>What a mail upload URL is bound to: the mailbox (case-insensitively), the draft and the session.</summary>
internal sealed record MailUploadUrlBinding(string Mailbox, string DraftId, string SessionId)
{
    public string[] BoundTo => [Mailbox.ToLowerInvariant(), DraftId, SessionId];
}

/// <summary>
/// A draft attachment upload URL:
/// <c>{version root}/users/{mailbox}/messages/{draft}/attachmentSessions/{session}/content?authtoken=…</c>.
/// Like the To Do one (ADR 0001 §39), the sealed credential in the query is the only way in, and it is bound to
/// the path; the session itself lives with the draft (ADR 0006 D5).
/// </summary>
internal static class MailUploadUrl
{
    public const string RoutePattern = "/users/{user}/messages/{messageId}/attachmentSessions/{sessionId}/content";

    public static readonly SelfAuthorisingUrl Kind = new("nightware/mail-upload-url/v1", HttpMethods.Put, TimeSpan.FromMinutes(60), BindingOf, urlOnly: true);

    public static string Build(string versionRoot, MailUploadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var mailbox = Uri.EscapeDataString(binding.Mailbox).Replace("%40", "@", StringComparison.Ordinal);
        return $"{versionRoot}/users/{mailbox}/messages/{Uri.EscapeDataString(binding.DraftId)}"
            + $"/attachmentSessions/{Uri.EscapeDataString(binding.SessionId)}/content?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    private static string[]? BindingOf(HttpRequest request) =>
        SelfAuthorisingUrl.RouteValue(request, "user") is { } mailbox
        && SelfAuthorisingUrl.RouteValue(request, "messageId") is { } draft
        && SelfAuthorisingUrl.RouteValue(request, "sessionId") is { } session
            ? new MailUploadUrlBinding(mailbox, draft, session).BoundTo
            : null;
}

/// <summary>Graph <c>uploadSession</c>.</summary>
public sealed record MailUploadSessionDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UploadUrl { get; init; }

    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? ExpirationDateTime { get; init; }

    public IReadOnlyList<string> NextExpectedRanges { get; init; } = ["0-"];
}

/// <summary>Reads Graph's <c>{"AttachmentItem": {…}}</c> body.</summary>
internal static class AttachmentItemReader
{
    public static Result<DraftUploadFile> Read(JsonObject? body)
    {
        if (Member(body, "attachmentItem") is not JsonObject item)
        {
            return Error.Validation("AttachmentItem is required.", "AttachmentItem");
        }

        try
        {
            if (Member(item, "attachmentType")?.GetValue<string>() is { } kind && !string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unsupported("Only file attachments can be uploaded.", "AttachmentItem.attachmentType");
            }

            return new DraftUploadFile(
                Member(item, "name")?.GetValue<string>() ?? string.Empty,
                Member(item, "contentType")?.GetValue<string>() ?? string.Empty,
                Member(item, "size")?.GetValue<long>() ?? 0)
            {
                IsInline = Member(item, "isInline")?.GetValue<bool>() == true,
                ContentId = Member(item, "contentId")?.GetValue<string>(),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return Error.Validation("AttachmentItem is not valid.", "AttachmentItem");
        }
    }

    private static JsonNode? Member(JsonObject? obj, string name) =>
        obj?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
