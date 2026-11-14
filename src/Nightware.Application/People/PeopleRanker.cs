using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.People;

/// <summary>
/// Merges personal contacts, directory users and recent correspondents into Graph <c>person</c>s, ranked
/// by relevance to the mailbox owner. Pure: the reference time is an argument, so a paged walk ranks
/// every page against the same instant.
/// </summary>
/// <remarks>
/// <para>People are merged by email address (case-insensitive). The display name comes from the most
/// deliberate source: a contact the owner saved, then the directory, then a correspondent's header name,
/// then the address itself.</para>
/// <para>Scoring follows what Graph's ranking is documented to weigh (communication and the user's own
/// contacts) on Graph's open-ended scale:</para>
/// <list type="bullet">
/// <item>correspondence: up to <see cref="FrequencyWeight"/> for message count (saturating at
/// <see cref="FrequencySaturation"/> messages) plus up to <see cref="RecencyWeight"/> for the last
/// exchange, halving every <see cref="RecencyHalfLifeDays"/> days;</item>
/// <item><see cref="ContactWeight"/> for a saved contact, <see cref="DirectoryWeight"/> for a colleague.</item>
/// </list>
/// Ties are broken by display name, then address, so the order is total and pages never overlap.
/// </remarks>
public sealed class PeopleRanker
{
    public const double FrequencyWeight = 10;
    public const int FrequencySaturation = 20;
    public const double RecencyWeight = 10;
    public const double RecencyHalfLifeDays = 30;
    public const double ContactWeight = 5;
    public const double DirectoryWeight = 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered service.")]
    public IReadOnlyList<Person> Rank(
        IEnumerable<Contact> contacts,
        IEnumerable<DirectoryUser> users,
        IEnumerable<Correspondent> correspondents,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(correspondents);

        var merger = new Merger();
        foreach (var contact in contacts)
        {
            var person = merger.Find(contact.EmailAddresses.Select(static e => e.Address));
            if (person is null)
            {
                continue;
            }

            person.Sources |= PersonSource.Contact;
            person.ContactName ??= contact.DisplayName;
            person.GivenName ??= contact.GivenName;
            person.Surname ??= contact.Surname;
            person.CompanyName ??= contact.CompanyName;
            person.JobTitle ??= contact.JobTitle;
        }

        foreach (var user in users)
        {
            var address = user.Mail ?? (user.UserPrincipalName.Contains('@', StringComparison.Ordinal) ? user.UserPrincipalName : null);
            var person = address is null ? null : merger.Find([address]);
            if (person is null)
            {
                continue;
            }

            person.Sources |= PersonSource.Directory;
            person.DirectoryName ??= user.DisplayName;
            person.GivenName ??= user.GivenName;
            person.Surname ??= user.Surname;
            person.CompanyName ??= user.CompanyName;
            person.JobTitle ??= user.JobTitle;
        }

        foreach (var correspondent in correspondents)
        {
            var person = merger.Find([correspondent.Address.Address]);
            if (person is null)
            {
                continue;
            }

            person.Sources |= PersonSource.Correspondent;
            person.CorrespondentName ??= correspondent.Address.Name;
            person.MessageCount += Math.Max(0, correspondent.MessageCount);
            person.LastContact = person.LastContact is { } last && last >= correspondent.LastContact ? last : correspondent.LastContact;
        }

        return [.. merger.People
            .Select(p => p.ToPerson(Score(p, now)))
            .OrderByDescending(static p => p.RelevanceScore)
            .ThenBy(static p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.ScoredEmailAddresses[0].Address, StringComparer.Ordinal)];
    }

    private static double Score(Builder person, DateTimeOffset now)
    {
        var score = 0d;
        if (person.Sources.HasFlag(PersonSource.Correspondent))
        {
            score += FrequencyWeight * Math.Min(person.MessageCount, FrequencySaturation) / FrequencySaturation;
            if (person.LastContact is { } last)
            {
                var ageDays = Math.Max(0, (now - last).TotalDays);
                score += RecencyWeight * Math.Pow(0.5, ageDays / RecencyHalfLifeDays);
            }
        }

        if (person.Sources.HasFlag(PersonSource.Contact))
        {
            score += ContactWeight;
        }

        if (person.Sources.HasFlag(PersonSource.Directory))
        {
            score += DirectoryWeight;
        }

        return Math.Round(score, 2);
    }

    /// <summary>A stable, opaque person id: a name-based UUID of the person's first address.</summary>
    internal static string IdFor(string address)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(address.ToLowerInvariant()));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5 (name-based), little-endian Guid layout
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes).ToString("D").ToUpperInvariant();
    }

    /// <summary>Indexes people by every address seen so far.</summary>
    private sealed class Merger
    {
        private readonly Dictionary<string, Builder> _byAddress = new(StringComparer.OrdinalIgnoreCase);

        public List<Builder> People { get; } = [];

        /// <summary>The person owning any of <paramref name="addresses"/>, created when none does; null when there are no usable addresses.</summary>
        public Builder? Find(IEnumerable<string> addresses)
        {
            var usable = addresses
                .Select(static a => a.Trim())
                .Where(static a => a.Contains('@', StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (usable.Count == 0)
            {
                return null;
            }

            var person = usable.Select(a => _byAddress.GetValueOrDefault(a)).FirstOrDefault(static p => p is not null);
            if (person is null)
            {
                person = new Builder();
                People.Add(person);
            }

            foreach (var address in usable.Where(a => !_byAddress.ContainsKey(a)))
            {
                _byAddress[address] = person;
                person.Addresses.Add(address);
            }

            return person;
        }
    }

    private sealed class Builder
    {
        public List<string> Addresses { get; } = [];

        public PersonSource Sources { get; set; }

        public string? ContactName { get; set; }

        public string? DirectoryName { get; set; }

        public string? CorrespondentName { get; set; }

        public string? GivenName { get; set; }

        public string? Surname { get; set; }

        public string? CompanyName { get; set; }

        public string? JobTitle { get; set; }

        public int MessageCount { get; set; }

        public DateTimeOffset? LastContact { get; set; }

        public Person ToPerson(double score)
        {
            var name = new[] { ContactName, DirectoryName, CorrespondentName }.FirstOrDefault(static n => !string.IsNullOrWhiteSpace(n))?.Trim()
                ?? Addresses[0];
            return new Person(IdFor(Addresses[0]), name, [.. Addresses.Select(a => new EmailAddress(a, name))])
            {
                GivenName = GivenName,
                Surname = Surname,
                CompanyName = CompanyName,
                JobTitle = JobTitle,
                RelevanceScore = score,
                Source = Sources,
            };
        }
    }
}
