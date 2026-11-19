using System.Text;

using Nightware.Domain.Administration;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// The Sieve system scripts that enforce every list's <see cref="SenderRule"/> (ADR 0003 D5). Stalwart runs one of them
/// at RCPT (<c>MtaStageRcpt.script</c>, set by <c>deploy/stalwart/configure.sh</c>): <see cref="Name"/> for signed-in
/// senders and the trusted Docker network, <see cref="UnauthenticatedName"/> for everyone else, which refuses every
/// restricted list outright, since an unauthenticated envelope sender proves nothing. A refused sender gets
/// <c>550 5.7.1</c> before any mail is accepted. One marked block per list, so the console rewrites one list's rule
/// without touching the others; anything outside the blocks is kept as it is.
/// </summary>
internal static class ListSenderScript
{
    public const string Name = "nightware-lists";

    public const string UnauthenticatedName = "nightware-lists-unauthenticated";

    private const string Require = "require [\"envelope\", \"reject\"];";
    private const string Begin = "# nightware-list ";
    private const string End = "# end nightware-list ";

    /// <summary>
    /// <paramref name="contents"/> with the block for <paramref name="list"/> replaced by <paramref name="rule"/>'s, or
    /// removed when it is null. For <paramref name="unauthenticated"/> senders the block refuses every sender.
    /// </summary>
    public static string With(string? contents, MailAddress list, SenderRule? rule, bool unauthenticated = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        (List<string> preamble, SortedDictionary<string, string> blocks) = Parse(contents);
        if (rule is null)
        {
            blocks.Remove(list.Value);
        }
        else
        {
            blocks[list.Value] = unauthenticated ? Refusal(list, rule) : Block(list, rule);
        }

        var script = new StringBuilder();
        foreach (string line in preamble.Count > 0 ? preamble : [Require])
        {
            script.Append(line).Append('\n');
        }

        foreach (string block in blocks.Values)
        {
            script.Append(block);
        }

        return script.ToString();
    }

    /// <summary>The rule as Sieve: mail to any of the list's addresses is refused unless the envelope sender is allowed.</summary>
    internal static string Block(MailAddress list, SenderRule rule)
    {
        var tests = new List<string> { $"envelope :is \"to\" {Strings(rule.ListAddresses.Select(static a => a.Value))}" };
        string[] senders = [.. rule.AllowedSenders.Where(IsPlain)];
        if (senders.Length > 0)
        {
            tests.Add($"not envelope :is \"from\" {Strings(senders)}");
        }

        if (rule.AllowedDomains.Count > 0)
        {
            tests.Add($"not envelope :domain :is \"from\" {Strings(rule.AllowedDomains.Select(static d => d.Name))}");
        }

        string who = rule.AllowedDomains.Count > 0 ? "the organisation" : "its members";
        return $"{Begin}{list.Value}\n"
            + $"if allof ({string.Join(",\n          ", tests)}) {{\n"
            + $"    reject \"550 5.7.1 {list.Value} accepts mail from {who} only\";\n"
            + "}\n"
            + $"{End}{list.Value}\n";
    }

    /// <summary>The rule for senders who did not sign in: none of them may send to a restricted list.</summary>
    internal static string Refusal(MailAddress list, SenderRule rule) =>
        $"{Begin}{list.Value}\n"
        + $"if allof (envelope :is \"to\" {Strings(rule.ListAddresses.Select(static a => a.Value))}) {{\n"
        + $"    reject \"550 5.7.1 {list.Value} accepts mail from signed-in senders only\";\n"
        + "}\n"
        + $"{End}{list.Value}\n";

    private static (List<string> Preamble, SortedDictionary<string, string> Blocks) Parse(string? contents)
    {
        var preamble = new List<string>();
        var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var block = new StringBuilder();
        foreach (string line in (contents ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (current is null && line.StartsWith(Begin, StringComparison.Ordinal))
            {
                current = line[Begin.Length..].Trim();
                block.Clear().Append(line).Append('\n');
            }
            else if (current is not null)
            {
                block.Append(line).Append('\n');
                if (string.Equals(line.Trim(), End + current, StringComparison.Ordinal))
                {
                    blocks[current] = block.ToString();
                    current = null;
                }
            }
            else if (line.Length > 0)
            {
                preamble.Add(line);
            }
        }

        // An unterminated block (edited by hand) is dropped: the console writes it again on the list's next change.
        return (preamble, blocks);
    }

    // List addresses and domains are validated (no quote or backslash); a member's Keycloak address is not, so one
    // that would need escaping is left out (that member cannot send to the list) rather than written.
    private static bool IsPlain(string value) => !value.Contains('"', StringComparison.Ordinal) && !value.Contains('\\', StringComparison.Ordinal);

    private static string Strings(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(static v => $"\"{v}\"")) + "]";
}
