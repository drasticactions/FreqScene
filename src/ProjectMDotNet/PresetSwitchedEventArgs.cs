namespace ProjectMDotNet;

public sealed class PresetSwitchedEventArgs : EventArgs
{
    internal PresetSwitchedEventArgs(bool isHardCut, uint index)
    {
        IsHardCut = isHardCut;
        Index = index;
    }

    /// <summary>True if the switch was a hard cut.</summary>
    public bool IsHardCut { get; }

    /// <summary>Playlist index of the new preset.</summary>
    public uint Index { get; }
}
