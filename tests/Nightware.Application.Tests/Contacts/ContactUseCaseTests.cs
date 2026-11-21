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

public sealed class ContactFoldersUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly ContactFolder Work = new(new ContactFolderId("w"), "Work", new ContactFolderId("root"));
    private static readonly ContactFolder Default = new(new ContactFolderId("root"), "Contacts", null, "contacts");
    private readonly IContactFolderStore _folders = Substitute.For<IContactFolderStore>();
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ContactFoldersUseCase UseCase => new(_folders, _contacts);

    public ContactFoldersUseCaseTests()
    {
        _folders.GetAsync(Me, "w", Arg.Any<CancellationToken>()).Returns(Work);
        _folders.GetAsync(Me, "root", Arg.Any<CancellationToken>()).Returns(Default);
        _folders.GetAsync(Me, "nope", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
    }

    [Fact]
    public async Task A_folder_needs_a_name()
    {
        _folders.CreateAsync(Me, "Friends", Ct).Returns(Work with { DisplayName = "Friends" });

        (await UseCase.CreateAsync(Me, " Friends ", Ct)).Value.DisplayName.ShouldBe("Friends");
        (await UseCase.CreateAsync(Me, " ", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.RenameAsync(Me, "w", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task The_default_folder_is_neither_renamed_nor_deleted()
    {
        (await UseCase.RenameAsync(Me, "root", "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.DeleteAsync(Me, "root", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _folders.DidNotReceiveWithAnyArgs().RenameAsync(default, default!, default!, Ct);
        await _folders.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Other_folders_are_renamed_and_deleted()
    {
        _folders.RenameAsync(Me, "w", "Work 2", Ct).Returns(Work with { DisplayName = "Work 2" });
        _folders.DeleteAsync(Me, "w", Ct).Returns(Result.Success());

        (await UseCase.RenameAsync(Me, "w", "Work 2", Ct)).Value.DisplayName.ShouldBe("Work 2");
        (await UseCase.DeleteAsync(Me, "w", Ct)).IsSuccess.ShouldBeTrue();
        (await UseCase.DeleteAsync(Me, "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Child_folders_are_empty_but_an_unknown_parent_is_404()
    {
        (await UseCase.ChildFoldersAsync(Me, "w", Ct)).Value.ShouldBeEmpty();
        (await UseCase.ChildFoldersAsync(Me, "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Contacts_are_listed_and_created_in_the_resolved_folder()
    {
        var page = new Nightware.Domain.Kernel.Page<Contact>([new Contact(new ContactId("c"), "Sam")]);
        _contacts.ListInAsync(Me, Work.Id, Nightware.Application.Common.Query.ItemQuery.Default, Ct).Returns(page);
        _contacts.CreateAsync(Me, Work.Id, Arg.Any<ContactDraft>(), Ct).Returns(new Contact(new ContactId("c"), "Sam"));

        (await UseCase.ContactsAsync(Me, "w", Nightware.Application.Common.Query.ItemQuery.Default, Ct)).Value.ShouldBe(page);
        (await UseCase.CreateContactAsync(Me, "w", new ContactDraft { GivenName = "Sam" }, Ct)).Value.DisplayName.ShouldBe("Sam");
        (await UseCase.ContactsAsync(Me, "nope", Nightware.Application.Common.Query.ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
