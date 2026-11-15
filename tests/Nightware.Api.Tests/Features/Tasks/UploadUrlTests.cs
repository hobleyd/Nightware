using Microsoft.AspNetCore.Http;

using Nightware.Api.Features.Tasks.Upload;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class UploadUrlTests
{
    [Fact]
    public void Builds_a_url_whose_path_parses_back()
    {
        var binding = new UploadUrlBinding("alice@example.com", "bGlzdA", "dGFzaw", "c2Vzc2lvbg");

        var url = new Uri(UploadUrl.Build("https://graph.example/v1.0", binding, "sealed-value"));

        url.AbsolutePath.ShouldBe("/v1.0/users/alice@example.com/todo/lists/bGlzdA/tasks/dGFzaw/attachmentSessions/c2Vzc2lvbg/content");
        url.Query.ShouldBe("?authtoken=sealed-value");
        UploadUrl.TryParse(new PathString(url.AbsolutePath), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(binding);
    }

    [Theory]
    [InlineData("/beta/users/a@b.c/todo/lists/l/tasks/t/attachmentSessions/s/content")]
    [InlineData("/v1.0/USERS/a@b.c/todo/lists/l/tasks/t/AttachmentSessions/s/content")]
    public void Parses_either_version_and_any_case(string path) =>
        UploadUrl.TryParse(new PathString(path), out _).ShouldBeTrue();

    [Theory]
    [InlineData("/v1.0/me/todo/lists/l/tasks/t/attachmentSessions/s/content")]
    [InlineData("/v1.0/users/a@b.c/todo/lists/l/tasks/t/attachmentSessions/s")]
    [InlineData("/v1.0/users/a@b.c/todo/lists/l/tasks/t/attachmentSessions/s/content/extra")]
    [InlineData("/v1.0/users/a@b.c/todo/lists/l/tasks/t/attachments/s/content")]
    [InlineData("/v1.0/users/a@b.c/todo/lists//tasks/t/attachmentSessions/s/content")]
    [InlineData("/other/users/a@b.c/todo/lists/l/tasks/t/attachmentSessions/s/content")]
    [InlineData("/v1.0/users/a@b.c/messages/m")]
    public void Rejects_other_paths(string path) =>
        UploadUrl.TryParse(new PathString(path), out _).ShouldBeFalse();
}
