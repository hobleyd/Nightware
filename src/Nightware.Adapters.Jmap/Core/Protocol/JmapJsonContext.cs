using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightware.Adapters.Jmap.Core.Protocol;

/// <summary>
/// Source-generated JSON metadata for every JMAP type the adapter sends or reads.
/// camelCase, nulls omitted (PatchObject dictionary values are always written,
/// so a null there still means "reset"), dates as UTCDate.
/// Feature phases add their own <c>[JsonSerializable]</c> entries here when they need new closed generics.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(UtcDateConverter)])]
[JsonSerializable(typeof(JmapSessionDocument))]
[JsonSerializable(typeof(CoreCapability))]
[JsonSerializable(typeof(MailAccountCapability))]
[JsonSerializable(typeof(SubmissionAccountCapability))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(MethodError))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(JmapDeltaCursorPayload))]
[JsonSerializable(typeof(GetRequest))]
[JsonSerializable(typeof(EmailGetRequest))]
[JsonSerializable(typeof(ChangesRequest))]
[JsonSerializable(typeof(ChangesResponse))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(QueryChangesResponse))]
[JsonSerializable(typeof(EmailQueryRequest))]
[JsonSerializable(typeof(MailboxQueryRequest))]
[JsonSerializable(typeof(QueryChangesRequest<EmailFilter>))]
[JsonSerializable(typeof(QueryChangesRequest<MailboxFilter>))]
[JsonSerializable(typeof(GetResponse<Mailbox>))]
[JsonSerializable(typeof(GetResponse<Email>))]
[JsonSerializable(typeof(GetResponse<EmailThread>))]
[JsonSerializable(typeof(GetResponse<Identity>))]
[JsonSerializable(typeof(GetResponse<EmailSubmission>))]
[JsonSerializable(typeof(GetResponse<VacationResponse>))]
[JsonSerializable(typeof(GetResponse<SieveScript>))]
[JsonSerializable(typeof(SieveScriptSetRequest))]
[JsonSerializable(typeof(SetResponse<SieveScript>))]
[JsonSerializable(typeof(SieveScriptValidateRequest))]
[JsonSerializable(typeof(SieveScriptValidateResponse))]
[JsonSerializable(typeof(BlobUploadRequest))]
[JsonSerializable(typeof(BlobUploadResponse))]
[JsonSerializable(typeof(BlobAccountCapability))]
[JsonSerializable(typeof(MailboxSetRequest))]
[JsonSerializable(typeof(SetRequest<Email>))]
[JsonSerializable(typeof(EmailSubmissionSetRequest))]
[JsonSerializable(typeof(SetRequest<VacationResponse>))]
[JsonSerializable(typeof(SetRequest<Identity>))]
[JsonSerializable(typeof(SetResponse<Mailbox>))]
[JsonSerializable(typeof(SetResponse<Email>))]
[JsonSerializable(typeof(SetResponse<EmailSubmission>))]
[JsonSerializable(typeof(SetResponse<VacationResponse>))]
[JsonSerializable(typeof(SetResponse<Identity>))]
[JsonSerializable(typeof(EmailImportRequest))]
[JsonSerializable(typeof(EmailImportResponse))]
[JsonSerializable(typeof(EmailParseRequest))]
[JsonSerializable(typeof(EmailParseResponse))]
internal sealed partial class JmapJsonContext : JsonSerializerContext
{
    /// <summary>Shared instance; <see cref="JsonSerializerOptions"/> are immutable once used.</summary>
    public static JmapJsonContext Shared => Default;

    public static JsonSerializerOptions SerializerOptions => Default.Options;
}
