using Nightware.Application.Mail.Rules;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Rules;

public sealed class MessageRuleUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageRuleActions Move = new() { MoveToFolder = new FolderId("f1") };
    private readonly IMessageRuleStore _store = Substitute.For<IMessageRuleStore>();
    private IReadOnlyList<MessageRule>? _saved;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MessageRule Rule(string id, int sequence, string name = "r") => new(id, name, sequence, true) { Actions = Move };

    private void Stored(params MessageRule[] rules)
    {
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MessageRule>>.Success(rules));
        _store.SaveAsync(Me, Arg.Do<IReadOnlyList<MessageRule>>(r => _saved = r), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Fact]
    public async Task Create_takes_its_place_by_sequence_and_everything_is_renumbered()
    {
        Stored(Rule("a", 1), Rule("b", 2));

        var created = await new CreateMessageRule(_store).ExecuteAsync(Me, new MessageRule("", "  New ", 1, true) { Actions = Move }, Ct);

        created.Value.Sequence.ShouldBe(1);
        created.Value.DisplayName.ShouldBe("New");
        _saved!.Select(r => (r.Id, r.Sequence)).ShouldBe([(created.Value.Id, 1), ("a", 2), ("b", 3)]);
    }

    [Fact]
    public async Task A_rule_asking_for_a_late_sequence_goes_last_with_the_next_number()
    {
        Stored(Rule("a", 1));

        (await new CreateMessageRule(_store).ExecuteAsync(Me, new MessageRule("", "n", 99, true) { Actions = Move }, Ct)).Value.Sequence.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public async Task Invalid_rules_are_refused_without_saving(MessageRule rule)
    {
        Stored();

        (await new CreateMessageRule(_store).ExecuteAsync(Me, rule, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, Ct);
    }

    public static TheoryData<MessageRule> InvalidRules() =>
    [
        new MessageRule("", " ", 1, true) { Actions = Move },
        new MessageRule("", "no actions", 1, true),
        new MessageRule("", "two redirects", 1, true)
        {
            Actions = new MessageRuleActions { ForwardTo = [new EmailAddress("a@example.com")], RedirectTo = [new EmailAddress("b@example.com")] },
        },
    ];

    [Fact]
    public async Task Update_applies_the_change_and_moves_the_rule_when_its_sequence_changes()
    {
        Stored(Rule("a", 1), Rule("b", 2), Rule("c", 3));

        var updated = await new UpdateMessageRule(_store).ExecuteAsync(Me, "c", r => r with { Sequence = 1, IsEnabled = false }, Ct);

        updated.Value.Sequence.ShouldBe(1);
        updated.Value.IsEnabled.ShouldBeFalse();
        _saved!.Select(r => r.Id).ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public async Task Update_keeps_the_id_and_returns_a_failing_change()
    {
        Stored(Rule("a", 1));
        var boom = Error.Unsupported("no");

        (await new UpdateMessageRule(_store).ExecuteAsync(Me, "a", r => r with { Id = "other", DisplayName = "renamed" }, Ct)).Value.Id.ShouldBe("a");
        (await new UpdateMessageRule(_store).ExecuteAsync(Me, "a", _ => boom, Ct)).Error.ShouldBe(boom);
        (await new UpdateMessageRule(_store).ExecuteAsync(Me, "nope", r => r, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Get_and_delete_find_by_id()
    {
        Stored(Rule("a", 1), Rule("b", 2));

        (await new GetMessageRule(_store).ExecuteAsync(Me, "b", Ct)).Value.Id.ShouldBe("b");
        (await new GetMessageRule(_store).ExecuteAsync(Me, "z", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new DeleteMessageRule(_store).ExecuteAsync(Me, "a", Ct)).IsSuccess.ShouldBeTrue();
        _saved!.Single().ShouldBe(Rule("b", 1));
        (await new DeleteMessageRule(_store).ExecuteAsync(Me, "z", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Store_failures_are_returned()
    {
        var boom = Error.BackendUnavailable("down");
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(boom);

        (await new ListMessageRules(_store).ExecuteAsync(Me, Ct)).Error.ShouldBe(boom);
        (await new CreateMessageRule(_store).ExecuteAsync(Me, Rule("", 1), Ct)).Error.ShouldBe(boom);
        (await new UpdateMessageRule(_store).ExecuteAsync(Me, "a", r => r, Ct)).Error.ShouldBe(boom);
        (await new DeleteMessageRule(_store).ExecuteAsync(Me, "a", Ct)).Error.ShouldBe(boom);
    }
}
