using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Calendar;

internal static class DavCalendarServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 4.</summary>
    public static IServiceCollection AddDavCalendar(this IServiceCollection services) => services;
}
