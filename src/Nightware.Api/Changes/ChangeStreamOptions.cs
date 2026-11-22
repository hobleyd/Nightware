using Microsoft.Extensions.Options;

namespace Nightware.Api.Changes;

/// <summary>The change stream (ADR 0007; configuration section <c>ChangeStream</c>).</summary>
public sealed class ChangeStreamOptions
{
    public const string SectionName = "ChangeStream";

    /// <summary>Off: the route is not mapped and discovery advertises no <c>changesUrl</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often a stream reads each area's state from the backends (one small request per area).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest a stream goes without writing anything; a comment line is sent then, so proxies and NAT
    /// tables keep the connection. Must exceed <see cref="PollInterval"/>, which is when it is checked.
    /// </summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The longest one stream lives. It also ends shortly before the caller's access token expires, since every
    /// read is made with that token; the client reconnects with a fresh one.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Open streams one user may hold at once (one per signed-in device and mailbox is typical).</summary>
    public int MaxStreamsPerUser { get; set; } = 8;
}

internal sealed class ChangeStreamOptionsValidator : IValidateOptions<ChangeStreamOptions>
{
    public ValidateOptionsResult Validate(string? name, ChangeStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.PollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("ChangeStream:PollInterval must be at least one second.");
        }

        if (options.KeepAlive <= options.PollInterval)
        {
            failures.Add("ChangeStream:KeepAlive must be longer than ChangeStream:PollInterval.");
        }

        if (options.MaxDuration < TimeSpan.FromMinutes(1))
        {
            failures.Add("ChangeStream:MaxDuration must be at least one minute.");
        }

        if (options.MaxStreamsPerUser < 1)
        {
            failures.Add("ChangeStream:MaxStreamsPerUser must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
