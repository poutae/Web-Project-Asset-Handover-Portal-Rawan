using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Portal.Api.Realtime;

namespace Portal.Tests.Infrastructure;

/// <summary>A signed-in SignalR connection that records every change event it receives.</summary>
public sealed class RealtimeListener : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Channel<RealtimeEvent> _events = Channel.CreateUnbounded<RealtimeEvent>();

    private RealtimeListener(HubConnection connection)
    {
        _connection = connection;
        _connection.On<RealtimeEvent>(PortalHub.ChangedMethod, change => _events.Writer.TryWrite(change));
    }

    public static async Task<RealtimeListener> ConnectAsync(PortalApiFactory factory, ApiClient signedIn)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, PortalHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Cookie"] = signedIn.CookieHeader;
            })
            .Build();

        var listener = new RealtimeListener(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return listener;
    }

    /// <summary>Waits for an event matching the predicate; returns null if none arrives in time.</summary>
    public async Task<RealtimeEvent?> WaitForAsync(Func<RealtimeEvent, bool> predicate, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (await _events.Reader.WaitToReadAsync(cts.Token))
            {
                while (_events.Reader.TryRead(out var change))
                {
                    if (predicate(change))
                    {
                        return change;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
