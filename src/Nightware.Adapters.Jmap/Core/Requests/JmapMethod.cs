using System.Text.Json.Serialization.Metadata;
using Nightware.Adapters.Jmap.Core.Protocol;

namespace Nightware.Adapters.Jmap.Core.Requests;

/// <summary>What a method does — decides retry safety and whether <c>ids</c> may be auto-chunked.</summary>
internal enum JmapMethodKind
{
    Get,
    Changes,
    Query,
    QueryChanges,
    Set,
    Other,
}

/// <summary>
/// A typed JMAP method descriptor: wire name, the capabilities it needs in
/// <c>using</c>, and the JSON metadata for its arguments and response.
/// </summary>
internal sealed record JmapMethod<TArgs, TResponse>(
    string Name,
    JmapMethodKind Kind,
    IReadOnlyList<string> Capabilities,
    JsonTypeInfo<TArgs> ArgumentsType,
    JsonTypeInfo<TResponse> ResponseType)
{
    /// <summary>
    /// Safe methods have no side effects, so a request made only of them may be
    /// retried after a transport failure. <c>/set</c>, <c>/import</c>, <c>/copy</c> are not.
    /// </summary>
    public bool IsSafe => Kind.IsSafe();
}

internal static class JmapMethodKindExtensions
{
    /// <summary>True for read-only kinds (/get, /changes, /query, /queryChanges).</summary>
    public static bool IsSafe(this JmapMethodKind kind) =>
        kind is JmapMethodKind.Get or JmapMethodKind.Changes or JmapMethodKind.Query or JmapMethodKind.QueryChanges;
}

/// <summary>The method descriptors feature phases use. Add new ones here (with their JSON context entries).</summary>
internal static class JmapMethods
{
    private static readonly string[] MailCaps = [JmapCapabilities.Mail];
    private static readonly string[] SubmissionCaps = [JmapCapabilities.Mail, JmapCapabilities.Submission];
    private static readonly string[] VacationCaps = [JmapCapabilities.VacationResponse];
    private static readonly string[] MailMetadataCaps = [JmapCapabilities.Mail, JmapCapabilities.Metadata];

    private static JmapJsonContext Json => JmapJsonContext.Default;

    public static JmapMethod<GetRequest, GetResponse<Mailbox>> MailboxGet { get; } =
        new("Mailbox/get", JmapMethodKind.Get, MailCaps, Json.GetRequest, Json.GetResponseMailbox);

    public static JmapMethod<ChangesRequest, ChangesResponse> MailboxChanges { get; } =
        new("Mailbox/changes", JmapMethodKind.Changes, MailCaps, Json.ChangesRequest, Json.ChangesResponse);

    public static JmapMethod<MailboxQueryRequest, QueryResponse> MailboxQuery { get; } =
        new("Mailbox/query", JmapMethodKind.Query, MailCaps, Json.MailboxQueryRequest, Json.QueryResponse);

    public static JmapMethod<QueryChangesRequest<MailboxFilter>, QueryChangesResponse> MailboxQueryChanges { get; } =
        new("Mailbox/queryChanges", JmapMethodKind.QueryChanges, MailCaps, Json.QueryChangesRequestMailboxFilter, Json.QueryChangesResponse);

    public static JmapMethod<MailboxSetRequest, SetResponse<Mailbox>> MailboxSet { get; } =
        new("Mailbox/set", JmapMethodKind.Set, MailCaps, Json.MailboxSetRequest, Json.SetResponseMailbox);

    public static JmapMethod<EmailGetRequest, GetResponse<Email>> EmailGet { get; } =
        new("Email/get", JmapMethodKind.Get, MailCaps, Json.EmailGetRequest, Json.GetResponseEmail);

    public static JmapMethod<ChangesRequest, ChangesResponse> EmailChanges { get; } =
        new("Email/changes", JmapMethodKind.Changes, MailCaps, Json.ChangesRequest, Json.ChangesResponse);

    public static JmapMethod<EmailQueryRequest, QueryResponse> EmailQuery { get; } =
        new("Email/query", JmapMethodKind.Query, MailCaps, Json.EmailQueryRequest, Json.QueryResponse);

    public static JmapMethod<QueryChangesRequest<EmailFilter>, QueryChangesResponse> EmailQueryChanges { get; } =
        new("Email/queryChanges", JmapMethodKind.QueryChanges, MailCaps, Json.QueryChangesRequestEmailFilter, Json.QueryChangesResponse);

    public static JmapMethod<SetRequest<Email>, SetResponse<Email>> EmailSet { get; } =
        new("Email/set", JmapMethodKind.Set, MailCaps, Json.SetRequestEmail, Json.SetResponseEmail);

    /// <summary><c>Email/get</c> asking for <c>metadata</c>: only for an account that advertises it (<c>JmapAccount.SupportsMetadata</c>).</summary>
    public static JmapMethod<EmailGetRequest, GetResponse<Email>> EmailGetWithMetadata { get; } =
        new("Email/get", JmapMethodKind.Get, MailMetadataCaps, Json.EmailGetRequest, Json.GetResponseEmail);

    /// <summary><c>Email/set</c> writing <c>metadata</c>.</summary>
    public static JmapMethod<SetRequest<Email>, SetResponse<Email>> EmailSetWithMetadata { get; } =
        new("Email/set", JmapMethodKind.Set, MailMetadataCaps, Json.SetRequestEmail, Json.SetResponseEmail);

    public static JmapMethod<EmailImportRequest, EmailImportResponse> EmailImport { get; } =
        new("Email/import", JmapMethodKind.Other, MailCaps, Json.EmailImportRequest, Json.EmailImportResponse);

    public static JmapMethod<GetRequest, GetResponse<EmailThread>> ThreadGet { get; } =
        new("Thread/get", JmapMethodKind.Get, MailCaps, Json.GetRequest, Json.GetResponseEmailThread);

    public static JmapMethod<ChangesRequest, ChangesResponse> ThreadChanges { get; } =
        new("Thread/changes", JmapMethodKind.Changes, MailCaps, Json.ChangesRequest, Json.ChangesResponse);

    public static JmapMethod<GetRequest, GetResponse<Identity>> IdentityGet { get; } =
        new("Identity/get", JmapMethodKind.Get, SubmissionCaps, Json.GetRequest, Json.GetResponseIdentity);

    public static JmapMethod<SetRequest<Identity>, SetResponse<Identity>> IdentitySet { get; } =
        new("Identity/set", JmapMethodKind.Set, SubmissionCaps, Json.SetRequestIdentity, Json.SetResponseIdentity);

    public static JmapMethod<GetRequest, GetResponse<EmailSubmission>> EmailSubmissionGet { get; } =
        new("EmailSubmission/get", JmapMethodKind.Get, SubmissionCaps, Json.GetRequest, Json.GetResponseEmailSubmission);

    public static JmapMethod<EmailSubmissionSetRequest, SetResponse<EmailSubmission>> EmailSubmissionSet { get; } =
        new("EmailSubmission/set", JmapMethodKind.Set, SubmissionCaps, Json.EmailSubmissionSetRequest, Json.SetResponseEmailSubmission);

    public static JmapMethod<GetRequest, GetResponse<VacationResponse>> VacationResponseGet { get; } =
        new("VacationResponse/get", JmapMethodKind.Get, VacationCaps, Json.GetRequest, Json.GetResponseVacationResponse);

    public static JmapMethod<SetRequest<VacationResponse>, SetResponse<VacationResponse>> VacationResponseSet { get; } =
        new("VacationResponse/set", JmapMethodKind.Set, VacationCaps, Json.SetRequestVacationResponse, Json.SetResponseVacationResponse);
}
