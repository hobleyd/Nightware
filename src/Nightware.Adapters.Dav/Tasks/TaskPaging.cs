using System.Globalization;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// Offset paging over an in-memory listing. The <c>$skiptoken</c> is the next offset; task lists and
/// tasks are read whole from their collection (Nextcloud has no server-side paging for them).
/// </summary>
internal static class TaskPaging
{
    public const int DefaultPageSize = 100;

    public static Result<Page<T>> Page<T>(IReadOnlyList<T> items, ItemQuery query)
    {
        var offset = 0;
        if (query.SkipToken is { } token)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
            }
        }
        else if (query.Skip is { } skip)
        {
            offset = Math.Max(skip, 0);
        }

        var size = query.Top is > 0 and var top ? top : DefaultPageSize;
        var page = items.Skip(offset).Take(size).ToList();
        var next = offset + page.Count < items.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<T>(page, next);
    }
}
