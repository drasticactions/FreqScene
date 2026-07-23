namespace FreqScene.Remote;

public static class RemoteProtocol
{
    public const int Version = 2;

    public const string BonjourServiceType = "_freqscene._tcp";

    public const int DefaultPort = 39501;

    public const int SampleRate = 44100;

    public const int Channels = 2;

    public const int PcmChunkSamples = 2048;
}
