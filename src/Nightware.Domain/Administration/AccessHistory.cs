using System.Globalization;

namespace Nightware.Domain.Administration;

/// <summary>What an administrator did to a user's access, as the user's history records it.</summary>
public enum AccessEventKind
{
    Offboarded,

    /// <summary>Offboarding run again on a user already offboarded, to shut what the first run could not.</summary>
    OffboardingFinished,

    Reinstated,
    MailDeleted,
    FilesDeleted,
}

/// <summary>A system's state right after an access event.</summary>
public readonly record struct SystemState(BackendKind Backend, LinkState State);

/// <summary>
/// One entry in a user's access history: when, what, by whom, and what each system said straight after. Kept on
/// the user in Keycloak (the system of record) as one short line per entry, so the console stores nothing of its
/// own; <see cref="Format"/> and <see cref="Parse"/> are that line.
/// </summary>
public sealed record AccessEvent(DateTimeOffset At, AccessEventKind Kind, string By, IReadOnlyList<SystemState> Systems)
{
    /// <summary>Entries kept per user, newest first.</summary>
    public const int Keep = 10;

    /// <summary>Longest administrator name kept, so a line stays inside one attribute value.</summary>
    private const int MaxBy = 64;

    /// <summary><c>2026-11-18T09:12:00Z|Offboarded|ops|Mail:Disabled,Groupware:Missing</c>.</summary>
    public string Format()
    {
        string by = new([.. By.Where(static c => c is not '|' and not ',' and not ':' && !char.IsControl(c)).Take(MaxBy)]);
        string systems = string.Join(',', Systems.Select(static s => $"{s.Backend}:{s.State}"));
        return string.Join('|', At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), Kind, by, systems);
    }

    /// <summary>The entry a <see cref="Format"/> line holds, or null for one the console cannot read.</summary>
    public static AccessEvent? Parse(string? line)
    {
        string[] parts = (line ?? string.Empty).Split('|');
        if (parts.Length != 4
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            || !Enum.TryParse(parts[1], ignoreCase: false, out AccessEventKind kind)
            || !Enum.IsDefined(kind))
        {
            return null;
        }

        var systems = new List<SystemState>();
        foreach (string pair in parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split(':');
            if (kv.Length == 2 && Enum.TryParse(kv[0], out BackendKind backend) && Enum.IsDefined(backend) && Enum.TryParse(kv[1], out LinkState state) && Enum.IsDefined(state))
            {
                systems.Add(new SystemState(backend, state));
            }
        }

        return new AccessEvent(at, kind, parts[2], systems);
    }

    /// <summary>A user's history with <paramref name="added"/> first, cut to <see cref="Keep"/> entries, as lines.</summary>
    public static IReadOnlyList<string> Prepend(AccessEvent added, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(existing);
        return [.. new[] { added.Format() }.Concat(existing.Where(static l => Parse(l) is not null)).Take(Keep)];
    }
}
