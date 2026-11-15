using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Ids;

/// <summary>
/// Wire format of Graph attachment ids: <c>a.</c> + unpadded base64url of the UTF-8 JSON
/// <c>[ordinal, size, name]</c>. A part is named by what survives a draft rebuild: its file name, its
/// decoded size, and its position among the message's leaf parts with that same name and size.
/// </summary>
/// <remarks>
/// <para>Neither JMAP <c>partId</c> nor <c>blobId</c> works. Every draft change creates a new Email
/// (ADR 0001 §1), whose parts are renumbered (an attachment that was part 4 becomes part 2) and whose
/// part blobIds derive from the new Email's blob, so both change although the content is carried over
/// unchanged. NightMail lists a draft's attachments once and then deletes several by the listed ids; with
/// either JMAP id the second delete would miss or remove the wrong file.</para>
/// <para>Ordinals count leaf parts of <c>bodyStructure</c> in order, so the id does not depend on whether
/// RFC 8621 files a part under <c>attachments</c> or the body (an inline image of a plain-text message,
/// an iTIP part in an alternative). Two copies of one file (same name and size) are told apart by
/// ordinal; deleting the first renumbers the second.</para>
/// <para>Canonical forms only: every id has exactly one spelling, and anything else is a malformed id (400).</para>
/// </remarks>
internal readonly record struct AttachmentItemId
{
    public const string Prefix = "a.";

    private const int MaxNameLength = 4096;

    private AttachmentItemId(string name, long size, int ordinal)
    {
        Name = name;
        Size = size;
        Ordinal = ordinal;
    }

    /// <summary>The part's file name; empty for a part without one.</summary>
    public string Name { get; }

    /// <summary>The part's decoded size in bytes.</summary>
    public long Size { get; }

    /// <summary>The part's position (from 0) among the leaf parts with this name and size.</summary>
    public int Ordinal { get; }

    public static AttachmentItemId Of(string? name, long size, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return new AttachmentItemId(name ?? string.Empty, size, ordinal);
    }

    /// <summary>The id of every leaf part of <paramref name="bodyStructure"/>, keyed by its JMAP <c>partId</c>.</summary>
    public static IReadOnlyDictionary<string, AttachmentItemId> Assign(EmailBodyPart? bodyStructure)
    {
        var ids = new Dictionary<string, AttachmentItemId>(StringComparer.Ordinal);
        var seen = new Dictionary<(string Name, long Size), int>();
        foreach (var part in Leaves(bodyStructure))
        {
            var key = (part.Name ?? string.Empty, part.Size ?? 0);
            var ordinal = seen.TryGetValue(key, out var count) ? count : 0;
            seen[key] = ordinal + 1;
            ids[part.PartId!] = new AttachmentItemId(key.Item1, key.Item2, ordinal);
        }

        return ids;
    }

    /// <summary>The leaf part of <paramref name="bodyStructure"/> this id names, or null.</summary>
    public EmailBodyPart? Find(EmailBodyPart? bodyStructure)
    {
        var self = this;
        var ids = Assign(bodyStructure);
        return Leaves(bodyStructure).FirstOrDefault(p => ids[p.PartId!] == self);
    }

    /// <summary>Parses a Graph attachment id. Anything not in canonical form is a malformed id (HTTP 400).</summary>
    public static Result<AttachmentItemId> Parse(AttachmentId id)
    {
        var text = id.Value;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Malformed();
        }

        var encoded = text[Prefix.Length..];
        if (encoded.Length == 0 || !Base64Url.IsValid(encoded) || encoded.Contains('=', StringComparison.Ordinal))
        {
            return Malformed();
        }

        AttachmentItemId parsed;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(encoded));
            if (document.RootElement is not { ValueKind: JsonValueKind.Array } array
                || array.GetArrayLength() != 3
                || array[0].ValueKind != JsonValueKind.Number || !array[0].TryGetInt32(out var ordinal) || ordinal < 0
                || array[1].ValueKind != JsonValueKind.Number || !array[1].TryGetInt64(out var size) || size < 0
                || array[2].ValueKind != JsonValueKind.String
                || array[2].GetString() is not { Length: <= MaxNameLength } name)
            {
                return Malformed();
            }

            parsed = new AttachmentItemId(name, size, ordinal);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return Malformed();
        }

        // Canonical check: the decoded value must re-encode to exactly the input.
        return string.Equals(parsed.ToString(), text, StringComparison.Ordinal) ? parsed : Malformed();
    }

    public AttachmentId ToAttachmentId() => new(ToString());

    public override string ToString()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Ordinal);
            writer.WriteNumberValue(Size);
            writer.WriteStringValue(Name);
            writer.WriteEndArray();
        }

        return Prefix + Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    /// <summary>Content-carrying leaves, in <c>bodyStructure</c> order.</summary>
    private static IEnumerable<EmailBodyPart> Leaves(EmailBodyPart? root)
    {
        if (root is null)
        {
            yield break;
        }

        var stack = new Stack<EmailBodyPart>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var part = stack.Pop();
            if (part.SubParts is { Count: > 0 } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
            else if (part.PartId is not null)
            {
                yield return part;
            }
        }
    }

    private static Error Malformed() =>
        new(ErrorKind.Validation, "ErrorInvalidIdMalformed", "The attachment id is malformed.", "attachmentId");
}
