using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph.Json;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class UploadUrlCredentialSourceTests
{
    private static readonly UploadUrlBinding Binding = new("alice@example.com", "l", "t", "s");
    private const string Path = "/v1.0/users/alice@example.com/todo/lists/l/tasks/t/attachmentSessions/s/content";

    private readonly UploadUrlProtector _protector = new(Options.Create(new DeltaLinkOptions { SigningKey = "k" }), TimeProvider.System);

    private string Sealed() => _protector.Seal(new UploadTicket("the-token", "a", 4, DateTimeOffset.UtcNow.AddMinutes(5)), Binding);

    private bool Resolve(string method, string path, string query, out string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return new UploadUrlCredentialSource(_protector).TryResolve(context.Request, out token);
    }

    [Fact]
    public void Recovers_the_token_for_a_put_to_its_own_path()
    {
        Resolve("PUT", Path, "?authtoken=" + Sealed(), out var token).ShouldBeTrue();
        token.ShouldBe("the-token");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Other_methods_get_nothing(string method) =>
        Resolve(method, Path, "?authtoken=" + Sealed(), out _).ShouldBeFalse();

    [Fact]
    public void Two_tokens_are_refused()
    {
        var value = Sealed();

        Resolve("PUT", Path, $"?authtoken={value}&authtoken={value}", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?authtoken=")]
    [InlineData("?token=x")]
    public void Requests_without_a_token_get_nothing(string query) =>
        Resolve("PUT", Path, query, out _).ShouldBeFalse();

    [Fact]
    public void Other_paths_get_nothing() =>
        Resolve("PUT", "/v1.0/users/alice@example.com/messages/m", "?authtoken=" + Sealed(), out _).ShouldBeFalse();
}
