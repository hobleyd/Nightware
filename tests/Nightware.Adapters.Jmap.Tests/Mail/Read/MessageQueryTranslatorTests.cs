using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

using QuerySort = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class MessageQueryTranslatorTests
{
    private static FilterNode.Compare Eq(string property, FilterValue value) => new FilterNode.Compare(property, CompareOp.Eq, value);

    private static FilterNode.Compare Ne(string property, string value) => new FilterNode.Compare(property, CompareOp.Ne, new FilterValue.Text(value));

    private static MessageQueryPlan Plan(ItemQuery query) => MessageQueryTranslator.Translate(query).Value;

    [Fact]
    public void The_default_is_newest_first_and_paged()
    {
        Plan(ItemQuery.Default).ShouldBe(new MessageQueryPlan.Query(null, [new Comparator("receivedAt", false)], Paged: true), new PlanComparer());
    }

    [Fact]
    public void Is_read_filters_on_the_seen_keyword()
    {
        var unread = (MessageQueryPlan.Query)Plan(new ItemQuery { Filter = Eq("isRead", new FilterValue.Bool(false)) });
        var read = (MessageQueryPlan.Query)Plan(new ItemQuery { Filter = new FilterNode.Compare("IsRead", CompareOp.Ne, new FilterValue.Bool(false)) });

        unread.Filter.ShouldBe(new EmailFilter { NotKeyword = "$seen" });
        read.Filter.ShouldBe(new EmailFilter { HasKeyword = "$seen" });
    }

    [Fact]
    public void Conversation_in_with_folder_exclusions_is_a_thread_expansion()
    {
        var filter = new FilterNode.And(
            new FilterNode.And(new FilterNode.In("conversationId", [new FilterValue.Text("t1"), new FilterValue.Text("t2")]), Ne("parentFolderId", "b")),
            Ne("parentFolderId", "c"));

        var plan = (MessageQueryPlan.Conversation)Plan(new ItemQuery { Filter = filter });

        plan.ThreadIds.ShouldBe(["t1", "t2"]);
        plan.ExcludedFolderIds.ShouldBe(["b", "c"]);
    }

    [Fact]
    public void Conversation_eq_is_a_thread_expansion_too()
    {
        ((MessageQueryPlan.Conversation)Plan(new ItemQuery { Filter = Eq("conversationId", new FilterValue.Text("t1")) })).ThreadIds.ShouldBe(["t1"]);
    }

    [Fact]
    public void Folder_exclusion_without_a_conversation_is_in_mailbox_other_than()
    {
        var plan = (MessageQueryPlan.Query)Plan(new ItemQuery { Filter = Ne("parentFolderId", "b") });

        plan.Filter!.InMailboxOtherThan.ShouldBe(["b"]);
    }

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Untranslatable_filters_are_a_validation_error(FilterNode filter)
    {
        MessageQueryTranslator.Translate(new ItemQuery { Filter = filter }).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    public static TheoryData<FilterNode> Unsupported() =>
    [
        Eq("subject", new FilterValue.Text("x")),
        new FilterNode.Or(Eq("isRead", new FilterValue.Bool(true)), Eq("isRead", new FilterValue.Bool(false))),
        new FilterNode.Compare("conversationId", CompareOp.Ne, new FilterValue.Text("t")),
        new FilterNode.And(Eq("conversationId", new FilterValue.Text("t")), Eq("isRead", new FilterValue.Bool(false))),
        new FilterNode.In("conversationId", [new FilterValue.Number(1)]),
    ];

    [Fact]
    public void A_conversation_filter_with_search_is_rejected()
    {
        var query = new ItemQuery { Filter = Eq("conversationId", new FilterValue.Text("t")), Search = new SearchQuery([new SearchTerm(SearchField.Any, SearchOp.Contains, "x")]) };

        MessageQueryTranslator.Translate(query).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Order_by_maps_received_and_sent_and_rejects_others()
    {
        ((MessageQueryPlan.Query)Plan(new ItemQuery { OrderBy = [new OrderBy("receivedDateTime", QuerySort.Ascending), new OrderBy("sentDateTime", QuerySort.Descending)] }))
            .Sort.ShouldBe([new Comparator("receivedAt", true), new Comparator("sentAt", false)]);
        MessageQueryTranslator.Translate(new ItemQuery { OrderBy = [new OrderBy("subject", QuerySort.Ascending)] }).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Search_is_not_paged_and_ands_its_terms()
    {
        var search = new SearchQuery(
        [
            new SearchTerm(SearchField.From, SearchOp.Contains, "alex@example.com"),
            new SearchTerm(SearchField.HasAttachments, SearchOp.Equals, "true"),
            new SearchTerm(SearchField.Any, SearchOp.Contains, "budget"),
        ]);

        var plan = (MessageQueryPlan.Query)Plan(new ItemQuery { Search = search });

        plan.Paged.ShouldBeFalse();
        plan.Filter.ShouldBe(EmailFilter.And(new EmailFilter { From = "alex@example.com" }, new EmailFilter { HasAttachment = true }, new EmailFilter { Text = "budget" }), new FilterComparer());
    }

    [Fact]
    public void Search_terms_map_to_jmap_conditions()
    {
        static EmailFilter One(SearchField field, string value, SearchOp op = SearchOp.Contains) =>
            MessageQueryTranslator.Search(new SearchQuery([new SearchTerm(field, op, value)]));

        One(SearchField.To, "sam").ShouldBe(new EmailFilter { To = "sam" });
        One(SearchField.Cc, "sam").ShouldBe(new EmailFilter { Cc = "sam" });
        One(SearchField.Subject, "invoice").ShouldBe(new EmailFilter { Subject = "invoice" });
        One(SearchField.Body, "q2").ShouldBe(new EmailFilter { Body = "q2" });
        One(SearchField.Attachment, "report").ShouldBe(new EmailFilter { Text = "report" });
        One(SearchField.HasAttachments, "no").ShouldBe(new EmailFilter { HasAttachment = false });
        One(SearchField.HasAttachments, "maybe").ShouldBe(new EmailFilter { Text = "maybe" });
        One(SearchField.Participants, "sam").Conditions!.Count.ShouldBe(4);
    }

    [Fact]
    public void Dates_become_received_ranges()
    {
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        static EmailFilter Received(SearchOp op) => MessageQueryTranslator.Search(new SearchQuery([new SearchTerm(SearchField.Received, op, "2026-01-01")]));

        Received(SearchOp.GreaterOrEqual).ShouldBe(new EmailFilter { After = day });
        Received(SearchOp.GreaterThan).ShouldBe(new EmailFilter { After = day.AddDays(1) });
        Received(SearchOp.LessThan).ShouldBe(new EmailFilter { Before = day });
        Received(SearchOp.LessOrEqual).ShouldBe(new EmailFilter { Before = day.AddDays(1) });
        Received(SearchOp.Equals).ShouldBe(new EmailFilter { After = day, Before = day.AddDays(1) });
        MessageQueryTranslator.Search(new SearchQuery([new SearchTerm(SearchField.Sent, SearchOp.Equals, "soon")])).ShouldBe(new EmailFilter { Text = "soon" });
    }

    [Fact]
    public void Negation_and_or_groups_are_kept()
    {
        var search = new SearchQuery([new SearchTerm(SearchField.From, SearchOp.Contains, "spam") { Negated = true }])
        {
            AnyOf = [[new SearchTerm(SearchField.From, SearchOp.Contains, "a"), new SearchTerm(SearchField.From, SearchOp.Contains, "b")]],
        };

        var filter = MessageQueryTranslator.Search(search);

        filter.Operator.ShouldBe("AND");
        filter.Conditions![0].Operator.ShouldBe("NOT");
        filter.Conditions[1].Operator.ShouldBe("OR");
        filter.Conditions[1].Conditions!.Select(c => c.From).ShouldBe(["a", "b"]);
    }

    /// <summary>Records holding lists compare by reference; these compare the parts that matter.</summary>
    private sealed class PlanComparer : IEqualityComparer<MessageQueryPlan>
    {
        public bool Equals(MessageQueryPlan? x, MessageQueryPlan? y) =>
            x is MessageQueryPlan.Query a && y is MessageQueryPlan.Query b && a.Filter == b.Filter && a.Paged == b.Paged && a.Sort.SequenceEqual(b.Sort);

        public int GetHashCode(MessageQueryPlan obj) => 0;
    }

    private sealed class FilterComparer : IEqualityComparer<EmailFilter>
    {
        public bool Equals(EmailFilter? x, EmailFilter? y) =>
            x is not null && y is not null && x.Operator == y.Operator
            && (x.Conditions is null ? y.Conditions is null : y.Conditions is not null && x.Conditions.SequenceEqual(y.Conditions));

        public int GetHashCode(EmailFilter obj) => 0;
    }
}
