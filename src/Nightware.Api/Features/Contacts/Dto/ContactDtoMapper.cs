using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Contacts.Dto;

public static class ContactDtoMapper
{
    /// <summary>The time of day Exchange gives every birthday.</summary>
    private static readonly TimeOnly BirthdayTime = new(11, 59);

    /// <summary>Members a request may carry that are the server's to set; they are ignored, as Graph ignores them.</summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "createdDateTime", "lastModifiedDateTime", "changeKey", "parentFolderId", "primaryEmailAddress",
    };

    public static ContactDto ToDto(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        // The client prefers an address's own name over the contact's displayName; Outlook names an
        // address after its contact when nothing better is known, and so do we.
        var emails = contact.EmailAddresses.Select(e => new EmailAddressDto { Name = e.Name ?? contact.DisplayName ?? e.Address, Address = e.Address }).ToList();
        return new ContactDto
        {
            Id = contact.Id.Value,
            CreatedDateTime = contact.CreatedDateTime,
            LastModifiedDateTime = contact.LastModifiedDateTime,
            ChangeKey = contact.ChangeKey?.Trim('"'),
            Categories = contact.Categories,
            ParentFolderId = contact.ParentFolderId?.Value,
            Birthday = contact.Birthday is { } birthday ? new DateTimeOffset(birthday.ToDateTime(BirthdayTime), TimeSpan.Zero) : null,
            FileAs = contact.FileAs ?? string.Empty,
            DisplayName = contact.DisplayName,
            GivenName = contact.GivenName,
            Initials = contact.Initials,
            MiddleName = contact.MiddleName,
            NickName = contact.NickName,
            Surname = contact.Surname,
            Title = contact.Title,
            YomiGivenName = contact.YomiGivenName,
            YomiSurname = contact.YomiSurname,
            YomiCompanyName = contact.YomiCompanyName,
            Generation = contact.Generation,
            ImAddresses = contact.ImAddresses,
            JobTitle = contact.JobTitle,
            CompanyName = contact.CompanyName,
            Department = contact.Department,
            OfficeLocation = contact.OfficeLocation,
            Profession = contact.Profession,
            BusinessHomePage = contact.BusinessHomePage,
            AssistantName = contact.AssistantName,
            Manager = contact.Manager,
            HomePhones = contact.HomePhones,
            MobilePhone = contact.MobilePhone,
            BusinessPhones = contact.BusinessPhones,
            SpouseName = contact.SpouseName,
            PersonalNotes = contact.PersonalNotes ?? string.Empty,
            Children = contact.Children,
            EmailAddresses = emails,
            PrimaryEmailAddress = emails.Count > 0 ? emails[0] : null,
            HomeAddress = ToDto(contact.HomeAddress),
            BusinessAddress = ToDto(contact.BusinessAddress),
            OtherAddress = ToDto(contact.OtherAddress),
        };
    }

    /// <summary>
    /// A create or PATCH body. A member set to null clears it (an empty string, list or address in the
    /// draft); an absent one is left alone. Members Nightware does not store are refused rather than
    /// silently dropped.
    /// </summary>
    public static Result<ContactDraft> ReadDraft(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A JSON body is required.");
        }

        var draft = new ContactDraft();
        foreach (var (key, value) in body)
        {
            if (key.StartsWith('@') || ReadOnly.Contains(key))
            {
                continue;
            }

            var read = Read(draft, key, value);
            if (!read.IsSuccess)
            {
                return read.Error;
            }

            draft = read.Value;
        }

        return draft;
    }

    private static Result<ContactDraft> Read(ContactDraft d, string key, JsonNode? value)
    {
        try
        {
            return key.ToUpperInvariant() switch
            {
                // FN is mandatory: a null displayName leaves it as it is.
                "DISPLAYNAME" => d with { DisplayName = NullableText(value) },
                "GIVENNAME" => d with { GivenName = Text(value) },
                "MIDDLENAME" => d with { MiddleName = Text(value) },
                "SURNAME" => d with { Surname = Text(value) },
                "TITLE" => d with { Title = Text(value) },
                "GENERATION" => d with { Generation = Text(value) },
                "NICKNAME" => d with { NickName = Text(value) },
                "INITIALS" => d with { Initials = Text(value) },
                "FILEAS" => d with { FileAs = Text(value) },
                "YOMIGIVENNAME" => d with { YomiGivenName = Text(value) },
                "YOMISURNAME" => d with { YomiSurname = Text(value) },
                "YOMICOMPANYNAME" => d with { YomiCompanyName = Text(value) },
                "COMPANYNAME" => d with { CompanyName = Text(value) },
                "DEPARTMENT" => d with { Department = Text(value) },
                "JOBTITLE" => d with { JobTitle = Text(value) },
                "PROFESSION" => d with { Profession = Text(value) },
                "OFFICELOCATION" => d with { OfficeLocation = Text(value) },
                "ASSISTANTNAME" => d with { AssistantName = Text(value) },
                "MANAGER" => d with { Manager = Text(value) },
                "SPOUSENAME" => d with { SpouseName = Text(value) },
                "BUSINESSHOMEPAGE" => d with { BusinessHomePage = Text(value) },
                "MOBILEPHONE" => d with { MobilePhone = Text(value) },
                "PERSONALNOTES" => d with { PersonalNotes = Text(value) },
                "BUSINESSPHONES" => d with { BusinessPhones = List(value) },
                "HOMEPHONES" => d with { HomePhones = List(value) },
                "IMADDRESSES" => d with { ImAddresses = List(value) },
                "CHILDREN" => d with { Children = List(value) },
                "CATEGORIES" => d with { Categories = List(value) },
                "EMAILADDRESSES" => d with { EmailAddresses = Emails(value) },
                "HOMEADDRESS" => d with { HomeAddress = Address(value) },
                "BUSINESSADDRESS" => d with { BusinessAddress = Address(value) },
                "OTHERADDRESS" => d with { OtherAddress = Address(value) },
                "BIRTHDAY" => value is null ? d with { ClearBirthday = true } : d with { Birthday = Birthday(value) },
                _ => Error.Validation($"Property '{key}' is not supported on contacts.", key),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            return Error.Validation($"'{key}' has the wrong type or format.", key);
        }
    }

    private static string Text(JsonNode? value) => value?.GetValue<string>() ?? string.Empty;

    private static string? NullableText(JsonNode? value) => value?.GetValue<string>();

    private static List<string> List(JsonNode? value) =>
        value is null ? [] : value.AsArray().Select(n => n?.GetValue<string>() ?? string.Empty).ToList();

    private static List<EmailAddress> Emails(JsonNode? value) =>
        value is null ? [] : value.AsArray().Select(n => new EmailAddress(n?["address"]?.GetValue<string>() ?? string.Empty, n?["name"]?.GetValue<string>())).ToList();

    private static PhysicalAddress Address(JsonNode? value) => value is null
        ? new PhysicalAddress(null, null, null, null, null)
        : new PhysicalAddress(
            value["street"]?.GetValue<string>(), value["city"]?.GetValue<string>(), value["state"]?.GetValue<string>(),
            value["postalCode"]?.GetValue<string>(), value["countryOrRegion"]?.GetValue<string>());

    /// <summary>The calendar day as written: Graph sends midnight UTC, Exchange answers 11:59 UTC.</summary>
    private static DateOnly Birthday(JsonNode value) =>
        DateOnly.FromDateTime(DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture).DateTime);

    private static PhysicalAddressDto ToDto(PhysicalAddress? address) => address is null
        ? new PhysicalAddressDto()
        : new PhysicalAddressDto
        {
            Street = address.Street,
            City = address.City,
            State = address.State,
            PostalCode = address.PostalCode,
            CountryOrRegion = address.CountryOrRegion,
        };
}
