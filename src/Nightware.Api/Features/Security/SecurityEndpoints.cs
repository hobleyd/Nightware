using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Graph;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Security;

public static class SecurityEndpoints
{
    /// <summary>
    /// <c>POST security/threatSubmission/emailThreats</c> is deferred (ADR 0001 §43): NightMail only
    /// submits threats for Microsoft accounts. Mapped so it answers a deliberate <c>501</c>.
    /// </summary>
    public static RouteGroupBuilder MapSecurityEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapPost("/security/threatSubmission/emailThreats", NotAvailable);
        return graph;
    }

    internal static IResult NotAvailable() =>
        GraphResults.Error(Error.Unsupported("Threat submission is not available on NightMail Server."));
}
