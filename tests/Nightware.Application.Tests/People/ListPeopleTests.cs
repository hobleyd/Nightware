using Microsoft.Extensions.Time.Testing;

using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Directory;
using Nightware.Application.Mail;
using Nightware.Application.People;
using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.People;

public sealed class ListPeopleTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IDirectoryAddresses _addresses = Substitute.For<IDirectoryAddresses>();
    private readonly IRecentCorrespondents _recent = Substitute.For<IRecentCorrespondents>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    public ListPeopleTests()
    {
        _contacts.ListAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<Contact>.Empty);
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<DirectoryUser>.Empty);
        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DirectoryAddress>>.Success([]));
        _recent.ListAsync(Me, ListPeople.MaxCorrespondents, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Correspondent>>.Success([]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ListPeople UseCase => new(_contacts, _directory, _addresses, _recent, new PeopleRanker(), _time);

    private static DirectoryUser User(int i) => new($"u{i}", $"User {i:D2}", $"user{i:D2}@example.com") { Mail = $"user{i:D2}@example.com" };

    [Fact]
    public async Task Merges_every_source_into_one_ranking()
    {
        _contacts.ListAsync(Me, Arg.Is<ItemQuery>(q => q.Top == ListPeople.SourcePageSize && q.SkipToken == null), Arg.Any<CancellationToken>())
            .Returns(new Page<Contact>([new Contact(new ContactId("c"), "Pat") { EmailAddresses = [new EmailAddress("pat@example.com")] }]));
        _directory.ListAsync(Arg.Is<ItemQuery>(q => q.Top == ListPeople.SourcePageSize && q.SkipToken == null), Arg.Any<CancellationToken>())
            .Returns(new Page<DirectoryUser>([User(1)]));
        _addresses.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DirectoryAddress>>.Success([new DirectoryAddress("support@example.com", "Support Inbox")]));
        _recent.ListAsync(Me, ListPeople.MaxCorrespondents, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Correspondent>>.Success([new Correspondent(new EmailAddress("alex@example.com", "Alex"), 20, _time.GetUtcNow())]));

        var page = (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Value;

        page.Items.Select(static p => p.DisplayName).ShouldBe(["Alex", "Pat", "Support Inbox", "User 01"]);
        page.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Reads_every_page_of_a_source()
    {
        _directory.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == null), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([User(1)], "p2"));
        _directory.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == "p2"), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([User(2)]));

        var page = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 999 }, Ct)).Value;

        page.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stops_reading_a_source_after_the_page_cap()
    {
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([User(1)], "again"));

        await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct);

        await _directory.Received(ListPeople.MaxSourcePages).ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pages_by_offset_and_freezes_the_ranking_instant()
    {
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([.. Enumerable.Range(1, 5).Select(User)]));

        var first = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 2 }, Ct)).Value;
        first.Items.Select(static p => p.DisplayName).ShouldBe(["User 01", "User 02"]);
        first.SkipToken.ShouldNotBeNull();

        // Recency would re-order correspondents if each page ranked against its own "now".
        _time.Advance(TimeSpan.FromDays(90));
        var second = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 2, SkipToken = first.SkipToken }, Ct)).Value;
        second.Items.Select(static p => p.DisplayName).ShouldBe(["User 03", "User 04"]);

        var last = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 2, SkipToken = second.SkipToken }, Ct)).Value;
        last.Items.Select(static p => p.DisplayName).ShouldBe(["User 05"]);
        last.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task The_frozen_instant_drives_recency_scores()
    {
        var mailed = _time.GetUtcNow();
        _recent.ListAsync(Me, ListPeople.MaxCorrespondents, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Correspondent>>.Success([
                new Correspondent(new EmailAddress("a@example.com"), 0, mailed),
                new Correspondent(new EmailAddress("b@example.com"), 0, mailed)]));
        var first = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 1 }, Ct)).Value;

        _time.Advance(TimeSpan.FromDays(PeopleRanker.RecencyHalfLifeDays));
        var second = (await UseCase.ExecuteAsync(Me, new ItemQuery { Top = 1, SkipToken = first.SkipToken }, Ct)).Value;

        second.Items.ShouldHaveSingleItem().RelevanceScore.ShouldBe(PeopleRanker.RecencyWeight);
    }

    [Fact]
    public async Task Defaults_to_graphs_page_size()
    {
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([.. Enumerable.Range(1, 15).Select(User)]));

        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Value.Items.Count.ShouldBe(ListPeople.DefaultPageSize);
    }

    [Theory]
    [InlineData("garbage!")]
    [InlineData("cDE6eDox")] // "p1:x:1"
    [InlineData("cDI6MDox")] // "p2:0:1"
    public async Task A_tampered_skiptoken_is_a_validation_error(string token)
    {
        (await UseCase.ExecuteAsync(Me, new ItemQuery { SkipToken = token }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    public static TheoryData<ItemQuery, string> UnsupportedQueries => new()
    {
        { new ItemQuery { Search = new SearchQuery([]) }, "$search" },
        { new ItemQuery { Filter = new FilterNode.Compare("displayName", CompareOp.Eq, new FilterValue.Text("x")) }, "$filter" },
        { new ItemQuery { OrderBy = [new OrderBy("displayName", Nightware.Application.Common.Query.SortDirection.Ascending)] }, "$orderby" },
        { new ItemQuery { Skip = 10 }, "$skip" },
        { new ItemQuery { Expand = [new ExpandClause("x")] }, "$expand" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedQueries))]
    public async Task Rejects_query_options_people_does_not_support(ItemQuery query, string option)
    {
        var error = (await UseCase.ExecuteAsync(Me, query, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe(option);
    }

    [Theory]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Forbidden)]
    [InlineData(ErrorKind.Unsupported)]
    public async Task A_source_that_does_not_apply_contributes_nobody(ErrorKind kind)
    {
        _contacts.ListAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([User(1)]));

        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Value.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(ErrorKind.BackendUnavailable)]
    [InlineData(ErrorKind.Throttled)]
    [InlineData(ErrorKind.BackendAuthFailed)]
    public async Task A_failing_contact_store_or_directory_fails_the_request(ErrorKind kind)
    {
        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(kind);

        _addresses.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DirectoryAddress>>.Success([]));
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(kind);

        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Page<DirectoryUser>.Empty);
        _contacts.ListAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(ErrorKind.Unsupported)]
    [InlineData(ErrorKind.BackendUnavailable)]
    public async Task Correspondents_are_best_effort(ErrorKind kind)
    {
        _recent.ListAsync(Me, ListPeople.MaxCorrespondents, Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([User(1)]));

        (await UseCase.ExecuteAsync(Me, ItemQuery.Default, Ct)).Value.Items.ShouldHaveSingleItem();
    }
}
