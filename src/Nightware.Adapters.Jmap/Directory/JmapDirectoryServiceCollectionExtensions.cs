using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Directory;

namespace Nightware.Adapters.Jmap.Directory;

internal static class JmapDirectoryServiceCollectionExtensions
{
    /// <summary>Registers the mail server's part of the directory: shared mailboxes as profiles (phase 10).</summary>
    public static IServiceCollection AddJmapDirectory(this IServiceCollection services)
    {
        services.TryAddScoped<ISharedMailboxDirectory, JmapSharedMailboxDirectory>();
        return services;
    }
}
