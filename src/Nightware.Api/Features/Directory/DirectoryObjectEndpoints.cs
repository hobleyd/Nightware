using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Directory.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Application.Directory;

namespace Nightware.Api.Features.Directory;

/// <summary>
/// Phase 21's directory routes: a user's <c>manager</c>, <c>directReports</c>, <c>memberOf</c> and
/// <c>transitiveMemberOf</c>; <c>/groups</c>, one group and its <c>members</c> (and <c>transitiveMembers</c>); and
/// <c>/organization</c>. Read-only: the console is where groups and managers are changed.
/// </summary>
/// <remarks>
/// <c>/users/{x}/…</c> is the directory's own lookup (as <c>/users/{x}</c> is), not a mailbox route. Groups don't nest
/// and hold only people, so the transitive forms map the same handlers.
/// </remarks>
public static class DirectoryObjectEndpoints
{
    private const string DirectoryObjectsContext = "directoryObjects";

    public static RouteGroupBuilder MapDirectoryObjectEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.MapGet("/me/manager", (ODataQuery odata, GetManager useCase, CancellationToken ct) => ManagerAsync(null, odata, useCase, ct));
        graph.MapGet("/users/{user}/manager", (string user, ODataQuery odata, GetManager useCase, CancellationToken ct) => ManagerAsync(user, odata, useCase, ct));
        graph.MapGet("/me/directReports", (ODataQuery odata, ListDirectReports useCase, CancellationToken ct) => DirectReportsAsync(null, odata, useCase, ct));
        graph.MapGet("/users/{user}/directReports", (string user, ODataQuery odata, ListDirectReports useCase, CancellationToken ct) => DirectReportsAsync(user, odata, useCase, ct));
        foreach (var relationship in new[] { "memberOf", "transitiveMemberOf" })
        {
            graph.MapGet($"/me/{relationship}", (ODataQuery odata, ListMemberOf useCase, CancellationToken ct) => MemberOfAsync(null, odata, useCase, ct));
            graph.MapGet($"/users/{{user}}/{relationship}", (string user, ODataQuery odata, ListMemberOf useCase, CancellationToken ct) => MemberOfAsync(user, odata, useCase, ct));
        }

        graph.MapGet("/groups", ListGroupsAsync);
        graph.MapGet("/groups/{groupId}", GetGroupAsync);
        foreach (var relationship in new[] { "members", "transitiveMembers" })
        {
            graph.MapGet($"/groups/{{groupId}}/{relationship}", MembersAsync);
        }

        graph.MapGet("/organization", ListOrganizationsAsync);
        graph.MapGet("/organization/{organizationId}", GetOrganizationAsync);
        return graph;
    }

    private static async Task<IResult> ManagerAsync(string? user, ODataQuery odata, GetManager useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(user, cancellationToken).ConfigureAwait(false),
            manager => GraphResults.Entity(UserObjectDto.From(manager, odata.Query.Select), DirectoryObjectsContext, odata.Query.Select));

    private static async Task<IResult> DirectReportsAsync(string? user, ODataQuery odata, ListDirectReports useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(user, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, u => UserObjectDto.From(u, odata.Query.Select), DirectoryObjectsContext, odata.Query));

    private static async Task<IResult> MemberOfAsync(string? user, ODataQuery odata, ListMemberOf useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(user, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, GroupObjectDto.From, DirectoryObjectsContext, odata.Query));

    private static async Task<IResult> ListGroupsAsync(ODataQuery odata, ListGroups useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, GroupDto.From, "groups", odata.Query));

    private static async Task<IResult> GetGroupAsync(string groupId, ODataQuery odata, GetGroup useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(groupId, cancellationToken).ConfigureAwait(false),
            group => GraphResults.Entity(GroupDto.From(group), "groups", odata.Query.Select));

    private static async Task<IResult> MembersAsync(string groupId, ODataQuery odata, ListGroupMembers useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(groupId, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, u => UserObjectDto.From(u, odata.Query.Select), DirectoryObjectsContext, odata.Query));

    private static async Task<IResult> ListOrganizationsAsync(ODataQuery odata, ListOrganizations useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            organizations => GraphResults.Collection(organizations.Select(OrganizationDto.From), "organization", odata.Query));

    private static async Task<IResult> GetOrganizationAsync(string organizationId, ODataQuery odata, GetOrganization useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(organizationId, cancellationToken).ConfigureAwait(false),
            organization => GraphResults.Entity(OrganizationDto.From(organization), "organization", odata.Query.Select));
}
