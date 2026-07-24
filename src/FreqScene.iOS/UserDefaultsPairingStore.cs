using Foundation;
using FreqScene.Remote.Client;

namespace FreqScene.iOS;

public sealed class UserDefaultsPairingStore : PairingStore
{
    private const string Key = "FreqScene.Pairings";

    protected override string? ReadPayload() =>
        NSUserDefaults.StandardUserDefaults.StringForKey(Key);

    protected override void WritePayload(string json) =>
        NSUserDefaults.StandardUserDefaults.SetString(json, Key);
}
