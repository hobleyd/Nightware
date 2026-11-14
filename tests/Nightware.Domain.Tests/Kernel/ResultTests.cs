using Nightware.Domain.Kernel;

namespace Nightware.Domain.Tests.Kernel;

public sealed class ResultTests
{
    private static readonly Error Missing = Error.NotFound("missing", "id");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Match(() => "ok", _ => "fail").ShouldBe("ok");
    }

    [Fact]
    public void Failure_carries_its_error_and_converts_implicitly()
    {
        Result explicitFailure = Result.Failure(Missing);
        Result implicitFailure = Missing;

        explicitFailure.IsSuccess.ShouldBeFalse();
        implicitFailure.Error.ShouldBe(Missing);
        implicitFailure.Match(() => "ok", e => e.Code).ShouldBe("itemNotFound");
    }

    [Fact]
    public void Failure_requires_an_error()
    {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    [Fact]
    public void Match_requires_both_branches()
    {
        Should.Throw<ArgumentNullException>(() => Result.Success().Match<int>(null!, _ => 1));
        Should.Throw<ArgumentNullException>(() => Result.Success().Match(() => 1, null!));
        Should.Throw<ArgumentNullException>(() => Result<int>.Success(1).Match<int>(null!, _ => 1));
        Should.Throw<ArgumentNullException>(() => Result<int>.Success(1).Match(v => v, null!));
    }

    [Fact]
    public void Generic_success_exposes_its_value()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
        result.Match(v => v + 1, _ => 0).ShouldBe(43);
        result.ToResult().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Generic_failure_throws_on_value_and_keeps_the_error()
    {
        Result<int> result = Missing;

        var ex = Should.Throw<InvalidOperationException>(() => result.Value);
        ex.Message.ShouldContain("itemNotFound");
        result.Match(v => v, _ => -1).ShouldBe(-1);
        result.ToResult().Error.ShouldBe(Missing);
    }

    [Fact]
    public void Map_and_bind_short_circuit_on_failure()
    {
        Result<int> ok = 2;
        Result<int> failed = Missing;

        ok.Map(v => v * 10).Value.ShouldBe(20);
        failed.Map(v => v * 10).Error.ShouldBe(Missing);
        ok.Bind(v => Result<string>.Success($"#{v}")).Value.ShouldBe("#2");
        ok.Bind(_ => Result<string>.Failure(Missing)).Error.ShouldBe(Missing);
        failed.Bind(_ => Result<string>.Success("never")).Error.ShouldBe(Missing);
        Should.Throw<ArgumentNullException>(() => ok.Map<int>(null!));
        Should.Throw<ArgumentNullException>(() => ok.Bind<int>(null!));
    }

    [Fact]
    public async Task BindAsync_short_circuits_on_failure()
    {
        Result<int> ok = 2;
        Result<int> failed = Missing;
        var called = false;

        (await ok.BindAsync(v => Task.FromResult(Result<int>.Success(v + 1)))).Value.ShouldBe(3);
        (await failed.BindAsync(v =>
        {
            called = true;
            return Task.FromResult(Result<int>.Success(v));
        })).Error.ShouldBe(Missing);
        called.ShouldBeFalse();
        await Should.ThrowAsync<ArgumentNullException>(() => ok.BindAsync<int>(null!));
    }

    [Fact]
    public void Default_result_is_an_uninitialised_failure()
    {
        // An unconfigured fake returns default(Result); it must never read as success.
        default(Result).IsSuccess.ShouldBeFalse();
        default(Result).Error.ShouldBe(Result.Uninitialised);
    }

    [Fact]
    public void Default_generic_result_is_an_uninitialised_failure()
    {
        default(Result<string>).IsSuccess.ShouldBeFalse();
        default(Result<string>).Error.ShouldBe(Result.Uninitialised);
        Should.Throw<InvalidOperationException>(() => default(Result<string>).Value);
    }

    [Fact]
    public void Success_with_a_null_value_is_still_a_success()
    {
        Result<string?> result = Result<string?>.Success(null);

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }
}
