using System.Globalization;

using Microsoft.Extensions.Primitives;

using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.OData;

/// <summary>
/// Turns a request's OData system query options into an <see cref="ItemQuery"/>.
/// </summary>
/// <remarks>
/// Accepted: <c>$select $filter $orderby $top $skip $skiptoken $search $expand</c>.
/// <c>$deltatoken</c> is accepted and ignored here (delta endpoints read it through
/// <see cref="Json.DeltaLinkCodec"/>). <c>$count</c> and every other <c>$</c>-option is a 400
/// naming the option, except on routes marked <see cref="CountSupported"/>, where <c>$count=true|false</c> is read. Non-<c>$</c> parameters (<c>startDateTime</c>, <c>format</c>) are left to
/// the endpoint. Option names match case-insensitively; a repeated option is a 400.
/// <c>$top</c> above <see cref="MaxTop"/> is clamped silently (the client sends 999 to
/// <c>/users</c> and <c>/people</c>).
/// </remarks>
public static class ODataQueryParser
{
    public const string SelectOption = "$select";
    public const string FilterOption = "$filter";
    public const string OrderByOption = "$orderby";
    public const string TopOption = "$top";
    public const string SkipOption = "$skip";
    public const string SkipTokenOption = "$skiptoken";
    public const string DeltaTokenOption = "$deltatoken";
    public const string SearchOption = "$search";
    public const string ExpandOption = "$expand";
    public const string CountOption = "$count";

    /// <summary>Largest page the façade serves; larger <c>$top</c> values are clamped.</summary>
    public const int MaxTop = 1000;

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        SelectOption, FilterOption, OrderByOption, TopOption, SkipOption, SkipTokenOption, DeltaTokenOption, SearchOption, ExpandOption,
    };

    public static Result<ItemQuery> Parse(IEnumerable<KeyValuePair<string, StringValues>> query, bool countSupported = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = false;
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith('$'))
            {
                continue;
            }

            if (string.Equals(key, CountOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!countSupported)
                {
                    return Error.Validation("The query option '$count' is not supported.", CountOption);
                }

                if (values.Count != 1 || !bool.TryParse(values[0], out count))
                {
                    return Error.Validation("$count must be true or false.", CountOption);
                }

                continue;
            }

            if (!Known.Contains(key))
            {
                return Error.Validation($"The query option '{key}' is not supported.", key);
            }

            if (values.Count != 1 || options.ContainsKey(key))
            {
                return Error.Validation($"The query option '{key}' was specified more than once.", key);
            }

            options[key] = values[0] ?? string.Empty;
        }

        var result = count ? ItemQuery.Default with { Count = true } : ItemQuery.Default;
        if (options.TryGetValue(SelectOption, out var select))
        {
            var parsed = ParseSelect(select, SelectOption);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Select = parsed.Value };
        }

        if (options.TryGetValue(FilterOption, out var filter))
        {
            var parsed = FilterParser.Parse(filter);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Filter = parsed.Value };
        }

        if (options.TryGetValue(OrderByOption, out var orderBy))
        {
            var parsed = ParseOrderBy(orderBy);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { OrderBy = parsed.Value };
        }

        if (options.TryGetValue(TopOption, out var top))
        {
            var parsed = ParseNonNegative(top, TopOption, allowZero: false);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Top = Math.Min(parsed.Value, MaxTop) };
        }

        if (options.TryGetValue(SkipOption, out var skip))
        {
            var parsed = ParseNonNegative(skip, SkipOption, allowZero: true);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Skip = parsed.Value };
        }

        if (options.TryGetValue(SkipTokenOption, out var skipToken) && !string.IsNullOrEmpty(skipToken))
        {
            result = result with { SkipToken = skipToken };
        }

        if (options.TryGetValue(SearchOption, out var search))
        {
            var parsed = KqlParser.Parse(search);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Search = parsed.Value };
        }

        if (options.TryGetValue(ExpandOption, out var expand))
        {
            var parsed = ParseExpand(expand);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            result = result with { Expand = parsed.Value };
        }

        return result;
    }

    /// <summary>Parses a comma-separated <c>$select</c> list; property names only (no paths, no <c>*</c>).</summary>
    public static Result<IReadOnlySet<string>> ParseSelect(string text, string option = SelectOption)
    {
        ArgumentNullException.ThrowIfNull(text);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!IsPropertyName(name))
            {
                return Error.Validation($"'{name}' is not a supported {option} item; only property names are allowed.", option);
            }

            set.Add(name);
        }

        return set.Count == 0
            ? Error.Validation($"{option} is empty.", option)
            : Result<IReadOnlySet<string>>.Success(set);
    }

    public static Result<IReadOnlyList<OrderBy>> ParseOrderBy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clauses = new List<OrderBy>();
        foreach (var raw in text.Split(','))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 2 || !IsPropertyPath(parts[0]))
            {
                return Error.Validation($"'{raw.Trim()}' is not a supported $orderby clause.", OrderByOption);
            }

            var direction = parts.Length == 1 ? "asc" : parts[1].ToLowerInvariant();
            if (direction is not ("asc" or "desc"))
            {
                return Error.Validation($"'{parts[1]}' is not a valid $orderby direction.", OrderByOption);
            }

            clauses.Add(new OrderBy(parts[0], direction == "desc" ? SortDirection.Descending : SortDirection.Ascending));
        }

        return clauses;
    }

    /// <summary>Parses <c>nav</c> or <c>nav($select=a,b)</c> items; only a nested <c>$select</c> is supported.</summary>
    public static Result<IReadOnlyList<ExpandClause>> ParseExpand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clauses = new List<ExpandClause>();
        foreach (var item in SplitTopLevel(text))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var open = trimmed.IndexOf('(', StringComparison.Ordinal);
            var name = open < 0 ? trimmed : trimmed[..open].Trim();
            if (!IsPropertyName(name))
            {
                return Error.Validation($"'{trimmed}' is not a supported $expand item.", ExpandOption);
            }

            if (open < 0)
            {
                clauses.Add(new ExpandClause(name));
                continue;
            }

            if (!trimmed.EndsWith(')'))
            {
                return Error.Validation($"'{trimmed}' has unbalanced parentheses in $expand.", ExpandOption);
            }

            var nested = trimmed[(open + 1)..^1];
            IReadOnlySet<string>? select = null;
            foreach (var option in nested.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = option.IndexOf('=', StringComparison.Ordinal);
                var key = eq < 0 ? option : option[..eq].Trim();
                if (!string.Equals(key, SelectOption, StringComparison.OrdinalIgnoreCase) || eq < 0 || select is not null)
                {
                    return Error.Validation($"The nested option '{key}' in $expand is not supported; only $select is.", ExpandOption);
                }

                var parsed = ParseSelect(option[(eq + 1)..], "$expand $select");
                if (!parsed.IsSuccess)
                {
                    return parsed.Error;
                }

                select = parsed.Value;
            }

            clauses.Add(new ExpandClause(name, select));
        }

        return clauses.Count == 0
            ? Error.Validation("$expand is empty.", ExpandOption)
            : Result<IReadOnlyList<ExpandClause>>.Success(clauses);
    }

    private static Result<int> ParseNonNegative(string text, string option, bool allowZero)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || (!allowZero && value == 0))
        {
            return Error.Validation($"Invalid value '{text}' for {option}; expected a {(allowZero ? "non-negative" : "positive")} integer.", option);
        }

        return value;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return text[start..];
    }

    private static bool IsPropertyName(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) && name.All(static c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsPropertyPath(string path) =>
        path.Split('/').All(IsPropertyName);
}

/// <summary>
/// Endpoint metadata: the route answers <c>$count=true</c> with an exact <c>@odata.count</c> (its adapter reports
/// <see cref="Page{T}.Total"/>). Elsewhere <c>$count</c> stays a 400 rather than being silently ignored.
/// </summary>
public sealed class CountSupported
{
    public static readonly CountSupported Instance = new();

    private CountSupported()
    {
    }
}

public static class CountSupportedExtensions
{
    /// <summary>Marks the route as answering <c>$count</c>.</summary>
    public static RouteHandlerBuilder WithCount(this RouteHandlerBuilder builder) => builder.WithMetadata(CountSupported.Instance);
}
