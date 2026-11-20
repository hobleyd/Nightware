using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail.Rules;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Rules;

public sealed class MessageRuleEndpointTests : IAsyncLifetime
{
    private static readonly MessageRule Stored = new("r1", "Filing", 1, true)
    {
        Conditions = new MessageRulePredicates { SubjectContains = ["invoice"], Importance = Importance.High },
        Actions = new MessageRuleActions { MoveToFolder = new FolderId("f1"), StopProcessingRules = true },
    };

    private readonly IMessageRuleStore _store = Substitute.For<IMessageRuleStore>();
    private IReadOnlyList<MessageRule>? _saved;
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Forms() => new()
    {
        { "/v1.0/me", TestHost.UserEmail },
        { "/v1.0/users/shared@example.com", "shared@example.com" },
    };

    public async ValueTask InitializeAsync()
    {
        _store.ListAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<MessageRule>>.Success([Stored]));
        _store.SaveAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success()).AndDoes(c => _saved = c.Arg<IReadOnlyList<MessageRule>>());
        _host = await TestHost.StartAsync(services => services.AddSingleton(_store));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return _host.AuthorizedClient().SendAsync(request, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_rules_writing_only_what_is_set(string prefix, string address)
    {
        var response = await SendAsync(HttpMethod.Get, $"{prefix}/mailFolders/inbox/messageRules");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync())["value"]!.AsArray().Single()!.ToJsonString().ShouldBe(
            """{"id":"r1","displayName":"Filing","sequence":1,"isEnabled":true,"hasError":false,"isReadOnly":false,"conditions":{"subjectContains":["invoice"],"importance":"high"},"actions":{"moveToFolder":"f1","stopProcessingRules":true},"exceptions":null}""");
        await _store.Received().ListAsync(new MailboxRef(address), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_maps_the_body_and_answers_201_with_a_location()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", """
            {"displayName":"New","sequence":1,"isEnabled":true,
             "conditions":{"fromAddresses":[{"emailAddress":{"address":"boss@example.com"}}],"hasAttachments":true},
             "exceptions":{"bodyContains":["unsubscribe"]},
             "actions":{"copyToFolder":"f2","markAsRead":true,"forwardTo":[{"emailAddress":{"address":"me@home.example"}}]}}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = _saved![0];
        created.Conditions.FromAddresses.ShouldBe([new EmailAddress("boss@example.com")]);
        created.Conditions.HasAttachments.ShouldBe(true);
        created.Exceptions.BodyContains.ShouldBe(["unsubscribe"]);
        created.Actions.CopyToFolder.ShouldBe(new FolderId("f2"));
        created.Actions.ForwardTo.ShouldBe([new EmailAddress("me@home.example")]);
        response.Headers.Location!.ToString().ShouldBe($"{TestHost.GraphBase}/users('me%40example.com')/mailFolders('inbox')/messageRules('{created.Id}')");
    }

    [Theory]
    [InlineData("""{"displayName":"x","actions":{"assignCategories":["Red"]}}""", "actions.assignCategories")]
    [InlineData("""{"displayName":"x","conditions":{"headerContains":["X-Spam"]},"actions":{"delete":true}}""", "conditions.headerContains")]
    [InlineData("""{"displayName":"x","exceptions":{"isAutomaticReply":true},"actions":{"delete":true}}""", "exceptions.isAutomaticReply")]
    public async Task What_the_filter_cannot_do_is_501_naming_the_field(string body, string field)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", body);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await response.ReadObjectAsync()).Str("error.message")!.ShouldContain(field);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Patch_replaces_only_the_members_sent()
    {
        var response = await SendAsync(HttpMethod.Patch, "/v1.0/me/mailFolders/inbox/messageRules/r1", """{"displayName":"Renamed","isEnabled":false}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = _saved!.Single();
        (updated.DisplayName, updated.IsEnabled).ShouldBe(("Renamed", false));
        updated.Conditions.ShouldBe(Stored.Conditions);
        updated.Actions.ShouldBe(Stored.Actions);
    }

    [Fact]
    public async Task Get_delete_and_errors()
    {
        (await SendAsync(HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messageRules/r1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messageRules/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Delete, "/v1.0/me/mailFolders/inbox/messageRules/r1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", """{"displayName":"no actions"}""")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", """{"displayName":"x","conditions":{"importance":"urgent"},"actions":{"delete":true}}"""))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
