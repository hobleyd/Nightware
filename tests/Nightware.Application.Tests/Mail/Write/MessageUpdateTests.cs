using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class MessageUpdateTests
{
    private static readonly EmailAddress Alex = new("alex@example.com");

    private static DraftUpdate Complete() => new()
    {
        Subject = "s",
        Body = new ItemBody(BodyType.Text, "b"),
        ToRecipients = [Alex],
        CcRecipients = [],
        BccRecipients = [],
        Importance = Importance.Low,
    };

    [Fact]
    public void Missing_names_every_absent_field()
    {
        new DraftUpdate().Missing().ShouldBe(
            ["subject", "body", "toRecipients", "ccRecipients", "bccRecipients", "importance"], ignoreOrder: true);
        Complete().Missing().ShouldBeEmpty();
        (Complete() with { Importance = null }).Missing().ShouldBe(["importance"]);
    }

    [Fact]
    public void Over_null_uses_the_update_and_defaults_the_rest()
    {
        var content = new DraftUpdate { Subject = "s" }.Over(null);

        content.Subject.ShouldBe("s");
        content.Body.ShouldBeNull();
        content.ToRecipients.ShouldBeEmpty();
        content.CcRecipients.ShouldBeEmpty();
        content.BccRecipients.ShouldBeEmpty();
        content.Importance.ShouldBe(Importance.Normal);
    }

    [Fact]
    public void Over_keeps_the_current_values_for_absent_fields()
    {
        var current = new MailMessage(new MessageId("d.x"))
        {
            Subject = "old",
            Body = new ItemBody(BodyType.Html, "<p>old</p>"),
            ToRecipients = [new EmailAddress("old@example.com")],
            CcRecipients = [new EmailAddress("cc@example.com")],
            BccRecipients = [new EmailAddress("bcc@example.com")],
            Importance = Importance.High,
        };

        var content = new DraftUpdate { Subject = "new", CcRecipients = [] }.Over(current);

        content.Subject.ShouldBe("new");
        content.Body.ShouldBe(current.Body);
        content.ToRecipients.ShouldBe(current.ToRecipients);
        content.CcRecipients.ShouldBeEmpty();
        content.BccRecipients.ShouldBe(current.BccRecipients);
        content.Importance.ShouldBe(Importance.High);
    }

    [Fact]
    public void Over_a_complete_update_ignores_the_current_message()
    {
        var content = Complete().Over(new MailMessage(new MessageId("d.x")) { Subject = "old", Importance = Importance.High });

        content.Subject.ShouldBe("s");
        content.ToRecipients.ShouldBe([Alex]);
        content.Importance.ShouldBe(Importance.Low);
    }

    [Fact]
    public void Has_patch_only_when_a_patch_field_is_set()
    {
        new MessageUpdate(null, null).HasPatch.ShouldBeFalse();
        new MessageUpdate(new MessagePatch(), null).HasPatch.ShouldBeFalse();
        new MessageUpdate(new MessagePatch { IsRead = false }, null).HasPatch.ShouldBeTrue();
        new MessageUpdate(new MessagePatch { Flag = FollowUpFlag.NotFlagged }, null).HasPatch.ShouldBeTrue();
        new MessageUpdate(new MessagePatch { Categories = [] }, null).HasPatch.ShouldBeTrue();
        new MessageUpdate(new MessagePatch { Importance = Importance.Low }, null).HasPatch.ShouldBeTrue();
    }
}
