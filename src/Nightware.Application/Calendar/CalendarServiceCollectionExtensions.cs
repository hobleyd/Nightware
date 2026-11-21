using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Application.Calendar.Events;
using Nightware.Application.Calendar.FreeBusy;
using Nightware.Application.Calendar.Messages;
using Nightware.Application.Calendar.Scheduling;

namespace Nightware.Application.Calendar;

public static class CalendarServiceCollectionExtensions
{
    /// <summary>Registers the Calendar use cases.</summary>
    public static IServiceCollection AddCalendarUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetCalendarView>();
        services.TryAddScoped<GetEvent>();
        services.TryAddScoped<ListEvents>();
        services.TryAddScoped<CreateEvent>();
        services.TryAddScoped<UpdateEvent>();
        services.TryAddScoped<DeleteEvent>();
        services.TryAddScoped<RespondToEvent>();
        services.TryAddScoped<CancelEvent>();
        services.TryAddScoped<ForwardEvent>();
        services.TryAddScoped<GetMessageEvent>();
        services.TryAddScoped<RespondToMessage>();
        services.TryAddScoped<GetSchedule>();
        services.TryAddScoped<FindMeetingTimes>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<Calendars.ListCalendars>();
        services.TryAddScoped<Calendars.GetCalendar>();
        services.TryAddScoped<Calendars.CreateCalendar>();
        services.TryAddScoped<Calendars.UpdateCalendar>();
        services.TryAddScoped<Calendars.DeleteCalendar>();
        services.TryAddScoped<Calendars.ListGroupCalendars>();
        services.TryAddScoped<Calendars.CalendarEventsIn>();
        services.TryAddScoped<Calendars.SyncCalendarView>();
        services.TryAddScoped<Calendars.GetReminderView>();
        services.TryAddScoped<Calendars.SnoozeReminder>();
        services.TryAddScoped<Calendars.DismissReminder>();
        services.TryAddScoped<Calendars.EventAttachmentsUseCase>();
        services.TryAddScoped<Calendars.CalendarPermissionsUseCase>();
        return services;
    }
}
