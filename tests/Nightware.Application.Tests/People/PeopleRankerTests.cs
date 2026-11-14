using Nightware.Application.People;
using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.People;

public sealed class PeopleRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly PeopleRanker _ranker = new();

    private static Contact Contact(string name, params string[] addresses) =>
        new(new ContactId("c-" + name), name) { EmailAddresses = [.. addresses.Select(a => new EmailAddress(a, name))] };

    private static DirectoryUser User(string name, string mail) => new("u-" + name, name, mail) { Mail = mail };

    private static Correspondent Mailed(string address, int count, double daysAgo, string? name = null) =>
        new(new EmailAddress(address, name), count, Now.AddDays(-daysAgo));

    [Fact]
    public void A_frequent_current_correspondent_scores_the_maximum_correspondence()
    {
        var person = _ranker.Rank([], [], [Mailed("alex@example.com", 50, 0, "Alex Sender")], Now).ShouldHaveSingleItem();

        person.DisplayName.ShouldBe("Alex Sender");
        person.RelevanceScore.ShouldBe(PeopleRanker.FrequencyWeight + PeopleRanker.RecencyWeight);
        person.Source.ShouldBe(PersonSource.Correspondent);
        person.ScoredEmailAddresses.ShouldBe([new EmailAddress("alex@example.com", "Alex Sender")]);
    }

    [Fact]
    public void Recency_halves_every_half_life()
    {
        var person = _ranker.Rank([], [], [Mailed("a@example.com", 0, PeopleRanker.RecencyHalfLifeDays)], Now).ShouldHaveSingleItem();

        person.RelevanceScore.ShouldBe(PeopleRanker.RecencyWeight / 2);
    }

    [Fact]
    public void Contact_directory_and_correspondence_add_up_for_one_person()
    {
        var person = _ranker.Rank(
            [Contact("Pat (personal)", "pat@example.com")],
            [User("Pat Colleague", "PAT@example.com")],
            [Mailed("pat@EXAMPLE.com", 10, 0, "pat")],
            Now).ShouldHaveSingleItem();

        person.Source.ShouldBe(PersonSource.Contact | PersonSource.Directory | PersonSource.Correspondent);
        person.DisplayName.ShouldBe("Pat (personal)", "a saved contact's name wins");
        person.RelevanceScore.ShouldBe(PeopleRanker.ContactWeight + PeopleRanker.DirectoryWeight + 5 + PeopleRanker.RecencyWeight);
    }

    [Fact]
    public void Directory_name_beats_a_header_name_and_the_address_is_the_last_resort()
    {
        var people = _ranker.Rank([], [User("Sam Attendee", "sam@example.com")], [Mailed("sam@example.com", 1, 400, "sammy"), Mailed("x@example.com", 1, 400)], Now);

        people.Single(p => p.ScoredEmailAddresses[0].Address == "sam@example.com").DisplayName.ShouldBe("Sam Attendee");
        people.Single(p => p.ScoredEmailAddresses[0].Address == "x@example.com").DisplayName.ShouldBe("x@example.com");
    }

    [Fact]
    public void A_contact_with_several_addresses_is_one_person_that_absorbs_correspondence_to_any_of_them()
    {
        var person = _ranker.Rank([Contact("Pat", "pat@example.com", "pat.home@example.net")], [], [Mailed("pat.home@example.net", 20, 0)], Now).ShouldHaveSingleItem();

        person.ScoredEmailAddresses.Select(static e => e.Address).ShouldBe(["pat@example.com", "pat.home@example.net"]);
        person.Source.ShouldBe(PersonSource.Contact | PersonSource.Correspondent);
    }

    [Fact]
    public void Correspondence_to_several_addresses_of_one_person_accumulates()
    {
        var person = _ranker.Rank([Contact("Pat", "a@example.com", "b@example.com")], [], [Mailed("a@example.com", 10, 60), Mailed("b@example.com", 10, 0)], Now).ShouldHaveSingleItem();

        person.RelevanceScore.ShouldBe(PeopleRanker.ContactWeight + PeopleRanker.FrequencyWeight + PeopleRanker.RecencyWeight);
    }

    [Fact]
    public void Entries_without_an_address_are_skipped()
    {
        _ranker.Rank([new Contact(new ContactId("c"), "No mail")], [new DirectoryUser("u", "Service", "svc")], [], Now).ShouldBeEmpty();
    }

    [Fact]
    public void Ranks_by_score_then_name_then_address()
    {
        var people = _ranker.Rank(
            [Contact("Bea", "bea@example.com"), Contact("ann", "ann2@example.com"), Contact("Ann", "ann1@example.com")],
            [User("Zed", "zed@example.com")],
            [Mailed("top@example.com", 20, 0, "Top")],
            Now);

        people.Select(static p => p.ScoredEmailAddresses[0].Address)
            .ShouldBe(["top@example.com", "ann1@example.com", "ann2@example.com", "bea@example.com", "zed@example.com"]);
    }

    [Fact]
    public void Ids_are_stable_opaque_uuids_of_the_first_address()
    {
        var first = _ranker.Rank([], [User("Sam", "Sam@Example.com")], [], Now).Single().Id;
        var again = _ranker.Rank([], [User("Sam", "sam@example.com")], [], Now.AddDays(3)).Single().Id;

        first.ShouldBe(again);
        Guid.TryParse(first, out _).ShouldBeTrue();
        first.ShouldBe(first.ToUpperInvariant());
        first.ShouldNotContain("sam", Case.Insensitive);
    }
}
