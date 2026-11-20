using Nightware.Adapters.Jmap.Mail.Categories;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Categories;

public sealed class JmapMasterCategoryStoreTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private FakeJmapServer _jmap = new(JmapTestHost.SessionWithMetadata());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMasterCategoryStore Store() => new(_jmap.Client());

    private void Inbox(string metadata) =>
        _jmap.On("Mailbox/get", $$$"""{"accountId":"c","state":"s1","list":[{"id":"b","role":"sent","metadata":{}},{"id":"a","role":"inbox","metadata":{{{metadata}}}}],"notFound":[]}""");

    [Fact]
    public async Task Reads_the_list_from_the_inbox_metadata()
    {
        Inbox("""{"nightware.ms":{"masterCategories":[{"id":"1","displayName":"Red","color":"preset0"},{"id":"2","displayName":"Plain"},{"bad":true}]}}""");

        var list = await Store().ListAsync(Alice, Ct);

        list.Value.ShouldBe([new MasterCategory("1", "Red", "preset0"), new MasterCategory("2", "Plain", "none")]);
    }

    [Fact]
    public async Task A_list_never_written_reads_as_null()
    {
        Inbox("{}");

        (await Store().ListAsync(Alice, Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Save_writes_the_whole_namespace_and_keeps_its_other_keys()
    {
        Inbox("""{"nightware.ms":{"other":1,"masterCategories":[]}}""");
        _jmap.On("Mailbox/set", """{"accountId":"c","oldState":"s1","newState":"s2","updated":{"a":null}}""");

        (await Store().SaveAsync(Alice, [new MasterCategory("1", "Red", "preset0")], Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Mailbox/set")["update"]!["a"]!.ToJsonString().ShouldBe(
            """{"metadata/nightware.ms":{"other":1,"masterCategories":[{"id":"1","displayName":"Red","color":"preset0"}]}}""");
    }

    [Fact]
    public async Task A_list_over_the_namespace_limit_is_refused()
    {
        Inbox("{}");
        var many = Enumerable.Range(0, 200).Select(static i => new MasterCategory(Guid.NewGuid().ToString(), $"Category number {i}", "preset1")).ToList();

        (await Store().SaveAsync(Alice, many, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Mailbox/set");
    }

    [Fact]
    public async Task A_server_without_metadata_cannot_keep_a_list()
    {
        await _jmap.DisposeAsync();
        _jmap = new FakeJmapServer();

        (await Store().ListAsync(Alice, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await Store().SaveAsync(Alice, [], Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }
}
