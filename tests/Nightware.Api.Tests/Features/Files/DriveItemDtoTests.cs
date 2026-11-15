using System.Text.Json;

using Nightware.Api.Features.Files.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Files;

namespace Nightware.Api.Tests.Features.Files;

public sealed class DriveItemDtoTests
{
    [Fact]
    public void A_file_has_the_file_facet_only()
    {
        var json = JsonSerializer.SerializeToNode(DriveItemDto.From(new SharedItem("385", "Budget.docx", 5119)
        {
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            WebUrl = "https://cloud.example.com/s/AbC",
            LastModifiedDateTime = new DateTimeOffset(2026, 11, 15, 3, 57, 33, TimeSpan.Zero),
        }), GraphJson.Options)!.AsObject();

        json.Select(p => p.Key).Order().ShouldBe(["file", "id", "lastModifiedDateTime", "name", "size", "webUrl"]);
        json["file"]!["mimeType"]!.GetValue<string>().ShouldBe("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        json["size"]!.GetValue<long>().ShouldBe(5119);
        json["lastModifiedDateTime"]!.GetValue<string>().ShouldBe("2026-11-15T03:57:33Z");
    }

    [Fact]
    public void A_file_of_unknown_type_is_octet_stream() =>
        DriveItemDto.From(new SharedItem("1", "x", 1)).File!.MimeType.ShouldBe("application/octet-stream");

    [Fact]
    public void A_folder_has_the_folder_facet_only()
    {
        var json = JsonSerializer.SerializeToNode(DriveItemDto.From(new SharedItem("384", "Reports", 0) { IsFolder = true }), GraphJson.Options)!.AsObject();

        json.Select(p => p.Key).Order().ShouldBe(["folder", "id", "name", "size"]);
        json["folder"]!.AsObject().Count.ShouldBe(0);
    }
}
