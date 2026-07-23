using MagicOnion;

namespace FreqScene.Remote;

public interface IPresetService : IService<IPresetService>
{
    UnaryResult<PresetPayload> GetPresetAsync(string presetId, string authToken);
}
