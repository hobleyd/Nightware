using Microsoft.Extensions.DependencyInjection;
using Nightware.Application.Mail;
using Nightware.Application.Calendar;
using Nightware.Application.Rooms;
using Nightware.Application.Directory;
using Nightware.Application.Contacts;
using Nightware.Application.People;
using Nightware.Application.Tasks;
using Nightware.Application.MailboxSettings;
using Nightware.Application.Files;
using Nightware.Application.Security;

namespace Nightware.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers every feature's use cases. Feature phases fill their own Add*UseCases, not this file.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services) =>
        services
            .AddMailUseCases()
            .AddCalendarUseCases()
            .AddRoomsUseCases()
            .AddDirectoryUseCases()
            .AddContactsUseCases()
            .AddPeopleUseCases()
            .AddTasksUseCases()
            .AddMailboxSettingsUseCases()
            .AddFilesUseCases()
            .AddSecurityUseCases();
}
