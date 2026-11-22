using System.Globalization;

using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// Paging and the accepted query options of the directory's lists (phase 20): <c>$select</c>, <c>$top</c> and the
/// <c>$skiptoken</c> of a previous page, which is the next offset. Anything else is a 400 naming it.
/// </summary>
internal static class DirectoryPaging
{
    /// <summary>Graph's default page size for directory lists.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The offset to start at, or the validation error for the first unsupported option.</summary>
    public static Result<int> Offset(ItemQuery query, string resource)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Unsupported(query) is { } option)
        {
            return Error.Validation($"{option} is not supported on {resource}.", option);
        }

        if (query.SkipToken is null)
        {
            return 0;
        }

        return int.TryParse(query.SkipToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : Error.Validation("The $skiptoken is not valid.", "$skiptoken");
    }

    public static int Top(ItemQuery query) => query.Top ?? DefaultPageSize;

    /// <summary>One page of <paramref name="all"/> from <paramref name="offset"/>, with the next offset when more remain.</summary>
    public static Page<T> Slice<T>(IReadOnlyList<T> all, int offset, int top)
    {
        ArgumentNullException.ThrowIfNull(all);
        var window = all.Skip(offset).Take(top).ToList();
        var next = offset + window.Count < all.Count ? (offset + window.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<T>(window, next);
    }

    private static string? Unsupported(ItemQuery query) =>
        query.Filter is not null ? "$filter"
        : query.Search is not null ? "$search"
        : query.OrderBy.Count > 0 ? "$orderby"
        : query.Skip is not null ? "$skip"
        : query.Expand.Count > 0 ? "$expand"
        : query.Count ? "$count"
        : null;
}
