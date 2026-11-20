using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Graph.Json;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>Loads fixtures, replays one against the façade and compares the answer.</summary>
public static class ConformanceRunner
{
    public const string FixturesFolder = "Fixtures";
    public const string ExamplesFolder = "Examples";

    /// <summary>What captures put in place of a continuation token (tools/capture).</summary>
    public const string TokenPlaceholder = "{token}";

    private static readonly HashSet<string> RequestHeadersNotReplayed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Host",
    };

    /// <summary>Fixture files (relative to the output folder), golden fixtures first, then examples. Missing folders yield nothing.</summary>
    public static IReadOnlyList<string> FixtureFiles() =>
        [.. Files(FixturesFolder), .. Files(ExamplesFolder)];

    public static GraphFixture Load(string relativePath) =>
        GraphFixture.Load(Path.Combine(AppContext.BaseDirectory, relativePath));

    /// <summary>Replays <paramref name="fixture"/> against a fresh host arranged by <paramref name="arrangement"/> and asserts the response.</summary>
    public static async Task AssertConformsAsync(GraphFixture fixture, ConformanceArrangement arrangement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(arrangement);
        var fakes = new PortFakes();
        arrangement.Arrange(fakes);
        await using var host = new ConformanceHost(fakes, arrangement);
        using var client = host.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://graph.microsoft.com") });

        var token = arrangement.RequestToken is { } adapterToken
            ? host.Services.GetRequiredService<DeltaLinkCodec>().Encode(new DeltaLinkState(adapterToken, arrangement.RequestTokenSelect, null))
            : null;
        using var request = BuildRequest(fixture.Request, token);
        using var response = await client.SendAsync(request, cancellationToken);
        var actualBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var actualText = Encoding.UTF8.GetString(actualBytes);

        ((int)response.StatusCode).ShouldBe(fixture.Response.Status, $"{fixture.OperationId}: status. Body: {actualText}");
        AssertHeaders(fixture, response);

        switch (fixture.Response)
        {
            case { BodyBase64: { } base64 }:
                actualBytes.ShouldBe(Convert.FromBase64String(base64), $"{fixture.OperationId}: binary body");
                break;
            case { Body: null }:
                actualText.ShouldBeEmpty($"{fixture.OperationId}: expected an empty body");
                break;
            case { Body: JsonValue value } when value.TryGetValue<string>(out var text):
                actualText.ShouldBe(text, $"{fixture.OperationId}: text body");
                break;
            default:
                AssertJson(fixture, arrangement, actualText);
                break;
        }
    }

    /// <summary>Compares JSON bodies after normalisation; exposed for the harness's own tests.</summary>
    public static void AssertJson(GraphFixture fixture, ConformanceArrangement arrangement, string actualText)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(arrangement);
        var expected = fixture.Response.Body!.DeepClone();
        var actual = JsonNode.Parse(actualText) ?? throw new ShouldAssertException($"{fixture.OperationId}: response body is not JSON: {actualText}");

        ResponseNormaliser.Normalise(expected, arrangement.IgnorePaths);
        var links = ResponseNormaliser.Normalise(actual, arrangement.IgnorePaths);
        foreach (var link in links)
        {
            (link.StartsWith(ConformanceHost.GraphBase + "/", StringComparison.Ordinal) || link.StartsWith(ConformanceHost.GraphBetaBase + "/", StringComparison.Ordinal))
                .ShouldBeTrue($"{fixture.OperationId}: link '{link}' is not on the configured Graph base");
        }

        arrangement.Normalise?.Invoke(expected, actual);
        JsonNode.DeepEquals(expected, actual).ShouldBeTrue(
            $"{fixture.OperationId}: body differs after normalisation.{Environment.NewLine}Expected: {expected.ToJsonString()}{Environment.NewLine}Actual:   {actual.ToJsonString()}");
    }

    /// <summary>The fixture's request; a <c>{token}</c> placeholder in its query becomes <paramref name="token"/>.</summary>
    internal static HttpRequestMessage BuildRequest(FixtureRequest fixture, string? token = null)
    {
        var url = token is null ? fixture.Url : fixture.Url.Replace(Uri.EscapeDataString(TokenPlaceholder), Uri.EscapeDataString(token), StringComparison.Ordinal)
            .Replace(TokenPlaceholder, Uri.EscapeDataString(token), StringComparison.Ordinal);
        var request = new HttpRequestMessage(new HttpMethod(fixture.Method), url);
        foreach (var (name, value) in fixture.Headers.Where(static h => !RequestHeadersNotReplayed.Contains(h.Key)))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (fixture.Body is not null)
        {
            var mediaType = fixture.Headers.TryGetValue("Content-Type", out var type) ? MediaTypeHeaderValue.Parse(type).MediaType! : "application/json";
            var text = fixture.Body is JsonValue raw && raw.TryGetValue<string>(out var s) ? s : fixture.Body.ToJsonString();
            request.Content = new StringContent(text, Encoding.UTF8, mediaType);
        }

        return request;
    }

    private static void AssertHeaders(GraphFixture fixture, HttpResponseMessage response)
    {
        if (fixture.Response.Headers.TryGetValue("Location", out _))
        {
            response.Headers.Location.ShouldNotBeNull($"{fixture.OperationId}: Location header");
        }

        if (fixture.Response.Headers.TryGetValue("Content-Type", out var contentType) && (fixture.Response.Body is not null || fixture.Response.BodyBase64 is not null))
        {
            var expected = MediaTypeHeaderValue.Parse(contentType).MediaType;
            response.Content.Headers.ContentType?.MediaType.ShouldBe(expected, $"{fixture.OperationId}: Content-Type");
        }
    }

    private static IEnumerable<string> Files(string folder)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, folder);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(AppContext.BaseDirectory, f))
                .Order(StringComparer.Ordinal)
            : [];
    }
}
