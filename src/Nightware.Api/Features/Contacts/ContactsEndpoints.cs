using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Contacts;

public static class ContactsEndpoints
{
    /// <summary>
    /// Maps the Contacts routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Contacts; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapContactsEndpoints(this RouteGroupBuilder graph) => graph;
}
