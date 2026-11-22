using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail;

/// <summary>Phase 19: <c>translateExchangeIds</c>, and <c>Prefer: IdType="ImmutableId"</c> (ids are already immutable).</summary>
public sealed class IdTranslationEndpointTests : IAsyncLifetime
{
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(services => services.AddSingleton(_messages));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task<HttpResponseMessage> PostAsync(string path, string json) =>
        _host.AuthorizedClient().PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    [Theory]
    [InlineData("/v1.0/me/translateExchangeIds")]
    [InlineData("/v1.0/users/shared@example.com/translateExchangeIds")]
    public async Task Rest_ids_come_back_as_themselves(string path)
    {
        var response = await PostAsync(path, """{"inputIds":["e.abc","e.def"],"sourceIdType":"restId","targetIdType":"restImmutableEntryId"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var value = (await response.ReadObjectAsync())["value"]!.AsArray();
        value.Select(v => (v!.Str("sourceId"), v!.Str("targetId"))).ShouldBe([("e.abc", "e.abc"), ("e.def", "e.def")]);
    }

    [Fact]
    public async Task An_exchange_store_format_is_400_notSupported()
    {
        var response = await PostAsync("/v1.0/me/translateExchangeIds", """{"inputIds":["e.abc"],"sourceIdType":"ewsId","targetIdType":"restId"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("notSupported");
    }

    [Fact]
    public async Task Prefer_IdType_ImmutableId_is_accepted_and_changes_nothing()
    {
        _messages.GetAsync(default, default!, default!, Ct).ReturnsForAnyArgs(new MailMessage(new MessageId("e.abc")) { Subject = "Hi" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/messages/e.abc?$select=id");
        request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");

        var response = await _host.AuthorizedClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync()).Str("id").ShouldBe("e.abc");
        response.Headers.Contains("Preference-Applied").ShouldBeFalse();
    }
}
