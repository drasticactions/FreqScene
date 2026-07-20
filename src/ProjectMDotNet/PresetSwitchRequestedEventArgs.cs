namespace ProjectMDotNet;

public sealed class PresetSwitchRequestedEventArgs : EventArgs
{
    internal PresetSwitchRequestedEventArgs(bool isHardCut) => IsHardCut = isHardCut;

    /// <summary>True for an immediate (hard) cut, false for a blended (soft) transition.</summary>
    public bool IsHardCut { get; }
}
