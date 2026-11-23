using System.Globalization;
using System.Text.Json;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Presence;
using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Adapters.Dav.Presence;

/// <summary>
/// <see cref="IUserStatusStore"/> over Nextcloud's <c>user_status</c> OCS API (ADR 0009 D6), as the caller. A 404 means
/// the user has no status row: never set, or deleted when a message with <c>clearAt</c> expired.
/// </summary>
internal sealed class NextcloudUserStatusStore(IDavClient dav) : IUserStatusStore
{
    private const string Api = "apps/user_status/api/v1/";
    private const string Own = Api + "user_status";

    public async Task<Result<UserStatus>> GetOwnAsync(CancellationToken cancellationToken) =>
        Read(await dav.OcsGetAsync(Own, null, cancellationToken).ConfigureAwait(false));

    public async Task<Result<UserStatus>> GetAsync(string accountName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        return Read(await dav.OcsGetAsync(Api + "statuses/" + Uri.EscapeDataString(accountName), null, cancellationToken).ConfigureAwait(false));
    }

    public Task<Result> SetStatusAsync(UserStatusType type, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, Own + "/status", new Dictionary<string, string>(StringComparer.Ordinal) { ["statusType"] = TypeName(type) }, cancellationToken);

    public Task<Result> SetPredefinedMessageAsync(string messageId, DateTimeOffset? clearAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["messageId"] = messageId };
        AddClearAt(form, clearAt);
        return SendAsync(HttpMethod.Put, Own + "/message/predefined", form, cancellationToken);
    }

    public Task<Result> SetCustomMessageAsync(string message, DateTimeOffset? clearAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = message };
        AddClearAt(form, clearAt);
        return SendAsync(HttpMethod.Put, Own + "/message/custom", form, cancellationToken);
    }

    public Task<Result> ClearMessageAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, Own + "/message", null, cancellationToken);

    private async Task<Result> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form, CancellationToken cancellationToken)
    {
        var sent = await dav.OcsSendAsync(method, path, form, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? Result.Success() : sent.Error;
    }

    private static void AddClearAt(Dictionary<string, string> form, DateTimeOffset? clearAt)
    {
        if (clearAt is { } at)
        {
            form["clearAt"] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
    }

    private static Result<UserStatus> Read(Result<JsonElement> response)
    {
        if (!response.IsSuccess)
        {
            return response.Error.Kind == ErrorKind.NotFound ? UserStatus.None : response.Error;
        }

        var data = response.Value;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return DavErrors.Unavailable("The groupware server returned an unexpected user status.");
        }

        var type = ParseType(Text(data, "status"));
        if (type is null)
        {
            return DavErrors.Unavailable("The groupware server returned an unknown user status type.");
        }

        return new UserStatus(type.Value)
        {
            MessageId = Text(data, "messageId"),
            Message = Text(data, "message"),
            Icon = Text(data, "icon"),
            ClearAt = data.TryGetProperty("clearAt", out var clearAt) && clearAt.ValueKind == JsonValueKind.Number && clearAt.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null,
        };
    }

    private static string? Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static UserStatusType? ParseType(string? name) => name switch
    {
        "online" => UserStatusType.Online,
        "away" => UserStatusType.Away,
        "dnd" => UserStatusType.DoNotDisturb,
        "busy" => UserStatusType.Busy,
        "invisible" => UserStatusType.Invisible,
        "offline" => UserStatusType.Offline,
        _ => null,
    };

    private static string TypeName(UserStatusType type) => type switch
    {
        UserStatusType.Online => "online",
        UserStatusType.Away => "away",
        UserStatusType.DoNotDisturb => "dnd",
        UserStatusType.Busy => "busy",
        UserStatusType.Invisible => "invisible",
        UserStatusType.Offline => "offline",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
