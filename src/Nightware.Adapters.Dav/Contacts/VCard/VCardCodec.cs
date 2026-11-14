using System.Globalization;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Contacts.VCard;

public enum VCardVersion
{
    V3,
    V4,
}

/// <summary>A contact parsed from a vCard, with what is needed to write it back.</summary>
public sealed record ParsedVCard(Contact Contact, string? Uid, string Version);

/// <summary>
/// vCard 3/4 ↔ Domain <see cref="Contact"/>: FN, N, ORG, TITLE, EMAIL, TEL (work/home/cell),
/// ADR (work/home), BDAY, NOTE, REV, UID. Mapping rules:
/// <list type="bullet">
/// <item>TEL: <c>cell</c> → mobilePhone (first), <c>home</c> → homePhones, <c>work</c> or untyped → businessPhones; fax/pager are ignored.</item>
/// <item>ADR: <c>work</c> → businessAddress, <c>home</c> or untyped → homeAddress (first of each).</item>
/// <item>BDAY without a year (<c>--MMDD</c>) cannot be a <see cref="DateOnly"/> and is ignored.</item>
/// </list>
/// Updates go through <see cref="Apply"/>, which replaces only the touched properties and
/// keeps PHOTO, X- properties, groups and everything else.
/// </summary>
public sealed class VCardCodec
{
    public const string ProductId = "-//Nightware//Nightware//EN";
    private readonly TimeProvider _timeProvider;

    public VCardCodec(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <param name="href">Resource href relative to the DAV root; when given the contact id is <c>DavItemId.Resource(href)</c>.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered codec.")]
    public Result<ParsedVCard> Parse(string vcf, string? href = null)
    {
        if (href is not null && !DavHrefRules.IsSafeRelative(href))
        {
            return Error.Validation("The resource href is not a safe DAV path.", "href");
        }

        var card = ReadFirst(vcf);
        if (!card.IsSuccess)
        {
            return card.Error;
        }

        var id = href is null ? new ContactId(string.Empty) : DavItemId.Resource(href).ToContactId();
        return new ParsedVCard(ToContact(card.Value, id), card.Value.First("UID")?.Text.Trim(), card.Value.Version);
    }

    /// <summary>A new vCard built from <paramref name="draft"/>.</summary>
    public string Create(ContactDraft draft, string uid, VCardVersion version = VCardVersion.V3)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        var card = new VCardDocument(version == VCardVersion.V4 ? "4.0" : "3.0", [])
        {
        };
        card.Properties.Add(Text("PRODID", ProductId, escape: false));
        card.Properties.Add(Text("UID", uid, escape: false));
        ApplyFields(card, draft);
        return VCardWriter.Write(card);
    }

    /// <summary>Serialises a whole contact; prefer <see cref="Apply"/> for updates.</summary>
    public string Serialize(Contact contact, string uid, VCardVersion version = VCardVersion.V3)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return Create(
            new ContactDraft
            {
                DisplayName = contact.DisplayName,
                GivenName = contact.GivenName,
                Surname = contact.Surname,
                CompanyName = contact.CompanyName,
                JobTitle = contact.JobTitle,
                EmailAddresses = contact.EmailAddresses,
                BusinessPhones = contact.BusinessPhones,
                HomePhones = contact.HomePhones,
                MobilePhone = contact.MobilePhone,
                BusinessAddress = contact.BusinessAddress,
                HomeAddress = contact.HomeAddress,
                Birthday = contact.Birthday,
                PersonalNotes = contact.PersonalNotes,
            },
            uid,
            version);
    }

    /// <summary>Applies the non-null members of <paramref name="patch"/> to an existing vCard (version kept; 2.1 is upgraded to 3.0).</summary>
    public Result<string> Apply(string existingVcf, ContactDraft patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var card = ReadFirst(existingVcf);
        if (!card.IsSuccess)
        {
            return card.Error;
        }

        if (!card.Value.IsVersion4)
        {
            card.Value.Version = "3.0";
        }

        ApplyFields(card.Value, patch);
        return VCardWriter.Write(card.Value);
    }

    private static Result<VCardDocument> ReadFirst(string? vcf)
    {
        if (string.IsNullOrWhiteSpace(vcf))
        {
            return Error.Validation("The content is not a vCard.");
        }

        var cards = VCardReader.Read(vcf);
        return cards.Count == 0 ? Error.Validation("The content is not a vCard.") : cards[0];
    }

    private void ApplyFields(VCardDocument card, ContactDraft patch)
    {
        var v4 = card.IsVersion4;
        if (patch.GivenName is not null || patch.Surname is not null)
        {
            var existing = card.First("N")?.Components ?? [];
            string? Part(int index) => index < existing.Count ? existing[index] : null;
            card.RemoveAll(p => p.Is("N"));
            card.Properties.Add(new VCardProperty(null, "N", [], VCardText.JoinStructured(
                [patch.Surname ?? Part(0), patch.GivenName ?? Part(1), Part(2), Part(3), Part(4)])));
        }

        if (patch.CompanyName is not null)
        {
            Replace(card, "ORG", patch.CompanyName.Length == 0 ? null : new VCardProperty(null, "ORG", [], VCardText.JoinStructured([patch.CompanyName])));
        }

        if (patch.JobTitle is not null)
        {
            Replace(card, "TITLE", patch.JobTitle.Length == 0 ? null : Text("TITLE", patch.JobTitle));
        }

        if (patch.EmailAddresses is { } emails)
        {
            card.RemoveAll(p => p.Is("EMAIL"));
            card.Properties.AddRange(emails.Where(e => !string.IsNullOrWhiteSpace(e.Address)).Select(e =>
                new VCardProperty(null, "EMAIL", v4 ? [] : [new VCardParameter("TYPE", ["INTERNET"])], VCardText.Escape(e.Address.Trim()))));
        }

        if (patch.BusinessPhones is { } business)
        {
            card.RemoveAll(p => p.Is("TEL") && Category(p) == PhoneCategory.Business);
            card.Properties.AddRange(business.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Phone(n, v4, "work", "voice")));
        }

        if (patch.HomePhones is { } home)
        {
            card.RemoveAll(p => p.Is("TEL") && Category(p) == PhoneCategory.Home);
            card.Properties.AddRange(home.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Phone(n, v4, "home", "voice")));
        }

        if (patch.MobilePhone is not null)
        {
            card.RemoveAll(p => p.Is("TEL") && Category(p) == PhoneCategory.Mobile);
            if (patch.MobilePhone.Length > 0)
            {
                card.Properties.Add(Phone(patch.MobilePhone, v4, "cell"));
            }
        }

        if (patch.BusinessAddress is { } businessAddress)
        {
            card.RemoveAll(p => p.Is("ADR") && p.HasType("work"));
            card.Properties.Add(Address(businessAddress, v4, "work"));
        }

        if (patch.HomeAddress is { } homeAddress)
        {
            card.RemoveAll(p => p.Is("ADR") && !p.HasType("work"));
            card.Properties.Add(Address(homeAddress, v4, "home"));
        }

        if (patch.Birthday is { } birthday)
        {
            Replace(card, "BDAY", new VCardProperty(null, "BDAY", [], birthday.ToString(v4 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        if (patch.PersonalNotes is not null)
        {
            Replace(card, "NOTE", patch.PersonalNotes.Length == 0 ? null : Text("NOTE", patch.PersonalNotes));
        }

        // FN is mandatory: keep it in step with the name when no display name is given.
        var displayName = patch.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) && card.First("FN") is null)
        {
            var n = card.First("N")?.Components ?? [];
            displayName = string.Join(' ', new[] { n.Count > 1 ? n[1] : null, n.Count > 0 ? n[0] : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = card.First("ORG")?.Components[0] ?? card.First("EMAIL")?.Text ?? string.Empty;
            }
        }

        if (displayName is not null)
        {
            Replace(card, "FN", Text("FN", displayName));
        }

        if (v4 && card.First("N") is null)
        {
            // N is optional in v4 but many clients expect it; v3 requires it.
        }
        else if (!v4 && card.First("N") is null)
        {
            card.Properties.Add(new VCardProperty(null, "N", [], ";;;;"));
        }

        Replace(card, "REV", new VCardProperty(null, "REV", [], _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
    }

    private static Contact ToContact(VCardDocument card, ContactId id)
    {
        var name = card.First("N")?.Components ?? [];
        var displayName = card.First("FN")?.Text.Trim();
        var phones = card.All("TEL").Select(p => (Category: Category(p), Number: PhoneNumber(p))).Where(p => p.Number.Length > 0).ToList();
        var addresses = card.All("ADR").ToList();
        var emails = card.All("EMAIL")
            .OrderBy(p => IsPreferred(p) ? 0 : 1)
            .Select(p => p.Text.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => new EmailAddress(a, displayName))
            .ToList();
        return new Contact(id, string.IsNullOrEmpty(displayName) ? null : displayName)
        {
            Surname = NullIfEmpty(name.Count > 0 ? name[0] : null),
            GivenName = NullIfEmpty(name.Count > 1 ? name[1] : null),
            CompanyName = NullIfEmpty(card.First("ORG")?.Components[0]),
            JobTitle = NullIfEmpty(card.First("TITLE")?.Text),
            EmailAddresses = emails,
            BusinessPhones = phones.Where(p => p.Category == PhoneCategory.Business).Select(p => p.Number).ToList(),
            HomePhones = phones.Where(p => p.Category == PhoneCategory.Home).Select(p => p.Number).ToList(),
            MobilePhone = phones.FirstOrDefault(p => p.Category == PhoneCategory.Mobile).Number,
            BusinessAddress = ToAddress(addresses.FirstOrDefault(a => a.HasType("work"))),
            HomeAddress = ToAddress(addresses.FirstOrDefault(a => !a.HasType("work"))),
            Birthday = ParseDate(card.First("BDAY")?.Text),
            PersonalNotes = NullIfEmpty(card.First("NOTE")?.Text),
            LastModifiedDateTime = ParseTimestamp(card.First("REV")?.Text),
        };
    }

    private enum PhoneCategory
    {
        Business,
        Home,
        Mobile,
        Ignored,
    }

    private static PhoneCategory Category(VCardProperty phone)
    {
        var types = phone.Types;
        if (types.Contains("fax") || types.Contains("pager") || types.Contains("textphone"))
        {
            return PhoneCategory.Ignored;
        }

        if (types.Contains("cell") || types.Contains("mobile") || types.Contains("iphone"))
        {
            return PhoneCategory.Mobile;
        }

        return types.Contains("home") && !types.Contains("work") ? PhoneCategory.Home : PhoneCategory.Business;
    }

    private static string PhoneNumber(VCardProperty phone)
    {
        var text = phone.Text.Trim();
        return text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? text[4..].Trim() : text;
    }

    private static bool IsPreferred(VCardProperty property) =>
        property.HasType("pref") || property.Parameters.Any(p => string.Equals(p.Name, "PREF", StringComparison.OrdinalIgnoreCase));

    private static PhysicalAddress? ToAddress(VCardProperty? address)
    {
        if (address is null)
        {
            return null;
        }

        var c = address.Components;
        string? Part(int index) => index < c.Count ? NullIfEmpty(c[index].Replace("\n", ", ", StringComparison.Ordinal)) : null;
        var street = string.Join(", ", new[] { Part(1), Part(2) }.Where(s => s is not null));
        var result = new PhysicalAddress(NullIfEmpty(street), Part(3), Part(4), Part(5), Part(6));
        return result is { Street: null, City: null, State: null, PostalCode: null, CountryOrRegion: null } ? null : result;
    }

    private static VCardProperty Address(PhysicalAddress address, bool v4, string type) =>
        new(null, "ADR", [new VCardParameter("TYPE", [v4 ? type : type.ToUpperInvariant()])], VCardText.JoinStructured(
            [string.Empty, string.Empty, address.Street, address.City, address.State, address.PostalCode, address.CountryOrRegion]));

    private static VCardProperty Phone(string number, bool v4, params string[] types) =>
        new(null, "TEL", [new VCardParameter("TYPE", v4 ? types : types.Select(t => t.ToUpperInvariant()).ToArray())], VCardText.Escape(number.Trim()));

    private static VCardProperty Text(string name, string value, bool escape = true) =>
        new(null, name, [], escape ? VCardText.Escape(value) : value);

    private static void Replace(VCardDocument card, string name, VCardProperty? replacement)
    {
        var index = card.Properties.FindIndex(p => p.Is(name));
        card.RemoveAll(p => p.Is(name));
        if (replacement is not null)
        {
            card.Properties.Insert(index < 0 ? card.Properties.Count : Math.Min(index, card.Properties.Count), replacement);
        }
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var datePart = value.Trim().Split('T')[0];
        return DateOnly.TryParseExact(datePart, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ssK", "yyyyMMdd'T'HHmmssK", "yyyy-MM-dd", "yyyyMMdd"];
        return DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp
            : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
