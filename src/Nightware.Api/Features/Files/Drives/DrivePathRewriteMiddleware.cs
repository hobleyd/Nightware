using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// Graph's path addressing (<c>…/root:/a/b.txt:</c>, <c>…/items/{id}:/b.txt:/content</c>) rewritten, before routing,
/// to the internal <c>…/items/{anchor}/nightware.path/{base64url path}{rest}</c> (ADR 0008 D2), so every drive route maps
/// one set of handlers. The colons can't be routed: a path may have any number of segments and ends at a colon.
/// </summary>
/// <remarks>
/// The path is carried as unpadded base64url of its decoded UTF-8 text, one route segment, so nothing in it is ever
/// re-read as routing syntax; the handler validates its segments (<see cref="Domain.Files.DriveItemRef.TryParsePath"/>).
/// The anchor is <c>root</c> for <c>root:</c>. Requests that don't use path addressing pass untouched.
/// </remarks>
public sealed partial class DrivePathRewriteMiddleware(RequestDelegate next)
{
    public const string PathSegment = "nightware.path";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryRewrite(context.Request.Path, out var rewritten))
        {
            context.Request.Path = rewritten;
        }

        return next(context);
    }

    /// <summary>The internal form of a path-addressed drive request; false for every other path.</summary>
    public static bool TryRewrite(PathString path, out PathString rewritten)
    {
        rewritten = path;
        var value = path.Value;
        if (value is null || !value.Contains(':', StringComparison.Ordinal) || !GraphPaths.IsGraphRequest(path))
        {
            return false;
        }

        var match = Addressed().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var anchor = match.Groups["root"].Success ? "root" : match.Groups["id"].Value;
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(match.Groups["path"].Value));
        rewritten = new PathString($"{match.Groups["base"].Value}/items/{anchor}/{PathSegment}/{encoded}{match.Groups["rest"].Value}");
        return true;
    }

    /// <summary>The base64url segment back to the path below the anchor (null when it isn't one).</summary>
    public static string? Decode(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || !Base64Url.IsValid(segment))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(Base64Url.DecodeFromChars(segment));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // {version}{/me/drive | /users/x/drive | /drives/x}/{root | items/id}:/path[:][/rest]
    [GeneratedRegex(
        @"^(?<base>/(?:v1\.0|beta)(?:/me/drive|/users/[^/:]+/drive|/drives/[^/:]+))/(?:(?<root>root)|items/(?<id>[^/:]+)):(?<path>/[^:]*?)(?::(?<rest>/[^:]*)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Addressed();
}
