using Nightware.Api.Composition;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.Routing;

public sealed class MailboxResolverTests
{
    private readonly IUserContext _user = Substitute.For<IUserContext>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();

    public MailboxResolverTests() =>
        _user.Principal.Returns(new Principal("sub-1", "me", "Me@Example.com", "Me"));

    private MailboxResolver Resolver(IUserDirectory? directory = null) => new(_user, directory ?? _directory);

    [Theory]
    [InlineData(null)]
    [InlineData("me")]
    [InlineData("ME")]
    public async Task Me_is_the_callers_mail_address(string? segment)
    {
        var result = await Resolver().ResolveAsync(segment, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new MailboxRef("me@example.com"));
    }

    [Fact]
    public async Task Me_without_a_mail_claim_is_forbidden()
    {
        _user.Principal.Returns(new Principal("sub-1", "me", string.Empty, "Me"));

        var result = await Resolver().ResolveAsync(null, TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Theory]
    [InlineData("Shared@Example.com")]
    [InlineData("shared%40example.com")]
    [InlineData("Shared%40Example.com")]
    public async Task Addresses_are_used_directly_lowercased_and_decoded(string segment)
    {
        var result = await Resolver().ResolveAsync(segment, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new MailboxRef("shared@example.com"));
        await _directory.DidNotReceiveWithAnyArgs().GetAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_escapes_are_kept_verbatim()
    {
        var result = await Resolver().ResolveAsync("bad%zz@example.com", TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new MailboxRef("bad%zz@example.com"));
    }

    [Fact]
    public async Task Directory_ids_resolve_through_the_directory()
    {
        _directory.GetAsync("1234", Arg.Any<CancellationToken>())
            .Returns(new DirectoryUser("1234", "Shared", "shared-upn@example.com") { Mail = "Shared@Example.com" });

        var result = await Resolver().ResolveAsync("1234", TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new MailboxRef("shared@example.com"));
    }

    [Fact]
    public async Task Directory_users_without_mail_fall_back_to_the_upn()
    {
        _directory.GetAsync("1234", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("1234", "Shared", "Upn@Example.com"));

        (await Resolver().ResolveAsync("1234", TestContext.Current.CancellationToken)).Value.ShouldBe(new MailboxRef("upn@example.com"));
    }

    [Fact]
    public async Task Directory_users_with_no_address_at_all_are_not_found()
    {
        _directory.GetAsync("1234", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("1234", "Shared", " "));

        (await Resolver().ResolveAsync("1234", TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_unimplemented_directory_makes_ids_not_found()
    {
        var stub = NotImplementedPort<IUserDirectory>.Create();

        var result = await Resolver(stub).ResolveAsync("1234", TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Unknown_ids_are_not_found()
    {
        _directory.GetAsync("nobody", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await Resolver().ResolveAsync("nobody", TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Other_directory_failures_propagate()
    {
        _directory.GetAsync("1234", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down", 3));

        var result = await Resolver().ResolveAsync("1234", TestContext.Current.CancellationToken);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Blank_segments_are_not_found()
    {
        (await Resolver().ResolveAsync("%20", TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void Mailbox_parameter_exposes_its_resolution()
    {
        var ok = Mailbox.From("a@b.c", new MailboxRef("a@b.c"));
        ok.Ref.Address.ShouldBe("a@b.c");
        ok.IsMe.ShouldBeFalse();
        ok.ContextSegment.ShouldBe("users('a%40b.c')");
        ok.BindingError.ShouldBeNull();

        var failed = Mailbox.From(null, Error.NotFound("x"));
        failed.IsMe.ShouldBeTrue();
        failed.BindingError!.Kind.ShouldBe(ErrorKind.NotFound);
        Should.Throw<InvalidOperationException>(() => failed.Ref);
    }
}
