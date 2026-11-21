using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Graph.Errors;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph;

/// <summary>
/// The responses a Graph endpoint returns. Every success body is serialised with
/// <see cref="GraphJson"/>, projected with <see cref="GraphProjection"/> and given an
/// <c>@odata.context</c>; every failure goes through <see cref="GraphErrorMapper"/>.
/// </summary>
/// <remarks>
/// <paramref name="contextFragment"/> arguments are the part of <c>@odata.context</c> after
/// <c>$metadata#</c>, without the select list (added automatically), e.g.
/// <c>$"{mailbox.ContextSegment}/mailFolders"</c> → <c>…/$metadata#users('a%40b')/mailFolders(id,displayName)</c>.
/// </remarks>
public static class GraphResults
{
    /// <summary>A single entity (<c>…/$entity</c> context). Pass the request's <c>$select</c>/<c>$expand</c>.</summary>
    public static IResult Entity(object dto, string contextFragment, IReadOnlySet<string>? select = null, IReadOnlyList<ExpandClause>? expand = null) =>
        EntityWithStatus(StatusCodes.Status200OK, dto, contextFragment, select, expand);

    /// <summary>A single entity with 201 Created (Graph uses it for POSTs that create, and for <c>move</c>).</summary>
    public static IResult Created(object dto, string contextFragment, IReadOnlySet<string>? select = null) =>
        EntityWithStatus(StatusCodes.Status201Created, dto, contextFragment, select, null);

    /// <summary>
    /// A created entity with Graph's <c>Location</c> header: <paramref name="location"/> is the new entity's
    /// path below the version root, e.g. <c>users('a%40b')/messages('id')</c>.
    /// </summary>
    public static IResult CreatedAt(object dto, string contextFragment, string location, IReadOnlySet<string>? select = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        var entity = EntityWithStatus(StatusCodes.Status201Created, dto, contextFragment, select, null);
        return new GraphJsonResult(StatusCodes.Status201Created, entity.Build, http => $"{Links(http).VersionRoot(http.Request)}/{location}");
    }

    /// <summary>
    /// A collection page. When <paramref name="skipToken"/> is set an <c>@odata.nextLink</c> is emitted that
    /// repeats the request's own query options plus <c>$skiptoken</c> (see <see cref="GraphLinkBuilder.NextLink"/>).
    /// </summary>
    public static IResult Collection(IEnumerable<object> items, string contextFragment, ItemQuery query, string? skipToken = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        var materialised = items.ToList();
        return new GraphJsonResult(StatusCodes.Status200OK, http =>
        {
            var links = Links(http);
            var value = new JsonArray();
            foreach (var item in materialised)
            {
                value.Add(GraphProjection.Apply(GraphJson.ToObject(item), query.Select, query.Expand));
            }

            var body = new JsonObject
            {
                ["@odata.context"] = links.Context(http.Request, WithSelect(contextFragment, query.Select)),
                ["value"] = value,
            };
            if (skipToken is not null)
            {
                body["@odata.nextLink"] = links.NextLink(http.Request, skipToken);
            }

            return body;
        });
    }

    /// <summary>A <see cref="Page{T}"/> mapped to DTOs, with a nextLink when the adapter has more.</summary>
    public static IResult Page<T>(Page<T> page, Func<T, object> toDto, string contextFragment, ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(toDto);
        return Collection(page.Items.Select(toDto), contextFragment, query, page.SkipToken);
    }

    /// <summary>
    /// One page of a delta feed. Upserts are projected with the cursor's frozen <c>$select</c>; removals
    /// are written as <c>{"@odata.type"?, "id", "@removed": {"reason": "deleted"|"changed"}}</c>. A
    /// <see cref="DeltaPage{T}.NextPage"/> becomes <c>@odata.nextLink</c> (<c>$skiptoken</c>), a
    /// <see cref="DeltaPage{T}.Final"/> becomes <c>@odata.deltaLink</c> (<c>$deltatoken</c>); both wrap
    /// the adapter token with the frozen <c>$select</c>/<c>$top</c> via <see cref="DeltaLinkCodec"/>.
    /// </summary>
    /// <param name="removedODataType">E.g. <c>#microsoft.graph.message</c>; null omits it.</param>
    /// <param name="typeUpserts">Also lead each upsert with <paramref name="removedODataType"/>, as Graph's contact and contactFolder deltas do.</param>
    public static IResult Delta<T>(DeltaPage<T> page, Func<T, object> toDto, DeltaCursor cursor, string contextFragment, string? removedODataType = null, bool typeUpserts = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(toDto);
        ArgumentNullException.ThrowIfNull(cursor);
        return new GraphJsonResult(StatusCodes.Status200OK, http =>
        {
            var links = Links(http);
            var codec = http.RequestServices.GetRequiredService<DeltaLinkCodec>();
            var value = new JsonArray();
            foreach (var entry in page.Entries)
            {
                switch (entry)
                {
                    case DeltaEntry<T>.Upsert upsert:
                        var projected = GraphProjection.Apply(GraphJson.ToObject(toDto(upsert.Item)!), cursor.Select);
                        if (typeUpserts && removedODataType is not null)
                        {
                            var typed = new JsonObject { [GraphProjection.ODataType] = removedODataType };
                            foreach (var (name, member) in projected.ToList())
                            {
                                projected.Remove(name);
                                typed[name] = member;
                            }

                            projected = typed;
                        }

                        value.Add(projected);
                        break;
                    case DeltaEntry<T>.Removed removed:
                        var item = new JsonObject();
                        if (removedODataType is not null)
                        {
                            item[GraphProjection.ODataType] = removedODataType;
                        }

                        item["id"] = removed.Id;
                        item[GraphProjection.Removed] = new JsonObject
                        {
                            ["reason"] = removed.Reason == RemovedReason.Changed ? "changed" : "deleted",
                        };
                        value.Add(item);
                        break;
                }
            }

            var body = new JsonObject
            {
                ["@odata.context"] = links.Context(http.Request, $"Collection({contextFragment})"),
                ["value"] = value,
            };
            if (page.NextPage is { } next)
            {
                body["@odata.nextLink"] = links.Link(http.Request, [new(ODataQueryParser.SkipTokenOption, codec.Encode(new DeltaLinkState(next, cursor.Select, cursor.Top)))]);
            }
            else if (page.Final is { } final)
            {
                body["@odata.deltaLink"] = links.Link(http.Request, [new(ODataQueryParser.DeltaTokenOption, codec.Encode(new DeltaLinkState(final, cursor.Select, cursor.Top)))]);
            }

            return body;
        });
    }

    /// <summary>A Graph error envelope for <paramref name="error"/>.</summary>
    public static IResult Error(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new GraphErrorResult(GraphErrorMapper.Map(error));
    }

    public static IResult NoContent() => Results.NoContent();

    public static IResult Accepted() => Results.StatusCode(StatusCodes.Status202Accepted);

    /// <summary><paramref name="onSuccess"/> for a success, the error envelope otherwise.</summary>
    public static IResult Match<T>(Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Error(result.Error);
    }

    /// <summary><paramref name="onSuccess"/> (default 204) for a success, the error envelope otherwise.</summary>
    public static IResult Match(Result result, Func<IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess ?? NoContent)() : Error(result.Error);

    internal static string WithSelect(string fragment, IReadOnlySet<string>? select) =>
        select is null || select.Count == 0 ? fragment : $"{fragment}({string.Join(',', select)})";

    private static GraphJsonResult EntityWithStatus(int status, object dto, string contextFragment, IReadOnlySet<string>? select, IReadOnlyList<ExpandClause>? expand)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new GraphJsonResult(status, http =>
        {
            var item = GraphProjection.Apply(GraphJson.ToObject(dto), select, expand);
            var body = new JsonObject
            {
                ["@odata.context"] = Links(http).Context(http.Request, WithSelect(contextFragment, select) + "/$entity"),
            };
            foreach (var (key, value) in item.ToList())
            {
                item.Remove(key);
                body[key] = value;
            }

            return body;
        });
    }

    private static GraphLinkBuilder Links(HttpContext http) => http.RequestServices.GetRequiredService<GraphLinkBuilder>();
}

/// <summary>Writes a JSON node built per request (so links can use the request's host).</summary>
public sealed class GraphJsonResult(int statusCode, Func<HttpContext, JsonNode> build, Func<HttpContext, string>? location = null) : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => statusCode;

    internal Func<HttpContext, JsonNode> Build => build;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var body = build(httpContext);
        httpContext.Response.StatusCode = statusCode;
        if (location is not null)
        {
            httpContext.Response.Headers.Location = location(httpContext);
        }

        httpContext.Response.ContentType = "application/json; odata.metadata=minimal; odata.streaming=true; IEEE754Compatible=false; charset=utf-8";
        await JsonSerializer.SerializeAsync(httpContext.Response.Body, body, GraphJson.Options, httpContext.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Writes the Graph error envelope.</summary>
public sealed class GraphErrorResult(GraphErrorDescriptor descriptor) : IResult, IStatusCodeHttpResult
{
    public GraphErrorDescriptor Descriptor { get; } = descriptor;

    public int? StatusCode => Descriptor.StatusCode;

    public Task ExecuteAsync(HttpContext httpContext) => GraphErrorWriter.WriteAsync(httpContext, Descriptor);
}
