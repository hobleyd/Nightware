using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Calls;

public static class CallsServiceCollectionExtensions
{
    /// <summary>Registers the Calls use cases (phase 26, ADR 0009): delegated calls that ring through Talk.</summary>
    public static IServiceCollection AddCallsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<PlaceCall>();
        services.TryAddScoped<GetCall>();
        services.TryAddScoped<HangUpCall>();
        return services;
    }
}
