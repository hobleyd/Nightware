using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Contacts;

public sealed class ContactUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly ContactId Id = new("c1");
    private static readonly Contact Pat = new(Id, "Pat");
    private readonly IContactStore _store = Substitute.For<IContactStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_writes_and_deletes_pass_to_the_store()
    {
        var folder = new ContactFolderId("f");
        var draft = new ContactDraft { GivenName = "Pat" };
        _store.GetAsync(Me, Id, Ct).Returns(Pat);
        _store.CreateAsync(Me, folder, draft, Ct).Returns(Pat);
        _store.UpdateAsync(Me, Id, draft, Ct).Returns(Pat);
        _store.DeleteAsync(Me, Id, Ct).Returns(Result.Success());

        (await new GetContact(_store).ExecuteAsync(Me, Id, Ct)).Value.ShouldBe(Pat);
        (await new CreateContact(_store).ExecuteAsync(Me, folder, draft, Ct)).Value.ShouldBe(Pat);
        (await new UpdateContact(_store).ExecuteAsync(Me, Id, draft, Ct)).Value.ShouldBe(Pat);
        (await new DeleteContact(_store).ExecuteAsync(Me, Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_bad_email_address_is_refused_before_the_store()
    {
        var draft = new ContactDraft { EmailAddresses = [new EmailAddress("not an address")] };

        (await new CreateContact(_store).ExecuteAsync(Me, null, draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UpdateContact(_store).ExecuteAsync(Me, Id, draft, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_photo_must_be_a_non_empty_image_of_at_most_4_mb()
    {
        _store.SetPhotoAsync(Me, Id, Arg.Any<byte[]>(), "image/png", Ct).Returns(Result.Success());
        var photos = new SetContactPhoto(_store);

        (await photos.ExecuteAsync(Me, Id, [1], "image/PNG; charset=x", Ct)).IsSuccess.ShouldBeTrue();
        (await photos.ExecuteAsync(Me, Id, [], "image/png", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await photos.ExecuteAsync(Me, Id, [1], "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await photos.ExecuteAsync(Me, Id, new byte[SetContactPhoto.MaxBytes + 1], "image/png", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_missing_content_type_is_taken_as_jpeg()
    {
        _store.SetPhotoAsync(Me, Id, Arg.Any<byte[]>(), "image/jpeg", Ct).Returns(Result.Success());
        _store.GetPhotoAsync(Me, Id, Ct).Returns(Error.NotFound("none"));

        (await new SetContactPhoto(_store).ExecuteAsync(Me, Id, [1], null, Ct)).IsSuccess.ShouldBeTrue();
        (await new GetContactPhoto(_store).ExecuteAsync(Me, Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
