namespace FreqScene.Remote.Server;

public sealed class PairedDevice
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string DeviceModel { get; set; } = "";

    public string TokenHash { get; set; } = "";

    public DateTimeOffset PairedAt { get; set; }
}
