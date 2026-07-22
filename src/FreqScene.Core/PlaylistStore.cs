using System.Text.Json;

namespace FreqScene;

/// <summary>Loads and saves <see cref="PlaylistState"/> under the user's app-data directory.</summary>
public static class PlaylistStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "FreqScene",
        "playlist.json");

    public static PlaylistState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = File.OpenRead(FilePath);
                return JsonSerializer.Deserialize(stream, PlaylistJsonContext.Default.PlaylistState) ?? new PlaylistState();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults; a corrupt file must not stop the app from starting.
        }

        return new PlaylistState();
    }

    public static void Save(PlaylistState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var stream = File.Create(FilePath);
            JsonSerializer.Serialize(stream, state, PlaylistJsonContext.Default.PlaylistState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
