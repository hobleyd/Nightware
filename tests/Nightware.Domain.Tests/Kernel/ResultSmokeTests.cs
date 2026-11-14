using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tests.Kernel;

public sealed class ResultSmokeTests
{
    [Fact]
    public void Failure_carries_its_error()
    {
        Result<int> result = Error.NotFound("missing");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
