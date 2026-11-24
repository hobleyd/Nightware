using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Auth;
using Nightware.Api.Discovery;
using Nightware.Application.Files.Drives;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 20: the caller's OneDrive (ADR 0008). The fixtures are constructed from Graph's reference pages with Nextcloud-shaped
/// ids; <c>cloud.example.com</c> is the file site, so <c>webUrl</c>s sit there.
/// </summary>
public sealed class DriveArrangements : IConformanceArrangements
{
    private const string DriveId = "nc!bWU";
    private const long SessionSize = 3670016;
    private static readonly DateTimeOffset Created = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    private static readonly DriveItem Notes = File("42", "notes.txt");
    private static readonly DriveItem Documents = new("7", "Documents")
    {
        DriveId = DriveId, Size = 5, IsFolder = true, ChildCount = 1, ETag = "e-7", ParentId = "1", ParentPath = "/", CreatedDateTime = Created, LastModifiedDateTime = Modified,
    };

    private static readonly DriveItem Root = new("1", "root")
    {
        DriveId = DriveId, Size = 5, IsFolder = true, IsRoot = true, ChildCount = 1, ETag = "e-1", CreatedDateTime = Created, LastModifiedDateTime = Modified,
    };

    private static readonly Drive Drive = new(DriveId, "OneDrive")
    {
        OwnerId = "me", OwnerDisplayName = "Me Example", LastModifiedDateTime = Modified, Quota = new DriveQuota(5, 1073741819, 1073741824),
    };

    private static readonly DateTimeOffset SharedAt = new(2026, 11, 24, 5, 35, 47, TimeSpan.Zero);

    private static readonly DriveItem SharedPlan = new("557", "plan.txt")
    {
        DriveId = DriveId, Size = 15, MimeType = "text/plain", ParentPath = "/", IsShared = true, CreatedDateTime = SharedAt,
        LastModifiedDateTime = new DateTimeOffset(2026, 11, 24, 5, 35, 35, TimeSpan.Zero), SharedBy = new DriveItemSharer("bob", "Bob Brown", SharedAt),
    };

    private static readonly DriveItem Recent = Notes with
    {
        Name = "notes.txt", Size = 15, CreatedDateTime = SharedAt, LastModifiedDateTime = new DateTimeOffset(2026, 11, 24, 5, 35, 35, TimeSpan.Zero), ETag = null, CTag = null,
    };

    private static readonly DriveShare Link = new("41", ["read"]) { Link = new SharingLink("view", "anonymous", "https://cloud.example.com/s/AbCdEfGh") };
    private static readonly DriveShare Invite = new("43", ["write"]) { GrantedTo = new EmailAddress("bob@example.com", "Bob Brown") };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Arrange("files.drive.get", static f => f.For<IDriveStore>().GetDriveAsync(null, Any).Returns(Drive));
        yield return Arrange("files.drives.list", static f => f.For<IDriveStore>().GetDriveAsync(null, Any).Returns(Drive));
        yield return Arrange("files.drives.get", static f => f.For<IDriveStore>().GetDriveAsync(DriveId, Any).Returns(Drive));
        yield return Arrange("files.drive.special.get", static f => f.For<IDriveStore>().GetSpecialFolderAsync(SpecialFolder.Documents, Arg.Any<string?>(), Any).Returns(Documents));
        yield return Arrange("files.drive.root.get", static f => f.For<IDriveStore>().GetItemAsync(Is(DriveItemRef.Root), Any).Returns(Root));
        yield return Arrange("files.drive.items.get", static f => f.For<IDriveStore>().GetItemAsync(Is(DriveItemRef.ById("42")), Any).Returns(Notes)) with
        {
            Normalise = SealedDownloadUrl,
        };
        yield return Arrange("files.drive.items.get-by-path", static f =>
            f.For<IDriveStore>().GetItemAsync(Is(new DriveItemRef(null, ["Documents", "notes.txt"])), Any).Returns(Notes)) with
        {
            Normalise = SealedDownloadUrl,
        };
        yield return Arrange("files.drive.root.children", static f =>
            f.For<IDriveStore>().ListChildrenAsync(Is(DriveItemRef.Root), new DriveListQuery(200, null), Any).Returns(new Page<DriveItem>([Notes], null)));
        yield return Arrange("files.drives.items.get", static f =>
            f.For<IDriveStore>().GetItemAsync(Arg.Is<DriveItemRef>(r => r.ItemId == "42" && r.DriveId == DriveId), Any).Returns(Notes)) with
        {
            Normalise = SealedDownloadUrl,
        };
        yield return Arrange("files.drive.shared-with-me", static f =>
            f.For<IDriveSharing>().SharedWithMeAsync(Any).Returns(Result<IReadOnlyList<DriveItem>>.Success([SharedPlan])));
        yield return Arrange("files.drive.recent", static f =>
            f.For<IDriveStore>().RecentAsync(100, Any).Returns(Result<IReadOnlyList<DriveItem>>.Success([Recent])));
        yield return Arrange("search.query", static f =>
            f.For<IDriveStore>().SearchAsync("notes", new DriveListQuery(50, null), Any).Returns(new Page<DriveItem>([Notes], null)));
        yield return Arrange("files.drive.items.children", static f =>
            f.For<IDriveStore>().ListChildrenAsync(Is(DriveItemRef.ById("7")), new DriveListQuery(1, null), Any).Returns(new Page<DriveItem>([Notes], "1")));
        yield return Arrange("files.drive.items.create-folder", static f =>
            f.For<IDriveStore>().CreateFolderAsync(Is(DriveItemRef.ById("7")), "New Folder", ConflictBehavior.Rename, Any)
                .Returns(Notes with { Id = "44", Name = "New Folder", IsFolder = true, ChildCount = 0, Size = 0, ETag = "e-44", CTag = null, MimeType = null }));
        yield return Arrange("files.drive.items.content", static f => f.For<IDriveStore>().GetItemAsync(Is(DriveItemRef.ById("42")), Any).Returns(Notes));
        yield return Arrange("files.drive.items.download", static f =>
            f.For<IDriveStore>().OpenContentAsync(Is(DriveItemRef.ById("42")), Any).Returns(_ => Blob("text/plain", "hello"u8.ToArray())), urlCredential: true);
        yield return Arrange("files.drive.items.upload", static f =>
            f.For<IDriveStore>().WriteContentAsync(Is(new DriveItemRef("7", ["notes.txt"])), Arg.Is<DriveUpload>(u => u.Conflict == ConflictBehavior.Replace), Any).Returns(Notes));
        yield return Arrange("files.drive.items.replace-content", static f =>
            f.For<IDriveStore>().WriteContentAsync(Is(DriveItemRef.ById("42")), Arg.Any<DriveUpload>(), Any).Returns(Notes));
        yield return Arrange("files.drive.items.update", static f =>
            f.For<IDriveStore>().UpdateAsync(Is(DriveItemRef.ById("42")), Arg.Is<DriveItemChange>(c => c.Name == "renamed.txt" && c.NewParent!.ItemId == "8"), Any)
                .Returns(Notes with { Name = "renamed.txt", ParentId = "8", ParentPath = "/Archive" }));
        yield return Arrange("files.drive.items.delete", static f => f.For<IDriveStore>().DeleteAsync(Is(DriveItemRef.ById("42")), null, Any).Returns(Result.Success()));
        yield return Arrange("files.drive.items.copy", static f =>
            f.For<IDriveStore>().CopyAsync(Is(DriveItemRef.ById("42")), Arg.Is<DriveItemRef?>(p => p!.ItemId == "8"), "notes (copy).txt", ConflictBehavior.Fail, Any)
                .Returns(Notes with { Id = "45", Name = "notes (copy).txt" }));
        yield return Arrange("files.drive.copy-monitor", static _ => { }, urlCredential: true);
        yield return new ConformanceArrangement("files.drive.items.create-upload-session", static f =>
            f.For<IDriveUploads>().BeginAsync(Arg.Is<DriveUploadFile>(u => u.Name == "big.bin" && u.Size == SessionSize && u.Conflict == ConflictBehavior.Rename && u.Parent.ItemId == "7"), Any)
                .Returns(new DriveUploadState("sess-0001", DateTimeOffset.UtcNow.AddHours(24), 0)))
        {
            Services = FileSite,
            Normalise = static (expected, actual) =>
            {
                actual["uploadUrl"]!.GetValue<string>().ShouldStartWith($"{ConformanceHost.GraphBase}/me/drive/uploadSessions/sess-0001?authtoken=");
                foreach (var body in new[] { expected, actual })
                {
                    body["uploadUrl"] = "<uploadUrl>";
                    body["expirationDateTime"] = "<expiry>";
                }
            },
        };
        yield return Arrange("files.drive.upload-session-put", static f =>
            f.For<IDriveUploads>().AppendAsync("sess-0001", 0, 1966080, SessionSize, Arg.Any<Stream>(), Any).Returns(new DriveUploadProgress(1966080, null)),
            urlCredential: true, expiry: true);
        yield return Arrange("files.drive.upload-session-put-final", static f =>
            f.For<IDriveUploads>().AppendAsync("sess-0001", 1966080, 1703936, SessionSize, Arg.Any<Stream>(), Any)
                .Returns(new DriveUploadProgress(SessionSize, Notes with { Id = "46", Name = "big.bin", Size = SessionSize, MimeType = "application/octet-stream", ETag = "e-46", CTag = "e-46" })),
            urlCredential: true);
        yield return Arrange("files.drive.upload-session-get", static f =>
            f.For<IDriveUploads>().GetAsync("sess-0001", Any).Returns(new DriveUploadState("sess-0001", DateTimeOffset.MaxValue, 1966080)), urlCredential: true, expiry: true);
        yield return Arrange("files.drive.upload-session-delete", static f => f.For<IDriveUploads>().CancelAsync("sess-0001", Any).Returns(Result.Success()), urlCredential: true);
        yield return Arrange("files.drive.root.delta", static f =>
            f.For<IDriveDelta>().ChangesAsync(null, DriveLimits.DefaultPageSize, false, Any).Returns(DeltaPage<DriveItem>.Done(
                [new DeltaEntry<DriveItem>.Upsert(Root), new DeltaEntry<DriveItem>.Upsert(Notes), new DeltaEntry<DriveItem>.Upsert(DriveItem.Deleted("9"))], new DeltaToken("t"))));
        yield return Arrange("files.drive.root.search", static f =>
            f.For<IDriveStore>().SearchAsync("notes", Arg.Any<DriveListQuery>(), Any).Returns(new Page<DriveItem>([Notes])));
        yield return new ConformanceArrangement("files.drive.items.thumbnails", static f =>
            f.For<IDriveStore>().GetItemAsync(Is(DriveItemRef.ById("42")), Any).Returns(Notes with { HasThumbnail = true }))
        {
            Services = FileSite,
            Normalise = static (expected, actual) =>
            {
                foreach (var body in new[] { expected, actual })
                {
                    foreach (var size in new[] { "small", "medium", "large" })
                    {
                        var thumbnail = body["value"]![0]![size]!;
                        thumbnail["url"]!.GetValue<string>().ShouldContain($"/me/drive/items/42/thumbnails/0/{size}/$value?authtoken=");
                        thumbnail["url"] = "<url>";
                    }
                }
            },
        };
        yield return Arrange("files.drive.items.thumbnail-content", static f =>
            f.For<IDriveStore>().OpenThumbnailAsync(Is(DriveItemRef.ById("42")), ThumbnailSize.Small, Any).Returns(_ => Blob("image/png", "PNG"u8.ToArray())));
        yield return Arrange("files.drive.items.create-link", static f =>
            f.For<IDriveSharing>().CreateLinkAsync(Is(DriveItemRef.ById("42")), new SharingLinkRequest("view", null, null), Any).Returns(Link));
        yield return Arrange("files.drive.items.invite", static f =>
            f.For<IDriveSharing>().InviteAsync(Is(DriveItemRef.ById("42")), Arg.Is<DriveInvitation>(i => i.Recipients.Single() == "bob@example.com" && i.Roles.Single() == "write"), Any)
                .Returns(Result<IReadOnlyList<DriveShare>>.Success([Invite])));
        yield return Arrange("files.drive.items.permissions", static f =>
            f.For<IDriveSharing>().ListPermissionsAsync(Is(DriveItemRef.ById("42")), Any).Returns(Result<IReadOnlyList<DriveShare>>.Success([Link, Invite])));
        yield return Arrange("files.drive.items.permissions.delete", static f =>
            f.For<IDriveSharing>().DeletePermissionAsync(Is(DriveItemRef.ById("42")), "41", Any).Returns(Result.Success()));
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();

    private static DriveItem File(string id, string name) => new(id, name)
    {
        DriveId = DriveId, Size = 5, MimeType = "text/plain", ETag = "e-" + id, CTag = "e-" + id, ParentId = "7", ParentPath = "/Documents",
        CreatedDateTime = Created, LastModifiedDateTime = Modified,
    };

    private static DriveItemRef Is(DriveItemRef expected) =>
        Arg.Is<DriveItemRef>(r => r.ItemId == expected.ItemId && r.Path.SequenceEqual(expected.Path));

    private static BlobContent Blob(string type, byte[] bytes) => new(type, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));

    private static ConformanceArrangement Arrange(string operation, Action<PortFakes> arrange, bool urlCredential = false, bool expiry = false) =>
        new(operation, arrange)
        {
            Services = services =>
            {
                FileSite(services);
                if (urlCredential)
                {
                    services.AddSingleton<IRequestCredentialSource>(new FixedCredential());
                }
            },
            Normalise = expiry
                ? static (expected, actual) =>
                {
                    expected["expirationDateTime"] = "<expiry>";
                    actual["expirationDateTime"] = "<expiry>";
                }
                : null,
        };

    private static void FileSite(IServiceCollection services) =>
        services.PostConfigure<DiscoveryOptions>(static o => o.FilesBaseUrls = ["https://cloud.example.com"]);

    /// <summary>The fixtures' self-authorising URLs carry no sealed token, so the credential is supplied as the URL would.</summary>
    private sealed class FixedCredential : IRequestCredentialSource
    {
        public bool TryResolve(HttpRequest request, [NotNullWhen(true)] out SealedTicket? ticket)
        {
            var path = request.Path.Value!;
            ticket = path.Contains("/uploadSessions/", StringComparison.Ordinal) || path.Contains("/copyMonitors/", StringComparison.Ordinal) || path.EndsWith("/$value", StringComparison.Ordinal)
                ? new SealedTicket("conformance-drive-url-token", DateTimeOffset.MaxValue)
                : null;
            return ticket is not null;
        }
    }

    /// <summary>
    /// The download URL seals a fresh credential every time: the fixture holds <c>&lt;volatile&gt;</c>, and the actual
    /// value must be the item's self-authorising <c>$value</c> URL.
    /// </summary>
    private static void SealedDownloadUrl(System.Text.Json.Nodes.JsonNode expected, System.Text.Json.Nodes.JsonNode actual)
    {
        const string Key = "@microsoft.graph.downloadUrl";
        var url = actual[Key]!.GetValue<string>();
        url.ShouldContain("/me/drive/items/42/$value?authtoken=");
        actual[Key] = ResponseNormaliser.VolatilePlaceholder;
    }
}
