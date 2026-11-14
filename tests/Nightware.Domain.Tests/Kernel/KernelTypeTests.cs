using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tests.Kernel;

public sealed class KernelTypeTests
{
    [Fact]
    public void Error_factories_set_kind_and_default_code()
    {
        Error.NotFound("m", "t").ShouldBe(new Error(ErrorKind.NotFound, "itemNotFound", "m", "t"));
        Error.Validation("m").ShouldBe(new Error(ErrorKind.Validation, "invalidRequest", "m"));
        Error.Unsupported("m").ShouldBe(new Error(ErrorKind.Unsupported, "notImplemented", "m"));
        Error.Conflict("m").ShouldBe(new Error(ErrorKind.Conflict, "conflict", "m"));
        Error.Forbidden("m").ShouldBe(new Error(ErrorKind.Forbidden, "accessDenied", "m"));
        Error.SyncStateGone("m").ShouldBe(new Error(ErrorKind.SyncStateGone, "syncStateNotFound", "m"));
        Error.BackendAuthFailed("m").ShouldBe(new Error(ErrorKind.BackendAuthFailed, "InvalidAuthenticationToken", "m"));
        Error.BackendUnavailable("m", 30).RetryAfterSeconds.ShouldBe(30);
        Error.BackendUnavailable("m").RetryAfterSeconds.ShouldBeNull();
        Error.BackendUnavailable("m").Code.ShouldBe("serviceNotAvailable");
    }

    [Fact]
    public void Ids_are_opaque_strings()
    {
        new MessageId("e.1").ToString().ShouldBe("e.1");
        new FolderId("f").ToString().ShouldBe("f");
        new AttachmentId("a").ToString().ShouldBe("a");
        new EventId("ev").ToString().ShouldBe("ev");
        new ContactId("c").ToString().ShouldBe("c");
        new TaskListId("l").ToString().ShouldBe("l");
        new TaskId("t").ToString().ShouldBe("t");
        new DeltaToken("d").ToString().ShouldBe("d");
        new MailboxRef("a@b.c").ToString().ShouldBe("a@b.c");
        new MessageId("x").ShouldBe(new MessageId("x"));
    }

    [Fact]
    public void Page_has_more_only_with_a_skip_token()
    {
        new Page<int>([1, 2]).HasMore.ShouldBeFalse();
        new Page<int>([1], "next").HasMore.ShouldBeTrue();
        Page<int>.Empty.Items.ShouldBeEmpty();
        Page<int>.Empty.HasMore.ShouldBeFalse();
    }

    [Fact]
    public void DeltaPage_sets_exactly_one_continuation()
    {
        var entries = new DeltaEntry<string>[] { new DeltaEntry<string>.Upsert("a"), new DeltaEntry<string>.Removed("b", RemovedReason.Changed) };

        var more = DeltaPage<string>.More(entries, new DeltaToken("n"));
        more.NextPage.ShouldBe(new DeltaToken("n"));
        more.Final.ShouldBeNull();
        more.Entries.Count.ShouldBe(2);

        var done = DeltaPage<string>.Done([], new DeltaToken("f"));
        done.NextPage.ShouldBeNull();
        done.Final.ShouldBe(new DeltaToken("f"));
    }

    [Fact]
    public async Task BlobContent_opens_lazily()
    {
        var opened = 0;
        var blob = new BlobContent("text/plain", 2, _ =>
        {
            opened++;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hi")));
        })
        { FileName = "a.txt" };

        opened.ShouldBe(0);
        await using var stream = await blob.Open(TestContext.Current.CancellationToken);
        opened.ShouldBe(1);
        blob.FileName.ShouldBe("a.txt");
    }

    [Fact]
    public void Principal_and_email_address_are_values()
    {
        new Principal("1", "u", "m@x", "N").ShouldBe(new Principal("1", "u", "m@x", "N"));
        new EmailAddress("a@b.c").Name.ShouldBeNull();
    }
}
