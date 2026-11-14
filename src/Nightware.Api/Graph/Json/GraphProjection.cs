using System.Text.Json.Nodes;

using Nightware.Application.Common.Query;

namespace Nightware.Api.Graph.Json;

/// <summary>
/// Applies <c>$select</c> (and nested <c>$expand(...$select)</c>) to serialised DTOs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>id</c>, <c>@odata.etag</c> and <c>@removed</c> always survive.</item>
/// <item>At the top level a <c>$select</c> drops <c>@odata.type</c> (ADR 0001 §14: Graph omits it on
/// <c>$select</c>ed responses and the client deliberately avoids <c>$select</c> where it needs it).</item>
/// <item>Inside an expanded collection <c>@odata.type</c> is kept (odata-subset.md §7: expanded
/// attachments still carry <c>#microsoft.graph.fileAttachment</c>/<c>itemAttachment</c>).</item>
/// <item>Property names match case-insensitively; unknown names are ignored.</item>
/// </list>
/// </remarks>
public static class GraphProjection
{
    public const string ODataType = "@odata.type";
    public const string ODataEtag = "@odata.etag";
    public const string Removed = "@removed";

    /// <summary>Projects <paramref name="item"/> in place and returns it.</summary>
    public static JsonObject Apply(JsonObject item, IReadOnlySet<string>? select, IReadOnlyList<ExpandClause>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (expand is not null)
        {
            foreach (var clause in expand)
            {
                ApplyNested(item, clause);
            }
        }

        if (select is not null)
        {
            Keep(item, select, keepODataType: false, expand);
        }

        return item;
    }

    private static void ApplyNested(JsonObject item, ExpandClause clause)
    {
        if (clause.Select is null)
        {
            return;
        }

        var key = item.Select(static p => p.Key).FirstOrDefault(k => string.Equals(k, clause.NavigationProperty, StringComparison.OrdinalIgnoreCase));
        switch (key is null ? null : item[key])
        {
            case JsonArray array:
                foreach (var element in array.OfType<JsonObject>())
                {
                    Keep(element, clause.Select, keepODataType: true, expand: null);
                }

                break;
            case JsonObject single:
                Keep(single, clause.Select, keepODataType: true, expand: null);
                break;
        }
    }

    private static void Keep(JsonObject item, IReadOnlySet<string> select, bool keepODataType, IReadOnlyList<ExpandClause>? expand)
    {
        var drop = item
            .Select(static p => p.Key)
            .Where(key => !ShouldKeep(key, select, keepODataType, expand))
            .ToList();
        foreach (var key in drop)
        {
            item.Remove(key);
        }
    }

    private static bool ShouldKeep(string key, IReadOnlySet<string> select, bool keepODataType, IReadOnlyList<ExpandClause>? expand)
    {
        if (key is "id" or ODataEtag or Removed)
        {
            return true;
        }

        if (key == ODataType)
        {
            return keepODataType;
        }

        return select.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
            || (expand?.Any(e => string.Equals(e.NavigationProperty, key, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}
