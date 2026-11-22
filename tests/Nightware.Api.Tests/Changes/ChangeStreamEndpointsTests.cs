using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Api.Changes;
using Nightware.Api.Tests.Support;
using Nightware.Application.Changes;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Changes;

public sealed class ChangeStreamEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedProbe(ChangeArea area, params Result<string>[] answers) : IChangeStateProbe
    {
        private int _next;

        public ChangeArea Area => area;

        public Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
            Task.FromResult(answers[Math.Min(Interlocked.Increment(ref _next) - 1, answers.Length - 1)]);
    }

    private static Task<TestHost> Host(params IChangeStateProbe[] probes) =>
        TestHost.StartAsync(
            services =>
            {
                services.RemoveAll<IChangeStateProbe>();
                foreach (var p in probes)
                {
                    services.AddSingleton(p);
                }
            },
            configuration: new Dictionary<string, string?>
            {
                ["ChangeStream:PollInterval"] = "00:00:01",
                ["ChangeStream:KeepAlive"] = "00:00:02",
                ["ChangeStream:MaxStreamsPerUser"] = "1",
            });

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        await using var host = await Host(new ScriptedProbe(ChangeArea.Mail, Result<string>.Success("a")));
        (await host.Client.GetAsync(ChangeStreamEndpoints.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_stream_says_ready_then_names_the_area_that_moved()
    {
        await using var host = await Host(new ScriptedProbe(ChangeArea.Mail, Result<string>.Success("a"), Result<string>.Success("b")));
        using var client = host.AuthorizedClient();
        using var response = await client.GetAsync(ChangeStreamEndpoints.Path, HttpCompletionOption.ResponseHeadersRead, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        var lines = new List<string>();
        while (!lines.Contains("event: changed"))
        {
            lines.Add((await reader.ReadLineAsync(Ct))!);
        }

        lines.ShouldContain("event: ready");
        lines.ShouldContain("""data: {"areas":["mail"]}""");
        (await reader.ReadLineAsync(Ct)).ShouldBe("""data: {"areas":["mail"]}""");
    }

    [Fact]
    public async Task Nothing_to_watch_is_404_and_a_refused_credential_401()
    {
        await using (var host = await Host(new ScriptedProbe(ChangeArea.Calendar, Error.NotFound("none"))))
        {
            (await host.AuthorizedClient().GetAsync(ChangeStreamEndpoints.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await using (var host = await Host(new ScriptedProbe(ChangeArea.Mail, Error.BackendAuthFailed("no"))))
        {
            (await host.AuthorizedClient().GetAsync(ChangeStreamEndpoints.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Past_the_per_user_cap_is_429()
    {
        await using var host = await Host(new ScriptedProbe(ChangeArea.Mail, Result<string>.Success("a")));
        using var held = host.App.Services.GetRequiredService<ChangeStreamLimiter>().TryAcquire("user-1");

        var response = await host.AuthorizedClient().GetAsync(ChangeStreamEndpoints.Path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_token_about_to_expire_ends_the_stream_and_frees_its_slot()
    {
        await using var host = await Host(new ScriptedProbe(ChangeArea.Mail, Result<string>.Success("a")));
        using var client = host.AuthorizedClient(TestHost.Token(expires: DateTime.UtcNow.AddSeconds(32)));

        var body = await client.GetStringAsync(ChangeStreamEndpoints.Path, Ct);

        body.ShouldContain("event: ready");
        body.ShouldContain("event: end\ndata: {\"reason\":\"expired\"}");
        host.App.Services.GetRequiredService<ChangeStreamLimiter>().OpenFor("user-1").ShouldBe(0);
    }

    [Fact]
    public async Task Discovery_advertises_the_derived_url_unless_the_stream_is_off()
    {
        await using (var on = await TestHost.StartAsync())
        {
            (await (await on.Client.GetAsync("/.well-known/nightware", Ct)).ReadObjectAsync()).Str("changesUrl")
                .ShouldBe("https://graph.test.example/nightware/v1/changes");
        }

        await using var off = await TestHost.StartAsync(configuration: new Dictionary<string, string?> { ["ChangeStream:Enabled"] = "false" });
        (await (await off.Client.GetAsync("/.well-known/nightware", Ct)).ReadObjectAsync()).ContainsKey("changesUrl").ShouldBeFalse();
        (await off.AuthorizedClient().GetAsync(ChangeStreamEndpoints.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("https://g.example.com/v1.0", "https://g.example.com/nightware/v1/changes")]
    [InlineData("https://h.example.com/graph/v1.0/", "https://h.example.com/graph/nightware/v1/changes")]
    [InlineData("https://h.example.com/graph", null)]
    public void The_url_is_derived_beside_v1(string graph, string? expected) =>
        ChangeStreamServiceCollectionExtensions.DerivedChangesUrl(graph).ShouldBe(expected);

    [Fact]
    public void The_limiter_caps_per_user_and_releases_once()
    {
        var limiter = new ChangeStreamLimiter(Microsoft.Extensions.Options.Options.Create(new ChangeStreamOptions { MaxStreamsPerUser = 1 }));
        var lease = limiter.TryAcquire("u").ShouldNotBeNull();
        limiter.TryAcquire("u").ShouldBeNull();
        limiter.TryAcquire("v").ShouldNotBeNull();
        lease.Dispose();
        lease.Dispose();
        limiter.OpenFor("u").ShouldBe(0);
        limiter.TryAcquire("u").ShouldNotBeNull();
    }
}
