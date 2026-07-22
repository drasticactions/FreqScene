using Grpc.Core;
using MagicOnion.Server.Hubs;

namespace FreqScene.Remote.Server;

public sealed class VisualizerHub(RemoteBroadcaster broadcaster)
    : StreamingHubBase<IVisualizerHub, IVisualizerHubReceiver>, IVisualizerHub
{
    private bool _joined;

    public Task<SessionSnapshot> JoinAsync(JoinRequest request)
    {
        if (request.ProtocolVersion != RemoteProtocol.Version)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Protocol version mismatch: server={RemoteProtocol.Version}, client={request.ProtocolVersion}."));
        }

        _joined = true;
        return Task.FromResult(broadcaster.Register(ConnectionId, Client, request));
    }

    public Task LeaveAsync()
    {
        if (_joined)
        {
            _joined = false;
            broadcaster.Unregister(ConnectionId);
        }

        return Task.CompletedTask;
    }

    protected override ValueTask OnDisconnected()
    {
        if (_joined)
        {
            broadcaster.Unregister(ConnectionId);
        }

        return CompletedTask;
    }
}
