using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 5A directory operations: <c>/me</c>, <c>/users/{x}</c>, <c>/users</c>, user photos.</summary>
public sealed class DirectoryArrangements : IConformanceArrangements
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        // The harness authenticates as sub 'conformance-user'; /me looks the caller up by subject.
        yield return new ConformanceArrangement("directory.me.get", static fakes =>
            fakes.For<IUserDirectory>()
                .GetAsync("conformance-user", Arg.Any<CancellationToken>())
                .Returns(new DirectoryUser("conformance-user", "Me Example", "me@corp.example.com")
                {
                    Mail = "me@example.com",
                    Aliases = ["me.alias@example.com"],
                    ProxyAddresses = ["SIP:me@example.com"],
                }));

        yield return new ConformanceArrangement("directory.users.get", static fakes =>
            fakes.For<IUserDirectory>()
                .GetAsync("sam@example.com", Arg.Any<CancellationToken>())
                .Returns(new DirectoryUser("id-sam", "Sam Attendee", "sam@example.com")
                {
                    Mail = "sam@example.com",
                    JobTitle = "Analyst",
                    Department = "Finance",
                    CompanyName = "Example Pty Ltd",
                    MobilePhone = "+61 400 000 000",
                    BusinessPhones = ["+61 2 9000 0000"],
                    OfficeLocation = "Sydney",
                }));

        yield return new ConformanceArrangement("directory.users.list", static fakes =>
            fakes.For<IUserDirectory>()
                .ListAsync(Arg.Is<ItemQuery>(q => q.Top == 999 && q.SkipToken == null), Arg.Any<CancellationToken>())
                .Returns(new Page<DirectoryUser>(
                    [
                        new DirectoryUser("id-sam", "Sam Attendee", "sam@example.com") { Mail = "sam@example.com" },
                        new DirectoryUser("id-boardroom", "Boardroom", "boardroom@example.com") { Mail = "boardroom@example.com" },
                    ],
                    SkipToken: "2")));

        yield return new ConformanceArrangement("directory.users.photo-value", static fakes =>
            fakes.For<IUserPhotoReader>()
                .GetAsync("sam@example.com", Arg.Any<CancellationToken>())
                .Returns(new BlobContent("image/png", Png.Length, static _ => Task.FromResult<Stream>(new MemoryStream(Png)))));
    }
}
