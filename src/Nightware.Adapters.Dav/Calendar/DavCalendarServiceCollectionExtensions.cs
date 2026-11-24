using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Adapters.Dav.Calendar.Talk;
using Nightware.Application.Calendar;

namespace Nightware.Adapters.Dav.Calendar;

internal static class DavCalendarServiceCollectionExtensions
{
    /// <summary>Registers the calendar, scheduling, free/busy and online-meeting ports (scoped, like the DAV client they use).</summary>
    public static IServiceCollection AddDavCalendar(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<DavCalendarScopes>();
        services.TryAddScoped<DavEventResources>();
        services.TryAddScoped<DavCalendarReader>();
        services.TryAddScoped<ICalendarReader>(sp => sp.GetRequiredService<DavCalendarReader>());
        services.TryAddScoped<DavCalendarWriter>();
        services.TryAddScoped<ICalendarWriter>(sp => sp.GetRequiredService<DavCalendarWriter>());
        services.TryAddScoped<IInvitationImporter, DavInvitationImporter>();
        services.TryAddScoped<DavCalendarStore>();
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarStore>(sp => sp.GetRequiredService<DavCalendarStore>());
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarEventsIn>(sp => sp.GetRequiredService<DavCalendarStore>());
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarViewDelta, DavCalendarViewDelta>();
        services.TryAddScoped<Application.Subscriptions.IEventChangeFeed, DavEventChangeFeed>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Application.Changes.IChangeStateProbe, DavCalendarChangeProbe>());
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarReminders, DavCalendarReminders>();
        services.TryAddScoped<Tasks.Attachments.TaskAttachmentFiles>();
        services.TryAddScoped<DavEventAttachmentStore>();
        services.TryAddScoped<Application.Calendar.Calendars.IEventAttachmentStore>(static sp => sp.GetRequiredService<DavEventAttachmentStore>());
        services.TryAddScoped<Application.Calendar.Calendars.IEventAttachmentUploads, DavEventAttachmentUploads>();
        services.TryAddScoped<Application.Calendar.Calendars.ICalendarPermissionStore, DavCalendarPermissionStore>();
        services.TryAddScoped<ISchedulingActions, DavSchedulingActions>();
        services.TryAddScoped<IFreeBusyReader, DavFreeBusyReader>();
        return services.AddDavTalk(configuration);
    }
}
