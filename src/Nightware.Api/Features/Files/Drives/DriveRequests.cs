using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;
using Nightware.Api.Graph.Errors;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>Reads what drive requests carry (item addresses, conflict behaviour, list options) and writes drive errors.</summary>
internal static class DriveRequests
{
    public const string ConflictBehaviorOption = "@microsoft.graph.conflictBehavior";

    /// <summary>
    /// The item a route names: <c>root</c> (or the anchor <c>root</c>), an id, and optionally a path below it that the
    /// rewrite (<see cref="DrivePathRewriteMiddleware"/>) carried as base64url.
    /// </summary>
    public static Result<DriveItemRef> ItemRef(string? itemId, string? itemPath, string? driveId)
    {
        var anchor = itemId is null || string.Equals(itemId, "root", StringComparison.OrdinalIgnoreCase) ? null : itemId;
        IReadOnlyList<string> segments = [];
        // The server decodes every escape but %2F (and leaves %5C as written): one left in a segment is a separator a
        // name can't hold, and would mean something else once Nextcloud decodes it.
        if (itemPath is not null
            && (DrivePathRewriteMiddleware.Decode(itemPath) is not { } decoded
                || decoded.Contains("%2F", StringComparison.OrdinalIgnoreCase)
                || decoded.Contains("%5C", StringComparison.OrdinalIgnoreCase)
                || !DriveItemRef.TryParsePath(decoded, out segments)))
        {
            return Error.Validation("The item path is not valid: no empty, '.' or '..' segments, backslashes or control characters.", "path");
        }

        return new DriveItemRef(anchor, segments) { DriveId = driveId };
    }

    /// <summary>
    /// <c>@microsoft.graph.conflictBehavior</c> from the query or, failing that, the body (where <c>children</c> and
    /// <c>createUploadSession</c> put it); <paramref name="fallback"/> when neither has it.
    /// </summary>
    public static Result<ConflictBehavior> Conflict(HttpRequest request, JsonObject? body, ConflictBehavior fallback)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Query[ConflictBehaviorOption].ToString();
        if (string.IsNullOrEmpty(text) && body is not null && body.TryGetPropertyValue(ConflictBehaviorOption, out var node) && node is JsonValue value)
        {
            text = value.TryGetValue<string>(out var s) ? s : null;
        }

        return text?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "fail" => ConflictBehavior.Fail,
            "replace" => ConflictBehavior.Replace,
            "rename" => ConflictBehavior.Rename,
            _ => Error.Validation($"{ConflictBehaviorOption} must be 'fail', 'replace' or 'rename'.", ConflictBehaviorOption),
        };
    }

    /// <summary>
    /// A drive listing's options: <c>$top</c> (default 200), <c>$skiptoken</c>, and <c>$orderby</c> on <c>name</c>,
    /// <c>lastModifiedDateTime</c> or <c>size</c>. <c>$filter</c>, <c>$search</c> and <c>$skip</c> aren't offered.
    /// </summary>
    public static Result<DriveListQuery> ListQuery(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Filter is not null || query.Search is not null || query.Skip is not null)
        {
            return Error.Validation("A drive listing takes $select, $expand, $top, $orderby and $skiptoken only.");
        }

        if (query.OrderBy.Count > 1)
        {
            return Error.Validation("A drive listing orders by one property.", "$orderby");
        }

        var sort = DriveSortKey.Name;
        var descending = false;
        if (query.OrderBy is [var order])
        {
            switch (order.Property.ToLowerInvariant())
            {
                case "name":
                    sort = DriveSortKey.Name;
                    break;
                case "lastmodifieddatetime":
                    sort = DriveSortKey.LastModifiedDateTime;
                    break;
                case "size":
                    sort = DriveSortKey.Size;
                    break;
                default:
                    return Error.Validation($"A drive listing can't be ordered by '{order.Property}'.", "$orderby");
            }

            descending = order.Direction == SortDirection.Descending;
        }

        return new DriveListQuery(query.Top ?? DriveLimits.DefaultPageSize, query.SkipToken, sort, descending);
    }

    /// <summary>A <c>/users/{x}/drive…</c> route is the caller's own drive only: the façade reads files as the caller.</summary>
    public static Error? OwnDrive(Mailbox mailbox, IUserContext caller)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(caller);
        return mailbox.IsMe || string.Equals(mailbox.Ref.Address, caller.Principal.Mail, StringComparison.OrdinalIgnoreCase)
            ? null
            : Error.Forbidden("Only your own drive can be reached.");
    }

    /// <summary>A drive error with OneDrive's codes (<c>itemNotFound</c>, <c>accessDenied</c>) where Exchange's would be emitted.</summary>
    public static IResult Fail(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var descriptor = GraphErrorMapper.Map(error);
        descriptor = error.Kind switch
        {
            ErrorKind.NotFound => descriptor with { Code = "itemNotFound" },
            ErrorKind.Forbidden => descriptor with { Code = "accessDenied" },
            _ => descriptor,
        };
        return new GraphErrorResult(descriptor);
    }

    public static IResult Match<T>(Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Fail(result.Error);
    }

    /// <summary>The <c>parentReference.id</c> (or <c>path</c>) of a body, as an item address; null when absent.</summary>
    public static Result<DriveItemRef?> ParentReference(JsonObject? body)
    {
        if (body is null || !body.TryGetPropertyValue("parentReference", out var node) || node is null)
        {
            return Result<DriveItemRef?>.Success(null);
        }

        if (node is not JsonObject reference)
        {
            return Error.Validation("parentReference must be an object.", "parentReference");
        }

        if (Text(reference, "id") is { Length: > 0 } id)
        {
            return DriveItemRef.ById(id);
        }

        if (Text(reference, "path") is { } path)
        {
            // Graph's form: "/drive/root:/a/b" (or "/drives/{id}/root:/a/b").
            var colon = path.IndexOf("root:", StringComparison.OrdinalIgnoreCase);
            var below = colon < 0 ? path : path[(colon + "root:".Length)..];
            return DriveItemRef.TryParsePath(below, out var segments)
                ? new DriveItemRef(null, segments)
                : Error.Validation("parentReference.path is not valid.", "parentReference.path");
        }

        return Error.Validation("parentReference needs an id or a path.", "parentReference");
    }

    public static string? Text(JsonObject? body, string name) =>
        body is not null && body.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static long? Number(JsonObject? body, string name) =>
        body is not null && body.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    public static DateTimeOffset? Instant(JsonObject? body, string name) =>
        Text(body, name) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime()
            : null;

    public static string Context(Mailbox mailbox, string tail) => $"{mailbox.ContextSegment}/drive{tail}";
}
