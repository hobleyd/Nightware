using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>
/// How to make the façade answer one fixture: configure the port fakes the endpoint will call
/// and, optionally, relax the comparison where Graph emits fields the façade deliberately does not.
/// </summary>
public sealed record ConformanceArrangement(string OperationId, Action<PortFakes> Arrange)
{
    /// <summary>
    /// Paths removed from both bodies before comparing, <c>/</c>-separated with <c>*</c> for every array
    /// element or object member, e.g. <c>value/*/@odata.type</c>. Keys containing dots are fine.
    /// </summary>
    public IReadOnlyList<string> IgnorePaths { get; init; } = [];

    /// <summary>Last-chance custom normalisation of (expected, actual) before comparison.</summary>
    public Action<JsonNode, JsonNode>? Normalise { get; init; }

    /// <summary>Harness self-tests only: extra endpoints for an example fixture. Feature fixtures never use this.</summary>
    public Action<RouteGroupBuilder>? MapEndpoints { get; init; }

    /// <summary>Extra services (e.g. a fixed <see cref="TimeProvider"/>).</summary>
    public Action<IServiceCollection>? Services { get; init; }
}

/// <summary>
/// Implemented once per feature (e.g. <c>Arrangements/MailArrangements.cs</c>); discovered by reflection,
/// so feature phases add their own file and never edit a shared one.
/// </summary>
public interface IConformanceArrangements
{
    IEnumerable<ConformanceArrangement> Arrangements();
}

/// <summary>All arrangements in this assembly, by operation id.</summary>
public static class ArrangementRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, ConformanceArrangement>> All = new(Discover);

    public static IReadOnlyDictionary<string, ConformanceArrangement> ByOperationId => All.Value;

    public static ConformanceArrangement? Find(string operationId) =>
        All.Value.TryGetValue(operationId, out var arrangement) ? arrangement : null;

    private static Dictionary<string, ConformanceArrangement> Discover()
    {
        var result = new Dictionary<string, ConformanceArrangement>(StringComparer.Ordinal);
        var providers = typeof(ArrangementRegistry).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(IConformanceArrangements).IsAssignableFrom(t));
        foreach (var provider in providers)
        {
            var instance = (IConformanceArrangements)Activator.CreateInstance(provider)!;
            foreach (var arrangement in instance.Arrangements())
            {
                if (!result.TryAdd(arrangement.OperationId, arrangement))
                {
                    throw new InvalidOperationException($"Operation '{arrangement.OperationId}' is arranged twice (second in {provider.Name}).");
                }
            }
        }

        return result;
    }
}

/// <summary>
/// NSubstitute fakes for Application ports. <see cref="For{T}"/> creates (once) and registers a substitute;
/// every port not asked for keeps the 501 stub, so an unconfigured call fails loudly rather than returning
/// <c>default(Result&lt;T&gt;)</c> (which is a success with a null value).
/// </summary>
public sealed class PortFakes
{
    private readonly Dictionary<Type, object> _fakes = [];

    public T For<T>()
        where T : class
    {
        if (!_fakes.TryGetValue(typeof(T), out var fake))
        {
            fake = Substitute.For<T>();
            _fakes[typeof(T)] = fake;
        }

        return (T)fake;
    }

    /// <summary>Registers a hand-written implementation instead of a substitute.</summary>
    public void Use<T>(T implementation)
        where T : class =>
        _fakes[typeof(T)] = implementation;

    internal IReadOnlyDictionary<Type, object> All => _fakes;

    internal void Register(IServiceCollection services)
    {
        foreach (var (type, fake) in _fakes)
        {
            services.AddSingleton(type, fake);
        }
    }
}
