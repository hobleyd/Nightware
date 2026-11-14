using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Graph.Routing;

/// <summary>
/// Declares a mailbox-scoped route once and maps it in both Graph forms:
/// <c>/me{pattern}</c> and <c>/users/{user}{pattern}</c>. Bind a <see cref="Mailbox"/> parameter
/// in the handler to get the resolved <see cref="Domain.Kernel.MailboxRef"/>.
/// </summary>
/// <example>
/// <code>
/// graph.MapMailboxGet("/mailFolders/{folderId}", async (Mailbox mailbox, string folderId, ODataQuery odata, ...) => ...);
/// </code>
/// </example>
public static class MailboxRoutes
{
    public const string MePrefix = "/me";
    public const string UsersPrefix = "/users/{" + Mailbox.RouteParameter + "}";

    public static MailboxRouteBuilder MapMailboxGet(this IEndpointRouteBuilder graph, [StringSyntax("Route")] string pattern, Delegate handler) =>
        graph.MapMailbox([HttpMethods.Get], pattern, handler);

    public static MailboxRouteBuilder MapMailboxPost(this IEndpointRouteBuilder graph, [StringSyntax("Route")] string pattern, Delegate handler) =>
        graph.MapMailbox([HttpMethods.Post], pattern, handler);

    public static MailboxRouteBuilder MapMailboxPatch(this IEndpointRouteBuilder graph, [StringSyntax("Route")] string pattern, Delegate handler) =>
        graph.MapMailbox([HttpMethods.Patch], pattern, handler);

    public static MailboxRouteBuilder MapMailboxPut(this IEndpointRouteBuilder graph, [StringSyntax("Route")] string pattern, Delegate handler) =>
        graph.MapMailbox([HttpMethods.Put], pattern, handler);

    public static MailboxRouteBuilder MapMailboxDelete(this IEndpointRouteBuilder graph, [StringSyntax("Route")] string pattern, Delegate handler) =>
        graph.MapMailbox([HttpMethods.Delete], pattern, handler);

    /// <param name="pattern">The path after the mailbox, starting with <c>/</c> (or empty for the mailbox itself).</param>
    public static MailboxRouteBuilder MapMailbox(this IEndpointRouteBuilder graph, IEnumerable<string> methods, [StringSyntax("Route")] string pattern, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        if (pattern.Length > 0 && pattern[0] != '/')
        {
            throw new ArgumentException("A mailbox route pattern must start with '/'.", nameof(pattern));
        }

        var verbs = methods.ToArray();
        var me = graph.MapMethods(MePrefix + pattern, verbs, handler);
        var users = graph.MapMethods(UsersPrefix + pattern, verbs, handler);
        return new MailboxRouteBuilder(me, users);
    }
}

/// <summary>Applies conventions (<c>WithMetadata</c>, <c>AddEndpointFilter</c>, …) to both forms of a mailbox route.</summary>
public sealed class MailboxRouteBuilder(RouteHandlerBuilder me, RouteHandlerBuilder users) : IEndpointConventionBuilder
{
    public RouteHandlerBuilder Me { get; } = me;

    public RouteHandlerBuilder Users { get; } = users;

    public void Add(Action<EndpointBuilder> convention)
    {
        ((IEndpointConventionBuilder)Me).Add(convention);
        ((IEndpointConventionBuilder)Users).Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        ((IEndpointConventionBuilder)Me).Finally(finallyConvention);
        ((IEndpointConventionBuilder)Users).Finally(finallyConvention);
    }
}
