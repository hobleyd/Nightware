using System.Text;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Rules;

/// <summary>Nightware's Sieve script as found on the server: its id and whether it is the active one.</summary>
internal sealed record LoadedFilter(string? ScriptId, bool IsActive, FilterState State)
{
    public bool Exists => ScriptId is not null;

    /// <summary>
    /// The name of an active script that is neither ours nor Stalwart's <c>vacation</c>: the user's own filter,
    /// which activating ours would silently switch off.
    /// </summary>
    public string? ForeignActiveScript { get; init; }
}

/// <summary>
/// Loads and saves Nightware's Sieve script (<see cref="SieveScriptWriter.ScriptName"/>). Stalwart runs one
/// active script per account and its <c>VacationResponse</c> is a script of its own, so rules and automatic
/// replies share this one (ADR 0006 D5). Every save is compiled with <c>SieveScript/validate</c> first, then
/// stored and made the active script in one <c>SieveScript/set</c>.
/// </summary>
internal sealed class NightwareSieve(IJmapClient jmap, IJmapBlobService blobs)
{
    private const string SieveType = "application/sieve";

    /// <summary>The script Stalwart writes for <c>VacationResponse</c>; taking over from it is expected.</summary>
    internal const string VacationScriptName = "vacation";

    public async Task<Result<LoadedFilter>> LoadAsync(JmapAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.SieveScriptGet, new GetRequest(account.AccountId) { Properties = ["id", "name", "blobId", "isActive"] });
        var scripts = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!scripts.IsSuccess)
        {
            return scripts.Error;
        }

        var foreign = scripts.Value.List.FirstOrDefault(static s => s.IsActive == true && s.Name is not (SieveScriptWriter.ScriptName or VacationScriptName))?.Name;
        var script = scripts.Value.List.FirstOrDefault(static s => s.Name == SieveScriptWriter.ScriptName);
        if (script?.Id is null || script.BlobId is null)
        {
            return new LoadedFilter(null, false, FilterState.Empty) { ForeignActiveScript = foreign };
        }

        var content = await blobs.DownloadAsync(account.AccountId, script.BlobId, SieveScriptWriter.ScriptName, SieveType, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return content.Error;
        }

        await using var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // A script of ours that no longer carries a readable state (edited by hand) starts again empty.
        return new LoadedFilter(script.Id, script.IsActive == true, FilterState.FromScript(text) ?? FilterState.Empty) { ForeignActiveScript = foreign };
    }

    /// <summary>Writes <paramref name="state"/> as the script and makes it the active one.</summary>
    public async Task<Result> SaveAsync(JmapAccount account, LoadedFilter loaded, FilterState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(state);
        if (account.IsReadOnly)
        {
            return Error.Forbidden("The signed-in user cannot change this mailbox's rules or automatic replies.");
        }

        if (loaded.ForeignActiveScript is { } foreign)
        {
            return new Error(ErrorKind.Conflict, "ErrorMailFilterInUse",
                $"The mailbox's own mail filter '{foreign}' is active; Nightware's rules would switch it off. Deactivate it first.");
        }

        var mailboxRequest = new JmapRequestBuilder();
        var mailboxGet = mailboxRequest.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(account.AccountId));
        var mailboxes = (await jmap.SendAsync(mailboxRequest, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(mailboxGet)).Map(static r => r.List);
        if (!mailboxes.IsSuccess)
        {
            return mailboxes.Error;
        }

        var known = mailboxes.Value.Select(static m => m.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var unknown = state.Rules.SelectMany(static r => new[] { r.Actions.MoveToFolder, r.Actions.CopyToFolder }).OfType<FolderId>()
            .FirstOrDefault(f => !known.Contains(f.Value));
        if (unknown.Value is not null)
        {
            return Error.Validation($"The folder '{unknown.Value}' does not exist.", "actions");
        }

        var trash = MailboxRoles.Find(mailboxes.Value, WellKnownFolder.DeletedItems)?.Id;
        var text = SieveScriptWriter.Write(state, trash);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var uploaded = await blobs.UploadAsync(account.AccountId, content, SieveType, cancellationToken).ConfigureAwait(false);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error;
        }

        var blobId = uploaded.Value.BlobId;
        var validateRequest = new JmapRequestBuilder();
        var validate = validateRequest.Add(JmapMethods.SieveScriptValidate, new SieveScriptValidateRequest(account.AccountId, blobId));
        var compiled = (await jmap.SendAsync(validateRequest, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(validate));
        if (!compiled.IsSuccess)
        {
            return compiled.Error;
        }

        if (compiled.Value.Error is { } error)
        {
            // Generated text that does not compile is our bug, not the caller's: nothing is changed.
            return JmapErrors.Unexpected($"The generated mail filter did not compile: {error.Description}");
        }

        const string created = "script";
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.SieveScriptSet, new SieveScriptSetRequest(account.AccountId)
        {
            Create = loaded.Exists ? null : new Dictionary<string, SieveScript>(StringComparer.Ordinal)
            {
                [created] = new SieveScript { Name = SieveScriptWriter.ScriptName, BlobId = blobId },
            },
            Update = loaded.Exists
                ? new Dictionary<string, PatchObject>(StringComparer.Ordinal) { [loaded.ScriptId!] = new Patch().Set("blobId", blobId).Build() }
                : null,
            OnSuccessActivateScript = loaded.Exists ? loaded.ScriptId : "#" + created,
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => loaded.Exists ? s.Updated(loaded.ScriptId!) : s.Created(created).ToResult());
    }
}
