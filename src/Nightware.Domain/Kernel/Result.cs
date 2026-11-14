using System.Diagnostics.CodeAnalysis;

namespace Nightware.Domain.Kernel;

/// <summary>Outcome of an operation that produces no value.</summary>
public readonly record struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(Error error) => Failure(error);

    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }
}

/// <summary>Outcome of an operation that produces a <typeparamref name="T"/> or an <see cref="Kernel.Error"/>.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value. Throws when the result is a failure — check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {Error.Code} — {Error.Message}");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result<TOut>.Success(map(_value!)) : Result<TOut>.Failure(Error);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value!) : Result<TOut>.Failure(Error);
    }

    public async Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? await bind(_value!).ConfigureAwait(false) : Result<TOut>.Failure(Error);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }

    /// <summary>Drops the value, keeping success/failure.</summary>
    public Result ToResult() => IsSuccess ? Result.Success() : Result.Failure(Error);
}
