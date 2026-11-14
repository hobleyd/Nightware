using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Application.Common.Identity;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class OutgoingEmailTests
{
    private static readonly IReadOnlyDictionary<string, bool> Drafts = new Dictionary<string, bool> { ["d"] = true };

    [Fact]
    public void Threading_ids_are_bare_and_invalid_ones_dropped()
    {
        var email = OutgoingEmail.FromContent(
            new DraftContent { InReplyTo = "<p@x>", References = ["<a@x>", "b@x", "bad id", ""] },
            new EmailAddress("me@x"), "m@x", Drafts, OutgoingEmail.DraftKeywords, []).Value.ToEmail();

        email.InReplyTo.ShouldBe(["p@x"]);
        email.References.ShouldBe(["a@x", "b@x"]);
    }

    [Fact]
    public void Empty_lists_are_left_out_and_subject_defaults_to_empty()
    {
        var json = JsonSerializer.SerializeToNode(
            OutgoingEmail.FromContent(new DraftContent(), new EmailAddress("me@x", " "), "m@x", Drafts, OutgoingEmail.DraftKeywords, []).Value.ToEmail(),
            JmapJsonContext.Default.Email)!.AsObject();

        json.Select(p => p.Key).ShouldBe(["mailboxIds", "keywords", "messageId", "from", "subject", "textBody", "bodyValues"], ignoreOrder: true);
        json["from"]!.ToJsonString().ShouldBe("""[{"email":"me@x"}]""");
        json["subject"]!.GetValue<string>().ShouldBe(string.Empty);
        json["bodyValues"]!["body"]!["value"]!.GetValue<string>().ShouldBe(string.Empty);
    }

    [Fact]
    public void Extra_headers_and_low_importance_become_text_header_properties()
    {
        var email = OutgoingEmail.FromContent(
            new DraftContent { Importance = Importance.Low, ExtraHeaders = [new InternetHeader("X-Tag", "v")] },
            new EmailAddress("me@x"), "m@x", Drafts, OutgoingEmail.DraftKeywords, []).Value.ToEmail();

        email.Extra!["header:Importance:asText"].GetString().ShouldBe("low");
        email.Extra["header:X-Tag:asText"].GetString().ShouldBe("v");
    }

    [Fact]
    public void Attachment_parts_keep_only_settable_properties_and_default_to_attachment()
    {
        var part = OutgoingEmail.AttachmentPart(new EmailBodyPart { PartId = "3", BlobId = "B", Size = 9, Type = "image/png", Name = "i.png", Cid = "c", Location = "l" });

        part.ShouldBe(new EmailBodyPart { BlobId = "B", Type = "image/png", Name = "i.png", Disposition = "attachment", Cid = "c", Location = "l" });
    }
}

public sealed class ExistingEmailTests
{
    private static Email Recorded() =>
        JsonSerializer.Deserialize(FakeJmapServer.Read("email-get-draft.json"), JmapJsonContext.Default.GetResponseEmail)!.List[0];

    [Fact]
    public void Snapshot_of_the_recorded_draft()
    {
        var snapshot = ExistingEmail.Snapshot(Recorded(), "m@x");

        snapshot.Body.ShouldBe(new ItemBody(BodyType.Html, "<p>see <img src=\"cid:img1@x\"></p>"));
        snapshot.Importance.ShouldBe(Importance.High);
        snapshot.Keywords.Keys.ShouldBe(["$draft", "$seen"], ignoreOrder: true);
        snapshot.Attachments.Select(a => a.PartId).ShouldBe(["3", "4"]);
        snapshot.From!.Email.ShouldBe("alice@nightware.localhost");
        snapshot.References.ShouldBe(["root-0001@example.com", "original-0001@example.com"]);
    }

    [Fact]
    public void A_plain_text_body_and_its_inline_image_count_as_body_and_attachment()
    {
        var email = new Email
        {
            TextBody = [new EmailBodyPart { PartId = "1", Type = "text/plain" }, new EmailBodyPart { PartId = "3", BlobId = "B3", Type = "image/png" }],
            HtmlBody = [new EmailBodyPart { PartId = "1", Type = "text/plain" }, new EmailBodyPart { PartId = "3", BlobId = "B3", Type = "image/png" }],
            Attachments = [new EmailBodyPart { PartId = "2", BlobId = "B2", Type = "text/plain", Name = "notes.txt" }],
            BodyValues = new Dictionary<string, EmailBodyValue> { ["1"] = new("plain") },
        };

        ExistingEmail.Body(email).ShouldBe(new ItemBody(BodyType.Text, "plain"));
        ExistingEmail.Attachments(email).Select(a => a.PartId).ShouldBe(["2", "3"]);
    }

    [Fact]
    public void No_body_values_means_no_body()
    {
        ExistingEmail.Body(new Email { HtmlBody = [new EmailBodyPart { PartId = "1", Type = "text/html" }] }).ShouldBeNull();
    }

    [Theory]
    [InlineData(new[] { "<a@x>" }, "a@x")]
    [InlineData(new[] { "a@x", "b@x" }, null)]
    [InlineData(new[] { "bad id" }, null)]
    public void Only_a_single_valid_message_id_is_kept(string[] ids, string? expected)
    {
        ExistingEmail.MessageId(new Email { MessageId = ids }).ShouldBe(expected);
    }

    [Fact]
    public void Draft_keywords_drop_unset_and_derived_ones_and_always_mark_the_draft()
    {
        var keywords = ExistingEmail.DraftKeywords(new Email { Keywords = new Dictionary<string, bool> { ["$flagged"] = true, ["$seen"] = false, ["$HasNoAttachment"] = true } });

        keywords.ShouldBe(new Dictionary<string, bool> { ["$flagged"] = true, ["$draft"] = true });
    }
}

public sealed class WriteMailboxesTests
{
    private static readonly IReadOnlyList<Mailbox> Boxes =
    [
        new() { Id = "a", Name = "Inbox", Role = "inbox", TotalEmails = 3, UnreadEmails = 1 },
        new() { Id = "h", Name = "P" },
        new() { Id = "i", Name = "C", ParentId = "h" },
        new() { Id = "j", Name = "G", ParentId = "i" },
        new() { Id = "k", Name = "C2", ParentId = "h" },
    ];

    [Fact]
    public void Resolves_ids_well_known_names_and_the_root()
    {
        WriteMailboxes.Resolve(Boxes, new FolderRef.ById(new FolderId("h"))).Value.MailboxId.ShouldBe("h");
        WriteMailboxes.Resolve(Boxes, new FolderRef.WellKnown(WellKnownFolder.Inbox)).Value.MailboxId.ShouldBe("a");
        WriteMailboxes.Resolve(Boxes, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot)).Value.IsRoot.ShouldBeTrue();
        WriteMailboxes.Resolve(Boxes, new FolderRef.ById(MailboxRoles.RootFolderId)).Value.IsRoot.ShouldBeTrue();
        WriteMailboxes.Resolve(Boxes, new FolderRef.WellKnown(WellKnownFolder.Archive)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void Descendants_come_deepest_first()
    {
        WriteMailboxes.Descendants(Boxes, "h").Select(m => m.Id).ShouldBe(["j", "i", "k"]);
        WriteMailboxes.Descendants(Boxes, "j").ShouldBeEmpty();
    }

    [Fact]
    public void Maps_to_a_graph_folder()
    {
        WriteMailboxes.ToFolder(Boxes[0], Boxes).ShouldBe(new MailFolder(new FolderId("a"), "Inbox", MailboxRoles.RootFolderId, 0, 1, 3) { WellKnownName = WellKnownFolder.Inbox });
        WriteMailboxes.ToFolder(Boxes[1], Boxes).ChildFolderCount.ShouldBe(2);
        WriteMailboxes.ToFolder(Boxes[2], Boxes).ParentFolderId.ShouldBe(new FolderId("h"));
    }
}

public sealed class SenderIdentityTests
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");

    [Fact]
    public void The_default_sender_is_named_only_for_the_callers_own_mailbox()
    {
        var user = Substitute.For<IUserContext>();
        user.Principal.Returns(new Principal("u", "alice", "Alice@Nightware.localhost", "Alice A"));

        SenderIdentity.DefaultFrom(Alice, user).ShouldBe(new EmailAddress("alice@nightware.localhost", "Alice A"));
        SenderIdentity.DefaultFrom(new MailboxRef("shared@nightware.localhost"), user).ShouldBe(new EmailAddress("shared@nightware.localhost"));
    }

    [Fact]
    public void Finds_exact_then_wildcard_identities()
    {
        IReadOnlyList<Identity> identities = [new() { Id = "w", Email = "*@nightware.localhost" }, new() { Id = "x", Email = "Alice@nightware.localhost" }];

        SenderIdentity.Find(identities, "alice@nightware.localhost")!.Id.ShouldBe("x");
        SenderIdentity.Find(identities, "bob@nightware.localhost")!.Id.ShouldBe("w");
        SenderIdentity.Find(identities, "bob@example.com").ShouldBeNull();
        SenderIdentity.Find(identities, "no-at-sign").ShouldBeNull();
    }

    [Fact]
    public void Creates_identities_only_for_the_mailbox_itself()
    {
        SenderIdentity.MayCreateFor(Alice, " ALICE@nightware.localhost ").ShouldBeTrue();
        SenderIdentity.MayCreateFor(Alice, "bob@nightware.localhost").ShouldBeFalse();
        SenderIdentity.SendAsDenied("bob@x").ShouldBe(new Error(ErrorKind.Forbidden, "ErrorSendAsDenied", "The mailbox is not allowed to send as 'bob@x'."));
    }
}

public sealed class AttachmentUploadsTests
{
    [Theory]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("png", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void Media_types_are_normalised(string? given, string expected) =>
        AttachmentUploads.MediaType(given).ShouldBe(expected);
}

public sealed class JmapMailWriteRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddJmapAdapter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jmap:SessionUrl"] = "http://stalwart/jmap/session" }).Build());
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddScoped(_ => Substitute.For<IUserContext>());
        services.AddLogging();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Registers_every_write_port()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMailMessageWriter>().ShouldBeOfType<JmapMailMessageWriter>();
        scope.ServiceProvider.GetRequiredService<IMailFolderWriter>().ShouldBeOfType<JmapMailFolderWriter>();
        scope.ServiceProvider.GetRequiredService<IDraftStore>().ShouldBeOfType<JmapDraftStore>();
        scope.ServiceProvider.GetRequiredService<IMailSender>().ShouldBeOfType<JmapMailSender>();
        scope.ServiceProvider.GetRequiredService<IMailImporter>().ShouldBeOfType<JmapMailImporter>();
    }

    [Fact]
    public async Task The_stand_in_locator_answers_501_and_never_replaces_a_real_one()
    {
        using (var provider = Build())
        {
            var locator = provider.CreateScope().ServiceProvider.GetRequiredService<IJmapMessageLocator>();
            (await locator.ResolveAsync(null!, new MessageId("e.x"), TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        }

        var real = Substitute.For<IJmapMessageLocator>();
        using var withReal = Build(s => s.AddScoped(_ => real));
        withReal.CreateScope().ServiceProvider.GetRequiredService<IJmapMessageLocator>().ShouldBeSameAs(real);
    }
}
