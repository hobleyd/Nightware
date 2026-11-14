using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Directory;

public sealed class DirectoryEndpointsTests : IAsyncLifetime
{
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IUserPhotoReader _photos = Substitute.For<IUserPhotoReader>();
    private TestHost _host = null!;

    private static readonly DirectoryUser Sam = new("id-sam", "Sam Attendee", "sam@example.com")
    {
        Mail = "sam@example.com",
        GivenName = "Sam",
        Surname = "Attendee",
        JobTitle = "Analyst",
        Department = "Finance",
        CompanyName = "Example Pty Ltd",
        OfficeLocation = "Sydney",
        MobilePhone = "+61 400 000 000",
        BusinessPhones = ["+61 2 9000 0000"],
        Aliases = ["s.attendee@example.com"],
    };

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_directory).AddSingleton(_photos));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync(string path) => _host.AuthorizedClient().GetAsync(path, Ct);

    [Fact]
    public async Task Me_is_the_caller_looked_up_by_subject()
    {
        _directory.GetAsync("user-1", Arg.Any<CancellationToken>()).Returns(Sam with { Id = "user-1" });

        var response = await GetAsync("/v1.0/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["@odata.context"]?.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#users/$entity");
        body.Str("id").ShouldBe("user-1");
        body.Str("displayName").ShouldBe("Sam Attendee");
        body.Str("givenName").ShouldBe("Sam");
        body.Str("mobilePhone").ShouldBe("+61 400 000 000");
        body["businessPhones"]!.AsArray().Select(static p => p!.GetValue<string>()).ShouldBe(["+61 2 9000 0000"]);
        body.ContainsKey("department").ShouldBeFalse("outside Graph's default user projection");
        body.ContainsKey("proxyAddresses").ShouldBeFalse();
    }

    [Fact]
    public async Task Me_projects_the_selected_properties_only()
    {
        _directory.GetAsync("user-1", Arg.Any<CancellationToken>()).Returns(Sam with { Id = "user-1" });

        var body = await (await GetAsync("/v1.0/me?%24select=mail%2CuserPrincipalName%2CproxyAddresses")).ReadObjectAsync();

        body.Select(static p => p.Key).ShouldBe(["@odata.context", "mail", "userPrincipalName", "proxyAddresses"], ignoreOrder: true);
        body["proxyAddresses"]!.AsArray().Select(static p => p!.GetValue<string>()).ShouldBe(["SMTP:sam@example.com", "smtp:s.attendee@example.com"]);
        body["@odata.context"]?.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#users(mail,userPrincipalName,proxyAddresses)/$entity");
    }

    [Fact]
    public async Task Me_falls_back_to_the_token_when_the_directory_does_not_know_the_caller()
    {
        _directory.GetAsync("user-1", Arg.Any<CancellationToken>()).Returns(Error.NotFound("nope"));

        var body = await (await GetAsync("/v1.0/me?$select=id,displayName,mail,userPrincipalName")).ReadObjectAsync();

        body.Str("id").ShouldBe("user-1");
        body.Str("mail").ShouldBe(TestHost.UserEmail);
        body.Str("userPrincipalName").ShouldBe(TestHost.UserEmail);
        body.Str("displayName").ShouldBe("Me Example");
    }

    [Fact]
    public async Task Me_reports_a_directory_outage()
    {
        _directory.GetAsync("user-1", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await GetAsync("/v1.0/me")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("/v1.0/users/sam%40example.com")]
    [InlineData("/v1.0/users/sam@example.com")]
    [InlineData("/beta/users/sam%40example.com")]
    public async Task A_user_is_found_by_address_in_either_encoding(string path)
    {
        _directory.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(Sam);

        var response = await GetAsync(path + "?$select=displayName,department,companyName,mail");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("department").ShouldBe("Finance");
        body.Str("companyName").ShouldBe("Example Pty Ltd");
        body.ContainsKey("id").ShouldBeFalse("Graph omits an unselected id on directory objects");
    }

    [Fact]
    public async Task A_user_is_found_by_directory_id()
    {
        _directory.GetAsync("id-sam", Arg.Any<CancellationToken>()).Returns(Sam);

        (await (await GetAsync("/v1.0/users/id-sam")).ReadObjectAsync()).Str("id").ShouldBe("id-sam");
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorKind.Validation, HttpStatusCode.NotFound)]
    [InlineData(ErrorKind.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorKind.BackendUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task User_lookup_failures_map_to_the_statuses_the_client_swallows(ErrorKind kind, HttpStatusCode status)
    {
        _directory.GetAsync("x@example.com", Arg.Any<CancellationToken>()).Returns(new Error(kind, "c", "m"));

        (await GetAsync("/v1.0/users/x%40example.com")).StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task The_directory_is_paged_with_links_on_the_graph_base()
    {
        _directory.ListAsync(Arg.Is<ItemQuery>(q => q.Top == 999 && q.SkipToken == null), Arg.Any<CancellationToken>())
            .Returns(new Page<DirectoryUser>([Sam], "999"));

        var body = await (await GetAsync("/v1.0/users?%24select=displayName%2Cmail%2CuserPrincipalName&%24top=999")).ReadObjectAsync();

        var user = body["value"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        user.Select(static p => p.Key).ShouldBe(["displayName", "mail", "userPrincipalName"], ignoreOrder: true);
        body["@odata.nextLink"]?.GetValue<string>().ShouldBe(TestHost.GraphBase + "/users?$select=displayName%2Cmail%2CuserPrincipalName&$top=999&$skiptoken=999");
    }

    [Fact]
    public async Task A_next_page_passes_the_skiptoken_back()
    {
        _directory.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == "999"), Arg.Any<CancellationToken>()).Returns(new Page<DirectoryUser>([Sam]));

        var body = await (await GetAsync("/v1.0/users?$top=999&$skiptoken=999")).ReadObjectAsync();

        body["value"]!.AsArray().Count.ShouldBe(1);
        body.ContainsKey("@odata.nextLink").ShouldBeFalse();
    }

    [Fact]
    public async Task Unsupported_directory_queries_are_a_400()
    {
        _directory.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.Validation("$filter is not supported on users."));

        var response = await GetAsync("/v1.0/users?$filter=displayName eq 'x'");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_photo_is_streamed_with_its_content_type()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>())
            .Returns(new BlobContent("image/png", png.Length, _ => Task.FromResult<Stream>(new MemoryStream(png))));

        var response = await GetAsync("/v1.0/users/sam%40example.com/photo/$value");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(png);
    }

    [Fact]
    public async Task A_photo_by_directory_id_resolves_the_address_first()
    {
        _directory.GetAsync("id-sam", Arg.Any<CancellationToken>()).Returns(Sam);
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>())
            .Returns(new BlobContent("image/jpeg", 1, _ => Task.FromResult<Stream>(new MemoryStream([1]))));

        (await GetAsync("/v1.0/users/id-sam/photo/$value")).Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task No_photo_is_a_404_envelope()
    {
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no photo"));

        var response = await GetAsync("/v1.0/users/sam@example.com/photo/$value");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync())["error"].ShouldBeOfType<JsonObject>();
    }

    [Fact]
    public async Task Directory_routes_require_a_bearer()
    {
        (await _host.Client.GetAsync("/v1.0/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _host.Client.GetAsync("/v1.0/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
