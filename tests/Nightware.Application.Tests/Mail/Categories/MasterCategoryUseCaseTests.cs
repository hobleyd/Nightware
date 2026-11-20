using Nightware.Application.Mail.Categories;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Categories;

public sealed class MasterCategoryUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MasterCategory Red = new("11111111-0000-4000-8000-000000000001", "Red", "preset0");
    private readonly IMasterCategoryStore _store = Substitute.For<IMasterCategoryStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Stored(params MasterCategory[] categories) =>
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MasterCategory>?>.Success(categories));

    private void NeverStored() =>
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MasterCategory>?>.Success(null));

    private void Saves() => _store.SaveAsync(Me, Arg.Any<IReadOnlyList<MasterCategory>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

    [Fact]
    public async Task A_mailbox_that_never_wrote_a_list_has_outlooks_six_defaults_with_stable_ids()
    {
        NeverStored();

        var first = (await new ListMasterCategories(_store).ExecuteAsync(Me, Ct)).Value;
        var second = (await new ListMasterCategories(_store).ExecuteAsync(Me, Ct)).Value;

        first.Select(c => (c.DisplayName, c.Color)).ShouldBe(
        [
            ("Red category", "preset0"), ("Orange category", "preset1"), ("Yellow category", "preset3"),
            ("Green category", "preset4"), ("Blue category", "preset7"), ("Purple category", "preset8"),
        ]);
        first.Select(c => c.Id).ShouldBe(second.Select(c => c.Id));
        first.Select(c => Guid.TryParse(c.Id, out var id) && id != Guid.Empty).ShouldAllBe(parsed => parsed);
        first.Select(c => c.Id).Distinct().Count().ShouldBe(6);
    }

    [Fact]
    public async Task Get_finds_by_id_ignoring_case_and_is_404_otherwise()
    {
        Stored(Red);

        (await new GetMasterCategory(_store).ExecuteAsync(Me, Red.Id.ToUpperInvariant(), Ct)).Value.ShouldBe(Red);
        (await new GetMasterCategory(_store).ExecuteAsync(Me, "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Create_appends_a_new_category_to_the_defaults_on_first_write()
    {
        NeverStored();
        Saves();

        var created = await new CreateMasterCategory(_store).ExecuteAsync(Me, "  Project X ", "preset2", Ct);

        created.Value.DisplayName.ShouldBe("Project X");
        created.Value.Color.ShouldBe("preset2");
        Guid.TryParse(created.Value.Id, out _).ShouldBeTrue();
        await _store.Received(1).SaveAsync(Me, Arg.Is<IReadOnlyList<MasterCategory>>(l => l.Count == 7 && l[6] == created.Value), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "preset0", ErrorKind.Validation)]
    [InlineData(" ", "preset0", ErrorKind.Validation)]
    [InlineData("New", "preset25", ErrorKind.Validation)]
    [InlineData("New", "preset07", ErrorKind.Validation)]
    [InlineData("new", "blue", ErrorKind.Validation)]
    [InlineData("RED", "preset1", ErrorKind.Conflict)]
    public async Task Create_refuses_bad_names_colours_and_duplicates(string? name, string color, ErrorKind kind)
    {
        Stored(Red);

        (await new CreateMasterCategory(_store).ExecuteAsync(Me, name, color, Ct)).Error!.Kind.ShouldBe(kind);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Create_without_a_colour_has_none()
    {
        Stored();
        Saves();

        (await new CreateMasterCategory(_store).ExecuteAsync(Me, "Plain", null, Ct)).Value.Color.ShouldBe("none");
    }

    [Fact]
    public async Task Update_changes_only_the_colour()
    {
        Stored(Red);
        Saves();

        (await new UpdateMasterCategory(_store).ExecuteAsync(Me, Red.Id, null, "preset5", Ct)).Value.ShouldBe(Red with { Color = "preset5" });
        (await new UpdateMasterCategory(_store).ExecuteAsync(Me, Red.Id, "Red", "preset6", Ct)).IsSuccess.ShouldBeTrue();
        (await new UpdateMasterCategory(_store).ExecuteAsync(Me, Red.Id, "Crimson", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UpdateMasterCategory(_store).ExecuteAsync(Me, Red.Id, null, "pink", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UpdateMasterCategory(_store).ExecuteAsync(Me, "nope", null, "preset1", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Delete_removes_it_and_is_404_when_absent()
    {
        Stored(Red);
        Saves();

        (await new DeleteMasterCategory(_store).ExecuteAsync(Me, Red.Id, Ct)).IsSuccess.ShouldBeTrue();
        await _store.Received(1).SaveAsync(Me, Arg.Is<IReadOnlyList<MasterCategory>>(l => l.Count == 0), Arg.Any<CancellationToken>());
        (await new DeleteMasterCategory(_store).ExecuteAsync(Me, "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Store_failures_are_returned()
    {
        var boom = Error.BackendUnavailable("down");
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(boom);

        (await new ListMasterCategories(_store).ExecuteAsync(Me, Ct)).Error.ShouldBe(boom);
        (await new CreateMasterCategory(_store).ExecuteAsync(Me, "X", null, Ct)).Error.ShouldBe(boom);
        (await new DeleteMasterCategory(_store).ExecuteAsync(Me, Red.Id, Ct)).Error.ShouldBe(boom);
    }
}
