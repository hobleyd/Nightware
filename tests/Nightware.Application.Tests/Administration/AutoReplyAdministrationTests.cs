using Nightware.Application.Administration;
using Nightware.Application.Administration.AutoReplies;
using Nightware.Domain.Administration;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.Tests.Administration;

public sealed class AutoReplyAdministrationTests
{
    private readonly AdminTestBed _bed = new();
    private readonly IMailboxAutoReplies _replies = Substitute.For<IMailboxAutoReplies>();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private MailboxAutoReplies UseCase() =>
        new(_bed.Actor, _bed.UserAccess(), _bed.SharedMailboxAccess(), _bed.Entities, _bed.SharedMail, _replies, _bed.Audit);

    private static readonly AutoReplySetting Away = new(AutoReplyStatus.AlwaysEnabled) { InternalReplyMessage = "<p>Away</p>" };

    [Fact]
    public async Task A_users_replies_are_written_on_their_mailbox_with_the_entitys_domains_and_audited()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _bed.AddMailbox("carol@acme.example", "m-carol");
        _replies.SetAsync("m-carol", Arg.Any<MailAddress>(), Away, Arg.Any<IReadOnlyList<string>>(), Ct).Returns(Away);

        (await UseCase().SetForUserAsync("u-carol", Away, Ct)).IsSuccess.ShouldBeTrue();

        await _replies.Received(1).SetAsync("m-carol", Arg.Is<MailAddress>(a => a.Value == "carol@acme.example"), Away,
            Arg.Is<IReadOnlyList<string>>(d => d.Contains("acme.example")), Ct);
        _bed.Audit.Single("mailbox.autoreply.set").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_without_a_mailbox_has_no_replies_to_set()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");

        (await UseCase().GetForUserAsync("u-carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _replies.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_schedule_that_ends_before_it_starts_is_refused()
    {
        _bed.AddUser("u-carol", "carol", "carol@acme.example");
        _bed.AddMailbox("carol@acme.example", "m-carol");
        var backwards = new AutoReplySetting(AutoReplyStatus.Scheduled)
        {
            InternalReplyMessage = "<p>x</p>",
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 12, 20), "UTC"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 12, 19), "UTC"),
        };

        (await UseCase().SetForUserAsync("u-carol", backwards, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _replies.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_shared_mailboxs_replies_go_to_its_mail_account()
    {
        var support = SharedMailboxSyncTests.Support();
        _bed.Mailboxes.GetAsync(AdminTestBed.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(support);
        _bed.SharedMail.FindByAddressAsync(support.Address, Arg.Any<CancellationToken>()).Returns(new SharedMailAccount("sm1", "support@acme.example"));
        _replies.GetAsync("sm1", support.Address, Ct).Returns(Away);

        (await UseCase().GetForMailboxAsync(AdminTestBed.Acme, "s-sup", Ct)).Value.ShouldBe(Away);
    }

    [Fact]
    public async Task A_shared_mailbox_not_on_the_mail_server_needs_a_repair_first()
    {
        var support = SharedMailboxSyncTests.Support();
        _bed.Mailboxes.GetAsync(AdminTestBed.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(support);

        (await UseCase().SetForMailboxAsync(AdminTestBed.Acme, "s-sup", Away, Ct)).Error!.Message.ShouldContain("Repair");
    }
}
