using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;

namespace Nightware.Api.Features.Chats;

/// <summary>
/// Graph's key form for chat and channel paths (<c>chats('{id}')/messages('{id}')</c>,
/// <c>teams('{id}')/channels('{id}')/messages('{id}')/replies('{id}')</c>, <c>members('{id}')</c>,
/// <c>teams('{id}')/operations('{id}')</c>) rewritten, before
/// routing, to the segment form every chat and channel route maps. Change notifications name resources this way
/// (ADR 0009 D11), and a client may read one as given. Only these entity sets are rewritten: a parenthesis elsewhere is a
/// function call (<c>delta()</c>) or another feature's key.
/// </summary>
public sealed partial class ChatKeySegmentMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryRewrite(context.Request.Path, out var rewritten))
        {
            context.Request.Path = rewritten;
        }

        return next(context);
    }

    /// <summary>The segment form of a keyed chat or channel path; false (unchanged) for any other path.</summary>
    public static bool TryRewrite(PathString path, out PathString rewritten)
    {
        rewritten = path;
        var value = path.Value;
        if (value is null || !value.Contains("('", StringComparison.Ordinal) || !GraphPaths.IsGraphRequest(path))
        {
            return false;
        }

        var replaced = Keyed().Replace(value, static m => $"{m.Groups["set"].Value}/{m.Groups["key"].Value}");
        if (replaced == value)
        {
            return false;
        }

        rewritten = new PathString(replaced);
        return true;
    }

    [GeneratedRegex(@"(?<=/)(?<set>chats|messages|replies|members|teams|channels|operations)\('(?<key>[^'/]+)'\)(?=/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Keyed();
}
