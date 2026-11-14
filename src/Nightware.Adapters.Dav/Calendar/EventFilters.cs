using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>The <c>$filter</c>s the calendar can translate to CalDAV: exactly <c>iCalUId eq '…'</c>.</summary>
internal static class EventFilters
{
    /// <summary>The UID to match, null for no filter, or a 400 for anything else (the client falls back on it).</summary>
    public static Result<string?> UidOf(FilterNode? filter) => filter switch
    {
        null => Result<string?>.Success(null),
        FilterNode.Compare { Op: CompareOp.Eq, Value: FilterValue.Text { Value: { Length: > 0 } uid } } compare
            when string.Equals(compare.Property, "iCalUId", StringComparison.OrdinalIgnoreCase) => uid,
        _ => Error.Validation("Only $filter=iCalUId eq '…' is supported on events.", "$filter"),
    };

    /// <summary>calendarView takes no OData beyond <c>$select</c>/<c>$top</c>/<c>$skip</c>.</summary>
    public static Error? Unsupported(ItemQuery query) =>
        query.Filter is not null || query.Search is not null || query.OrderBy.Count > 0
            ? Error.Validation("calendarView supports only $select, $top and $skip.", query.Filter is not null ? "$filter" : query.Search is not null ? "$search" : "$orderby")
            : null;
}
