using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class UpdateMessageTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Id = new("d.abc");
    private static readonly MessageId NewId = new("d.new");
    private static readonly MessageFetch Fetch = new(new HashSet<string> { "id", "isRead" });
    private static readonly Error Boom = Error.NotFound("gone");

    private readonly IMailMessageWriter _writer = Substitute.For<IMailMessageWriter>();
    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();
    private readonly IMailMessageReader _reader = Substitute.For<IMailMessageReader>();

    private UpdateMessage UseCase => new(_writer, _drafts, _reader);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DraftUpdate CompleteDraft() => new()
    {
        Subject = "s",
        Body = new ItemBody(BodyType.Text, "b"),
        ToRecipients = [new EmailAddress("a@example.com")],
        CcRecipients = [],
        BccRecipients = [],
        Importance = Importance.Normal,
    };

    [Fact]
    public async Task Patches_and_rereads_with_the_requested_projection()
    {
        var patch = new MessagePatch { IsRead = true };
        var message = new MailMessage(Id) { IsRead = true };
        _writer.PatchAsync(Me, Id, patch, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _reader.GetAsync(Me, Id, Fetch, Arg.Any<CancellationToken>()).Returns(message);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(patch, null), Fetch, Ct);

        result.Value.ShouldBe(message);
        await _drafts.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task A_patch_failure_is_returned_without_a_read()
    {
        _writer.PatchAsync(Me, Id, Arg.Any<MessagePatch>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(Boom));

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(new MessagePatch { IsRead = false }, null), Fetch, Ct);

        result.Error.ShouldBe(Boom);
        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task An_empty_update_only_rereads()
    {
        var message = new MailMessage(Id);
        _reader.GetAsync(Me, Id, Fetch, Arg.Any<CancellationToken>()).Returns(message);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(new MessagePatch(), null), Fetch, Ct);

        result.Value.ShouldBe(message);
        await _writer.DidNotReceiveWithAnyArgs().PatchAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task A_complete_draft_update_replaces_without_reading_first()
    {
        var message = new MailMessage(NewId);
        _drafts.ReplaceAsync(Me, Id, Arg.Is<DraftContent>(c => c.Subject == "s" && c.Body == new ItemBody(BodyType.Text, "b")), Arg.Any<CancellationToken>())
            .Returns(NewId);
        _reader.GetAsync(Me, NewId, Fetch, Arg.Any<CancellationToken>()).Returns(message);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(null, CompleteDraft()), Fetch, Ct);

        result.Value.ShouldBe(message);
        await _reader.Received(1).GetAsync(Me, Arg.Any<MessageId>(), Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>());
        await _writer.DidNotReceiveWithAnyArgs().PatchAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task A_partial_draft_update_reads_only_the_missing_fields()
    {
        var current = new MailMessage(Id) { Importance = Importance.High, Subject = "ignored" };
        _reader.GetAsync(Me, Id, Arg.Is<MessageFetch>(f => f.Select != null && f.Select.SetEquals(new[] { "importance" })), Arg.Any<CancellationToken>())
            .Returns(current);
        _drafts.ReplaceAsync(Me, Id, Arg.Is<DraftContent>(c => c.Subject == "s" && c.Importance == Importance.High), Arg.Any<CancellationToken>())
            .Returns(Id);
        _reader.GetAsync(Me, Id, Fetch, Arg.Any<CancellationToken>()).Returns(new MailMessage(Id));

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(null, CompleteDraft() with { Importance = null }), Fetch, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_read_of_the_draft_stops_the_update()
    {
        _reader.GetAsync(Me, Id, Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(Boom);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(null, new DraftUpdate { Subject = "s" }), Fetch, Ct);

        result.Error.ShouldBe(Boom);
        await _drafts.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task A_failed_replace_is_returned()
    {
        _drafts.ReplaceAsync(Me, Id, Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(Boom);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(new MessagePatch { IsRead = true }, CompleteDraft()), Fetch, Ct);

        result.Error.ShouldBe(Boom);
        await _writer.DidNotReceiveWithAnyArgs().PatchAsync(default, default, default!, Ct);
        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Draft_and_patch_together_patch_the_replacement()
    {
        var patch = new MessagePatch { IsRead = false };
        _drafts.ReplaceAsync(Me, Id, Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(NewId);
        _writer.PatchAsync(Me, NewId, patch, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _reader.GetAsync(Me, NewId, Fetch, Arg.Any<CancellationToken>()).Returns(new MailMessage(NewId));

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(patch, CompleteDraft()), Fetch, Ct);

        result.Value.Id.ShouldBe(NewId);
        await _writer.DidNotReceive().PatchAsync(Me, Id, Arg.Any<MessagePatch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_reread_is_returned()
    {
        _writer.PatchAsync(Me, Id, Arg.Any<MessagePatch>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _reader.GetAsync(Me, Id, Fetch, Arg.Any<CancellationToken>()).Returns(Boom);

        var result = await UseCase.ExecuteAsync(Me, Id, new MessageUpdate(new MessagePatch { IsRead = true }, null), Fetch, Ct);

        result.Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => UseCase.ExecuteAsync(Me, Id, null!, Fetch, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => UseCase.ExecuteAsync(Me, Id, new MessageUpdate(null, null), null!, Ct));
    }
}
