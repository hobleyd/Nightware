using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Calendar;

public static class CalendarServiceCollectionExtensions
{
    /// <summary>Registers the Calendar use cases. Owned by the phase that builds Calendar.</summary>
    public static IServiceCollection AddCalendarUseCases(this IServiceCollection services) => services;
}
