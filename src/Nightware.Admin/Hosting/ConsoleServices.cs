using System.Security.Cryptography;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Hosting;

/// <summary>
/// The audit trail (ADR 0002 D11): one structured <c>admin.audit</c> event per administrative action, with
/// the human actor that Keycloak's own admin events cannot see (it records the console's service account).
/// Goes through the host's log redaction; an <see cref="AuditEvent"/> never carries a secret.
/// </summary>
public sealed partial class LoggerAuditLog(ILogger<LoggerAuditLog> logger) : IAuditLog
{
    public void Record(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        string details = string.Join(' ', auditEvent.Details.Where(static d => d.Value is not null).Select(static d => $"{d.Key}={d.Value}"));
        Log(logger, auditEvent.Action, auditEvent.ActorId, auditEvent.Succeeded, auditEvent.TargetUserId, auditEvent.EntityId, details);
    }

    [LoggerMessage(EventId = 9000, EventName = "admin.audit", Level = LogLevel.Information,
        Message = "admin.audit {Action} by {ActorId} succeeded={Succeeded} target={TargetUserId} entity={EntityId} {Details}")]
    private static partial void Log(ILogger logger, string action, string actorId, bool succeeded, string? targetUserId, string? entityId, string details);
}

/// <summary>
/// 20-character temporary passwords from a CSPRNG, with every character class Keycloak's password policies
/// commonly require and no look-alike characters, since an administrator may read it out to the person.
/// </summary>
public sealed class RandomPasswordGenerator : IPasswordGenerator
{
    internal const int Length = 20;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "-_.!@#%+=";

    public TemporaryCredential Generate()
    {
        const string all = Upper + Lower + Digits + Symbols;
        char[] chars = new char[Length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (int i = 4; i < Length; i++)
        {
            chars[i] = Pick(all);
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new TemporaryCredential(new string(chars));
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
