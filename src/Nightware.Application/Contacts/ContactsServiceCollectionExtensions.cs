using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Contacts;

public static class ContactsServiceCollectionExtensions
{
    /// <summary>Registers the Contacts use cases (Phase 5A). Contacts are read-only per the contract.</summary>
    public static IServiceCollection AddContactsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListContacts>();
        return services;
    }
}
