using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

public enum AudioChannels
{
    /// <summary>Single-channel audio.</summary>
    Mono = (int)projectm_channels.PROJECTM_MONO,

    /// <summary>Interleaved two-channel audio (LRLRLR...).</summary>
    Stereo = (int)projectm_channels.PROJECTM_STEREO,
}
