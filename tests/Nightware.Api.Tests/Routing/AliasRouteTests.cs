using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Routing;

/// <summary>
/// Phase 18's folder-scoped item routes (<c>…/messages/{id}</c>, <c>…/contacts/{id}</c>) sit beside the
/// <c>…/delta</c> literals; a delta request must still reach delta (a tampered link is its 410 syncStateNotFound), never the item read.
/// </summary>
public sealed class AliasRouteTests : IAsyncLifetime
{
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly ICalendarStore _calendars = Substitute.For<ICalendarStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_messages).AddSingleton(_contacts).AddSingleton(_calendars));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=garbage")]
    [InlineData("/v1.0/me/contactFolders/f1/contacts/delta?$deltatoken=garbage")]
    public async Task A_delta_request_still_reaches_delta(string path)
    {
        var response = await _host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone, await response.Content.ReadAsStringAsync(Ct));
        _messages.ReceivedCalls().ShouldBeEmpty();
        _contacts.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_one_calendar_group_exists()
    {
        var group = await _host.AuthorizedClient().GetAsync("/v1.0/me/calendarGroups/calendargroup-default", Ct);
        var other = await _host.AuthorizedClient().GetAsync("/v1.0/me/calendarGroups/somewhere-else", Ct);

        group.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await group.ReadObjectAsync())["name"]!.GetValue<string>().ShouldBe("My Calendars");
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("PATCH", "/v1.0/me/calendar")]
    [InlineData("GET", "/v1.0/me/calendar/calendarPermissions")]
    [InlineData("DELETE", "/v1.0/me/calendar/calendarPermissions/p1")]
    public async Task A_default_calendar_route_without_a_default_is_the_lookups_error(string method, string path)
    {
        _calendars.ListAsync(default, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<Nightware.Domain.Calendar.EventCalendar>>.Success([]));
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PATCH" ? new StringContent("""{"name":"x"}""", System.Text.Encoding.UTF8, "application/json") : null,
        };

        (await _host.AuthorizedClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_folder_scoped_count_still_reaches_the_count_not_an_item()
    {
        _messages.ListAsync(default, null, null!, Ct).ReturnsForAnyArgs(new Page<Nightware.Domain.Mail.MailMessage>([]) { Total = 2 });

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders/inbox/messages/$count", Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("2");
    }
}
