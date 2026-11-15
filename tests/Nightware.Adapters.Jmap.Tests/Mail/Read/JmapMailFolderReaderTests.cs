using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailFolderReaderTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Alice = new(JmapTestHost.AliceAddress);

    private static IMailFolderReader Reader(JmapTestHost host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IMailFolderReader>();

    private static Task<JmapTestHost> HostAsync()
    {
        var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        return Task.FromResult(host);
    }

    [Fact]
    public async Task Top_level_folders_report_the_synthetic_root_as_parent()
    {
        await using var host = await HostAsync();

        var page = (await Reader(host).ListAsync(Alice, null, new ItemQuery { Top = 100 }, Ct)).Value;

        // Role folders first (sortOrder, then name), then the user's.
        page.Items.Select(f => f.DisplayName).ShouldBe(["Deleted Items", "Drafts", "Inbox", "Junk Mail", "Sent Items", "Archive 2025"]);
        page.Items.ShouldAllBe(f => f.ParentFolderId == MailboxRoles.RootFolderId);
        page.Items.Single(f => f.Id.Value == "a").ShouldBe(new MailFolder(new FolderId("a"), "Inbox", MailboxRoles.RootFolderId, 1, 3, 4) { WellKnownName = WellKnownFolder.Inbox });
        page.SkipToken.ShouldBeNull();

        var call = host.Calls("Mailbox/get").ShouldHaveSingleItem();
        call.ToJsonString().ShouldBe("""[["Mailbox/get",{"accountId":"c","properties":["id","name","parentId","role","sortOrder","totalEmails","unreadEmails"]},"c0"]]""");
    }

    [Fact]
    public async Task The_root_by_name_or_id_lists_the_top_level()
    {
        await using var host = await HostAsync();

        (await Reader(host).ListAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), ItemQuery.Default, Ct)).Value.Items.Count.ShouldBe(6);
        (await Reader(host).ListAsync(Alice, new FolderRef.ById(MailboxRoles.RootFolderId), new ItemQuery { Top = 2 }, Ct)).Value.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Children_of_a_well_known_folder()
    {
        await using var host = await HostAsync();

        var page = (await Reader(host).ListAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), ItemQuery.Default, Ct)).Value;

        page.Items.ShouldBe([new MailFolder(new FolderId("f"), "Projects", new FolderId("a"), 0, 1, 12)]);
    }

    [Fact]
    public async Task Pages_by_offset_with_a_skip_token()
    {
        await using var host = await HostAsync();
        var reader = Reader(host);

        var first = (await reader.ListAsync(Alice, null, new ItemQuery { Top = 4 }, Ct)).Value;
        var second = (await reader.ListAsync(Alice, null, new ItemQuery { Top = 4, Skip = 0, SkipToken = first.SkipToken }, Ct)).Value;

        first.SkipToken.ShouldBe("4");
        second.Items.Select(f => f.Id.Value).ShouldBe(["e", "g"]);
        second.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_parents_and_bad_tokens_fail()
    {
        await using var host = await HostAsync();

        (await Reader(host).ListAsync(Alice, new FolderRef.ById(new FolderId("AAMk=")), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).ListAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Outbox), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).ListAsync(Alice, null, new ItemQuery { SkipToken = "x" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Gets_by_well_known_name_id_and_root()
    {
        await using var host = await HostAsync();
        var reader = Reader(host);

        (await reader.GetAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.DeletedItems), Ct)).Value.Id.ShouldBe(new FolderId("b"));
        (await reader.GetAsync(Alice, new FolderRef.ById(new FolderId("f")), Ct)).Value.DisplayName.ShouldBe("Projects");
        var root = (await reader.GetAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Ct)).Value;
        root.Id.ShouldBe(MailboxRoles.RootFolderId);
        root.ParentFolderId.ShouldBeNull();
        root.ChildFolderCount.ShouldBe(6);
        (await reader.GetAsync(Alice, new FolderRef.ById(new FolderId("zz")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_shared_mailbox_probe_reads_the_shared_account_or_is_not_found()
    {
        await using var host = await HostAsync();

        // ADR 0001 §19: a mailbox the session does not share is a 404 (403 when it lacks mail —
        // JmapAccountResolverTests covers that case).
        (await Reader(host).GetAsync(new MailboxRef("bob@nightware.localhost"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).IsSuccess.ShouldBeTrue();
        host.Calls("Mailbox/get").ShouldHaveSingleItem()[0]![1]!["accountId"]!.GetValue<string>().ShouldBe("d");
        (await Reader(host).GetAsync(new MailboxRef("nobody@nightware.localhost"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Backend_method_errors_are_passed_on()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.On("Mailbox/get", """[["error",{"type":"forbidden"},"c0"]]""");

        (await Reader(host).ListAsync(Alice, null, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Reader(host).GetAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
