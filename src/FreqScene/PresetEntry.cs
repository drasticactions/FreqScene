using CommunityToolkit.Mvvm.ComponentModel;

namespace FreqScene;

public sealed partial class PresetEntry : ObservableObject
{
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private bool _isPlaying;

    public PresetEntry(string fullPath)
    {
        FullPath = fullPath;
        Name = Path.GetFileNameWithoutExtension(fullPath);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
    }

    public string FullPath { get; }

    public string Name { get; }

    public string Directory { get; }
}
