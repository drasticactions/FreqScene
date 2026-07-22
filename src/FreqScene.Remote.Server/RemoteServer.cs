using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FreqScene.Remote.Server;

public sealed class RemoteServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RemoteServer(WebApplication app, RemoteBroadcaster broadcaster)
    {
        _app = app;
        Broadcaster = broadcaster;
    }

    public RemoteBroadcaster Broadcaster { get; }

    public static async Task<RemoteServer> StartAsync(int port, string? serverName, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Dual-stack: clients resolving the Bonjour host may dial IPv4 or IPv6.
            options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddMagicOnion(options =>
        {
            options.EnableStreamingHubHeartbeat = true;
            options.StreamingHubHeartbeatInterval = TimeSpan.FromSeconds(5);
            options.StreamingHubHeartbeatTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddSingleton<RemoteBroadcaster>();

        var app = builder.Build();
        app.MapMagicOnionService(typeof(VisualizerHub), typeof(PresetService));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var broadcaster = app.Services.GetRequiredService<RemoteBroadcaster>();
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            broadcaster.ServerName = serverName;
        }

        return new RemoteServer(app, broadcaster);
    }

    public async ValueTask DisposeAsync()
    {
        Broadcaster.BroadcastShutdown();
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(stopTimeout.Token).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
