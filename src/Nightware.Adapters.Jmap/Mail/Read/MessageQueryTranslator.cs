using System.Globalization;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// Translates the provider-neutral <see cref="ItemQuery"/> of a message collection into a
/// <see cref="MessageQueryPlan"/>. Anything it cannot express is <see cref="ErrorKind.Validation"/>
/// (HTTP 400), which is what the client's fallbacks key on (e.g. <c>conversationId in</c> → per-id <c>eq</c>).
/// </summary>
internal static class MessageQueryTranslator
{
    private const string ConversationId = "conversationId";
    private const string ParentFolderId = "parentFolderId";
    private const string IsRead = "isRead";

    private static readonly Comparator NewestFirst = new("receivedAt", IsAscending: false);

    public static Result<MessageQueryPlan> Translate(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var threads = new List<string>();
        var excluded = new List<string>();
        var conditions = new List<EmailFilter>();

        foreach (var conjunct in query.Filter?.Conjuncts() ?? [])
        {
            switch (conjunct)
            {
                case FilterNode.Compare { Op: CompareOp.Eq, Value: FilterValue.Text id } c when Is(c.Property, ConversationId):
                    threads.Add(id.Value);
                    break;
                case FilterNode.In @in when Is(@in.Property, ConversationId) && @in.Values.All(static v => v is FilterValue.Text):
                    threads.AddRange(@in.Values.Cast<FilterValue.Text>().Select(static v => v.Value));
                    break;
                case FilterNode.Compare { Op: CompareOp.Ne, Value: FilterValue.Text folder } c when Is(c.Property, ParentFolderId):
                    excluded.Add(folder.Value);
                    break;
                case FilterNode.Compare { Op: CompareOp.Eq or CompareOp.Ne, Value: FilterValue.Bool read } c when Is(c.Property, IsRead):
                    var seen = read.Value == (c.Op == CompareOp.Eq);
                    conditions.Add(seen ? new EmailFilter { HasKeyword = Keywords.Seen } : new EmailFilter { NotKeyword = Keywords.Seen });
                    break;
                default:
                    return Error.Validation("This $filter is not supported on messages; use conversationId eq/in, parentFolderId ne or isRead eq.", "$filter");
            }
        }

        if (threads.Count > 0)
        {
            if (conditions.Count > 0 || query.Search is not null)
            {
                return Error.Validation("A conversationId filter can only be combined with parentFolderId ne.", "$filter");
            }

            return new MessageQueryPlan.Conversation(threads, excluded);
        }

        if (excluded.Count > 0)
        {
            conditions.Add(new EmailFilter { InMailboxOtherThan = excluded });
        }

        if (query.Search is { } search)
        {
            conditions.Add(Search(search));
        }

        var sort = Sort(query.OrderBy);
        if (!sort.IsSuccess)
        {
            return sort.Error;
        }

        return new MessageQueryPlan.Query(Combine(conditions), sort.Value, Paged: true);
    }

    /// <summary>KQL terms are AND-ed; each OR group becomes one JMAP OR; negation becomes NOT.</summary>
    internal static EmailFilter Search(SearchQuery search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var conditions = search.Terms.Select(Term).ToList();
        conditions.AddRange(search.AnyOf.Where(static g => g.Count > 0).Select(static g => g.Count == 1 ? Term(g[0]) : EmailFilter.Or([.. g.Select(Term)])));
        return Combine(conditions) ?? new EmailFilter { Text = string.Empty };
    }

    private static EmailFilter Term(SearchTerm term)
    {
        var condition = Condition(term);
        return term.Negated ? EmailFilter.Not(condition) : condition;
    }

    private static EmailFilter Condition(SearchTerm term) => term.Field switch
    {
        SearchField.From => new EmailFilter { From = term.Value },
        SearchField.To => new EmailFilter { To = term.Value },
        SearchField.Cc => new EmailFilter { Cc = term.Value },
        SearchField.Participants => EmailFilter.Or(
            new EmailFilter { From = term.Value }, new EmailFilter { To = term.Value },
            new EmailFilter { Cc = term.Value }, new EmailFilter { Bcc = term.Value }),
        SearchField.Subject => new EmailFilter { Subject = term.Value },
        SearchField.Body => new EmailFilter { Body = term.Value },
        SearchField.HasAttachments when bool.TryParse(term.Value, out var has) => new EmailFilter { HasAttachment = has },
        SearchField.HasAttachments when term.Value is "yes" or "no" => new EmailFilter { HasAttachment = term.Value == "yes" },
        SearchField.Received or SearchField.Sent when DateRange(term) is { } range => range,
        _ => new EmailFilter { Text = term.Value },
    };

    /// <summary>
    /// <c>received&gt;=2026-01-01</c> and friends. JMAP's <c>before</c>/<c>after</c> compare
    /// <c>receivedAt</c> (the only date it filters on), so <c>sent:</c> uses it too.
    /// </summary>
    private static EmailFilter? DateRange(SearchTerm term)
    {
        if (!DateTimeOffset.TryParse(term.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return null;
        }

        var dayEnd = at.TimeOfDay == TimeSpan.Zero ? at.AddDays(1) : at;
        return term.Op switch
        {
            SearchOp.GreaterOrEqual => new EmailFilter { After = at },
            SearchOp.GreaterThan => new EmailFilter { After = dayEnd },
            SearchOp.LessThan => new EmailFilter { Before = at },
            SearchOp.LessOrEqual => new EmailFilter { Before = dayEnd },
            _ => new EmailFilter { After = at, Before = dayEnd },
        };
    }

    private static Result<IReadOnlyList<Comparator>> Sort(IReadOnlyList<OrderBy> orderBy)
    {
        if (orderBy.Count == 0)
        {
            return new[] { NewestFirst };
        }

        var sort = new List<Comparator>(orderBy.Count);
        foreach (var clause in orderBy)
        {
            var property = clause.Property switch
            {
                _ when Is(clause.Property, "receivedDateTime") => "receivedAt",
                _ when Is(clause.Property, "sentDateTime") => "sentAt",
                _ => null,
            };
            if (property is null)
            {
                return Error.Validation($"$orderby={clause.Property} is not supported on messages.", "$orderby");
            }

            sort.Add(new Comparator(property, clause.Direction == SortDirection.Ascending));
        }

        return sort;
    }

    private static EmailFilter? Combine(List<EmailFilter> conditions) => conditions.Count switch
    {
        0 => null,
        1 => conditions[0],
        _ => EmailFilter.And([.. conditions]),
    };

    private static bool Is(string property, string name) => string.Equals(property, name, StringComparison.OrdinalIgnoreCase);
}
