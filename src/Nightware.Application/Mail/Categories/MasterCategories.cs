using System.Security.Cryptography;
using System.Text;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Categories;

/// <summary>Rules shared by the master category use cases.</summary>
public static class MasterCategories
{
    public const string NoColor = "none";

    /// <summary>
    /// The list a new Exchange mailbox starts with (captured, mail.master-categories.list). Ids are derived from
    /// the name, so they are the same on every read until the list is first written, and then kept.
    /// </summary>
    public static IReadOnlyList<MasterCategory> Defaults { get; } =
    [
        Default("Red category", "preset0"),
        Default("Orange category", "preset1"),
        Default("Yellow category", "preset3"),
        Default("Green category", "preset4"),
        Default("Blue category", "preset7"),
        Default("Purple category", "preset8"),
    ];

    /// <summary>Graph's <c>categoryColor</c>: <c>none</c> or <c>preset0</c>–<c>preset24</c>.</summary>
    public static bool IsColor(string color) =>
        color == NoColor
        || color.StartsWith("preset", StringComparison.Ordinal)
            && int.TryParse(color.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n is >= 0 and <= 24
            && color.Length == (n < 10 ? 7 : 8);

    internal static async Task<Result<IReadOnlyList<MasterCategory>>> CurrentAsync(IMasterCategoryStore store, MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var stored = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess ? Result<IReadOnlyList<MasterCategory>>.Success(stored.Value ?? Defaults) : stored.Error;
    }

    internal static Error NotFound(string id) => Error.NotFound($"The category '{id}' does not exist.");

    private static MasterCategory Default(string name, string color)
    {
        // A name-based (version 5 style) GUID: stable, and never mistaken for a random one.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("nightware.master-category:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new MasterCategory(new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString(), name, color);
    }
}

/// <summary>Graph <c>GET outlook/masterCategories</c>.</summary>
public sealed class ListMasterCategories(IMasterCategoryStore store)
{
    public Task<Result<IReadOnlyList<MasterCategory>>> ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        MasterCategories.CurrentAsync(store, mailbox, cancellationToken);
}

/// <summary>Graph <c>GET outlook/masterCategories/{id}</c>.</summary>
public sealed class GetMasterCategory(IMasterCategoryStore store)
{
    public async Task<Result<MasterCategory>> ExecuteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var current = await MasterCategories.CurrentAsync(store, mailbox, cancellationToken).ConfigureAwait(false);
        return current.Bind(list => list.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is { } found
            ? Result<MasterCategory>.Success(found)
            : MasterCategories.NotFound(id));
    }
}

/// <summary>Graph <c>POST outlook/masterCategories</c>: a name not already in the list (ignoring case), and a colour.</summary>
public sealed class CreateMasterCategory(IMasterCategoryStore store)
{
    public async Task<Result<MasterCategory>> ExecuteAsync(MailboxRef mailbox, string? displayName, string? color, CancellationToken cancellationToken)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return Error.Validation("A category needs a displayName.", "displayName");
        }

        var chosen = color ?? MasterCategories.NoColor;
        if (!MasterCategories.IsColor(chosen))
        {
            return Error.Validation($"'{chosen}' is not a category colour.", "color");
        }

        var current = await MasterCategories.CurrentAsync(store, mailbox, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        if (current.Value.Any(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            return new Error(ErrorKind.Conflict, "ErrorDuplicateCategory", $"A category named '{name}' already exists.");
        }

        var created = new MasterCategory(Guid.NewGuid().ToString(), name, chosen);
        var saved = await store.SaveAsync(mailbox, [.. current.Value, created], cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? created : saved.Error;
    }
}

/// <summary>Graph <c>PATCH outlook/masterCategories/{id}</c>: only the colour changes; the name is fixed once created.</summary>
public sealed class UpdateMasterCategory(IMasterCategoryStore store)
{
    public async Task<Result<MasterCategory>> ExecuteAsync(MailboxRef mailbox, string id, string? displayName, string? color, CancellationToken cancellationToken)
    {
        if (color is not null && !MasterCategories.IsColor(color))
        {
            return Error.Validation($"'{color}' is not a category colour.", "color");
        }

        var current = await MasterCategories.CurrentAsync(store, mailbox, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        var existing = current.Value.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return MasterCategories.NotFound(id);
        }

        if (displayName is not null && !string.Equals(displayName.Trim(), existing.DisplayName, StringComparison.Ordinal))
        {
            return Error.Validation("A category's displayName cannot be changed.", "displayName");
        }

        var updated = existing with { Color = color ?? existing.Color };
        var saved = await store.SaveAsync(mailbox, [.. current.Value.Select(c => ReferenceEquals(c, existing) ? updated : c)], cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? updated : saved.Error;
    }
}

/// <summary>Graph <c>DELETE outlook/masterCategories/{id}</c>. Messages keep the name; it only leaves the list.</summary>
public sealed class DeleteMasterCategory(IMasterCategoryStore store)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var current = await MasterCategories.CurrentAsync(store, mailbox, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        var remaining = current.Value.Where(c => !string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        return remaining.Count == current.Value.Count
            ? MasterCategories.NotFound(id)
            : await store.SaveAsync(mailbox, remaining, cancellationToken).ConfigureAwait(false);
    }
}
