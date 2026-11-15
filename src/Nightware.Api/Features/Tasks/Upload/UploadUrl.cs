using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;

namespace Nightware.Api.Features.Tasks.Upload;

/// <summary>
/// The shape of a To Do upload URL:
/// <c>{version root}/users/{mailbox}/todo/lists/{list}/tasks/{task}/attachmentSessions/{session}/content?authtoken=…</c>.
/// The path names the target (as Graph's own upload URLs do) and the store's session token; the
/// sealed credential travels in <see cref="AuthTokenParameter"/>.
/// </summary>
internal static class UploadUrl
{
    public const string AuthTokenParameter = "authtoken";
    public const string RoutePattern = "/users/{user}/todo/lists/{listId}/tasks/{taskId}/attachmentSessions/{sessionId}/content";

    /// <summary>Kestrel's default request-line limit; an upload URL must stay well inside it.</summary>
    public const int MaxRequestLine = 8192;

    public static string Build(string versionRoot, UploadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        // The address keeps its literal @, as NightMail writes shared-mailbox paths.
        var mailbox = Uri.EscapeDataString(binding.Mailbox).Replace("%40", "@", StringComparison.Ordinal);
        return $"{versionRoot}/users/{mailbox}/todo/lists/{Uri.EscapeDataString(binding.ListId)}/tasks/{Uri.EscapeDataString(binding.TaskId)}"
            + $"/attachmentSessions/{Uri.EscapeDataString(binding.SessionId)}/content?{AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    /// <summary>The binding named by a request path, when it is an upload URL path under a Graph version root.</summary>
    public static bool TryParse(PathString path, [NotNullWhen(true)] out UploadUrlBinding? binding)
    {
        binding = null;
        var version = GraphPaths.VersionOf(path);
        if (!path.StartsWithSegments(version, StringComparison.OrdinalIgnoreCase, out var rest) || rest.Value is not { } value)
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments is not ["", var users, var mailbox, var todo, var lists, var list, var tasks, var task, var sessions, var session, var content]
            || !Is(users, "users") || !Is(todo, "todo") || !Is(lists, "lists") || !Is(tasks, "tasks")
            || !Is(sessions, "attachmentSessions") || !Is(content, "content")
            || mailbox.Length == 0 || list.Length == 0 || task.Length == 0 || session.Length == 0)
        {
            return false;
        }

        binding = new UploadUrlBinding(mailbox, list, task, session);
        return true;
    }

    private static bool Is(string segment, string expected) => string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);
}
