using System.Text;
using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Contacts.VCard;

public sealed class VCardCodecTests
{
    private const string AppleV3 = """
        BEGIN:VCARD
        VERSION:3.0
        PRODID:-//Apple Inc.//iPhone OS 18.1//EN
        N:Smith;Jane;Q.;Dr.;PhD
        FN:Dr. Jane Q. Smith
        ORG:Contoso\, Ltd.;Research
        TITLE:Head of R&D
        item1.EMAIL;type=INTERNET;type=pref:jane@contoso.com
        item1.X-ABLabel:_$!<Other>!$_
        EMAIL;type=INTERNET;type=HOME:jane.smith@example.org
        TEL;type=CELL;type=VOICE;type=pref:+61 400 000 001
        TEL;type=WORK;type=VOICE:+61 2 9000 0001
        TEL;type=WORK;type=VOICE:+61 2 9000 0002
        TEL;type=HOME;type=VOICE:+61 2 9000 0003
        TEL;type=WORK;type=FAX:+61 2 9000 0009
        ADR;type=WORK;type=pref:;Level 3;1 Example Street;Sydney;NSW;2000;Australia
        ADR;type=HOME:;;22 Home Road\nUnit 4;Newtown;NSW;2042;Australia
        BDAY;value=date:1985-04-12
        NOTE:Met at the conference\; follow up re: grants.\nSecond line\, with comma.
        REV:2026-11-02T09:15:00Z
        PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJ
         C4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/
        UID:6C2F0B6A-1C4E-4D8E-9C1F-AAAABBBBCCCC
        X-SOCIALPROFILE;type=linkedin:https://www.linkedin.com/in/janesmith
        END:VCARD
        """;

    private const string NextcloudV4 = """
        BEGIN:VCARD
        VERSION:4.0
        PRODID:-//Sabre//Sabre VObject 4.5.6//EN
        UID:urn:uuid:4fbe8971-0bc3-424c-9c26-36c3e1eff6b1
        FN:Ana María Núñez
        N:Núñez;Ana María;;;
        EMAIL;TYPE="work,pref";PREF=1:ana@empresa.es
        TEL;VALUE=uri;TYPE="voice,cell":tel:+34-600-000-001
        TEL;VALUE=uri;TYPE=home:tel:+34-910-000-002
        TEL:+34 910 000 003
        ADR;TYPE=work;LABEL="Calle Mayor 1\n28013 Madrid":;;Calle Mayor 1;Madrid;;28013;España
        BDAY:--0412
        REV:20261101T120000Z
        END:VCARD
        """;

    private const string OutlookV21 = "BEGIN:VCARD\r\nVERSION:2.1\r\nN;LANGUAGE=de;CHARSET=utf-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;J=C3=BCrgen\r\nFN;CHARSET=utf-8;ENCODING=QUOTED-PRINTABLE:J=C3=BCrgen M=C3=BCller\r\n"
        + "TEL;WORK;VOICE:+49 30 1234567\r\nTEL;CELL;VOICE:+49 170 1234567\r\nNOTE;ENCODING=QUOTED-PRINTABLE;CHARSET=utf-8:Erste Zeile=0D=0AZweite Zeile mit einem sehr langen Text, der weich umbro=\r\nchen wird.\r\n"
        + "ADR;HOME;ENCODING=QUOTED-PRINTABLE:;;Hauptstra=C3=9Fe 5;Berlin;;10115;Deutschland\r\nEMAIL;PREF;INTERNET:juergen@example.de\r\nEND:VCARD\r\n";

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly VCardCodec _codec = new(new FakeTimeProvider(Now));

    private static string Unfold(string text) => text.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    [Fact]
    public void Parses_an_apple_v3_card()
    {
        var parsed = _codec.Parse(AppleV3, "addressbooks/users/alice/contacts/6C2F.vcf").Value;
        parsed.Version.ShouldBe("3.0");
        parsed.Uid.ShouldBe("6C2F0B6A-1C4E-4D8E-9C1F-AAAABBBBCCCC");
        var contact = parsed.Contact;
        contact.Id.ShouldBe(DavItemId.Resource("addressbooks/users/alice/contacts/6C2F.vcf").ToContactId());
        contact.DisplayName.ShouldBe("Dr. Jane Q. Smith");
        contact.GivenName.ShouldBe("Jane");
        contact.Surname.ShouldBe("Smith");
        contact.CompanyName.ShouldBe("Contoso, Ltd.");
        contact.JobTitle.ShouldBe("Head of R&D");
        contact.EmailAddresses.Select(e => e.Address).ShouldBe(["jane@contoso.com", "jane.smith@example.org"]);
        contact.EmailAddresses[0].Name.ShouldBe("Dr. Jane Q. Smith");
        contact.MobilePhone.ShouldBe("+61 400 000 001");
        contact.BusinessPhones.ShouldBe(["+61 2 9000 0001", "+61 2 9000 0002"]);
        contact.HomePhones.ShouldBe(["+61 2 9000 0003"]);
        contact.BusinessAddress.ShouldBe(new PhysicalAddress("Level 3, 1 Example Street", "Sydney", "NSW", "2000", "Australia"));
        contact.HomeAddress.ShouldBe(new PhysicalAddress("22 Home Road, Unit 4", "Newtown", "NSW", "2042", "Australia"));
        contact.Birthday.ShouldBe(new DateOnly(1985, 4, 12));
        contact.PersonalNotes.ShouldBe("Met at the conference; follow up re: grants.\nSecond line, with comma.");
        contact.LastModifiedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 2, 9, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_a_v4_card_with_quoted_types_and_tel_uris()
    {
        var parsed = _codec.Parse(NextcloudV4).Value;
        parsed.Version.ShouldBe("4.0");
        parsed.Uid.ShouldBe("urn:uuid:4fbe8971-0bc3-424c-9c26-36c3e1eff6b1");
        var contact = parsed.Contact;
        contact.Id.Value.ShouldBeEmpty();
        contact.DisplayName.ShouldBe("Ana María Núñez");
        contact.EmailAddresses.Single().Address.ShouldBe("ana@empresa.es");
        contact.MobilePhone.ShouldBe("+34-600-000-001");
        contact.HomePhones.ShouldBe(["+34-910-000-002"]);
        contact.BusinessPhones.ShouldBe(["+34 910 000 003"]); // untyped → business
        contact.BusinessAddress!.CountryOrRegion.ShouldBe("España");
        contact.HomeAddress.ShouldBeNull();
        contact.Birthday.ShouldBeNull(); // --MMDD has no year
        contact.LastModifiedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parses_a_v21_card_with_quoted_printable_and_bare_types()
    {
        var contact = _codec.Parse(OutlookV21).Value.Contact;
        contact.DisplayName.ShouldBe("Jürgen Müller");
        contact.Surname.ShouldBe("Müller");
        contact.GivenName.ShouldBe("Jürgen");
        contact.BusinessPhones.ShouldBe(["+49 30 1234567"]);
        contact.MobilePhone.ShouldBe("+49 170 1234567");
        contact.PersonalNotes.ShouldBe("Erste Zeile\r\nZweite Zeile mit einem sehr langen Text, der weich umbrochen wird.");
        contact.HomeAddress!.Street.ShouldBe("Hauptstraße 5");
        contact.EmailAddresses.Single().Address.ShouldBe("juergen@example.de");
    }

    [Fact]
    public void Creates_v3_with_escaping_types_and_uid()
    {
        var vcf = _codec.Create(Draft(), "new-uid");

        vcf.ShouldStartWith("BEGIN:VCARD\r\nVERSION:3.0\r\n");
        vcf.ShouldEndWith("END:VCARD\r\n");
        var text = Unfold(vcf);
        text.ShouldContain("UID:new-uid");
        text.ShouldContain("FN:O'Brien\\, Seán");
        text.ShouldContain("N:O'Brien;Seán;;;");
        text.ShouldContain("ORG:Acme\\; Sons");
        text.ShouldContain("EMAIL;TYPE=INTERNET:sean@acme.ie");
        text.ShouldContain("TEL;TYPE=WORK,VOICE:+353 1 000 0001");
        text.ShouldContain("TEL;TYPE=HOME,VOICE:+353 1 000 0002");
        text.ShouldContain("TEL;TYPE=CELL:+353 87 000 0003");
        text.ShouldContain("ADR;TYPE=WORK:;;1 Quay St;Galway;;H91;Ireland");
        text.ShouldContain("BDAY:1990-03-17");
        text.ShouldContain("NOTE:Line one\\nLine two");
        text.ShouldContain("REV:20261115T100000Z");
    }

    [Fact]
    public void Creates_v4_with_lower_case_types_and_basic_dates()
    {
        var text = Unfold(_codec.Create(Draft(), "u4", VCardVersion.V4));
        text.ShouldContain("VERSION:4.0");
        text.ShouldContain("EMAIL:sean@acme.ie");
        text.ShouldContain("TEL;TYPE=work,voice:+353 1 000 0001");
        text.ShouldContain("TEL;TYPE=cell:+353 87 000 0003");
        text.ShouldContain("BDAY:19900317");
        var back = _codec.Parse(text).Value.Contact;
        back.Birthday.ShouldBe(new DateOnly(1990, 3, 17));
        back.BusinessAddress!.City.ShouldBe("Galway");
    }

    [Theory]
    [InlineData(VCardVersion.V3)]
    [InlineData(VCardVersion.V4)]
    public void Round_trips_every_mapped_field(VCardVersion version)
    {
        var draft = Draft();
        var contact = _codec.Parse(_codec.Create(draft, "rt", version)).Value.Contact;
        contact.DisplayName.ShouldBe(draft.DisplayName);
        contact.GivenName.ShouldBe(draft.GivenName);
        contact.Surname.ShouldBe(draft.Surname);
        contact.CompanyName.ShouldBe(draft.CompanyName);
        contact.JobTitle.ShouldBe(draft.JobTitle);
        contact.EmailAddresses.Select(e => e.Address).ShouldBe(draft.EmailAddresses!.Select(e => e.Address));
        contact.BusinessPhones.ShouldBe(draft.BusinessPhones!);
        contact.HomePhones.ShouldBe(draft.HomePhones!);
        contact.MobilePhone.ShouldBe(draft.MobilePhone);
        contact.BusinessAddress.ShouldBe(draft.BusinessAddress);
        contact.HomeAddress.ShouldBe(draft.HomeAddress);
        contact.Birthday.ShouldBe(draft.Birthday);
        contact.PersonalNotes.ShouldBe(draft.PersonalNotes);
        contact.LastModifiedDateTime.ShouldBe(Now);

        var reserialised = _codec.Parse(_codec.Serialize(contact, "rt", version)).Value.Contact;
        reserialised.ShouldBe(contact with
        {
            EmailAddresses = reserialised.EmailAddresses, BusinessPhones = reserialised.BusinessPhones, HomePhones = reserialised.HomePhones,
            ImAddresses = reserialised.ImAddresses, Children = reserialised.Children, Categories = reserialised.Categories,
        });
    }

    [Theory]
    [InlineData(VCardVersion.V3)]
    [InlineData(VCardVersion.V4)]
    public void Round_trips_the_fields_beyond_the_basics(VCardVersion version)
    {
        var draft = new ContactDraft
        {
            GivenName = "Pat", MiddleName = "Q", Surname = "Capture", Title = "Dr", Generation = "Jr", NickName = "Patty, P",
            Department = "R&D", CompanyName = "Example Ltd", Profession = "Boffin", OfficeLocation = "Level 3", Initials = "P.Q.C.",
            FileAs = "Capture, Pat", YomiGivenName = "Patto", YomiSurname = "Kapcha", YomiCompanyName = "Ekzampl",
            AssistantName = "Ann", Manager = "Max", SpouseName = "Sam", Children = ["Kid One", "Kid; Two"],
            ImAddresses = ["pat@im.example.com"], BusinessHomePage = "https://example.com/pat", Categories = ["Red, hot", "Blue"],
            OtherAddress = new PhysicalAddress("3 Other St", "Sydney", "NSW", "2002", "Australia"),
            EmailAddresses = [new EmailAddress("pat@example.com", "Pat at work")],
        };

        var vcf = _codec.Create(draft, "f", version);
        var c = _codec.Parse(vcf).Value.Contact;

        c.DisplayName.ShouldBe("Dr Pat Q Capture Jr", "Exchange composes the display name from the name parts");
        (c.Title, c.GivenName, c.MiddleName, c.Surname, c.Generation).ShouldBe(("Dr", "Pat", "Q", "Capture", "Jr"));
        (c.CompanyName, c.Department, c.Profession, c.OfficeLocation).ShouldBe(("Example Ltd", "R&D", "Boffin", "Level 3"));
        (c.NickName, c.Initials, c.FileAs).ShouldBe(("Patty, P", "P.Q.C.", "Capture, Pat"));
        (c.YomiGivenName, c.YomiSurname, c.YomiCompanyName).ShouldBe(("Patto", "Kapcha", "Ekzampl"));
        (c.AssistantName, c.Manager, c.SpouseName).ShouldBe(("Ann", "Max", "Sam"));
        c.Children.ShouldBe(["Kid One", "Kid; Two"]);
        c.ImAddresses.ShouldBe(["pat@im.example.com"]);
        c.BusinessHomePage.ShouldBe("https://example.com/pat");
        c.Categories.ShouldBe(["Red, hot", "Blue"]);
        c.OtherAddress.ShouldBe(draft.OtherAddress);
        c.HomeAddress.ShouldBeNull("an other address is not the home one");
        c.EmailAddresses.ShouldBe([new EmailAddress("pat@example.com", "Pat at work")]);
        c.CreatedDateTime.ShouldBe(Now);
    }

    [Fact]
    public void Patching_clears_with_empty_values_and_keeps_untouched_name_parts()
    {
        var vcf = _codec.Create(new ContactDraft { GivenName = "Pat", Surname = "Capture", Title = "Dr", Department = "R&D", CompanyName = "Ex",
            OtherAddress = new PhysicalAddress("3 Other St", null, null, null, null), Categories = ["Red"], SpouseName = "Sam", Birthday = new DateOnly(1990, 5, 17) }, "p");

        var patched = _codec.Parse(_codec.Apply(vcf, new ContactDraft
        {
            MiddleName = "Q", Department = string.Empty, OtherAddress = new PhysicalAddress(null, null, null, null, null),
            Categories = [], SpouseName = string.Empty, ClearBirthday = true,
        }).Value).Value.Contact;

        (patched.Title, patched.GivenName, patched.MiddleName, patched.Surname).ShouldBe(("Dr", "Pat", "Q", "Capture"));
        (patched.CompanyName, patched.Department).ShouldBe(("Ex", null));
        patched.OtherAddress.ShouldBeNull();
        patched.Categories.ShouldBeEmpty();
        patched.SpouseName.ShouldBeNull();
        patched.Birthday.ShouldBeNull();
    }

    [Fact]
    public void Photos_are_read_from_v3_base64_and_v4_data_uris()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var b64 = Convert.ToBase64String(bytes);

        var v3 = _codec.ReadPhoto($"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:x\r\nPHOTO;ENCODING=b;TYPE=PNG:{b64}\r\nEND:VCARD\r\n").Value!.Value;
        v3.Bytes.ShouldBe(bytes);
        v3.ContentType.ShouldBe("image/png");
        var v4 = _codec.ReadPhoto($"BEGIN:VCARD\r\nVERSION:4.0\r\nFN:x\r\nPHOTO;VALUE=URI:data:image/jpeg;base64\\,{b64}\r\nEND:VCARD\r\n").Value!.Value;
        v4.Bytes.ShouldBe(bytes);
        v4.ContentType.ShouldBe("image/jpeg");
        _codec.ReadPhoto("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:x\r\nPHOTO:https://example.com/p.jpg\r\nEND:VCARD\r\n").Value.ShouldBeNull();
        _codec.ReadPhoto("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:x\r\nEND:VCARD\r\n").Value.ShouldBeNull();
    }

    [Fact]
    public void Writing_a_photo_makes_the_card_vcard_3()
    {
        var v4 = _codec.Create(new ContactDraft { DisplayName = "x" }, "p", VCardVersion.V4);

        var written = Unfold(_codec.WritePhoto(v4, [1, 2, 3], "image/png").Value);

        written.ShouldContain("VERSION:3.0");
        written.ShouldContain("PHOTO;ENCODING=b;TYPE=PNG:AQID");
    }

    [Fact]
    public void Folds_long_lines_at_75_octets_without_splitting_utf8()
    {
        var note = string.Concat(Enumerable.Repeat("Grüße aus Zürich — 日本語のテキスト 🎉 ", 8));
        var vcf = _codec.Create(new ContactDraft { DisplayName = "Fold", PersonalNotes = note }, "fold");

        foreach (var line in vcf.Split("\r\n"))
        {
            Encoding.UTF8.GetByteCount(line).ShouldBeLessThanOrEqualTo(75);
        }

        vcf.ShouldNotContain("�");
        _codec.Parse(vcf).Value.Contact.PersonalNotes.ShouldBe(note.Trim());
    }

    [Fact]
    public void Apply_replaces_only_touched_properties_and_keeps_photo_groups_and_x_props()
    {
        var edited = _codec.Apply(AppleV3, new ContactDraft
        {
            JobTitle = "CTO",
            MobilePhone = "+61 400 999 999",
            EmailAddresses = [new EmailAddress("jane@new.example")],
            GivenName = "Janet",
        }).Value;

        var text = Unfold(edited);
        text.ShouldContain("VERSION:3.0");
        text.ShouldContain("PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/");
        text.ShouldContain("item1.X-ABLabel:_$!<Other>!$_");
        text.ShouldContain("X-SOCIALPROFILE;type=linkedin:https://www.linkedin.com/in/janesmith");
        text.ShouldContain("N:Smith;Janet;Q.;Dr.;PhD");
        text.ShouldContain("REV:20261115T100000Z");
        var contact = _codec.Parse(edited).Value.Contact;
        contact.JobTitle.ShouldBe("CTO");
        contact.GivenName.ShouldBe("Janet");
        contact.Surname.ShouldBe("Smith");
        contact.MobilePhone.ShouldBe("+61 400 999 999");
        contact.EmailAddresses.Single().Address.ShouldBe("jane@new.example");
        contact.BusinessPhones.Count.ShouldBe(2);
        contact.HomePhones.ShouldBe(["+61 2 9000 0003"]);
        contact.DisplayName.ShouldBe("Dr. Jane Q. Smith");
        contact.Birthday.ShouldBe(new DateOnly(1985, 4, 12));
        text.ShouldContain("TEL;type=WORK;type=FAX:+61 2 9000 0009"); // fax untouched
    }

    [Fact]
    public void Apply_clears_with_empty_strings_and_upgrades_v21()
    {
        var edited = _codec.Apply(OutlookV21, new ContactDraft { PersonalNotes = string.Empty, MobilePhone = string.Empty, CompanyName = "Neu GmbH" }).Value;
        edited.ShouldContain("VERSION:3.0");
        edited.ShouldNotContain("QUOTED-PRINTABLE");
        var contact = _codec.Parse(edited).Value.Contact;
        contact.PersonalNotes.ShouldBeNull();
        contact.MobilePhone.ShouldBeNull();
        contact.CompanyName.ShouldBe("Neu GmbH");
        contact.DisplayName.ShouldBe("Jürgen Müller");
    }

    [Fact]
    public void Fn_is_derived_when_missing()
    {
        Unfold(_codec.Create(new ContactDraft { GivenName = "Ada", Surname = "Lovelace" }, "a")).ShouldContain("FN:Ada Lovelace");
        Unfold(_codec.Create(new ContactDraft { EmailAddresses = [new EmailAddress("x@example.com")] }, "b")).ShouldContain("FN:x@example.com");
        Unfold(_codec.Create(new ContactDraft { CompanyName = "Org Only" }, "c")).ShouldContain("FN:Org Only");
    }

    [Fact]
    public void Errors()
    {
        _codec.Parse("not a card").Error!.Kind.ShouldBe(ErrorKind.Validation);
        _codec.Parse(" ").Error!.Kind.ShouldBe(ErrorKind.Validation);
        _codec.Parse(AppleV3, "../x.vcf").Error!.Kind.ShouldBe(ErrorKind.Validation);
        _codec.Apply("BEGIN:VCALENDAR", new ContactDraft()).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    private static ContactDraft Draft() => new()
    {
        DisplayName = "O'Brien, Seán",
        GivenName = "Seán",
        Surname = "O'Brien",
        CompanyName = "Acme; Sons",
        JobTitle = "Engineer",
        EmailAddresses = [new EmailAddress("sean@acme.ie"), new EmailAddress("sean@home.ie")],
        BusinessPhones = ["+353 1 000 0001"],
        HomePhones = ["+353 1 000 0002"],
        MobilePhone = "+353 87 000 0003",
        BusinessAddress = new PhysicalAddress("1 Quay St", "Galway", null, "H91", "Ireland"),
        HomeAddress = new PhysicalAddress("2 Shop St", "Galway", "Connacht", "H91 X", "Ireland"),
        Birthday = new DateOnly(1990, 3, 17),
        PersonalNotes = "Line one\nLine two",
    };
}

public sealed class VCardReaderTests
{
    [Fact]
    public void Reads_multiple_cards_with_lf_line_ends_and_tab_folding()
    {
        var cards = VCardReader.Read("BEGIN:VCARD\nVERSION:4.0\nFN:One\n\tTwo\nEND:VCARD\nBEGIN:VCARD\nFN:Three\nEND:VCARD\n");
        cards.Count.ShouldBe(2);
        cards[0].First("FN")!.Text.ShouldBe("OneTwo");
        cards[1].Version.ShouldBe("3.0");
    }

    [Fact]
    public void Parses_groups_quoted_parameters_and_colons_in_values()
    {
        var property = VCardReader.ParseLine("item2.URL;TYPE=\"home,pref\";X-LABEL=\"a:b;c\":https://example.com/a:b")!;
        property.Group.ShouldBe("item2");
        property.Name.ShouldBe("URL");
        property.Types.ShouldBe(["home", "pref"], ignoreOrder: true);
        property.Parameters.Single(p => p.Name == "X-LABEL").Values.ShouldBe(["a:b;c"]);
        property.Value.ShouldBe("https://example.com/a:b");
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOCOLON")]
    [InlineData(":value")]
    public void Ignores_garbage_lines(string line) => VCardReader.ParseLine(line).ShouldBeNull();

    [Fact]
    public void Text_escaping_round_trips()
    {
        const string text = "a\\b,c;d\ne";
        VCardText.Unescape(VCardText.Escape(text)).ShouldBe(text);
        VCardText.Escape("x\r\ny").ShouldBe("x\\ny");
        VCardText.SplitStructured(@"a\;b;c\,d;").ShouldBe(["a;b", "c,d", ""]);
        VCardText.Unescape(@"line\Nbreak").ShouldBe("line\nbreak");
    }

    [Fact]
    public void Quoted_printable_with_unknown_charset_falls_back_to_utf8() =>
        VCardReader.DecodeQuotedPrintable("Gr=C3=BC=C3=9Fe", "x-unknown").ShouldBe("Grüße");

    [Fact]
    public void Writer_quotes_parameter_values_that_need_it()
    {
        var card = new VCardDocument("4.0", [new VCardProperty(null, "ADR", [new VCardParameter("LABEL", ["1 Main St, Town"])], ";;1 Main St;Town;;;")]);
        VCardWriter.Write(card).ShouldContain("ADR;LABEL=\"1 Main St, Town\":");
    }
}
