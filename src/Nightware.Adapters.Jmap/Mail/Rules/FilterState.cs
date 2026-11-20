using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightware.Domain.Mail;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.Mail.Rules;

/// <summary>
/// What Nightware's Sieve script carries (ADR 0006 D5): the Inbox rules and the automatic reply. The script is
/// generated from it, and it rides along in the script as a base64 comment, so the script is the only record.
/// </summary>
internal sealed record FilterState(IReadOnlyList<MessageRule> Rules, AutoReplySetting? AutoReply)
{
    public static FilterState Empty { get; } = new([], null);

    private const string Marker = "# nightware-state: ";

    public string ToComment()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new FilterStateDocument(1, Rules, AutoReply), FilterStateJsonContext.Default.FilterStateDocument);
        return Marker + Convert.ToBase64String(json);
    }

    /// <summary>The state in <paramref name="script"/>, or null when it is not one of Nightware's.</summary>
    public static FilterState? FromScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var line = script.Split('\n').Select(static l => l.TrimEnd('\r')).FirstOrDefault(static l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize(Convert.FromBase64String(line[Marker.Length..]), FilterStateJsonContext.Default.FilterStateDocument);
            return document is { V: 1 } ? new FilterState(document.Rules ?? [], document.AutoReply) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

internal sealed record FilterStateDocument(int V, IReadOnlyList<MessageRule>? Rules, AutoReplySetting? AutoReply);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(FilterStateDocument))]
internal sealed partial class FilterStateJsonContext : JsonSerializerContext;
