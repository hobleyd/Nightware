using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Graph.Errors;

/// <summary>
/// Answers a request under <c>/v1.0</c> or <c>/beta</c> that no feature maps.
/// </summary>
/// <remarks>
/// Graph answers an unknown path with <c>400 BadRequest</c> "Resource not found for the
/// segment '…'". NightMail already treats that as a settled "no such resource" answer
/// (it is what Graph says for <c>messages/{id}/event</c> without the cast segment), and its
/// delta poller resets on 400 the same way it does on 404/410, so mirroring Graph is the
/// safest behaviour for a route a feature phase has not filled yet. Routes that <i>are</i>
/// mapped but whose port is still the stub answer <c>501 notImplemented</c>.
/// Graph names the first segment it could not resolve; the façade cannot know which one
/// that is, so it names the last.
/// </remarks>
public static class UnknownGraphRoute
{
    public const string Code = "BadRequest";

    public static Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var segment = LastSegment(context.Request.Path);
        return GraphErrorWriter.WriteAsync(context, new GraphErrorDescriptor(
            StatusCodes.Status400BadRequest,
            Code,
            $"Resource not found for the segment '{segment}'.",
            null));
    }

    internal static string LastSegment(PathString path)
    {
        var value = path.Value ?? string.Empty;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }
}
