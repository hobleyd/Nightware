using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Core.Errors;

public sealed class SetResultsTests
{
    private static readonly SetResponse<Mailbox> Response = new()
    {
        NewState = "2",
        Created = new() { ["k1"] = new Mailbox { Id = "M1" } },
        NotCreated = new() { ["k2"] = new SetError { Type = "invalidProperties", Properties = ["name"] } },
        Updated = new() { ["M2"] = null },
        NotUpdated = new() { ["M3"] = new SetError { Type = "notFound" } },
        Destroyed = ["M4"],
        NotDestroyed = new() { ["M5"] = new SetError { Type = "mailboxHasChild" } },
    };

    [Fact]
    public void Created_returns_the_server_object_or_the_mapped_error()
    {
        Response.Created("k1").Value.Id.ShouldBe("M1");
        Response.Created("k2").Error!.Kind.ShouldBe(ErrorKind.Validation);
        Response.Created("k3").Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public void Updated_reports_success_or_the_mapped_error()
    {
        Response.Updated("M2").IsSuccess.ShouldBeTrue();
        Response.Updated("M3").Error!.Kind.ShouldBe(ErrorKind.NotFound);
        Response.Updated("M9").Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public void Destroyed_reports_success_or_the_mapped_error()
    {
        Response.Destroyed("M4").IsSuccess.ShouldBeTrue();
        Response.Destroyed("M5").Error!.Kind.ShouldBe(ErrorKind.Conflict);
        Response.Destroyed("M9").Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public void AllSucceeded_returns_the_first_failure()
    {
        Response.AllSucceeded().Error!.Kind.ShouldBe(ErrorKind.Validation);
        new SetResponse<Mailbox> { Destroyed = ["x"] }.AllSucceeded().IsSuccess.ShouldBeTrue();
        new SetResponse<Mailbox> { NotDestroyed = new() { ["x"] = new SetError { Type = "forbidden" } } }.AllSucceeded().Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void Import_results_map_the_same_way()
    {
        var import = new EmailImportResponse
        {
            Created = new() { ["a"] = new Email { Id = "E1" } },
            NotCreated = new() { ["b"] = new SetError { Type = "overQuota" } },
        };

        import.Created("a").Value.Id.ShouldBe("E1");
        import.Created("b").Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }
}
