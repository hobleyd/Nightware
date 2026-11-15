using System.Net;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class MessageWriteEndpointTests : MailWriteEndpointTestBase
{
    private const string Id = "e.m1";
    private const string Draft = "d.ZHJhZnRAZXhhbXBsZS5jb20";

    private static MailMessage Message(string id) => new(new MessageId(id))
    {
        Subject = "Quarterly numbers",
        IsRead = true,
        BodyPreview = "preview",
        ParentFolderId = new FolderId("inbox-id"),
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Patch_isRead_patches_the_message_and_returns_it_projected(string prefix, string address)
    {
        Writer.PatchAsync(Box(address), new MessageId(Id), Arg.Any<MessagePatch>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        Reader.GetAsync(Box(address), new MessageId(Id), Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(Message(Id));

        var response = await SendAsync(HttpMethod.Patch, $"{prefix}/messages/{Id}?$select=id,subject,isRead", """{"isRead":true}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Select(p => p.Key).ShouldBe(["@odata.context", "id", "subject", "isRead"], ignoreOrder: true);
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/messages(id,subject,isRead)/$entity");
        await Writer.Received(1).PatchAsync(Box(address), new MessageId(Id), Arg.Is<MessagePatch>(p => p.IsRead == true && p.Flag == null), Arg.Any<CancellationToken>());
        await Reader.Received(1).GetAsync(Box(address), new MessageId(Id), Arg.Is<MessageFetch>(f => f.Select!.SetEquals(new[] { "id", "subject", "isRead" })), Arg.Any<CancellationToken>());
        await Drafts.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default, null!, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Patch_with_draft_fields_replaces_the_draft(string prefix, string address)
    {
        Reader.GetAsync(Box(address), new MessageId(Draft), Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(Message(Draft));
        Drafts.ReplaceAsync(Box(address), new MessageId(Draft), Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(new MessageId(Draft));

        var response = await SendAsync(HttpMethod.Patch, $"{prefix}/messages/{Draft}",
            """{"subject":"S","body":{"contentType":"Text","content":"hi"},"toRecipients":[{"emailAddress":{"address":"a@example.com"}}],"ccRecipients":[],"bccRecipients":[]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe(Draft);
        await Drafts.Received(1).ReplaceAsync(Box(address), new MessageId(Draft), Arg.Is<DraftContent>(c =>
            c.Subject == "S" && c.Body == new ItemBody(BodyType.Text, "hi") && c.ToRecipients.Single().Address == "a@example.com"), Arg.Any<CancellationToken>());
        await Writer.DidNotReceiveWithAnyArgs().PatchAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task Patch_with_a_bad_content_type_is_400()
    {
        var response = await SendAsync(HttpMethod.Patch, $"/v1.0/me/messages/{Draft}", """{"body":{"contentType":"rtf","content":"x"}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Fact]
    public async Task Patch_of_a_missing_message_is_404()
    {
        Writer.PatchAsync(default, default, null!, Ct).ReturnsForAnyArgs(Result.Failure(Error.NotFound("gone")));

        var response = await SendAsync(HttpMethod.Patch, $"/v1.0/me/messages/{Id}", """{"isRead":false}""");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorItemNotFound");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Create_draft_is_201_with_the_draft(string prefix, string address)
    {
        Drafts.CreateAsync(Box(address), Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(new MessageId(Draft));
        Reader.GetAsync(Box(address), new MessageId(Draft), MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Message(Draft) with { IsDraft = true });

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages",
            """{"subject":"D","body":{"contentType":"Html","content":"<p>x</p>"},"toRecipients":[{"emailAddress":{"address":"a@example.com"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe(Draft);
        body["isDraft"]!.GetValue<bool>().ShouldBeTrue();
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/messages/$entity");
        await Drafts.Received(1).CreateAsync(Box(address), Arg.Is<DraftContent>(c => c.Subject == "D" && c.Body == new ItemBody(BodyType.Html, "<p>x</p>")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"toRecipients":[{"emailAddress":{"name":"No address"}}]}""")]
    [InlineData("""{"body":{"contentType":"markdown","content":"x"}}""")]
    [InlineData("""{"attachments":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"a.txt","contentBytes":"not base64!"}]}""")]
    public async Task Create_draft_with_a_malformed_body_is_400(string json)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/messages", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
        await Drafts.DidNotReceiveWithAnyArgs().CreateAsync(default, null!, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Delete_is_204(string prefix, string address)
    {
        Writer.SoftDeleteAsync(Box(address), new MessageId(Draft), Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await SendAsync(HttpMethod.Delete, $"{prefix}/messages/{Draft}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_of_a_missing_message_is_404()
    {
        Writer.SoftDeleteAsync(default, default, Ct).ReturnsForAnyArgs(Result.Failure(Error.NotFound("gone")));

        (await SendAsync(HttpMethod.Delete, $"/v1.0/me/messages/{Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Move_is_201_with_the_moved_message(string prefix, string address)
    {
        Writer.MoveAsync(Box(address), new MessageId(Id), new FolderRef.WellKnown(WellKnownFolder.DeletedItems), Arg.Any<CancellationToken>()).Returns(new MessageId(Id));
        Reader.GetAsync(Box(address), new MessageId(Id), MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Message(Id));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/move", """{"destinationId":"DeletedItems"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe(Id);
    }

    [Fact]
    public async Task Move_to_a_folder_id_passes_it_through()
    {
        Writer.MoveAsync(default, default, null!, Ct).ReturnsForAnyArgs(new MessageId(Id));
        Reader.GetAsync(default, default, null!, Ct).ReturnsForAnyArgs(Message(Id));

        (await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/move", """{"destinationId":"mbx-7"}""")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await Writer.Received(1).MoveAsync(Box(TestHost.UserEmail), new MessageId(Id), new FolderRef.ById(new FolderId("mbx-7")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Move_without_a_destination_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/move", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Writer.DidNotReceiveWithAnyArgs().MoveAsync(default, default, null!, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Permanent_delete_is_204(string prefix, string address)
    {
        Writer.PermanentDeleteAsync(Box(address), new MessageId(Id), Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/permanentDelete")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Permanent_delete_failure_is_mapped()
    {
        Writer.PermanentDeleteAsync(default, default, Ct).ReturnsForAnyArgs(Result.Failure(Error.Forbidden("no")));

        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/permanentDelete");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorAccessDenied");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Import_maps_the_mapi_properties_and_is_201(string prefix, string address)
    {
        Importer.ImportAsync(Box(address), new FolderRef.ById(new FolderId("mbx-9")), Arg.Any<DraftContent>(), Arg.Any<ImportOptions>(), Arg.Any<CancellationToken>())
            .Returns(new MessageId("e.imported"));
        Reader.GetAsync(Box(address), new MessageId("e.imported"), MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Message("e.imported"));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/mailFolders/mbx-9/messages", """
            {"subject":"Old","body":{"contentType":"text","content":"x"},
             "from":{"emailAddress":{"address":"alex@example.com","name":"Alex"}},
             "toRecipients":[{"emailAddress":{"address":"me@example.com"}}],
             "singleValueExtendedProperties":[
               {"id":"SystemTime 0x0E06","value":"2026-03-04T05:06:07.000Z"},
               {"id":"SystemTime 0x0039","value":"2026-03-04T05:06:00.000Z"},
               {"id":"Integer 0x0E07","value":"1"}],
             "isRead":true}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe("e.imported");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/mailFolders('mbx-9')/messages/$entity");
        await Importer.Received(1).ImportAsync(Box(address), Arg.Any<FolderRef>(),
            Arg.Is<DraftContent>(c => c.From == new EmailAddress("alex@example.com", "Alex")),
            new ImportOptions(true, new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero), IsDraft: false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Import_with_a_bad_mapi_value_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messages",
            """{"subject":"x","singleValueExtendedProperties":[{"id":"Integer 0x0E07","value":"one"}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Importer.DidNotReceiveWithAnyArgs().ImportAsync(default, null!, null!, null!, Ct);
    }

    [Fact]
    public async Task Import_refused_as_send_as_is_403_so_the_client_retries_without_from()
    {
        Importer.ImportAsync(default, null!, null!, null!, Ct)
            .ReturnsForAnyArgs(Result<MessageId>.Failure(new Error(ErrorKind.Forbidden, "ErrorSendAsDenied", "no")));

        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messages", """{"subject":"x"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorSendAsDenied");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Add_attachment_is_201_with_the_attachment(string prefix, string address)
    {
        Drafts.AddAttachmentAsync(Box(address), new MessageId(Draft), Arg.Any<NewAttachment>(), Arg.Any<CancellationToken>())
            .Returns(new MailAttachment(new AttachmentId("3"), "notes.txt", "text/plain", 14, AttachmentKind.File) { IsInline = true, ContentId = "cid1" });

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Draft}/attachments",
            """{"@odata.type":"#microsoft.graph.fileAttachment","name":"notes.txt","contentType":"text/plain","contentBytes":"TWVldGluZyBub3Rlcwo=","isInline":true,"contentId":"cid1"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe("3");
        body["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.fileAttachment");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/messages('{Draft}')/attachments/$entity");
        await Drafts.Received(1).AddAttachmentAsync(Box(address), new MessageId(Draft), Arg.Is<NewAttachment>(a =>
            a.Name == "notes.txt" && a.Content.Length == 14 && a.IsInline && a.ContentId == "cid1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_attachment_with_bad_base64_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Draft}/attachments", """{"name":"a.txt","contentBytes":"%%%"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Add_item_attachment_is_501()
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Draft}/attachments", """{"@odata.type":"#microsoft.graph.itemAttachment","name":"a"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("notImplemented");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Remove_attachment_is_204(string prefix, string address)
    {
        Drafts.RemoveAttachmentAsync(Box(address), new MessageId(Draft), new AttachmentId("3"), Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await SendAsync(HttpMethod.Delete, $"{prefix}/messages/{Draft}/attachments/3")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Remove_missing_attachment_is_404()
    {
        Drafts.RemoveAttachmentAsync(default, default, default, Ct).ReturnsForAnyArgs(Result.Failure(Error.NotFound("no")));

        (await SendAsync(HttpMethod.Delete, $"/v1.0/me/messages/{Draft}/attachments/9")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
