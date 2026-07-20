namespace ProjectMDotNet;

public sealed class PresetSwitchFailedEventArgs : EventArgs
{
    internal PresetSwitchFailedEventArgs(string presetFilename, string message)
    {
        PresetFilename = presetFilename;
        Message = message;
    }

    /// <summary>The preset file that failed to load.</summary>
    public string PresetFilename { get; }

    /// <summary>The native error message.</summary>
    public string Message { get; }
}
