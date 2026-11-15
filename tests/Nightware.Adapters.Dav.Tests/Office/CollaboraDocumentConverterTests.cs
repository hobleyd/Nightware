using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Office;
using Nightware.Application.Files;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Office;

/// <summary>
/// The converter through the adapter's real registration, against a WireMock Collabora. <c>Fixtures/Office</c>
/// holds a .docx and the PDF CODE 26.04.5.2 rendered from it (recorded from the dev stack).
/// </summary>
public sealed class CollaboraDocumentConverterTests : IDisposable
{
    private const string ConvertPath = "/cool/convert-to/pdf";
    private static readonly byte[] Docx = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Office", "Budget.docx"));
    private static readonly byte[] Pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Office", "Budget.pdf"));

    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Provider(Dictionary<string, string?>? settings = null, bool configured = true)
    {
        var values = new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/" };
        if (configured)
        {
            values["Collabora:BaseUrl"] = _server.Url + "/";
        }

        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var services = new ServiceCollection().AddLogging();
        services.AddDavAdapter(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static (BlobContent Blob, TrackingStream Stream) Source(byte[] bytes, long? length = null)
    {
        var stream = new TrackingStream(bytes);
        return (new BlobContent("application/octet-stream", length ?? bytes.Length, _ => Task.FromResult<Stream>(stream)), stream);
    }

    private void Answer(int status, byte[]? body = null) =>
        _server.Given(Request.Create().WithPath(ConvertPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/octet-stream").WithBody(body ?? []));

    [Fact]
    public void Is_registered_when_configured()
    {
        using var provider = Provider();
        provider.GetRequiredService<IDocumentConverter>().ShouldBeOfType<CollaboraDocumentConverter>();
    }

    [Fact]
    public async Task Without_a_base_url_nothing_converts()
    {
        using var provider = Provider(configured: false);
        var converter = provider.GetRequiredService<IDocumentConverter>();
        var (blob, stream) = Source(Docx);

        converter.ShouldBeOfType<UnconfiguredDocumentConverter>();
        converter.CanConvertToPdf("Budget.docx", null).ShouldBeFalse();
        (await converter.ConvertToPdfAsync(blob, "Budget.docx", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        stream.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Budget.docx", null, true)]
    [InlineData("Budget.XLSX", null, true)]
    [InlineData("Deck.pptx", null, true)]
    [InlineData("notes.odt", null, true)]
    [InlineData("data.csv", "text/csv", true)]
    [InlineData("noextension", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("legacy", "application/msword", true)]
    [InlineData("report.pdf", "application/pdf", false)]
    [InlineData("photo.jpg", "image/jpeg", false)]
    [InlineData("notes.txt", "text/plain", false)]
    [InlineData("README.md", "text/markdown", false)]
    [InlineData("trailingdot.", null, false)]
    public void Offers_office_and_opendocument_formats_only(string name, string? mime, bool expected)
    {
        using var provider = Provider();
        provider.GetRequiredService<IDocumentConverter>().CanConvertToPdf(name, mime).ShouldBe(expected);
    }

    [Fact]
    public async Task Posts_the_document_as_multipart_data_and_streams_back_a_pdf()
    {
        Answer(200, Pdf);
        using var provider = Provider();
        var (blob, stream) = Source(Docx);

        var result = (await provider.GetRequiredService<IDocumentConverter>().ConvertToPdfAsync(blob, "Q3 Büdget \"final\".docx", Ct)).Value;

        result.ContentType.ShouldBe("application/pdf");
        result.FileName.ShouldBe("Q3 Büdget \"final\".pdf");
        await using (var pdf = await result.Open(Ct))
        {
            using var copy = new MemoryStream();
            await pdf.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe(Pdf);
        }

        stream.Disposed.ShouldBeTrue();
        var request = _server.LogEntries.Single().RequestMessage!;
        request.Headers!["Content-Type"].Single().ShouldStartWith("multipart/form-data");
        var body = request.Body ?? System.Text.Encoding.Latin1.GetString(request.BodyAsBytes!);
        body.ShouldContain("name=data");
        body.ShouldContain("filename=document.docx");
        body.ShouldNotContain("final");
    }

    [Theory]
    [InlineData(400, ErrorKind.Validation)]
    [InlineData(413, ErrorKind.PayloadTooLarge)]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(403, ErrorKind.BackendUnavailable)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    public async Task Failures_are_mapped_and_release_the_source(int status, ErrorKind kind)
    {
        Answer(status);
        using var provider = Provider();
        var (blob, stream) = Source(Docx);

        (await provider.GetRequiredService<IDocumentConverter>().ConvertToPdfAsync(blob, "Budget.docx", Ct)).Error!.Kind.ShouldBe(kind);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unreachable_converter_is_unavailable()
    {
        using var provider = Provider();
        _server.Stop();
        var (blob, _) = Source(Docx);

        (await provider.GetRequiredService<IDocumentConverter>().ConvertToPdfAsync(blob, "Budget.docx", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_slow_converter_times_out_as_unavailable()
    {
        _server.Given(Request.Create().WithPath(ConvertPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));
        using var provider = Provider(new() { ["Collabora:Timeout"] = "00:00:00.200" });
        var (blob, _) = Source(Docx);

        (await provider.GetRequiredService<IDocumentConverter>().ConvertToPdfAsync(blob, "Budget.docx", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_document_over_the_cap_is_refused_without_a_request()
    {
        using var provider = Provider(new() { ["Collabora:MaxSourceBytes"] = "100" });
        var (blob, stream) = Source(Docx);

        (await provider.GetRequiredService<IDocumentConverter>().ConvertToPdfAsync(blob, "Budget.docx", Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        stream.Disposed.ShouldBeTrue();
        _server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Budget.docx", "document.docx")]
    [InlineData("Budget.DOCX", "document.docx")]
    [InlineData("no-extension", "document")]
    [InlineData("weird.döcx", "document")]
    public void The_part_is_named_by_extension_only(string fileName, string expected) =>
        CollaboraDocumentConverter.PartName(fileName).ShouldBe(expected);

    [Theory]
    [InlineData("http://collabora:9980/", true)]
    [InlineData("ftp://collabora/", false)]
    [InlineData("http://collabora:9980/?x=1", false)]
    public void The_base_url_is_validated(string url, bool valid)
    {
        using var provider = Provider(new() { ["Collabora:BaseUrl"] = url });
        var resolve = () => provider.GetRequiredService<IOptions<CollaboraOptions>>().Value;

        if (valid)
        {
            resolve().BaseUrl.ShouldBe(new Uri(url));
        }
        else
        {
            Should.Throw<OptionsValidationException>(resolve);
        }
    }

    [Theory]
    [InlineData("Collabora:Timeout", "00:00:00")]
    [InlineData("Collabora:MaxSourceBytes", "0")]
    public void Limits_must_be_positive(string key, string value)
    {
        using var provider = Provider(new() { [key] = value });
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CollaboraOptions>>().Value);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
