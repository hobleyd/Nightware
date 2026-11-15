using System.Buffers.Text;
using System.Text;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Tasks.Attachments;

public sealed class TaskAttachmentPartsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly TaskUploadToken Token = new("alice@example.com", "calendars/alice/tasks/", "calendars/alice/tasks/1.ics", "a.eml", "message/rfc822", 42, Now.AddMinutes(15).ToUnixTimeSeconds());

    [Fact]
    public void Upload_tokens_round_trip()
    {
        var encoded = Token.Encode();

        encoded.ShouldNotContain("=");
        TaskUploadToken.Decode(encoded, Now).Value.ShouldBe(Token);
    }

    public static TheoryData<string> BadTokens() =>
    [
        string.Empty,
        "not base64!",
        Base64Url.EncodeToString("{}"u8),
        Base64Url.EncodeToString("not json"u8),
        Encode(Token with { Version = 2 }),
        Encode(Token with { Size = 0 }),
        Encode(Token with { ListHref = "../../files/bob/" }),
        Encode(Token with { TaskHref = "/etc/passwd" }),
        Encode(Token with { Name = " " }),
        Encode(Token with { Mailbox = string.Empty }),
        new string('A', 5000),
    ];

    [Theory]
    [MemberData(nameof(BadTokens))]
    public void Malformed_tokens_are_not_found(string token) =>
        TaskUploadToken.Decode(token, Now).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void A_missing_token_is_not_found() =>
        TaskUploadToken.Decode(null, Now).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void Expired_tokens_are_not_found() =>
        TaskUploadToken.Decode(Token.Encode(), Now.AddMinutes(15)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Theory]
    [InlineData("Quarterly numbers.eml", "Quarterly numbers.eml")]
    [InlineData("../../secret", "_.._secret")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData(".htaccess", "htaccess")]
    [InlineData("  ...  ", "attachment")]
    [InlineData("tab\there", "tab_here")]
    public void File_names_are_made_safe(string name, string expected) =>
        TaskAttachmentFiles.SafeName(name).ShouldBe(expected);

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var safe = TaskAttachmentFiles.SafeName(new string('x', 300) + ".eml");

        safe.Length.ShouldBe(200);
        safe.ShouldEndWith(".eml");
    }

    [Fact]
    public void Files_home_comes_from_the_principal()
    {
        var files = Files();

        files.HomeOf(new DavPrincipal("/remote.php/dav/principals/users/alice/", true)).Value.ShouldBe("files/alice/");
        files.HomeOf(new DavPrincipal("/remote.php/dav/principals/calendar-rooms/r/", false)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Theory]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice/a/b.eml", "files/alice/a/b.eml")]
    [InlineData("http://nextcloud/remote.php/dav/files/alice/Task%20attachments/b.eml", "files/alice/Task%20attachments/b.eml")]
    [InlineData("https://cloud.example.com/remote.php/dav/calendars/alice/tasks/1.ics", null)]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice/folder/", null)]
    [InlineData("https://cloud.example.com/f/123", null)]
    [InlineData("mailto:a@example.com", null)]
    [InlineData("https://cloud.example.com/remote.php/dav/files/alice/../../x", null)]
    public void Attach_urls_map_to_files_by_path(string url, string? href) =>
        Files().ToHref(new Uri(url)).ShouldBe(href);

    [Fact]
    public async Task Counting_stream_passes_a_body_within_the_limit()
    {
        await using var counted = new CountingReadStream(new MemoryStream(new byte[10]), 10);

        await counted.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        counted.BytesRead.ShouldBe(10);
        counted.Exceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Counting_stream_stops_at_the_limit_and_notices_more()
    {
        await using var counted = new CountingReadStream(new MemoryStream(new byte[11]), 10);
        using var sink = new MemoryStream();

        await counted.CopyToAsync(sink, TestContext.Current.CancellationToken);

        sink.Length.ShouldBe(10);
        counted.Exceeded.ShouldBeTrue();
    }

    [Fact]
    public void Counting_stream_without_a_limit_counts_everything()
    {
        using var counted = new CountingReadStream(new MemoryStream(new byte[7]), null);
        var buffer = new byte[100];

        counted.Read(buffer, 0, 100).ShouldBe(7);
        counted.Read(buffer, 0, 100).ShouldBe(0);
        counted.BytesRead.ShouldBe(7);
        counted.CanSeek.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => counted.Seek(0, SeekOrigin.Begin));
    }

    private static TaskAttachmentFiles Files() =>
        new(Substitute.For<IDavClient>(), new DavPaths(Options.Create(new DavOptions { BaseUrl = new Uri("http://nextcloud/") })));

    private static string Encode(TaskUploadToken token) => token.Encode();
}
