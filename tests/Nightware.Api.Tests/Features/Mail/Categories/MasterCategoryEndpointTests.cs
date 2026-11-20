using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail.Categories;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Categories;

public sealed class MasterCategoryEndpointTests : IAsyncLifetime
{
    private static readonly MasterCategory Red = new("11111111-0000-4000-8000-000000000001", "Red", "preset0");
    private readonly IMasterCategoryStore _store = Substitute.For<IMasterCategoryStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Forms() => new()
    {
        { "/v1.0/me", TestHost.UserEmail },
        { "/v1.0/users/shared@example.com", "shared@example.com" },
    };

    public async ValueTask InitializeAsync()
    {
        _store.ListAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<MasterCategory>?>.Success([Red]));
        _store.SaveAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        _host = await TestHost.StartAsync(services => services.AddSingleton(_store));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return _host.AuthorizedClient().SendAsync(request, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_the_mailbox_s_categories(string prefix, string address)
    {
        var response = await SendAsync(HttpMethod.Get, $"{prefix}/outlook/masterCategories");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["value"]!.AsArray().Single()!.ToJsonString().ShouldBe("""{"id":"11111111-0000-4000-8000-000000000001","displayName":"Red","color":"preset0"}""");
        await _store.Received().ListAsync(new MailboxRef(address), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_is_201_with_a_location()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/outlook/masterCategories", """{"displayName":"Project","color":"preset2"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await response.ReadObjectAsync())["id"]!.GetValue<string>();
        response.Headers.Location!.ToString().ShouldBe($"{TestHost.GraphBase}/users('me%40example.com')/MasterCategories('{id}')");
    }

    [Fact]
    public async Task Get_update_and_delete_work_by_id()
    {
        (await SendAsync(HttpMethod.Get, $"/v1.0/me/outlook/masterCategories/{Red.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await SendAsync(HttpMethod.Patch, $"/v1.0/me/outlook/masterCategories/{Red.Id}", """{"color":"preset5"}""");
        (await updated.ReadObjectAsync())["color"]!.GetValue<string>().ShouldBe("preset5");
        (await SendAsync(HttpMethod.Delete, $"/v1.0/me/outlook/masterCategories/{Red.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Errors_map_to_graph_statuses()
    {
        (await SendAsync(HttpMethod.Get, "/v1.0/me/outlook/masterCategories/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/v1.0/me/outlook/masterCategories", """{"displayName":"red"}""")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Patch, $"/v1.0/me/outlook/masterCategories/{Red.Id}", """{"displayName":"Crimson"}""")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
