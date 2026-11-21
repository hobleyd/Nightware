using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Contacts;

public static class ContactsServiceCollectionExtensions
{
    /// <summary>Registers the Contacts use cases.</summary>
    public static IServiceCollection AddContactsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListContacts>();
        services.TryAddScoped<GetContact>();
        services.TryAddScoped<CreateContact>();
        services.TryAddScoped<UpdateContact>();
        services.TryAddScoped<DeleteContact>();
        services.TryAddScoped<GetContactPhoto>();
        services.TryAddScoped<SetContactPhoto>();
        services.TryAddScoped<ContactFoldersUseCase>();
        return services;
    }
}
