using Nightware.Api.Features.Mail.Dto;
using Nightware.Domain.Contacts;

namespace Nightware.Api.Features.Contacts.Dto;

public static class ContactDtoMapper
{
    public static ContactDto ToDto(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return new ContactDto
        {
            Id = contact.Id.Value,
            DisplayName = contact.DisplayName,
            GivenName = contact.GivenName,
            Surname = contact.Surname,
            CompanyName = contact.CompanyName,
            JobTitle = contact.JobTitle,

            // The client prefers an address's own name over the contact's displayName; Outlook names an
            // address after its contact when nothing better is known, and so do we.
            EmailAddresses = [.. contact.EmailAddresses.Select(e => new EmailAddressDto
            {
                Name = e.Name ?? contact.DisplayName ?? e.Address,
                Address = e.Address,
            })],
            BusinessPhones = contact.BusinessPhones,
            HomePhones = contact.HomePhones,
            MobilePhone = contact.MobilePhone,
            BusinessAddress = ToDto(contact.BusinessAddress),
            HomeAddress = ToDto(contact.HomeAddress),
            Birthday = contact.Birthday is { } birthday ? new DateTimeOffset(birthday.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            PersonalNotes = contact.PersonalNotes,
            LastModifiedDateTime = contact.LastModifiedDateTime,
        };
    }

    private static PhysicalAddressDto? ToDto(PhysicalAddress? address) => address is null
        ? null
        : new PhysicalAddressDto
        {
            Street = address.Street,
            City = address.City,
            State = address.State,
            PostalCode = address.PostalCode,
            CountryOrRegion = address.CountryOrRegion,
        };
}
