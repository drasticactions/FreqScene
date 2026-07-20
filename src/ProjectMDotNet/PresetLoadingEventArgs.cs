namespace ProjectMDotNet;

public sealed class PresetLoadingEventArgs : EventArgs
{
    public PresetLoadingEventArgs()
    {
    }

    internal PresetLoadingEventArgs(uint index, string filename, bool isHardCut)
    {
        Index = index;
        Filename = filename;
        IsHardCut = isHardCut;
    }

    /// <summary>Playlist index of the preset about to load.</summary>
    public uint Index { get; }

    /// <summary>File about to be loaded.</summary>
    public string Filename { get; }

    /// <summary>True if the pending switch is a hard cut.</summary>
    public bool IsHardCut { get; }

    /// <summary>
    /// Set to true to prevent the playlist from loading the preset (the
    /// playlist position still advances; the app may load something itself).
    /// </summary>
    public bool Cancel { get; set; }
}
