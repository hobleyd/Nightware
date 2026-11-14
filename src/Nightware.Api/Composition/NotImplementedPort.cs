using System.Collections.Concurrent;
using System.Reflection;

using Nightware.Domain.Kernel;

namespace Nightware.Api.Composition;

/// <summary>
/// A stand-in for an Application port no adapter has registered yet. Every method that returns
/// <see cref="Result"/>/<see cref="Result{T}"/> (directly, or as <see cref="Task{T}"/>/<see cref="ValueTask{T}"/>)
/// answers <see cref="ErrorKind.Unsupported"/>, so the route behind it is a clean <c>501 notImplemented</c>.
/// Any other method throws <see cref="NotSupportedException"/>.
/// </summary>
/// <typeparam name="T">The port interface.</typeparam>
public class NotImplementedPort<T> : DispatchProxy
    where T : class
{
    private static readonly ConcurrentDictionary<MethodInfo, Func<object?>> Answers = new();

    /// <summary>Creates the stub for <typeparamref name="T"/>.</summary>
    public static T Create()
    {
        if (!typeof(T).IsInterface)
        {
            throw new InvalidOperationException($"{typeof(T).Name} is not an interface.");
        }

        return Create<T, NotImplementedPort<T>>();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return Answers.GetOrAdd(targetMethod, Build)();
    }

    private static Func<object?> Build(MethodInfo method)
    {
        var error = Error.Unsupported($"{typeof(T).Name}.{method.Name} is not implemented yet.");
        var returnType = method.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            && FailureFor(returnType.GetGenericArguments()[0], error) is { } taskValue)
        {
            var task = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [taskValue]);
            return () => task;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)
            && FailureFor(returnType.GetGenericArguments()[0], error) is { } valueTaskValue)
        {
            var valueTask = Activator.CreateInstance(returnType, valueTaskValue);
            return () => valueTask;
        }

        if (FailureFor(returnType, error) is { } value)
        {
            return () => value;
        }

        return () => throw new NotSupportedException(error.Message);
    }

    /// <summary>A boxed failed <see cref="Result"/>/<see cref="Result{T}"/>, or null when the type is neither.</summary>
    private static object? FailureFor(Type type, Error error)
    {
        if (type == typeof(Result))
        {
            return Result.Failure(error);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>))
        {
            return type.GetMethod(nameof(Result<int>.Failure), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [error]);
        }

        return null;
    }
}
