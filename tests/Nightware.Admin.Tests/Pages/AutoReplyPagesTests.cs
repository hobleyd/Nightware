using System.Net;

using Nightware.Admin.Pages;
using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Admin.Tests.Pages;

public sealed class AutoReplyPagesTests : IAsyncDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public void The_form_turns_text_into_simple_html_and_back()
    {
        var html = AutoReplyForm.Html("Away until Monday.\n\nUrgent? Call <Ana>\n0400 000 000");

        html.ShouldBe("<p>Away until Monday.</p><p>Urgent? Call &lt;Ana&gt;<br>0400 000 000</p>");
        AutoReplyForm.Text(html).ShouldBe("Away until Monday.\n\nUrgent? Call <Ana>\n0400 000 000");
    }

    [Fact]
    public void A_scheduled_reply_needs_its_times_and_a_message()
    {
        new AutoReplyForm { Status = "scheduled", InternalMessage = "x" }.ToSetting().Error.ShouldNotBeNull();
        new AutoReplyForm { Status = "on" }.ToSetting().Error.ShouldNotBeNull();

        var (setting, error) = new AutoReplyForm { Status = "scheduled", Audience = "internal", InternalMessage = "Away", Start = "2026-12-20T09:00", End = "2026-12-27T17:00" }.ToSetting();

        error.ShouldBeNull();
        setting!.Status.ShouldBe(AutoReplyStatus.Scheduled);
        setting.ExternalAudience.ShouldBe(ExternalAudience.None);
        setting.ScheduledStartDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 12, 20, 9, 0, 0), "UTC"));
    }

    private void Dan()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        _host.Mail.FindByAddressAsync("dan@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-dan", "dan@acme.example", true));
    }

    [Fact]
    public async Task A_users_replies_show_in_the_form_and_save()
    {
        Dan();
        _host.AutoReplies.GetAsync("m-dan", Arg.Any<MailAddress>(), Arg.Any<CancellationToken>()).Returns(new AutoReplySetting(AutoReplyStatus.AlwaysEnabled)
        {
            InternalReplyMessage = "<p>Back Monday</p>",
            ExternalReplyMessage = "<p>Out of the office</p>",
        });
        _host.AutoReplies.SetAsync("m-dan", Arg.Any<MailAddress>(), Arg.Any<AutoReplySetting>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => Result<AutoReplySetting>.Success(c.Arg<AutoReplySetting>()));
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        string html = await client.GetStringAsync("/users/u-dan/autoreply", Ct);
        html.ShouldContain("Back Monday");
        html.ShouldContain("Out of the office");
        html.ShouldContain("value=\"on\" checked");

        using HttpResponseMessage saved = await ConsoleHost.PostFormAsync(client, "/users/u-dan/autoreply", "/users/u-dan/autoreply", new Dictionary<string, string>
        {
            ["Form.Status"] = "off",
            ["Form.Audience"] = "all",
        });

        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.AutoReplies.Received(1).SetAsync("m-dan", Arg.Any<MailAddress>(), Arg.Is<AutoReplySetting>(s => s.Status == AutoReplyStatus.Disabled),
            Arg.Is<IReadOnlyList<string>>(d => d.Contains("acme.example")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_form_that_does_not_make_sense_is_explained_and_nothing_is_saved()
    {
        Dan();
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        string html = await (await ConsoleHost.PostFormAsync(client, "/users/u-dan/autoreply", "/users/u-dan/autoreply", new Dictionary<string, string> { ["Form.Status"] = "on" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Write the reply people should get.");
        await _host.AutoReplies.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default!, Ct);
    }
}
