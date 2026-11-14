using System.Text.Json.Nodes;

namespace Nightware.Api.Tests.Support;

internal static class Json
{
    public static async Task<JsonObject> ReadObjectAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Empty body.");
    }

    public static string? Str(this JsonNode? node, string path)
    {
        var current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }

        return current?.GetValue<string>();
    }
}
