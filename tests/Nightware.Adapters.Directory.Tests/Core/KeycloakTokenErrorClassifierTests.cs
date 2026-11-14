using System.Net;

using Nightware.Adapters.Directory.Core;

namespace Nightware.Adapters.Directory.Tests.Core;

public sealed class KeycloakTokenErrorClassifierTests
{
    [Theory]
    [InlineData(400, "invalid_request", "Invalid token", "SubjectRejected")]
    [InlineData(400, "invalid_request", "Token expired", "SubjectRejected")]
    [InlineData(400, "invalid_grant", "Session not active", "SubjectRejected")]
    [InlineData(400, "invalid_token", null, "SubjectRejected")]
    [InlineData(403, "access_denied", "Client is not within the token audience", "SubjectRejected")]
    [InlineData(403, null, null, "SubjectRejected")]
    [InlineData(400, "invalid_request", "Requested audience not available: nightmail", "Misconfigured")]
    [InlineData(400, "invalid_request", "Parameter 'subject_token_type' required for standard token exchange", "Misconfigured")]
    [InlineData(400, "invalid_client", "Audience not found", "Misconfigured")]
    [InlineData(401, "unauthorized_client", "Invalid client or Invalid client credentials", "Misconfigured")]
    [InlineData(400, null, null, "Misconfigured")]
    [InlineData(429, null, null, "Unavailable")]
    [InlineData(500, "unknown_error", null, "Unavailable")]
    [InlineData(503, null, null, "Unavailable")]
    public void Classifies_exchange_failures(int status, string? error, string? description, string expected)
    {
        OAuthErrorResponse? body = error is null && description is null ? null : new OAuthErrorResponse(error, description);

        KeycloakTokenErrorClassifier.ClassifyExchange((HttpStatusCode)status, body).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(400, "Misconfigured")]
    [InlineData(401, "Misconfigured")]
    [InlineData(403, "Misconfigured")]
    [InlineData(429, "Unavailable")]
    [InlineData(502, "Unavailable")]
    public void Client_credentials_failures_are_never_the_callers_fault(int status, string expected) =>
        KeycloakTokenErrorClassifier.ClassifyClientCredentials((HttpStatusCode)status).ToString().ShouldBe(expected);
}
