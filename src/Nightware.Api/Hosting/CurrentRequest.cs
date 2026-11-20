using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Api.Hosting;

/// <summary>
/// The request a DI scope belongs to. <see cref="IHttpContextAccessor"/> follows the async flow, so
/// inside a <c>$batch</c> sub-request it still returns the batch itself; this follows the scope,
/// which each sub-request has of its own.
/// </summary>
public sealed class CurrentRequest
{
    public HttpContext? Context { get; set; }
}

/// <summary>Records the request in its scope's <see cref="CurrentRequest"/>; first step after the envelope.</summary>
public sealed class CurrentRequestMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequestServices.GetRequiredService<CurrentRequest>().Context = context;
        return next(context);
    }
}
