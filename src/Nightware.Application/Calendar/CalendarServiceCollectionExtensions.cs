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
        return services;
    }
}
