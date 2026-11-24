using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

/// <summary>An outside organizer's invitation is put into the default calendar once, with Sabre told to stay silent.</summary>
public sealed class DavInvitationImporterTests : IDisposable
{
    private const string Outside = "dana@contoso.example";
    private readonly CalendarAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private DavInvitationImporter Importer => new(_h.Scopes, _h.Dav.Client, _h.Discovery);

    private static string WithMethod(string ics) => ics.Replace("BEGIN:VCALENDAR\r\n", "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n", StringComparison.Ordinal);

    private static string Uid => IcalNetCalendarCodec.Load(Read("series-bob.ics")).Value.Events.First().Uid!;

    [Fact]
    public async Task The_invitation_is_put_in_the_default_calendar_without_its_method_and_with_the_organizer_silenced()
    {
        _h.Discovery.DiscoverAsync(new MailboxRef(Outside), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no principal"));
        var href = AlicePersonal + DavInvitationImporter.ResourceName(Uid);
        _h.Serve("PUT", href, 201);

        (await Importer.ImportAsync(AliceMailbox, WithMethod(Read("series-bob.ics").Replace("mailto:alice@nightware.localhost", "mailto:" + Outside, StringComparison.Ordinal)), Ct))
            .IsSuccess.ShouldBeTrue();

        var put = _h.Requests("PUT").ShouldHaveSingleItem();
        Header(put, "If-None-Match").ShouldBe("*");
        var body = BodyOf(put).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        body.ShouldNotContain("METHOD:");
        body.ShouldContain("SCHEDULE-AGENT=CLIENT");
    }

    [Fact]
    public async Task An_invitation_already_there_is_left_alone()
    {
        _h.Discovery.DiscoverAsync(new MailboxRef(Outside), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no principal"));
        _h.Serve("PUT", AlicePersonal + DavInvitationImporter.ResourceName(Uid), 412);

        (await Importer.ImportAsync(AliceMailbox, WithMethod(Read("series-bob.ics").Replace("mailto:alice@nightware.localhost", "mailto:" + Outside, StringComparison.Ordinal)), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_organizer_on_this_server_is_never_imported()
    {
        (await Importer.ImportAsync(AliceMailbox, WithMethod(Read("series-bob.ics")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _h.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public void The_resource_name_is_derived_from_the_uid_and_never_contains_it()
    {
        var name = DavInvitationImporter.ResourceName("weird/../uid@x");

        name.ShouldBe(DavInvitationImporter.ResourceName("weird/../uid@x"));
        name.ShouldStartWith("imported-");
        name.ShouldEndWith(".ics");
        name.ShouldNotContain("/");
        name.Length.ShouldBe("imported-".Length + 32 + ".ics".Length);
    }

    [Fact]
    public void For_import_drops_the_method_and_marks_every_organizer_client_scheduled()
    {
        var edited = IcsEditor.ForImport(WithMethod(Read("series-bob.ics"))).Value.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

        edited.ShouldNotContain("METHOD:");
        var organizers = edited.Split("\r\n").Where(static l => l.StartsWith("ORGANIZER", StringComparison.Ordinal)).ToList();
        organizers.ShouldNotBeEmpty();
        organizers.ShouldAllBe(static l => l.Contains("SCHEDULE-AGENT=CLIENT", StringComparison.Ordinal));
        IcsEditor.ForImport("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n").IsSuccess.ShouldBeFalse();
    }
}
