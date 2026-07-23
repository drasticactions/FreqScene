using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;

namespace FreqScene.Remote.Server;

public sealed class PresetService(RemoteBroadcaster broadcaster, PairingManager pairing)
    : ServiceBase<IPresetService>, IPresetService
{
    public UnaryResult<PresetPayload> GetPresetAsync(string presetId, string authToken)
    {
        if (pairing.ValidateToken(authToken) is null)
        {
            throw new ReturnStatusException(
                StatusCode.Unauthenticated, "This device is not paired with the server.");
        }

        var payload = broadcaster.GetPresetPayload(presetId)
            ?? throw new ReturnStatusException(StatusCode.NotFound, $"Unknown preset id '{presetId}'.");
        return UnaryResult.FromResult(payload);
    }
}
