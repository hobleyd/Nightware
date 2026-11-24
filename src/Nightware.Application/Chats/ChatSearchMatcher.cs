using System.Globalization;

using Nightware.Application.Common.Query;

namespace Nightware.Application.Chats;

/// <summary>
/// What a chat search backend can apply itself (ADR 0009 D10): one substring, a sent-time range and one sender. Each is
/// taken only from a positive top-level term, so whatever the backend finds is a superset of the query's matches; the
/// whole query is then checked on every candidate (<see cref="ChatSearchMatcher"/>).
/// </summary>
/// <param name="Text">The longest positive free-text term; null when there is none.</param>
/// <param name="Since">The earliest sent time a match can have (inclusive); null for no bound.</param>
/// <param name="Until">The latest sent time a match can have (inclusive, to the second); null for no bound.</param>
/// <param name="Sender">A positive <c>from:</c> value, for a backend that can look the person up; null when there is none.</param>
public sealed record ChatSearchPlan(string? Text, DateTimeOffset? Since, DateTimeOffset? Until, string? Sender)
{
    public static ChatSearchPlan Of(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var positive = query.Terms.Where(t => !t.Negated).ToList();
        var text = positive.Where(t => t.Field == SearchField.Any).Select(t => t.Value)
            .OrderByDescending(v => v.Length).FirstOrDefault();

        DateTimeOffset? since = null, until = null;
        foreach (var term in positive.Where(t => t.Field == SearchField.Sent))
        {
            if (ChatSearchMatcher.Range(term.Value) is not { } range)
            {
                continue;
            }

            var (start, end) = range;

            (DateTimeOffset? low, DateTimeOffset? high) = term.Op switch
            {
                SearchOp.GreaterOrEqual => ((DateTimeOffset?)start, (DateTimeOffset?)null),
                SearchOp.GreaterThan => (end, null),
                SearchOp.LessOrEqual => (null, end.AddSeconds(-1)),
                SearchOp.LessThan => (null, start.AddSeconds(-1)),
                _ => (start, end.AddSeconds(-1)),
            };
            since = low is { } l && (since is null || l > since) ? l : since;
            until = high is { } h && (until is null || h < until) ? h : until;
        }

        var sender = positive.FirstOrDefault(t => t.Field == SearchField.From)?.Value;
        return new ChatSearchPlan(text, since, until, sender);
    }
}

/// <summary>
/// One message a backend found, with what the query may ask of it. <see cref="Text"/>, <see cref="HasAttachment"/>,
/// <see cref="IsMentioned"/> and <see cref="Mentioned"/> need the whole message; a backend leaves them null when
/// <see cref="ChatSearchMatcher.NeedsMessage"/> said they aren't asked about.
/// </summary>
public sealed record ChatSearchCandidate(DateTimeOffset Sent)
{
    /// <summary>The sender's names: account name, display name, addresses.</summary>
    public IReadOnlyList<string> Sender { get; init; } = [];

    /// <summary>Who it was sent to: in a one-on-one chat, the other person; empty elsewhere.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = [];

    public bool IsRead { get; init; }

    /// <summary>The message as plain text, mentions and files by name.</summary>
    public string? Text { get; init; }

    public bool? HasAttachment { get; init; }

    /// <summary>The message mentions the caller (by name, or the whole conversation).</summary>
    public bool? IsMentioned { get; init; }

    /// <summary>The names (account, display, addresses, directory id with and without dashes) of each person mentioned.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? Mentioned { get; init; }
}

/// <summary>
/// Checks a <see cref="ChatSearchCandidate"/> against the whole KQL query: every term and one term of each
/// <see cref="SearchQuery.AnyOf"/> group. Text and names match case-insensitively as substrings (<c>:</c>) or whole
/// (<c>=</c>); a <c>sent</c> date is a UTC day.
/// </summary>
public static class ChatSearchMatcher
{
    /// <summary>
    /// Whether the query asks what only the whole message tells: free text beyond the one substring the backend
    /// matched (<paramref name="backendText"/>), attachments or mentions.
    /// </summary>
    public static bool NeedsMessage(SearchQuery query, string? backendText)
    {
        ArgumentNullException.ThrowIfNull(query);
        var matchedByBackend = false;
        foreach (var term in query.Terms)
        {
            if (!matchedByBackend && !term.Negated && term.Field == SearchField.Any && term.Value == backendText)
            {
                matchedByBackend = true;
                continue;
            }

            if (ReadsMessage(term))
            {
                return true;
            }
        }

        return query.AnyOf.SelectMany(g => g).Any(ReadsMessage);
    }

    public static bool Matches(SearchQuery query, ChatSearchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        return query.Terms.All(t => Matches(t, candidate)) && query.AnyOf.All(g => g.Any(t => Matches(t, candidate)));
    }

    /// <summary>A <c>sent</c> value as the half-open span it names: a UTC day, or the second of an instant.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End)? Range(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (start, start.AddDays(1));
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? (instant, instant.AddSeconds(1))
            : null;
    }

    private static bool ReadsMessage(SearchTerm term) =>
        term.Field is SearchField.Any or SearchField.HasAttachments or SearchField.IsMentioned or SearchField.Mentions;

    private static bool Matches(SearchTerm term, ChatSearchCandidate candidate)
    {
        var holds = term.Field switch
        {
            // The text is left unread only when the backend matched the one free-text term.
            SearchField.Any => candidate.Text is not { } text || Contains(text, term),
            SearchField.From => Names(candidate.Sender, term),
            SearchField.To => Names(candidate.Recipients, term),
            SearchField.Sent => Sent(candidate.Sent, term),
            SearchField.IsRead => Flag(candidate.IsRead, term),
            SearchField.HasAttachments => candidate.HasAttachment is { } has && Flag(has, term),
            SearchField.IsMentioned => candidate.IsMentioned is { } mentioned && Flag(mentioned, term),
            SearchField.Mentions => candidate.Mentioned?.Any(person => Names(person, term)) == true,
            _ => false,
        };
        return holds != term.Negated;
    }

    private static bool Contains(string text, SearchTerm term) => text.Contains(term.Value, StringComparison.OrdinalIgnoreCase);

    private static bool Names(IEnumerable<string> names, SearchTerm term) => term.Op == SearchOp.Equals
        ? names.Any(n => string.Equals(n, term.Value, StringComparison.OrdinalIgnoreCase))
        : names.Any(n => n.Contains(term.Value, StringComparison.OrdinalIgnoreCase));

    private static bool Flag(bool value, SearchTerm term) => bool.TryParse(term.Value, out var wanted) && value == wanted;

    private static bool Sent(DateTimeOffset sent, SearchTerm term)
    {
        if (Range(term.Value) is not { } range)
        {
            return false;
        }

        var (start, end) = range;

        return term.Op switch
        {
            SearchOp.GreaterOrEqual => sent >= start,
            SearchOp.GreaterThan => sent >= end,
            SearchOp.LessOrEqual => sent < end,
            SearchOp.LessThan => sent < start,
            _ => sent >= start && sent < end,
        };
    }
}
