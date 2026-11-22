using System.Reflection;

using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// The drive segments a route matched: <c>{driveId}</c>, <c>{itemId}</c> and the rewritten <c>{itemPath}</c>, each null where
/// the route has no such segment (one handler serves <c>root</c>, <c>items/{id}</c> and path addressing).
/// </summary>
public sealed class DriveAddress
{
    private DriveAddress(string? driveId, string? itemId, string? itemPath)
    {
        DriveId = driveId;
        ItemId = itemId;
        ItemPath = itemPath;
    }

    public string? DriveId { get; }

    public string? ItemId { get; }

    public string? ItemPath { get; }

    public static ValueTask<DriveAddress?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = context.Request.RouteValues;
        return ValueTask.FromResult<DriveAddress?>(new DriveAddress(Value(values, "driveId"), Value(values, "itemId"), Value(values, "itemPath")));
    }

    private static string? Value(Microsoft.AspNetCore.Routing.RouteValueDictionary values, string name) =>
        values.TryGetValue(name, out var value) && value is string { Length: > 0 } text ? text : null;
}
