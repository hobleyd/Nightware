using System.Reflection;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Routing;

/// <summary>
/// The mailbox a <c>/me/...</c> or <c>/users/{user}/...</c> route targets, as an endpoint parameter.
/// Resolved through <see cref="IMailboxResolver"/>; a failure becomes the Graph error envelope through
/// <see cref="GraphBindingFilter"/> before the handler runs.
/// </summary>
public sealed class Mailbox : IGraphBoundParameter
{
    /// <summary>The route parameter that carries the <c>{id|upn}</c> segment.</summary>
    public const string RouteParameter = "user";

    private readonly MailboxRef? _ref;

    private Mailbox(string? userSegment, MailboxRef? mailbox, Error? error)
    {
        UserSegment = userSegment;
        _ref = mailbox;
        BindingError = error;
    }

    /// <summary>The raw segment after <c>/users/</c>, or null for <c>/me</c>.</summary>
    public string? UserSegment { get; }

    public bool IsMe => UserSegment is null;

    public Error? BindingError { get; }

    /// <summary>The resolved mailbox. Only read it from a handler (the filter guarantees success there).</summary>
    public MailboxRef Ref => _ref ?? throw new InvalidOperationException($"Mailbox resolution failed: {BindingError?.Message}");

    /// <summary>Graph's entity-set segment for <c>@odata.context</c>, e.g. <c>users('alice%40example.com')</c>.</summary>
    public string ContextSegment => $"users('{Uri.EscapeDataString(Ref.Address)}')";

    public static Mailbox From(string? userSegment, Result<MailboxRef> result) =>
        result.IsSuccess ? new Mailbox(userSegment, result.Value, null) : new Mailbox(userSegment, null, result.Error);

    public static async ValueTask<Mailbox?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        var segment = context.Request.RouteValues.TryGetValue(RouteParameter, out var value) ? value?.ToString() : null;
        var resolver = context.RequestServices.GetRequiredService<IMailboxResolver>();
        var result = await resolver.ResolveAsync(segment, context.RequestAborted).ConfigureAwait(false);
        return From(segment, result);
    }
}
