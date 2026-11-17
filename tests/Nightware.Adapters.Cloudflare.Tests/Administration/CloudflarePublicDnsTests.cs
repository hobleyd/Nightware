using System.Net;

using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Cloudflare.Tests.Administration;

public sealed class CloudflarePublicDnsTests : IDisposable
{
    private const string Host = "mail.operator.example";

    private readonly CloudflareTestHost _host = new(s =>
    {
        s["Cloudflare:PublicDnsTimeout"] = "00:00:01";
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CloudflarePublicDnsTests() =>
        _host.Server.Given(Request.Create().WithPath("/dns-query").UsingGet()).AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"Status":0,"Answer":[]}"""));

    public void Dispose() => _host.Dispose();

    private IPublicDns Dns()
    {
        _host.Server.ResetLogEntries();
        return _host.Get<IPublicDns>();
    }

    private void Answer(string type, string json, int status = 200, TimeSpan? delay = null)
    {
        IResponseBuilder response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/dns-json").WithBody(json);
        if (delay is { } d)
        {
            response = response.WithDelay(d);
        }

        _host.Server.Given(Request.Create().WithPath("/dns-query").UsingGet().WithParam("name", Host).WithParam("type", type)).AtPriority(1).RespondWith(response);
    }

    [Fact]
    public async Task Returns_the_a_and_aaaa_answers_at_the_end_of_any_cname_chain()
    {
        Answer("A", """{"Status":0,"Answer":[{"name":"mail.operator.example","type":5,"data":"edge.operator.example."},{"name":"edge.operator.example","type":1,"data":"81.2.69.160"}]}""");
        Answer("AAAA", """{"Status":0,"Answer":[{"name":"mail.operator.example","type":28,"data":"2a01:4f8:c0c:1::1"}]}""");

        Result<IReadOnlyList<IPAddress>> result = await Dns().ResolveAsync(Host, Ct);

        result.Value.ShouldBe([IPAddress.Parse("81.2.69.160"), IPAddress.Parse("2a01:4f8:c0c:1::1")], ignoreOrder: true);
        _host.Server.LogEntries.ShouldAllBe(e => e.RequestMessage!.Headers!["Accept"].Contains("application/dns-json"));
    }

    [Fact]
    public async Task A_name_that_does_not_exist_is_not_found()
    {
        Answer("A", """{"Status":3}""");
        Answer("AAAA", """{"Status":3}""");

        (await Dns().ResolveAsync(Host, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task One_family_answering_is_enough()
    {
        Answer("A", """{"Status":0,"Answer":[{"name":"mail.operator.example","type":1,"data":"81.2.69.160"}]}""");
        Answer("AAAA", "oops", status: 502);

        (await Dns().ResolveAsync(Host, Ct)).Value.ShouldBe([IPAddress.Parse("81.2.69.160")]);
    }

    [Fact]
    public async Task A_resolver_that_is_slow_or_broken_is_unavailable_never_an_exception()
    {
        Answer("A", "{}", delay: TimeSpan.FromSeconds(3));
        Answer("AAAA", "not json");

        Result<IReadOnlyList<IPAddress>> result = await Dns().ResolveAsync(Host, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _host.Logs.Lines.ShouldContain(l => l.Contains("Public DNS lookup", StringComparison.Ordinal));
    }
}
