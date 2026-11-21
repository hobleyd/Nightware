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
/// vCard 3/4 ↔ Domain <see cref="Contact"/>. The full mapping table is in the adapter README; in short:
/// <list type="bullet">
/// <item>N ↔ surname;givenName;middleName;title;generation. ORG ↔ companyName;department. TITLE ↔ jobTitle, ROLE ↔ profession.</item>
/// <item>TEL: <c>cell</c> → mobilePhone (first), <c>home</c> → homePhones, <c>work</c> or untyped → businessPhones; fax/pager are ignored.</item>
/// <item>ADR: <c>work</c> → businessAddress, <c>other</c> → otherAddress, <c>home</c> or untyped → homeAddress (first of each).</item>
/// <item>EMAIL carries its Graph name in an <c>X-NAME</c> parameter. IMPP ↔ imAddresses, URL;TYPE=work ↔ businessHomePage,
/// CATEGORIES ↔ categories, NICKNAME ↔ nickName, RELATED;TYPE=spouse/child ↔ spouseName/children.</item>
/// <item>No standard home: X-ASSISTANT, X-MANAGER, X-PHONETIC-FIRST-NAME/-LAST-NAME/-ORG (yomi…), and
/// X-NIGHTWARE-INITIALS, -OFFICE-LOCATION, -FILE-AS, -CREATED.</item>
/// <item>BDAY without a year (<c>--MMDD</c>) cannot be a <see cref="DateOnly"/> and is ignored.</item>
/// </list>
/// Updates go through <see cref="Apply"/>, which replaces only the touched properties and
/// keeps PHOTO, other X- properties, groups and everything else.
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
        card.Properties.Add(new VCardProperty(null, Created, [], Timestamp(_timeProvider.GetUtcNow())));
        ApplyFields(card, draft);
        return VCardWriter.Write(card);
    }

    /// <summary>Serialises a whole contact; prefer <see cref="Apply"/> for updates.</summary>
    public string Serialize(Contact contact, string uid, VCardVersion version = VCardVersion.V3)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return Create(ToDraft(contact), uid, version);
    }

    /// <summary>The photo, decoded: v3 <c>ENCODING=b</c> or a v4 <c>data:</c> URI. Null when there is none (or it is a link).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered codec.")]
    public Result<(byte[] Bytes, string ContentType)?> ReadPhoto(string vcf)
    {
        var card = ReadFirst(vcf);
        if (!card.IsSuccess)
        {
            return card.Error;
        }

        if (card.Value.First("PHOTO") is not { } photo)
        {
            return Result<(byte[], string)?>.Success(null);
        }

        var value = photo.Value.Replace(@"\,", ",", StringComparison.Ordinal).Trim();
        string contentType;
        string base64;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',', StringComparison.Ordinal);
            var header = comma < 0 ? string.Empty : value[5..comma];
            if (comma < 0 || !header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return Result<(byte[], string)?>.Success(null);
            }

            contentType = header[..^";base64".Length];
            base64 = value[(comma + 1)..];
        }
        else if (photo.Parameters.Any(p => p.Is("ENCODING") && p.Values.Any(v => v.Equals("b", StringComparison.OrdinalIgnoreCase) || v.Equals("BASE64", StringComparison.OrdinalIgnoreCase))))
        {
            var type = photo.Parameters.FirstOrDefault(p => p.Is("TYPE"))?.Values is [var first, ..] ? first : "JPEG";
            contentType = type.Contains('/', StringComparison.Ordinal) ? type.ToLowerInvariant() : "image/" + type.ToLowerInvariant();
            base64 = value;
        }
        else
        {
            return Result<(byte[], string)?>.Success(null);
        }

        try
        {
            return Result<(byte[], string)?>.Success((Convert.FromBase64String(base64), string.IsNullOrEmpty(contentType) ? "image/jpeg" : contentType));
        }
        catch (FormatException)
        {
            return Result<(byte[], string)?>.Success(null);
        }
    }

    /// <summary>
    /// Sets the photo. The card is written as vCard 3 with <c>ENCODING=b</c>: Nextcloud stores cards as
    /// vCard 3 and drops a v4 <c>data:</c> photo when it converts (Step 0).
    /// </summary>
    public Result<string> WritePhoto(string existingVcf, byte[] bytes, string contentType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(contentType);
        var card = ReadFirst(existingVcf);
        if (!card.IsSuccess)
        {
            return card.Error;
        }

        card.Value.Version = "3.0";
        var type = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? contentType[6..].ToUpperInvariant() : "JPEG";
        Replace(card.Value, "PHOTO", new VCardProperty(null, "PHOTO", [new VCardParameter("ENCODING", ["b"]), new VCardParameter("TYPE", [type])], Convert.ToBase64String(bytes)));
        Replace(card.Value, "REV", new VCardProperty(null, "REV", [], Timestamp(_timeProvider.GetUtcNow())));
        return VCardWriter.Write(card.Value);
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

    private const string Created = "X-NIGHTWARE-CREATED";

    private void ApplyFields(VCardDocument card, ContactDraft patch)
    {
        var v4 = card.IsVersion4;
        if (patch.Surname is not null || patch.GivenName is not null || patch.MiddleName is not null || patch.Title is not null || patch.Generation is not null)
        {
            var existing = card.First("N")?.Components ?? [];
            string? Part(int index) => index < existing.Count ? existing[index] : null;
            card.RemoveAll(p => p.Is("N"));
            card.Properties.Add(new VCardProperty(null, "N", [], VCardText.JoinStructured(
                [patch.Surname ?? Part(0), patch.GivenName ?? Part(1), patch.MiddleName ?? Part(2), patch.Title ?? Part(3), patch.Generation ?? Part(4)])));
        }

        if (patch.CompanyName is not null || patch.Department is not null)
        {
            var existing = card.First("ORG")?.Components ?? [];
            var company = patch.CompanyName ?? (existing.Count > 0 ? existing[0] : string.Empty);
            var department = patch.Department ?? (existing.Count > 1 ? existing[1] : string.Empty);
            Replace(card, "ORG", company.Length == 0 && department.Length == 0 ? null
                : new VCardProperty(null, "ORG", [], department.Length == 0 ? VCardText.JoinStructured([company]) : VCardText.JoinStructured([company, department])));
        }

        SetText(card, "NICKNAME", patch.NickName);
        SetText(card, "TITLE", patch.JobTitle);
        SetText(card, "ROLE", patch.Profession);
        SetText(card, "X-ASSISTANT", patch.AssistantName);
        SetText(card, "X-MANAGER", patch.Manager);
        SetText(card, "X-PHONETIC-FIRST-NAME", patch.YomiGivenName);
        SetText(card, "X-PHONETIC-LAST-NAME", patch.YomiSurname);
        SetText(card, "X-PHONETIC-ORG", patch.YomiCompanyName);
        SetText(card, "X-NIGHTWARE-INITIALS", patch.Initials);
        SetText(card, "X-NIGHTWARE-OFFICE-LOCATION", patch.OfficeLocation);
        SetText(card, "X-NIGHTWARE-FILE-AS", patch.FileAs);
        SetText(card, "NOTE", patch.PersonalNotes);

        if (patch.EmailAddresses is { } emails)
        {
            card.RemoveAll(p => p.Is("EMAIL"));
            card.Properties.AddRange(emails.Where(e => !string.IsNullOrWhiteSpace(e.Address)).Select(e =>
            {
                List<VCardParameter> parameters = v4 ? [] : [new VCardParameter("TYPE", ["INTERNET"])];
                if (!string.IsNullOrWhiteSpace(e.Name))
                {
                    parameters.Add(new VCardParameter("X-NAME", [e.Name.Trim()]));
                }

                return new VCardProperty(null, "EMAIL", parameters, VCardText.Escape(e.Address.Trim()));
            }));
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

        if (patch.ImAddresses is { } im)
        {
            card.RemoveAll(p => p.Is("IMPP"));
            card.Properties.AddRange(im.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new VCardProperty(null, "IMPP", [], a.Trim())));
        }

        if (patch.BusinessHomePage is not null)
        {
            card.RemoveAll(p => p.Is("URL") && p.HasType("work"));
            if (patch.BusinessHomePage.Length > 0)
            {
                card.Properties.Add(new VCardProperty(null, "URL", [new VCardParameter("TYPE", [v4 ? "work" : "WORK"])], patch.BusinessHomePage.Trim()));
            }
        }

        if (patch.Categories is { } categories)
        {
            var kept = categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => VCardText.Escape(c.Trim())).ToList();
            Replace(card, "CATEGORIES", kept.Count == 0 ? null : new VCardProperty(null, "CATEGORIES", [], string.Join(',', kept)));
        }

        if (patch.SpouseName is not null)
        {
            card.RemoveAll(p => p.Is("RELATED") && p.HasType("spouse"));
            if (patch.SpouseName.Length > 0)
            {
                card.Properties.Add(Related("spouse", patch.SpouseName));
            }
        }

        if (patch.Children is { } children)
        {
            card.RemoveAll(p => p.Is("RELATED") && p.HasType("child"));
            card.Properties.AddRange(children.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => Related("child", c)));
        }

        SetAddress(card, patch.BusinessAddress, v4, "work", p => p.HasType("work"));
        SetAddress(card, patch.OtherAddress, v4, "other", p => p.HasType("other") && !p.HasType("work"));
        SetAddress(card, patch.HomeAddress, v4, "home", p => !p.HasType("work") && !p.HasType("other"));

        if (patch.ClearBirthday)
        {
            Replace(card, "BDAY", null);
        }
        else if (patch.Birthday is { } birthday)
        {
            Replace(card, "BDAY", new VCardProperty(null, "BDAY", [], birthday.ToString(v4 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        // FN is mandatory: Exchange composes it from the name parts when no display name is given.
        var displayName = patch.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) && card.First("FN") is null)
        {
            var n = card.First("N")?.Components ?? [];
            string? Part(int index) => index < n.Count ? n[index] : null;
            displayName = string.Join(' ', new[] { Part(3), Part(1), Part(2), Part(0), Part(4) }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = card.First("ORG")?.Components[0] ?? card.First("EMAIL")?.Text ?? string.Empty;
            }
        }

        if (displayName is not null)
        {
            Replace(card, "FN", Text("FN", displayName));
        }

        // N is optional in v4 but many clients expect it; v3 requires it.
        if (!v4 && card.First("N") is null)
        {
            card.Properties.Add(new VCardProperty(null, "N", [], ";;;;"));
        }

        Replace(card, "REV", new VCardProperty(null, "REV", [], Timestamp(_timeProvider.GetUtcNow())));
    }

    private static Contact ToContact(VCardDocument card, ContactId id)
    {
        var name = card.First("N")?.Components ?? [];
        string? NamePart(int index) => NullIfEmpty(index < name.Count ? name[index] : null);
        var org = card.First("ORG")?.Components ?? [];
        var displayName = card.First("FN")?.Text.Trim();
        var phones = card.All("TEL").Select(p => (Category: Category(p), Number: PhoneNumber(p))).Where(p => p.Number.Length > 0).ToList();
        var addresses = card.All("ADR").ToList();
        var emails = card.All("EMAIL")
            .OrderBy(p => IsPreferred(p) ? 0 : 1)
            .Select(p => (Address: p.Text.Trim(), Name: p.Parameters.FirstOrDefault(x => x.Is("X-NAME"))?.Values is [var name, ..] ? NullIfEmpty(name) : null))
            .Where(e => e.Address.Length > 0)
            .DistinctBy(e => e.Address, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EmailAddress(e.Address, e.Name ?? displayName))
            .ToList();
        var related = card.All("RELATED").ToList();
        return new Contact(id, string.IsNullOrEmpty(displayName) ? null : displayName)
        {
            Surname = NamePart(0),
            GivenName = NamePart(1),
            MiddleName = NamePart(2),
            Title = NamePart(3),
            Generation = NamePart(4),
            NickName = TextOf(card, "NICKNAME"),
            CompanyName = NullIfEmpty(org.Count > 0 ? org[0] : null),
            Department = NullIfEmpty(org.Count > 1 ? org[1] : null),
            JobTitle = TextOf(card, "TITLE"),
            Profession = TextOf(card, "ROLE"),
            AssistantName = TextOf(card, "X-ASSISTANT"),
            Manager = TextOf(card, "X-MANAGER"),
            YomiGivenName = TextOf(card, "X-PHONETIC-FIRST-NAME"),
            YomiSurname = TextOf(card, "X-PHONETIC-LAST-NAME"),
            YomiCompanyName = TextOf(card, "X-PHONETIC-ORG"),
            Initials = TextOf(card, "X-NIGHTWARE-INITIALS"),
            OfficeLocation = TextOf(card, "X-NIGHTWARE-OFFICE-LOCATION"),
            FileAs = TextOf(card, "X-NIGHTWARE-FILE-AS"),
            EmailAddresses = emails,
            BusinessPhones = phones.Where(p => p.Category == PhoneCategory.Business).Select(p => p.Number).ToList(),
            HomePhones = phones.Where(p => p.Category == PhoneCategory.Home).Select(p => p.Number).ToList(),
            MobilePhone = phones.FirstOrDefault(p => p.Category == PhoneCategory.Mobile).Number,
            ImAddresses = card.All("IMPP").Select(p => p.Text.Trim()).Where(a => a.Length > 0).ToList(),
            BusinessHomePage = NullIfEmpty(card.All("URL").FirstOrDefault(p => p.HasType("work"))?.Text),
            Categories = card.All("CATEGORIES").SelectMany(p => VCardText.SplitList(p.Value)).Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
            SpouseName = NullIfEmpty(related.FirstOrDefault(p => p.HasType("spouse"))?.Text),
            Children = related.Where(p => p.HasType("child")).Select(p => p.Text.Trim()).Where(c => c.Length > 0).ToList(),
            BusinessAddress = ToAddress(addresses.FirstOrDefault(a => a.HasType("work"))),
            OtherAddress = ToAddress(addresses.FirstOrDefault(a => a.HasType("other") && !a.HasType("work"))),
            HomeAddress = ToAddress(addresses.FirstOrDefault(a => !a.HasType("work") && !a.HasType("other"))),
            Birthday = ParseDate(card.First("BDAY")?.Text),
            PersonalNotes = TextOf(card, "NOTE"),
            CreatedDateTime = ParseTimestamp(card.First(Created)?.Text) ?? ParseTimestamp(card.First("REV")?.Text),
            LastModifiedDateTime = ParseTimestamp(card.First("REV")?.Text),
        };
    }

    private static ContactDraft ToDraft(Contact c) => new()
    {
        DisplayName = c.DisplayName,
        GivenName = c.GivenName,
        MiddleName = c.MiddleName,
        Surname = c.Surname,
        Title = c.Title,
        Generation = c.Generation,
        NickName = c.NickName,
        Initials = c.Initials,
        FileAs = c.FileAs,
        YomiGivenName = c.YomiGivenName,
        YomiSurname = c.YomiSurname,
        YomiCompanyName = c.YomiCompanyName,
        CompanyName = c.CompanyName,
        Department = c.Department,
        JobTitle = c.JobTitle,
        Profession = c.Profession,
        OfficeLocation = c.OfficeLocation,
        AssistantName = c.AssistantName,
        Manager = c.Manager,
        SpouseName = c.SpouseName,
        BusinessHomePage = c.BusinessHomePage,
        MobilePhone = c.MobilePhone,
        BusinessAddress = c.BusinessAddress,
        HomeAddress = c.HomeAddress,
        OtherAddress = c.OtherAddress,
        PersonalNotes = c.PersonalNotes,
        EmailAddresses = c.EmailAddresses,
        BusinessPhones = c.BusinessPhones,
        HomePhones = c.HomePhones,
        ImAddresses = c.ImAddresses,
        Children = c.Children,
        Categories = c.Categories,
        Birthday = c.Birthday,
    };

    private static string? TextOf(VCardDocument card, string name) => NullIfEmpty(card.First(name)?.Text);

    private static void SetText(VCardDocument card, string name, string? value)
    {
        if (value is not null)
        {
            Replace(card, name, value.Length == 0 ? null : Text(name, value));
        }
    }

    private static void SetAddress(VCardDocument card, PhysicalAddress? address, bool v4, string type, Func<VCardProperty, bool> slot)
    {
        if (address is null)
        {
            return;
        }

        var index = card.Properties.FindIndex(p => p.Is("ADR") && slot(p));
        card.RemoveAll(p => p.Is("ADR") && slot(p));
        if (!address.IsEmpty)
        {
            card.Properties.Insert(index < 0 ? card.Properties.Count : Math.Min(index, card.Properties.Count), Address(address, v4, type));
        }
    }

    private static VCardProperty Related(string type, string name) =>
        new(null, "RELATED", [new VCardParameter("TYPE", [type]), new VCardParameter("VALUE", ["text"])], VCardText.Escape(name.Trim()));

    private static string Timestamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

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
