using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Nightware.Integration.Tests.Subscriptions;

/// <summary>A throwaway Postgres for the subscription store (the dev stack's own database is not assumed).</summary>
internal sealed class SubscriptionDatabase : IAsyncDisposable
{
    private readonly IContainer _container;

    private SubscriptionDatabase(IContainer container) => _container = container;

    public string ConnectionString =>
        $"Host=127.0.0.1;Port={_container.GetMappedPublicPort(5432)};Username=postgres;Password=nightware-test;Database=postgres";

    public static async Task<SubscriptionDatabase> StartAsync(CancellationToken cancellationToken)
    {
        var container = new ContainerBuilder("postgres:17.12-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "nightware-test")
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "postgres"))
            .Build();
        await container.StartAsync(cancellationToken);
        return new SubscriptionDatabase(container);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// A subscriber's notification endpoint on loopback: it answers Graph's validation handshake and keeps every
/// notification it receives. Nightware may send to it only because the test names its origin as test-only.
/// </summary>
internal sealed class NotificationReceiver : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public NotificationReceiver()
    {
        var port = FreePort();
        Origin = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Origin { get; }

    public Uri NotifyUrl => new(Origin + "/notify");

    public ConcurrentQueue<string> Notifications { get; } = new();

    public int Validations;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            var token = context.Request.QueryString["validationToken"];
            if (token is not null)
            {
                Interlocked.Increment(ref Validations);
                context.Response.ContentType = "text/plain";
                var body = Encoding.UTF8.GetBytes(token);
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.StatusCode = 200;
            }
            else
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                Notifications.Enqueue(await reader.ReadToEndAsync());
                context.Response.StatusCode = 202;
            }

            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
