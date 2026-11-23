using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Application.Directory;

namespace Nightware.Adapters.Dav.Talk;

internal static class TalkServiceCollectionExtensions
{
    /// <summary>The <c>Talk</c> section, bound and validated once however many features use it.</summary>
    public static IServiceCollection AddTalkOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(d => d.ServiceType == typeof(IValidateOptions<TalkOptions>) && d.ImplementationType == typeof(TalkOptionsValidator)))
        {
            return services;
        }

        services.AddOptions<TalkOptions>().Bind(configuration.GetSection(TalkOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TalkOptions>, TalkOptionsValidator>());
        return services;
    }

    /// <summary>
    /// The uid ↔ directory id map. A factory, because <c>IUserAccountNames</c> comes from the directory adapter: a host
    /// that registers the Dav adapter alone still builds, and only resolving a Talk port asks for the directory.
    /// </summary>
    public static IServiceCollection AddTalkPeople(this IServiceCollection services)
    {
        services.TryAddScoped(static sp => new TalkPeople(sp.GetRequiredService<IUserAccountNames>()));
        return services;
    }
}
