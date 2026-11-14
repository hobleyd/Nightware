using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application;

namespace Nightware.Api.Composition;

/// <summary>Registers <see cref="NotImplementedPort{T}"/> for every Application port nobody else registered.</summary>
public static class PortFallbacks
{
    /// <summary>Every public interface the Application assembly declares (its ports).</summary>
    public static IReadOnlyList<Type> PortInterfaces { get; } =
        typeof(ApplicationServiceCollectionExtensions).Assembly
            .GetExportedTypes()
            .Where(static t => t.IsInterface && !t.IsGenericTypeDefinition)
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Call after the adapters (and the Api's own <c>IUserContext</c>/<c>IMailboxResolver</c>) are
    /// registered: <c>TryAdd</c> means a real registration always wins.
    /// </summary>
    public static IServiceCollection AddNotImplementedPortFallbacks(this IServiceCollection services)
    {
        var create = typeof(PortFallbacks).GetMethod(nameof(CreateStub), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var port in PortInterfaces)
        {
            var stub = create.MakeGenericMethod(port).Invoke(null, null)!;
            services.TryAdd(ServiceDescriptor.Singleton(port, stub));
        }

        return services;
    }

    private static object CreateStub<T>()
        where T : class => NotImplementedPort<T>.Create();
}
