using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Files;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Files;

/// <summary>The <c>/shares</c> routes through the real composition, with the file reader and converter faked.</summary>
public sealed class FilesEndpointsTests : IAsyncLifetime
{
    private const string Site = "https://cloud.example.com";
    private static readonly string ShareId = SharingUrlsTests.Encode(Site + "/s/AbCdEfGh");
    private static readonly SharedItem Docx = new("385", "Budget.docx", 5119)
    {
        MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        WebUrl = Site + "/s/AbCdEfGh",
    };

    private readonly ISharedFileReader _reader = Substitute.For<ISharedFileReader>();
    private readonly IDocumentConverter _converter = Substitute.For<IDocumentConverter>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await StartAsync(Site);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task<TestHost> StartAsync(string? site) => TestHost.StartAsync(
        services =>
        {
            services.AddScoped(_ => _reader);
            services.AddScoped(_ => _converter);
        },
        configuration: site is null ? null : new Dictionary<string, string?> { ["Discovery:FilesBaseUrls:0"] = site });

    private static ShareLink Link => Arg.Is<ShareLink>(l => l.SitePath == "s/AbCdEfGh" && l.Url.AbsoluteUri == Site + "/s/AbCdEfGh");

    private static BlobContent Blob(string type, byte[] bytes, string? name = null) =>
        new(type, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes))) { FileName = name };

    private void GivenItem(SharedItem item) => _reader.GetItemAsync(Link, Arg.Any<CancellationToken>()).Returns(item);

    private HttpClient Anonymous() => _host.Client;

    private async Task<Uri> RedirectAsync(string version = "v1.0", string query = "")
    {
        using var response = await _host.AuthorizedClient().GetAsync($"/{version}/shares/{ShareId}/driveItem/content{query}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return response.Headers.Location!;
    }

    [Theory]
    [InlineData("v1.0")]
    [InlineData("beta")]
    public async Task Drive_item_is_projected_as_graph_does(string version)
    {
        GivenItem(Docx);

        var response = await _host.AuthorizedClient().GetAsync($"/{version}/shares/{ShareId}/driveItem?$select=id,name,size,file,folder", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("id").ShouldBe("385");
        body.Str("name").ShouldBe("Budget.docx");
        body.Str("file.mimeType").ShouldBe(Docx.MimeType);
        body.ContainsKey("folder").ShouldBeFalse();
        body.ContainsKey("webUrl").ShouldBeFalse();
        body["@odata.context"]!.GetValue<string>().ShouldContain("/driveItem(");
    }

    [Fact]
    public async Task A_folder_carries_the_folder_facet()
    {
        GivenItem(new SharedItem("384", "Reports", 0) { IsFolder = true });

        var body = await (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem", Ct)).ReadObjectAsync();

        body["folder"].ShouldNotBeNull();
        body.ContainsKey("file").ShouldBeFalse();
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorKind.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorKind.BackendUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Reader_failures_are_graph_errors(ErrorKind kind, HttpStatusCode status)
    {
        _reader.GetItemAsync(Link, Arg.Any<CancellationToken>()).Returns(new Error(kind, "x", "nope"));

        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem", Ct)).StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task A_link_on_another_host_is_404_without_touching_the_backend()
    {
        var other = SharingUrlsTests.Encode("https://evil.example.com/s/AbCdEfGh");

        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{other}/driveItem", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{other}/driveItem/content", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _reader.DidNotReceiveWithAnyArgs().GetItemAsync(null!, Ct);
    }

    [Fact]
    public async Task Without_file_sites_the_feature_is_off()
    {
        await using var host = await StartAsync(null);

        (await host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_share_id_is_400()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/shares/not-a-share/driveItem", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Fact]
    public async Task Shares_require_authentication() =>
        (await _host.Client.GetAsync($"/v1.0/shares/{ShareId}/driveItem", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("v1.0", "", "")]
    [InlineData("v1.0", "?format=pdf", "format=pdf&")]
    [InlineData("beta", "?format=PDF", "format=pdf&")]
    public async Task Content_redirects_to_a_short_lived_download_url_on_the_public_base(string version, string query, string expectedFormat)
    {
        GivenItem(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        var token = TestHost.Token();

        using var response = await _host.AuthorizedClient(token).GetAsync($"/{version}/shares/{ShareId}/driveItem/content{query}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.AbsoluteUri;
        var root = version == "beta" ? TestHost.GraphBetaBase : TestHost.GraphBase;
        location.ShouldStartWith($"{root}/shares/{Uri.EscapeDataString(ShareId)}/driveItem/download?{expectedFormat}authtoken=");
        location.ShouldNotContain(token);
        await _reader.DidNotReceiveWithAnyArgs().GetContentAsync(null!, Ct);
    }

    [Fact]
    public async Task The_download_url_streams_the_file_without_a_bearer()
    {
        GivenItem(Docx);
        _reader.GetContentAsync(Link, Arg.Any<CancellationToken>()).Returns(_ => Blob("application/octet-stream", [1, 2, 3]));
        var location = await RedirectAsync();

        using var response = await Anonymous().GetAsync(location, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(new byte[] { 1, 2, 3 });
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("Budget.docx");
    }

    [Fact]
    public async Task The_pdf_download_url_streams_the_converted_document()
    {
        GivenItem(Docx);
        var stored = Blob("application/octet-stream", [1, 2, 3]);
        _reader.GetContentAsync(Link, Arg.Any<CancellationToken>()).Returns(stored);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        _converter.ConvertToPdfAsync(stored, Docx.Name, Arg.Any<CancellationToken>()).Returns(Blob("application/pdf", "%PDF-1.7"u8.ToArray(), "Budget.pdf"));
        var location = await RedirectAsync(query: "?format=pdf");

        using var response = await Anonymous().GetAsync(location, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("%PDF-1.7");
    }

    [Fact]
    public async Task A_pdf_the_converter_cannot_make_is_refused_on_the_first_leg()
    {
        GivenItem(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(false);

        var response = await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem/content?format=pdf", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("notSupported");
    }

    [Fact]
    public async Task An_unsupported_format_is_400()
    {
        var response = await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem/content?format=html", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _reader.DidNotReceiveWithAnyArgs().GetItemAsync(null!, Ct);
    }

    [Fact]
    public async Task Content_of_a_missing_item_is_404_not_a_redirect()
    {
        _reader.GetItemAsync(Link, Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem/content", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_failed_download_is_a_graph_error()
    {
        GivenItem(Docx);
        _reader.GetContentAsync(Link, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));
        var location = await RedirectAsync();

        using var response = await Anonymous().GetAsync(location, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("authtoken")]
    [InlineData("format")]
    [InlineData("share")]
    public async Task A_changed_download_url_is_401(string change)
    {
        GivenItem(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        var location = (await RedirectAsync(query: "?format=pdf")).AbsoluteUri;
        var tampered = change switch
        {
            "authtoken" => location[..^3] + (location[^3] == 'A' ? "B" : "A") + location[^2..],
            "format" => location.Replace("format=pdf&", string.Empty, StringComparison.Ordinal),
            _ => location.Replace(Uri.EscapeDataString(ShareId), Uri.EscapeDataString(SharingUrlsTests.Encode(Site + "/s/Other")), StringComparison.Ordinal),
        };

        (await Anonymous().GetAsync(tampered, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_download_url_is_not_a_bearer_for_other_routes()
    {
        GivenItem(Docx);
        var location = await RedirectAsync();
        var authtoken = System.Web.HttpUtility.ParseQueryString(location.Query)["authtoken"];

        (await Anonymous().GetAsync($"/v1.0/shares/{ShareId}/driveItem?authtoken={Uri.EscapeDataString(authtoken!)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Anonymous().GetAsync($"/v1.0/me/messages?authtoken={Uri.EscapeDataString(authtoken!)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_download_url_for_an_expired_token_is_401()
    {
        GivenItem(Docx);
        // Beyond the JWT handler's five-minute clock skew.
        var expired = TestHost.Token(expires: DateTime.UtcNow.AddMinutes(-10));
        var protector = _host.App.Services.GetRequiredService<Api.Features.Files.Download.DownloadUrlProtector>();
        var binding = new Api.Features.Files.Download.DownloadUrlBinding(ShareId, ContentFormat.Original);
        var url = Api.Features.Files.Download.DownloadUrl.Build("/v1.0", binding,
            protector.Seal(new Api.Features.Files.Download.DownloadTicket(expired, DateTimeOffset.UtcNow.AddMinutes(5)), binding));

        (await Anonymous().GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_download_route_with_a_bearer_and_no_authtoken_streams_too()
    {
        GivenItem(Docx);
        _reader.GetContentAsync(Link, Arg.Any<CancellationToken>()).Returns(_ => Blob("text/plain", [65]));

        using var response = await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem/download", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_download_route_checks_its_format_and_link_too()
    {
        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{ShareId}/driveItem/download?format=html", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _host.AuthorizedClient().GetAsync($"/v1.0/shares/{SharingUrlsTests.Encode("https://evil.example.com/s/x")}/driveItem/download", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_oversized_access_token_is_refused_rather_than_minting_an_unreadable_url()
    {
        GivenItem(Docx);
        var huge = TestHost.Token(sub: new string('x', 7000));

        (await _host.AuthorizedClient(huge).GetAsync($"/v1.0/shares/{ShareId}/driveItem/content", Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }
}
