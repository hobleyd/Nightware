using System.Globalization;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// Pages a fully-read, ordered collection. CalDAV has no server-side paging for calendar-query or
/// principal listings, so the continuation is just the offset of the next item (stateless, ADR rule 2).
/// </summary>
internal static class OffsetPaging
{
    public static Result<Page<T>> Slice<T>(IReadOnlyList<T> ordered, ItemQuery query, int defaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(query);
        int offset;
        if (query.SkipToken is { } token)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
            }
        }
        else
        {
            offset = Math.Max(0, query.Skip ?? 0);
        }

        var size = Math.Max(1, query.Top ?? defaultPageSize);
        var items = ordered.Skip(offset).Take(size).ToList();
        var next = (long)offset + size < ordered.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<T>(items, next);
    }
}
