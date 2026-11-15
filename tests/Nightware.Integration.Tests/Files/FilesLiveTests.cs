using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Nightware.Integration.Tests.Files;

/// <summary>
/// Phase 7 against the live stack: alice shares documents from her Nextcloud, and bob — whose mail carried the
/// links — previews them through his own façade, as NightMail does for a SharePoint link.
/// </summary>
public sealed class FilesLiveTests : IAsyncLifetime
{
    private const int PublicLink = 3;
    private const int UserShare = 0;

    private FilesLiveHost _host = null!;
    private string _folder = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = FilesLiveHost.Start();
        _folder = "Phase7-live-" + Guid.NewGuid().ToString("N")[..8];
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private static async Task<JsonObject> DriveItemAsync(HttpClient graph, string link)
    {
        using var response = await graph.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId(link)}/driveItem?$select=id,name,size,file,folder", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The client's two legs: <c>/content</c> answers 302, and the Location is fetched with no bearer.</summary>
    private async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(HttpClient graph, string link, bool pdf = false)
    {
        using var redirect = await graph.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId(link)}/driveItem/content{(pdf ? "?format=pdf" : string.Empty)}", Ct);
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect, await redirect.Content.ReadAsStringAsync(Ct));
        using var download = await _host.Anonymous().GetAsync(FilesLiveHost.Local(redirect.Headers.Location!), Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.OK, await download.Content.ReadAsStringAsync(Ct));
        return (await download.Content.ReadAsByteArrayAsync(Ct), download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Bob_previews_alices_public_docx_and_pdf_shares()
    {
        var docx = await _host.RenderAsync("Quarterly budget " + _folder, "docx", Ct);
        var pdf = await _host.RenderAsync("Quarterly report " + _folder, "pdf", Ct);
        await _host.UploadAsync(FilesLiveHost.Alice, $"{_folder}/Budget.docx", docx, Ct);
        await _host.UploadAsync(FilesLiveHost.Alice, $"{_folder}/Report.pdf", pdf, Ct);
        var (docxLink, docxId) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}/Budget.docx", PublicLink, Ct);
        var (pdfLink, _) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}/Report.pdf", PublicLink, Ct);
        docxLink!.ShouldStartWith(FilesLiveHost.CloudBase + "/s/");
        using var bob = await _host.GraphAsync(FilesLiveHost.Bob, Ct);

        var item = await DriveItemAsync(bob, docxLink);
        item["id"]!.GetValue<string>().ShouldBe(docxId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        item["name"]!.GetValue<string>().ShouldBe("Budget.docx");
        item["size"]!.GetValue<long>().ShouldBe(docx.Length);
        item["file"]!["mimeType"]!.GetValue<string>().ShouldBe("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        (await DriveItemAsync(bob, pdfLink!))["file"]!["mimeType"]!.GetValue<string>().ShouldBe("application/pdf");

        (await DownloadAsync(bob, docxLink)).Bytes.ShouldBe(docx);
        (await DownloadAsync(bob, pdfLink!)).Bytes.ShouldBe(pdf);
        (await DownloadAsync(bob, pdfLink!, pdf: true)).Bytes.ShouldBe(pdf);

        var rendered = await DownloadAsync(bob, docxLink, pdf: true);
        rendered.ContentType.ShouldBe("application/pdf");
        Encoding.ASCII.GetString(rendered.Bytes, 0, 5).ShouldBe("%PDF-");
    }

    [Fact]
    public async Task Bob_previews_an_internal_link_only_when_it_is_shared_with_him()
    {
        // Unique: the share is mounted in bob's root, where Nextcloud renames a clash ("Shared (2).docx").
        var name = $"Shared-{_folder[^8..]}.docx";
        var docx = await _host.RenderAsync("Shared notes " + _folder, "docx", Ct);
        await _host.UploadAsync(FilesLiveHost.Alice, $"{_folder}/{name}", docx, Ct);
        await _host.UploadAsync(FilesLiveHost.Alice, $"{_folder}/Private.docx", docx, Ct);
        var (_, sharedId) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}/{name}", UserShare, Ct, shareWith: FilesLiveHost.Bob);
        var (_, privateId) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}/Private.docx", PublicLink, Ct);
        using var bob = await _host.GraphAsync(FilesLiveHost.Bob, Ct);

        var link = $"{FilesLiveHost.CloudBase}/f/{sharedId}";
        (await DriveItemAsync(bob, link))["name"]!.GetValue<string>().ShouldBe(name);
        (await DownloadAsync(bob, link)).Bytes.ShouldBe(docx);
        Encoding.ASCII.GetString((await DownloadAsync(bob, link, pdf: true)).Bytes, 0, 5).ShouldBe("%PDF-");

        // Public-linked, but not shared with bob: the internal link reveals nothing.
        using var hidden = await bob.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId($"{FilesLiveHost.CloudBase}/f/{privateId}")}/driveItem", Ct);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Shares_the_preview_cannot_show_answer_as_the_client_expects()
    {
        var docx = await _host.RenderAsync("Locked " + _folder, "docx", Ct);
        await _host.UploadAsync(FilesLiveHost.Alice, $"{_folder}/Locked.docx", docx, Ct);
        var (lockedLink, _) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}/Locked.docx", PublicLink, Ct, password: "Phase7-" + Guid.NewGuid().ToString("N"));
        var (folderLink, _) = await _host.ShareAsync(FilesLiveHost.Alice, $"/{_folder}", PublicLink, Ct);
        using var bob = await _host.GraphAsync(FilesLiveHost.Bob, Ct);

        using (var locked = await bob.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId(lockedLink!)}/driveItem", Ct))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var folder = await DriveItemAsync(bob, folderLink!);
        folder["folder"].ShouldNotBeNull();
        folder.ContainsKey("file").ShouldBeFalse();

        using (var gone = await bob.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId(FilesLiveHost.CloudBase + "/s/doesNotExist" + _folder[^8..])}/driveItem", Ct))
        {
            gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var elsewhere = await bob.GetAsync($"/v1.0/shares/{FilesLiveHost.ShareId("https://evil.example.com/s/AbCdEfGh")}/driveItem", Ct);
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
