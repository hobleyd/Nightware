using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMessageLocatorTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MessageId Draft = MailItemId.ForDraft("abc@nightware.localhost").ToMessageId();

    private static async Task<(IJmapMessageLocator Locator, JmapAccount Account)> ArrangeAsync(JmapTestHost host)
    {
        var scope = host.Services.CreateScope().ServiceProvider;
        var account = (await scope.GetRequiredService<IJmapClient>().ResolveAccountAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).Value;
        return (scope.GetRequiredService<IJmapMessageLocator>(), account);
    }

    private static JmapTestHost Host()
    {
        var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        return host;
    }

    [Fact]
    public async Task An_email_id_resolves_without_a_request()
    {
        await using var host = Host();
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, new MessageId("e.Maaa1"), Ct)).Value.ShouldBe("Maaa1");
        host.ApiBodies().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_id_is_a_validation_error()
    {
        await using var host = Host();
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, new MessageId("AAMkAGVm="), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_draft_id_is_found_by_message_id_in_drafts_and_confirmed()
    {
        await using var host = Host();
        // "Contains" matching returns a near miss too; only the exact Message-ID counts, newest first.
        host.On("Email/query", """
            [["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":["d1","d2","d3"]},"c0"],
             ["Email/get",{"accountId":"c","state":"s","list":[
               {"id":"d1","messageId":["xabc@nightware.localhost"],"receivedAt":"2026-11-14T10:00:00Z"},
               {"id":"d2","messageId":["abc@nightware.localhost"],"receivedAt":"2026-11-14T10:00:00Z"},
               {"id":"d3","messageId":["abc@nightware.localhost"],"receivedAt":"2026-11-14T11:00:00Z"}],"notFound":[]},"c1"]]
            """);
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, Draft, Ct)).Value.ShouldBe("d3");
        var calls = host.Calls("Email/query").ShouldHaveSingleItem();
        calls[0]![1]!["filter"]!.ToJsonString().ShouldBe("""{"operator":"AND","conditions":[{"inMailbox":"d"},{"header":["Message-ID","abc@nightware.localhost"]}]}""");
        calls[1]![1]!["properties"]!.ToJsonString().ShouldBe("""["id","messageId","receivedAt"]""");
    }

    [Fact]
    public async Task A_draft_that_matches_nothing_is_not_found()
    {
        await using var host = Host();
        host.On("Email/query", """
            [["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":["d1"]},"c0"],
             ["Email/get",{"accountId":"c","state":"s","list":[{"id":"d1","messageId":["other@x"]}],"notFound":[]},"c1"]]
            """);
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, Draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Without_a_drafts_mailbox_a_draft_id_is_not_found()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.On("Mailbox/get", """[["Mailbox/get",{"accountId":"c","state":"m","list":[{"id":"a","name":"Inbox","role":"inbox"}],"notFound":[]},"c0"]]""");
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, Draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        host.Calls("Email/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task Backend_errors_are_passed_on()
    {
        await using var host = Host();
        host.On("Email/query", """[["error",{"type":"forbidden"},"c0"],["error",{"type":"invalidResultReference"},"c1"]]""");
        var (locator, account) = await ArrangeAsync(host);

        (await locator.ResolveAsync(account, Draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}
