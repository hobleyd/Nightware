using System.Net;
using System.Text.Json.Nodes;

using Nightware.Conformance.Tests.Harness;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Operations deliberately left unimplemented (ADR 0001 §43): NightMail calls them only for Microsoft
/// accounts, never for NightMail Server ones (<c>/shares</c> left this list in Phase 7). Their fixtures
/// stay unarranged (the harness reports them as skipped); this pins down that the façade answers each
/// with a deliberate <c>501 notImplemented</c> rather than the unknown-route 400, and fails as soon as
/// one is arranged or its fixture disappears.
/// </summary>
public sealed class DeferredOperationsTests
{
    public static TheoryData<string> Deferred() =>
    [
        "security.threat-submission.email-threats.create",
    ];

    [Theory]
    [MemberData(nameof(Deferred))]
    public async Task Answers_501_and_stays_unarranged(string operationId)
    {
        ArrangementRegistry.Find(operationId).ShouldBeNull($"{operationId} is deferred by ADR 0001 §43; remove it from this list when it is implemented.");
        var fixture = ConformanceRunner.Load(Path.Combine(ConformanceRunner.FixturesFolder, operationId + ".json"));
        await using var host = new ConformanceHost(new PortFakes());
        using var client = host.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://graph.microsoft.com") });

        using var response = await client.SendAsync(ConformanceRunner.BuildRequest(fixture.Request), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["error"]!["code"]!.GetValue<string>().ShouldBe("notImplemented");
    }
}
