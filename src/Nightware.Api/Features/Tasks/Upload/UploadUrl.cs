using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;

namespace Nightware.Api.Features.Tasks.Upload;

/// <summary>What an upload URL is bound to: the one route it may be PUT to (mailbox, case-insensitively; list; task; session).</summary>
internal sealed record UploadUrlBinding(string Mailbox, string ListId, string TaskId, string SessionId)
{
    public string[] BoundTo => [Mailbox.ToLowerInvariant(), ListId, TaskId, SessionId];
}

/// <summary>
/// The shape of a To Do upload URL:
/// <c>{version root}/users/{mailbox}/todo/lists/{list}/tasks/{task}/attachmentSessions/{session}/content?authtoken=…</c>.
/// The path names the target (as Graph's own upload URLs do) and the store's session token; the sealed
/// credential (<see cref="Kind"/>) travels in the query.
/// </summary>
/// <remarks>
/// The URL is the only credential the route takes (<see cref="SelfAuthorisingUrl.UrlOnly"/>): the session token
/// in the path is the store's and is not signed, so only a URL the façade sealed — after
/// <c>BeginTaskAttachmentUpload</c> checked the target, name and size — may carry one to the store. Its ticket
/// also carries the file's name and size, which the PUT enforces.
/// </remarks>
internal static class UploadUrl
{
    public const string RoutePattern = "/users/{user}/todo/lists/{listId}/tasks/{taskId}/attachmentSessions/{sessionId}/content";

    /// <summary>The longest an upload URL lives; the store's session (and the access token) may end it sooner.</summary>
    public static readonly SelfAuthorisingUrl Kind = new("nightware/todo-upload-url/v1", HttpMethods.Put, TimeSpan.FromMinutes(15), BindingOf, urlOnly: true);

    public static string Build(string versionRoot, UploadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        // The address keeps its literal @, as NightMail writes shared-mailbox paths.
        var mailbox = Uri.EscapeDataString(binding.Mailbox).Replace("%40", "@", StringComparison.Ordinal);
        return $"{versionRoot}/users/{mailbox}/todo/lists/{Uri.EscapeDataString(binding.ListId)}/tasks/{Uri.EscapeDataString(binding.TaskId)}"
            + $"/attachmentSessions/{Uri.EscapeDataString(binding.SessionId)}/content?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    private static string[]? BindingOf(HttpRequest request) =>
        SelfAuthorisingUrl.RouteValue(request, "user") is { } mailbox
        && SelfAuthorisingUrl.RouteValue(request, "listId") is { } list
        && SelfAuthorisingUrl.RouteValue(request, "taskId") is { } task
        && SelfAuthorisingUrl.RouteValue(request, "sessionId") is { } session
            ? new UploadUrlBinding(mailbox, list, task, session).BoundTo
            : null;
}
