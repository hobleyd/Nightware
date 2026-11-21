using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Application.Calendar;

namespace Nightware.Adapters.Dav.Calendar;

internal static class DavCalendarServiceCollectionExtensions
{
    /// <summary>Registers the calendar, scheduling and free/busy ports (scoped, like the DAV client they use).</summary>
    public static IServiceCollection AddDavCalendar(this IServiceCollection services)
    {
        services.TryAddScoped<DavCalendarScopes>();
        services.TryAddScoped<DavEventResources>();
        services.TryAddScoped<DavCalendarReader>();
        services.TryAddScoped<ICalendarReader>(sp => sp.GetRequiredService<DavCalendarReader>());
        services.TryAddScoped<DavCalendarWriter>();
        services.TryAddScoped<ICalendarWriter>(sp => sp.GetRequiredService<DavCalendarWriter>());
        services.TryAddScoped<DavCalendarStore>();
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarStore>(sp => sp.GetRequiredService<DavCalendarStore>());
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarEventsIn>(sp => sp.GetRequiredService<DavCalendarStore>());
        services.TryAddScoped<ISchedulingActions, DavSchedulingActions>();
        services.TryAddScoped<IFreeBusyReader, DavFreeBusyReader>();
        return services;
    }
}
