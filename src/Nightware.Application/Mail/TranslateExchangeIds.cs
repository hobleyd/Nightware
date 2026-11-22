using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail;

/// <summary>One id and its translation (Graph <c>convertIdResult</c>).</summary>
public sealed record TranslatedId(string SourceId, string TargetId);

/// <summary>
/// Graph <c>POST translateExchangeIds</c> (phase 19). Nightware's ids are already immutable (a message keeps its id across
/// moves, ADR 0001 §1-2), so a REST id and a REST immutable id are the same string and translate to themselves. The
/// binary formats (<c>entryId</c>, <c>immutableEntryId</c>, <c>ewsId</c>) are Exchange store ids with no counterpart here:
/// asking for one is 400 rather than an id that would mean nothing to Exchange tooling.
/// </summary>
public sealed class TranslateExchangeIds
{
    /// <summary>Graph's limit on <c>inputIds</c>.</summary>
    public const int MaxIds = 1000;

    private static readonly HashSet<string> RestFormats = new(StringComparer.OrdinalIgnoreCase) { "restId", "restImmutableEntryId" };
    private static readonly HashSet<string> KnownFormats = new(StringComparer.OrdinalIgnoreCase) { "restId", "restImmutableEntryId", "entryId", "immutableEntryId", "ewsId" };

    /// <remarks>No I/O: the translation is the identity. Async for the use-case convention.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered use case.")]
    public Task<Result<IReadOnlyList<TranslatedId>>> ExecuteAsync(IReadOnlyList<string>? inputIds, string? sourceIdType, string? targetIdType, CancellationToken cancellationToken) =>
        Task.FromResult(Translate(inputIds, sourceIdType, targetIdType));

    private static Result<IReadOnlyList<TranslatedId>> Translate(IReadOnlyList<string>? inputIds, string? sourceIdType, string? targetIdType)
    {
        if (inputIds is null || inputIds.Count == 0)
        {
            return Error.Validation("inputIds is required.", "inputIds");
        }

        if (inputIds.Count > MaxIds)
        {
            return Error.Validation($"inputIds can hold at most {MaxIds} ids.", "inputIds");
        }

        foreach (var (format, name) in new[] { (sourceIdType, "sourceIdType"), (targetIdType, "targetIdType") })
        {
            if (format is null || !KnownFormats.Contains(format))
            {
                return Error.Validation($"{name} must be one of entryId, ewsId, immutableEntryId, restId or restImmutableEntryId.", name);
            }

            if (!RestFormats.Contains(format))
            {
                return new Error(ErrorKind.Validation, "notSupported", $"'{format}' ids are Exchange store ids; Nightware's ids are REST ids, already immutable.", name);
            }
        }

        if (inputIds.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("inputIds can't hold an empty id.", "inputIds");
        }

        return inputIds.Select(static id => new TranslatedId(id, id)).ToList();
    }
}
