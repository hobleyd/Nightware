using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Errors;

/// <summary>Per-object outcome of a <c>Foo/set</c> / <c>Email/import</c>, as Domain results.</summary>
internal static class SetResults
{
    /// <summary>The server's view of a created object (at least its <c>id</c>), or the mapped SetError.</summary>
    public static Result<T> Created<T>(this SetResponse<T> response, string creationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Created(response.Created, response.NotCreated, creationId);
    }

    public static Result<Email> Created(this EmailImportResponse response, string creationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Created(response.Created, response.NotCreated, creationId);
    }

    /// <summary>Success when <paramref name="id"/> was updated; the mapped SetError otherwise.</summary>
    public static Result Updated<T>(this SetResponse<T> response, string id)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.NotUpdated?.TryGetValue(id, out var error) == true)
        {
            return JmapErrors.FromSetError(error, id);
        }

        return response.Updated?.ContainsKey(id) == true
            ? Result.Success()
            : JmapErrors.Unexpected($"The mail server did not report the update of {id}.");
    }

    /// <summary>Success when <paramref name="id"/> was destroyed; the mapped SetError otherwise.</summary>
    public static Result Destroyed<T>(this SetResponse<T> response, string id)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.NotDestroyed?.TryGetValue(id, out var error) == true)
        {
            return JmapErrors.FromSetError(error, id);
        }

        return response.Destroyed?.Contains(id) == true
            ? Result.Success()
            : JmapErrors.Unexpected($"The mail server did not report the destruction of {id}.");
    }

    /// <summary>The first failure across all three maps, or success. For batch operations that are all-or-nothing to the caller.</summary>
    public static Result AllSucceeded<T>(this SetResponse<T> response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var map in new[] { response.NotCreated, response.NotUpdated, response.NotDestroyed })
        {
            if (map?.FirstOrDefault() is { Key: not null } failure)
            {
                return JmapErrors.FromSetError(failure.Value, failure.Key);
            }
        }

        return Result.Success();
    }

    private static Result<T> Created<T>(Dictionary<string, T>? created, Dictionary<string, SetError>? notCreated, string creationId)
    {
        if (notCreated?.TryGetValue(creationId, out var error) == true)
        {
            return JmapErrors.FromSetError(error, creationId);
        }

        return created?.TryGetValue(creationId, out var value) == true
            ? value
            : JmapErrors.Unexpected($"The mail server did not report the creation of {creationId}.");
    }
}
