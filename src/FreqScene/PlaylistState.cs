namespace FreqScene;

public sealed class PlaylistState
{
    public List<string> Presets { get; set; } = [];

    public List<string> TextureFolders { get; set; } = [];

    public bool Shuffle { get; set; }

    public bool PresetLocked { get; set; }

    public double PresetDuration { get; set; } = 30;

    public float Gain { get; set; } = 1.0f;

    public string? AudioSource { get; set; }
}
