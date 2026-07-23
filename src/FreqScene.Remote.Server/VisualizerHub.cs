using Grpc.Core;
using MagicOnion;
using MagicOnion.Server.Hubs;

namespace FreqScene.Remote.Server;

public sealed class VisualizerHub(RemoteBroadcaster broadcaster, PairingManager pairing)
    : StreamingHubBase<IVisualizerHub, IVisualizerHubReceiver>, IVisualizerHub
{
    private bool _joined;

    public Task<SessionSnapshot> JoinAsync(JoinRequest request)
    {
        if (request.ProtocolVersion != RemoteProtocol.Version)
        {
            throw new ReturnStatusException(
                StatusCode.FailedPrecondition,
                $"Protocol version mismatch: server={RemoteProtocol.Version}, client={request.ProtocolVersion}.");
        }

        var device = pairing.ValidateToken(request.AuthToken)
            ?? throw new ReturnStatusException(
                StatusCode.Unauthenticated, "This device is not paired with the server.");

        _joined = true;
        return Task.FromResult(broadcaster.Register(ConnectionId, Client, request, device.Id));
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
