using Nightware.Application.Files;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Files;

public sealed class SharedItemUseCaseTests
{
    private static readonly ShareLink Link = new(new Uri("https://files.example.com/s/AbCdEfGh"), "s/AbCdEfGh");
    private static readonly SharedItem Docx = new("385", "Budget.docx", 5119)
    {
        MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    private static readonly SharedItem Pdf = new("386", "Budget.pdf", 13353) { MimeType = "application/pdf" };
    private static readonly SharedItem Folder = new("384", "Reports", 0) { IsFolder = true };

    private readonly ISharedFileReader _reader = Substitute.For<ISharedFileReader>();
    private readonly IDocumentConverter _converter = Substitute.For<IDocumentConverter>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BlobContent Blob(string type, string? name = null) =>
        new(type, 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))) { FileName = name };

    private void Given(SharedItem item) => _reader.GetItemAsync(Link, Ct).Returns(item);

    [Fact]
    public async Task Get_returns_the_item_from_the_reader()
    {
        Given(Docx);

        (await new GetSharedItem(_reader).ExecuteAsync(Link, Ct)).Value.ShouldBeSameAs(Docx);
    }

    [Fact]
    public async Task Get_passes_reader_failures_through()
    {
        _reader.GetItemAsync(Link, Ct).Returns(Error.NotFound("gone"));

        (await new GetSharedItem(_reader).ExecuteAsync(Link, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(ContentFormat.Original)]
    [InlineData(ContentFormat.Pdf)]
    public async Task Prepare_allows_a_convertible_file_in_either_format(ContentFormat format)
    {
        Given(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);

        (await new PrepareSharedItemDownload(_reader, _converter).ExecuteAsync(Link, format, Ct)).Value.ShouldBeSameAs(Docx);
    }

    [Fact]
    public async Task Prepare_allows_a_pdf_as_pdf_without_asking_the_converter()
    {
        Given(Pdf);

        (await new PrepareSharedItemDownload(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).IsSuccess.ShouldBeTrue();
        _converter.DidNotReceiveWithAnyArgs().CanConvertToPdf(null!, null);
    }

    [Fact]
    public async Task Prepare_refuses_a_pdf_the_converter_cannot_produce()
    {
        Given(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(false);

        var error = (await new PrepareSharedItemDownload(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Code.ShouldBe("notSupported");
    }

    [Theory]
    [InlineData(ContentFormat.Original)]
    [InlineData(ContentFormat.Pdf)]
    public async Task Prepare_refuses_a_folder(ContentFormat format)
    {
        Given(Folder);

        (await new PrepareSharedItemDownload(_reader, _converter).ExecuteAsync(Link, format, Ct)).Error!.Code.ShouldBe("notSupported");
    }

    [Fact]
    public async Task Prepare_passes_reader_failures_through()
    {
        _reader.GetItemAsync(Link, Ct).Returns(Error.Forbidden("password"));

        (await new PrepareSharedItemDownload(_reader, _converter).ExecuteAsync(Link, ContentFormat.Original, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Download_original_streams_the_stored_bytes_named_after_the_item()
    {
        Given(Docx);
        _reader.GetContentAsync(Link, Ct).Returns(Blob("application/octet-stream"));

        var content = (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Original, Ct)).Value;

        content.FileName.ShouldBe("Budget.docx");
        await _converter.DidNotReceiveWithAnyArgs().ConvertToPdfAsync(null!, null!, Ct);
    }

    [Fact]
    public async Task Download_pdf_converts_an_office_file()
    {
        Given(Docx);
        var stored = Blob("application/octet-stream", "Budget.docx");
        var pdf = Blob("application/pdf", "Budget.pdf");
        _reader.GetContentAsync(Link, Ct).Returns(stored);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        _converter.ConvertToPdfAsync(stored, Docx.Name, Ct).Returns(pdf);

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Value.ShouldBeSameAs(pdf);
    }

    [Fact]
    public async Task Download_pdf_of_a_pdf_serves_it_unconverted()
    {
        Given(Pdf);
        var stored = Blob("application/pdf", "Budget.pdf");
        _reader.GetContentAsync(Link, Ct).Returns(stored);

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Value.ShouldBe(stored);
        await _converter.DidNotReceiveWithAnyArgs().ConvertToPdfAsync(null!, null!, Ct);
    }

    [Fact]
    public async Task Download_rechecks_the_item_before_fetching_anything()
    {
        Given(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(false);

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Error!.Code.ShouldBe("notSupported");
        await _reader.DidNotReceiveWithAnyArgs().GetContentAsync(null!, Ct);
    }

    [Fact]
    public async Task Download_passes_item_failures_through()
    {
        _reader.GetItemAsync(Link, Ct).Returns(Error.NotFound("gone"));

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Original, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Download_passes_content_failures_through_without_converting()
    {
        Given(Docx);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        _reader.GetContentAsync(Link, Ct).Returns(Error.BackendUnavailable("down"));

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        await _converter.DidNotReceiveWithAnyArgs().ConvertToPdfAsync(null!, null!, Ct);
    }

    [Fact]
    public async Task Download_passes_conversion_failures_through()
    {
        Given(Docx);
        var stored = Blob("application/octet-stream");
        _reader.GetContentAsync(Link, Ct).Returns(stored);
        _converter.CanConvertToPdf(Docx.Name, Docx.MimeType).Returns(true);
        _converter.ConvertToPdfAsync(stored, Docx.Name, Ct).Returns(Error.BackendUnavailable("collabora down"));

        (await new DownloadSharedItem(_reader, _converter).ExecuteAsync(Link, ContentFormat.Pdf, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("a.pdf", null, true)]
    [InlineData("a.PDF", "application/octet-stream", true)]
    [InlineData("a.bin", "application/pdf", true)]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    public void An_item_is_a_pdf_by_type_or_name(string name, string? mime, bool expected) =>
        new SharedItem("1", name, 1) { MimeType = mime }.IsPdf.ShouldBe(expected);

    [Fact]
    public void A_folder_is_never_a_pdf() =>
        new SharedItem("1", "x.pdf", 0) { IsFolder = true }.IsPdf.ShouldBeFalse();
}
