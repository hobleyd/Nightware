using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using DomainTaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// Applies <c>$filter</c>/<c>$orderby</c>/paging to tasks read from a collection. Supported:
/// <c>status eq|ne '…'</c> (joined with <c>and</c>) and ordering by <c>createdDateTime</c>,
/// <c>lastModifiedDateTime</c>, <c>title</c> or <c>dueDateTime/dateTime</c>; anything else is a 400.
/// </summary>
internal static class TaskQueryEvaluator
{
    public static Result<Page<TodoTask>> Apply(IEnumerable<TodoTask> tasks, ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<TodoTask> result = tasks;
        if (query.Filter is { } filter)
        {
            foreach (var conjunct in filter.Conjuncts())
            {
                var predicate = Predicate(conjunct);
                if (!predicate.IsSuccess)
                {
                    return predicate.Error;
                }

                result = result.Where(predicate.Value);
            }
        }

        IOrderedEnumerable<TodoTask>? ordered = null;
        foreach (var clause in query.OrderBy)
        {
            var key = SortKey(clause.Property);
            if (key is null)
            {
                return Error.Validation($"$orderby on '{clause.Property}' is not supported for tasks.", "$orderby");
            }

            ordered = (ordered, clause.Direction) switch
            {
                (null, SortDirection.Ascending) => result.OrderBy(key, KeyComparer.Instance),
                (null, _) => result.OrderByDescending(key, KeyComparer.Instance),
                (_, SortDirection.Ascending) => ordered.ThenBy(key, KeyComparer.Instance),
                _ => ordered.ThenByDescending(key, KeyComparer.Instance),
            };
        }

        var matched = (ordered ?? result).ToList();
        return TaskPaging.Page(matched, query).Map(page => query.Count ? page with { Total = matched.Count } : page);
    }

    private static Result<Func<TodoTask, bool>> Predicate(FilterNode node)
    {
        if (node is FilterNode.Compare { Property: var property, Op: CompareOp.Eq or CompareOp.Ne, Value: var value } compare
            && string.Equals(property, "status", StringComparison.OrdinalIgnoreCase)
            && ParseStatus(value) is { } status)
        {
            return compare.Op == CompareOp.Eq
                ? Result<Func<TodoTask, bool>>.Success(t => t.Status == status)
                : Result<Func<TodoTask, bool>>.Success(t => t.Status != status);
        }

        return Error.Validation("This $filter is not supported for tasks: only status eq/ne '<status>' (joined with and).", "$filter");
    }

    private static DomainTaskStatus? ParseStatus(FilterValue value)
    {
        var text = value switch
        {
            FilterValue.Text t => t.Value,
            // microsoft.graph.taskStatus'completed' or a bare completed
            FilterValue.EnumMember e => e.Value.IndexOf('\'', StringComparison.Ordinal) is >= 0 and var quote ? e.Value[(quote + 1)..].TrimEnd('\'') : e.Value,
            _ => null,
        };
        // Names only: Enum.TryParse would also take "1".
        return text is { Length: > 0 } && text.All(char.IsAsciiLetter) && Enum.TryParse<DomainTaskStatus>(text, ignoreCase: true, out var status) ? status : null;
    }

    private static Func<TodoTask, IComparable?>? SortKey(string property) => property.ToUpperInvariant() switch
    {
        "CREATEDDATETIME" => static t => t.CreatedDateTime,
        "LASTMODIFIEDDATETIME" => static t => t.LastModifiedDateTime,
        "TITLE" => static t => t.Title,
        "DUEDATETIME/DATETIME" or "DUEDATETIME" => static t => t.DueDateTime?.DateTime,
        _ => null,
    };

    /// <summary>Orders missing values first, like Graph's ascending sort on nulls; strings ignore case.</summary>
    private sealed class KeyComparer : IComparer<IComparable?>
    {
        public static KeyComparer Instance { get; } = new();

        public int Compare(IComparable? x, IComparable? y) => (x, y) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (string a, string b) => StringComparer.CurrentCultureIgnoreCase.Compare(a, b),
            _ => x.CompareTo(y),
        };
    }
}
