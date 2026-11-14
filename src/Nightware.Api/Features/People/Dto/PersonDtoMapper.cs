using Nightware.Domain.Contacts;

namespace Nightware.Api.Features.People.Dto;

public static class PersonDtoMapper
{
    public static PersonDto ToDto(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return new PersonDto
        {
            Id = person.Id,
            DisplayName = person.DisplayName,
            GivenName = person.GivenName,
            Surname = person.Surname,
            CompanyName = person.CompanyName,
            JobTitle = person.JobTitle,
            ScoredEmailAddresses = [.. person.ScoredEmailAddresses.Select(e => new ScoredEmailAddressDto
            {
                Address = e.Address,
                RelevanceScore = person.RelevanceScore,
            })],
        };
    }
}
