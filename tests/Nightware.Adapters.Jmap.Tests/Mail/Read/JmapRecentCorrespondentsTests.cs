using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapRecentCorrespondentsTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Alice = new(JmapTestHost.AliceAddress);

    private static IRecentCorrespondents Port(JmapTestHost host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IRecentCorrespondents>();

    [Fact]
    public async Task Ranks_recipients_of_sent_mail_and_senders_of_inbox_mail_by_count_then_recency()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        host.On("Email/query", """
            [["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":["s1","s2"]},"c0"],
             ["Email/get",{"accountId":"c","state":"s","list":[
               {"id":"s1","to":[{"name":"Bob","email":"bob@nightware.localhost"}],"cc":[{"name":null,"email":"carol@x.com"},{"name":null,"email":"ALICE@nightware.localhost"}],"receivedAt":"2026-11-10T00:00:00Z"},
               {"id":"s2","to":[{"name":"Bob B","email":"Bob@nightware.localhost"},{"name":"Bob B","email":"bob@nightware.localhost"}],"receivedAt":"2026-11-12T00:00:00Z"}],"notFound":[]},"c1"],
             ["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":["i1"]},"c2"],
             ["Email/get",{"accountId":"c","state":"s","list":[
               {"id":"i1","from":[{"name":"Dan","email":"dan@x.com"}],"receivedAt":"2026-11-13T00:00:00Z"}],"notFound":[]},"c3"]]
            """);

        var people = (await Port(host).ListAsync(Alice, 10, Ct)).Value;

        people.ShouldBe(
        [
            new Correspondent(new EmailAddress("bob@nightware.localhost", "Bob B"), 2, new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero)),
            new Correspondent(new EmailAddress("dan@x.com", "Dan"), 1, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)),
            new Correspondent(new EmailAddress("carol@x.com"), 1, new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)),
        ]);
        var calls = host.Calls("Email/query").ShouldHaveSingleItem();
        calls[0]![1]!["filter"]!.ToJsonString().ShouldBe("""{"inMailbox":"e"}""");
        calls[0]![1]!["limit"]!.GetValue<int>().ShouldBe(200);
        calls[2]![1]!["filter"]!.ToJsonString().ShouldBe("""{"inMailbox":"a"}""");
        (await Port(host).ListAsync(Alice, 1, Ct)).Value.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_is_asked_for_zero_and_no_folders_means_no_one()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.On("Mailbox/get", """[["Mailbox/get",{"accountId":"c","state":"m","list":[{"id":"g","name":"Other","role":null}],"notFound":[]},"c0"]]""");

        (await Port(host).ListAsync(Alice, 0, Ct)).Value.ShouldBeEmpty();
        (await Port(host).ListAsync(Alice, 5, Ct)).Value.ShouldBeEmpty();
        host.Calls("Email/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task Failures_are_passed_on()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        host.On("Email/query", """[["error",{"type":"serverUnavailable"},"c0"],["error",{"type":"serverUnavailable"},"c1"],["error",{"type":"serverUnavailable"},"c2"],["error",{"type":"serverUnavailable"},"c3"]]""");

        (await Port(host).ListAsync(Alice, 5, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await Port(host).ListAsync(new MailboxRef("nobody@nightware.localhost"), 5, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
