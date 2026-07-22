using Grpc.Core;
using MagicOnion;
using MagicOnion.Server;

namespace FreqScene.Remote.Server;

public sealed class PresetService(RemoteBroadcaster broadcaster) : ServiceBase<IPresetService>, IPresetService
{
    public UnaryResult<PresetPayload> GetPresetAsync(string presetId)
    {
        var payload = broadcaster.GetPresetPayload(presetId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Unknown preset id '{presetId}'."));
        return UnaryResult.FromResult(payload);
    }
}
