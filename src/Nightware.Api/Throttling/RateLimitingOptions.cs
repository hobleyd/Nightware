using Microsoft.Extensions.Options;

namespace Nightware.Api.Throttling;

/// <summary>
/// Per-user request limits for the Graph routes (configuration section <c>RateLimiting</c>).
/// </summary>
/// <remarks>
/// <para>Sized from the NightMail client, not from Graph's own limits. One signed-in device makes,
/// per account: a mail poll every 30 s (inbox delta, the folder list when the delta reports
/// changes, the folder on screen), a 15 s list + folder-list refresh while the mobile list is
/// visible, a calendar sync every 15 min and a contacts sync every 6 h — about 10–20 requests a
/// minute at rest. The bursts are what matter: start-up (folders, a page of mail, up to 20 body
/// prefetches spaced one frame apart, calendarView, task lists, contacts paging) and the offline
/// outbox, which drains one Graph call per queued mutation back to back. A user typically has two
/// or three devices on one account, all sharing this partition.</para>
/// <para>The defaults (a 300-request burst refilling at 10 a second, so 600 a minute sustained,
/// and 32 requests in flight, because start-up fans out to sender photos and several feature lists
/// at once) are an order of magnitude above that steady state, so the limiter only ever bites a
/// runaway loop or an abusive caller. Graph itself allows a mailbox roughly
/// 10,000 requests per 10 minutes and 4 concurrent requests per app, and NightMail already retries
/// 429 honouring <c>Retry-After</c>.</para>
/// </remarks>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Turns the per-user limiter off entirely (e.g. for a load test of the backends).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Burst size: requests a user can make at once from a full bucket.</summary>
    public int TokenLimit { get; set; } = 300;

    /// <summary>Requests returned to the bucket every <see cref="ReplenishmentPeriod"/>.</summary>
    public int TokensPerPeriod { get; set; } = 10;

    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Requests one user may have in flight at once; 0 disables the concurrency limit.</summary>
    public int ConcurrentRequestLimit { get; set; } = 32;
}

/// <summary>Rejects limits that would make the limiter throw on construction or never admit a request.</summary>
internal sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (options.TokenLimit < 1)
        {
            failures.Add("RateLimiting:TokenLimit must be at least 1.");
        }

        if (options.TokensPerPeriod < 1)
        {
            failures.Add("RateLimiting:TokensPerPeriod must be at least 1.");
        }

        if (options.ReplenishmentPeriod <= TimeSpan.Zero)
        {
            failures.Add("RateLimiting:ReplenishmentPeriod must be positive.");
        }

        if (options.ConcurrentRequestLimit < 0)
        {
            failures.Add("RateLimiting:ConcurrentRequestLimit cannot be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
