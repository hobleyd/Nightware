using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Calls;

namespace Nightware.Adapters.Dav.Calls;

internal static class DavCallsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Calls ports (phase 26, ADR 0009): delegated calls that ring through Talk, on when both
    /// <c>Calls:SealingKey</c> and <c>Talk:PublicBaseUrl</c> are set; otherwise every call is 501.
    /// </summary>
    public static IServiceCollection AddDavCalls(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTalkOptions(configuration);
        services.AddOptions<CallOptions>().Bind(configuration.GetSection(CallOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CallOptions>, CallOptionsValidator>());
        services.TryAddSingleton<CallIdSealer>();
        services.AddTalkPeople();
        // A factory: IUserContext is the host's (the request's caller), so the adapter alone still builds.
        services.TryAddScoped(static sp => ActivatorUtilities.CreateInstance<TalkCalls>(sp));
        services.TryAddScoped<ICallService>(static sp =>
            sp.GetRequiredService<IOptions<CallOptions>>().Value.IsConfigured && sp.GetRequiredService<IOptions<TalkOptions>>().Value.PublicBaseUrl is not null
                ? sp.GetRequiredService<TalkCalls>()
                : UnconfiguredCalls.Instance);
        return services;
    }
}
