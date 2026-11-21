using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailDeltaReaderTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Alice = new(JmapTestHost.AliceAddress);
    private static readonly FolderRef Inbox = new FolderRef.WellKnown(WellKnownFolder.Inbox);

    private static IMailDeltaReader Reader(JmapTestHost host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IMailDeltaReader>();

    private static JmapTestHost Host()
    {
        var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        return host;
    }

    private static JmapDeltaCursor Decode(DeltaToken? token) => JmapDeltaTokenCodec.Decode(token!.Value).Value;

    [Fact]
    public async Task An_initial_sync_captures_the_state_first_then_pages_the_folder_newest_first()
    {
        await using var host = Host();
        host.On("Email/get", $$"""
            [["Email/get",{"accountId":"c","state":"s1","list":[],"notFound":[]},"c0"],
             ["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":["m1","m2"],"total":3},"c1"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("m1"), MailReadStubs.Email("m2"))}},"c2"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, null, new DeltaRequest(2), Ct)).Value;

        page.Entries.Select(e => ((DeltaEntry<MailMessage>.Upsert)e).Item.Id.Value).ShouldBe(["e.m1", "e.m2"]);
        page.Final.ShouldBeNull();
        Decode(page.NextPage).ShouldBe(JmapDeltaCursor.Initial("a", "s1", 2, "q"));
        var calls = host.Calls("Email/get").ShouldHaveSingleItem();
        calls[0]!.ToJsonString().ShouldBe("""["Email/get",{"accountId":"c","ids":[],"properties":["id"]},"c0"]""");
        calls[1]!.ToJsonString().ShouldBe("""["Email/query",{"accountId":"c","filter":{"inMailbox":"a"},"sort":[{"property":"receivedAt","isAscending":false}],"position":0,"limit":2,"calculateTotal":true},"c1"]""");
    }

    [Fact]
    public async Task The_last_initial_page_hands_over_to_changes_from_the_captured_state()
    {
        await using var host = Host();
        host.On("Email/query", $$"""
            [["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":2,"ids":["m3"],"total":3},"c0"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("m3"))}},"c1"]]
            """);
        var token = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial("a", "s1", 2, "q0"));

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, token, new DeltaRequest(2), Ct)).Value;

        page.Entries.Count.ShouldBe(1);
        Decode(page.Final).ShouldBe(JmapDeltaCursor.Changes("a", "s1", "q0"), "the query state of the start, like the Email state");
        host.Calls("Email/query")[0][0]![1]!["position"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task An_empty_folder_is_done_at_once()
    {
        await using var host = Host();
        host.On("Email/get", """
            [["Email/get",{"accountId":"c","state":"s5","list":[],"notFound":[]},"c0"],
             ["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":[],"total":0},"c1"],
             ["Email/get",{"accountId":"c","state":"s5","list":[],"notFound":[]},"c2"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, null, DeltaRequest.Default, Ct)).Value;

        page.Entries.ShouldBeEmpty();
        Decode(page.Final).ShouldBe(JmapDeltaCursor.Changes("a", "s5", "q"));
    }

    [Fact]
    public async Task Changes_upsert_what_is_in_the_folder_and_remove_only_what_left_it_or_is_gone()
    {
        await using var host = Host();

        // u2 changed in another folder; l1 left the inbox (no Email/changes entry needed for it to show).
        host.On("Email/queryChanges", $$"""
            [["Email/queryChanges",{"accountId":"c","oldQueryState":"q1","newQueryState":"q2","removed":["l1","u1","x1"],"added":[{"id":"u1","index":0}]},"c0"],
             ["Email/changes",{"accountId":"c","oldState":"s1","newState":"s2","hasMoreChanges":false,"created":["n1","n2"],"updated":["u1","u2"],"destroyed":["x1"]},"c1"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("n1"), MailReadStubs.Email("n2", mailbox: "e"))}},"c2"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("u1", keywords: """{"$seen":true}"""), MailReadStubs.Email("u2", mailbox: "b"))}},"c3"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s1", "q1")), new DeltaRequest(50), Ct)).Value;

        page.Entries.ShouldBe(
        [
            new DeltaEntry<MailMessage>.Upsert(((DeltaEntry<MailMessage>.Upsert)page.Entries[0]).Item),
            new DeltaEntry<MailMessage>.Upsert(((DeltaEntry<MailMessage>.Upsert)page.Entries[1]).Item),
            new DeltaEntry<MailMessage>.Removed("e.l1", RemovedReason.Changed),
            new DeltaEntry<MailMessage>.Removed("e.x1", RemovedReason.Deleted),
        ]);
        ((DeltaEntry<MailMessage>.Upsert)page.Entries[0]).Item.Id.Value.ShouldBe("e.n1");
        ((DeltaEntry<MailMessage>.Upsert)page.Entries[1]).Item.IsRead.ShouldBeTrue();
        Decode(page.Final).ShouldBe(JmapDeltaCursor.Changes("a", "s2", "q2"));
        var calls = host.Calls("Email/queryChanges").ShouldHaveSingleItem();
        calls[0]!.ToJsonString().ShouldBe("""["Email/queryChanges",{"accountId":"c","sinceQueryState":"q1","filter":{"inMailbox":"a"},"sort":[{"property":"receivedAt","isAscending":false}]},"c0"]""");
        calls[1]!.ToJsonString().ShouldBe("""["Email/changes",{"accountId":"c","sinceState":"s1","maxChanges":50},"c1"]""");
        calls[2]![1]!["#ids"]!["path"]!.GetValue<string>().ShouldBe("/created");
        calls[3]![1]!["#ids"]!["path"]!.GetValue<string>().ShouldBe("/updated");
    }

    [Fact]
    public async Task A_token_without_a_query_state_reports_every_change_outside_the_folder_and_captures_one()
    {
        await using var host = Host();
        host.On("Email/query", $$"""
            [["Email/query",{"accountId":"c","queryState":"q9","canCalculateChanges":true,"position":0,"ids":[]},"c0"],
             ["Email/changes",{"accountId":"c","oldState":"s1","newState":"s2","hasMoreChanges":false,"created":[],"updated":["u2"],"destroyed":[]},"c1"],
             ["Email/get",{"accountId":"c","state":"s","list":[],"notFound":[]},"c2"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("u2", mailbox: "b"))}},"c3"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s1")), new DeltaRequest(50), Ct)).Value;

        page.Entries.ShouldBe([new DeltaEntry<MailMessage>.Removed("e.u2", RemovedReason.Changed)]);
        Decode(page.Final).ShouldBe(JmapDeltaCursor.Changes("a", "s2", "q9"));
        host.Calls("Email/query")[0][0]![1]!["limit"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Query_changes_the_server_cannot_calculate_fall_back_for_that_page()
    {
        await using var host = Host();
        host.On("Email/queryChanges", $$"""
            [["error",{"type":"cannotCalculateChanges"},"c0"],
             ["Email/changes",{"accountId":"c","oldState":"s1","newState":"s2","hasMoreChanges":false,"created":[],"updated":["u2"],"destroyed":[]},"c1"],
             ["Email/get",{"accountId":"c","state":"s","list":[],"notFound":[]},"c2"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("u2", mailbox: "b"))}},"c3"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s1", "q1")), new DeltaRequest(50), Ct)).Value;

        page.Entries.ShouldBe([new DeltaEntry<MailMessage>.Removed("e.u2", RemovedReason.Changed)]);
        Decode(page.Final).ShouldBe(JmapDeltaCursor.Changes("a", "s2"), "no state now: the next page captures one");
    }

    [Fact]
    public async Task More_changes_continue_on_a_next_page_keeping_the_query_state()
    {
        await using var host = Host();
        host.On("Email/queryChanges", """
            [["Email/queryChanges",{"accountId":"c","oldQueryState":"q1","newQueryState":"q2","removed":[],"added":[]},"c0"],
             ["Email/changes",{"accountId":"c","oldState":"s1","newState":"s2","hasMoreChanges":true,"created":[],"updated":[],"destroyed":["x1"]},"c1"],
             ["Email/get",{"accountId":"c","state":"s","list":[],"notFound":[]},"c2"],
             ["Email/get",{"accountId":"c","state":"s","list":[],"notFound":[]},"c3"]]
            """);

        var page = (await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s1", "q1")), DeltaRequest.Default, Ct)).Value;

        Decode(page.NextPage).ShouldBe(JmapDeltaCursor.Changes("a", "s2", "q1"));
        page.Final.ShouldBeNull();
    }

    [Fact]
    public async Task A_lost_sync_state_is_gone()
    {
        await using var host = Host();
        host.On("Email/queryChanges", """[["Email/queryChanges",{"accountId":"c","oldQueryState":"q1","newQueryState":"q2","removed":[],"added":[]},"c0"],["error",{"type":"cannotCalculateChanges"},"c1"],["error",{"type":"invalidResultReference"},"c2"],["error",{"type":"invalidResultReference"},"c3"]]""");

        var result = await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s0", "q1")), DeltaRequest.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public async Task A_token_for_another_folder_or_garbage_is_gone_before_any_mail_request()
    {
        await using var host = Host();

        (await Reader(host).DeltaAsync(Alice, Inbox, JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("e", "s0")), DeltaRequest.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        (await Reader(host).DeltaAsync(Alice, Inbox, new DeltaToken("garbage"), DeltaRequest.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        host.Calls("Email/changes").ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_folders_are_not_found_and_pages_are_capped_by_the_session()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxObjectsInGet: 10));
        host.StubMailboxes();
        host.On("Email/get", """
            [["Email/get",{"accountId":"c","state":"s1","list":[],"notFound":[]},"c0"],
             ["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":0,"ids":[],"total":0},"c1"],
             ["Email/get",{"accountId":"c","state":"s1","list":[],"notFound":[]},"c2"]]
            """);

        (await Reader(host).DeltaAsync(Alice, new FolderRef.ById(new FolderId("zz")), null, DeltaRequest.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).DeltaAsync(Alice, Inbox, null, new DeltaRequest(50), Ct)).IsSuccess.ShouldBeTrue();
        host.Calls("Email/get").Single()[1]![1]!["limit"]!.GetValue<int>().ShouldBe(10);
    }

    [Fact]
    public async Task An_initial_sync_the_session_limits_would_split_fails_without_reading_anything()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxCallsInRequest: 2));
        host.StubMailboxes();

        var result = await Reader(host).DeltaAsync(Alice, Inbox, null, new DeltaRequest(2), Ct);

        // State in one HTTP request and the query in the next would let a change between them escape
        // both the walk and the Email/changes replay.
        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        host.Calls("Email/get").ShouldBeEmpty();
        host.Calls("Email/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task Later_initial_pages_may_be_split_because_the_state_is_already_captured()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxCallsInRequest: 2));
        host.StubMailboxes();
        host.On("Email/query", $$"""
            [["Email/query",{"accountId":"c","queryState":"q","canCalculateChanges":true,"position":2,"ids":["m3"],"total":3},"c0"],
             ["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("m3"))}},"c1"]]
            """);
        var token = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Initial("a", "s1", 2));

        (await Reader(host).DeltaAsync(Alice, Inbox, token, new DeltaRequest(2), Ct)).Value.Entries.Count.ShouldBe(1);
    }
}
