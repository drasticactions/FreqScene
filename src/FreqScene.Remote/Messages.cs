using MessagePack;

namespace FreqScene.Remote;

[MessagePackObject]
public sealed class JoinRequest
{
    [Key(0)]
    public int ProtocolVersion { get; set; } = RemoteProtocol.Version;

    [Key(1)]
    public string ClientName { get; set; } = "";

    [Key(2)]
    public string DeviceModel { get; set; } = "";
}

[MessagePackObject]
public sealed class SessionSnapshot
{
    [Key(0)]
    public string ServerName { get; set; } = "";

    [Key(1)]
    public PresetInfo? CurrentPreset { get; set; }

    [Key(2)]
    public double PresetDurationSeconds { get; set; }

    [Key(3)]
    public bool PresetLocked { get; set; }

    [Key(4)]
    public int SampleRate { get; set; } = RemoteProtocol.SampleRate;

    [Key(5)]
    public int Channels { get; set; } = RemoteProtocol.Channels;
}

[MessagePackObject]
public sealed class PresetInfo
{
    [Key(0)]
    public string Id { get; set; } = "";

    [Key(1)]
    public string Name { get; set; } = "";

    [Key(2)]
    public int ByteSize { get; set; }
}

[MessagePackObject]
public sealed class PlaybackSettings
{
    [Key(0)]
    public double PresetDurationSeconds { get; set; }

    [Key(1)]
    public bool PresetLocked { get; set; }
}

[MessagePackObject]
public sealed class PcmChunk
{
    [Key(0)]
    public uint Sequence { get; set; }

    [Key(1)]
    public float[] Samples { get; set; } = [];
}

[MessagePackObject]
public sealed class PresetPayload
{
    [Key(0)]
    public string Id { get; set; } = "";

    [Key(1)]
    public string Name { get; set; } = "";

    [Key(2)]
    public byte[] Content { get; set; } = [];
}
