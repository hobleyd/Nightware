using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;

namespace Nightware.Adapters.Jmap.Tests.Core.Requests;

public sealed class JmapRequestPlannerTests
{
    private static string[] Ids(int count) => [.. Enumerable.Range(1, count).Select(i => $"e{i}")];

    private static string[][] Shape(JmapRequestPlan plan) =>
        [.. plan.Batches.Select(b => b.Select(c => c.WireCallId).ToArray())];

    [Fact]
    public void Small_requests_are_one_batch()
    {
        var request = new JmapRequestBuilder();
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        var plan = JmapRequestPlanner.Plan(request.Invocations, 500, 16);

        Shape(plan).ShouldBe([["c0", "c1"]]);
        plan.WireIds["c1"].ShouldBe(["c1"]);
        plan.IsSplit.ShouldBeFalse();
    }

    [Fact]
    public void Gets_are_chunked_to_max_objects()
    {
        var request = new JmapRequestBuilder();
        request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = Ids(1001) });

        var plan = JmapRequestPlanner.Plan(request.Invocations, 500, 16);

        plan.WireIds["c0"].ShouldBe(["c0", "c0.1", "c0.2"]);
        plan.Batches[0].Select(c => c.Logical.Arguments["ids"]!.AsArray().Count).ShouldBe([500, 500, 1]);
        plan.IsSplit.ShouldBeFalse("chunks of one /get still travel in one HTTP request");
        plan.Batches[0][2].Logical.Arguments["ids"]![0]!.GetValue<string>().ShouldBe("e1001");
        request.Invocations[0].Arguments["ids"]!.AsArray().Count.ShouldBe(1001);
    }

    [Fact]
    public void Referenced_gets_are_not_chunked()
    {
        var request = new JmapRequestBuilder();
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = Ids(10) });
        request.Add(JmapMethods.ThreadGet, new GetRequest("c"), emails.Ref("ids", JmapPaths.ListThreadIds));

        var plan = JmapRequestPlanner.Plan(request.Invocations, 3, 16);

        Shape(plan).ShouldBe([["c0", "c1"]]);
    }

    [Fact]
    public void Non_get_calls_and_back_referenced_ids_are_not_chunked()
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c"));
        request.Add(JmapMethods.EmailGet, new EmailGetRequest("c"), query.Ref("ids", JmapPaths.Ids));
        request.Add(JmapMethods.EmailSet, new SetRequest<Email>("c") { Destroy = Ids(10) });

        Shape(JmapRequestPlanner.Plan(request.Invocations, 3, 16)).ShouldBe([["c0", "c1", "c2"]]);
    }

    [Fact]
    public void Calls_beyond_the_limit_are_split_into_batches()
    {
        var request = new JmapRequestBuilder();
        for (var i = 0; i < 5; i++)
        {
            request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        }

        var plan = JmapRequestPlanner.Plan(request.Invocations, 500, 2);
        Shape(plan).ShouldBe([["c0", "c1"], ["c2", "c3"], ["c4"]]);
        plan.IsSplit.ShouldBeTrue();
    }

    [Fact]
    public void Batches_never_separate_a_reference_from_its_target()
    {
        var request = new JmapRequestBuilder();
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c"));
        request.Add(JmapMethods.EmailGet, new EmailGetRequest("c"), query.Ref("ids", JmapPaths.Ids));
        request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        // A cut after c1 would strand c2's reference, so the cut moves before c1.
        Shape(JmapRequestPlanner.Plan(request.Invocations, 500, 2)).ShouldBe([["c0"], ["c1", "c2"], ["c3"]]);
    }

    [Fact]
    public void Unsplittable_chains_are_sent_whole()
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c"));
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c"), query.Ref("ids", JmapPaths.Ids));
        request.Add(JmapMethods.ThreadGet, new GetRequest("c"), emails.Ref("ids", JmapPaths.ListThreadIds));
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));

        Shape(JmapRequestPlanner.Plan(request.Invocations, 500, 2)).ShouldBe([["c0", "c1", "c2"], ["c3"]]);
    }

    [Fact]
    public void Chunks_count_against_max_calls()
    {
        var request = new JmapRequestBuilder();
        request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = Ids(5) });

        Shape(JmapRequestPlanner.Plan(request.Invocations, 2, 2)).ShouldBe([["c0", "c0.1"], ["c0.2"]]);
    }

    [Fact]
    public void Limits_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => JmapRequestPlanner.Plan([], 0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => JmapRequestPlanner.Plan([], 1, 0));
    }
}
