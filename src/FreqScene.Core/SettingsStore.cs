using System.Text.Json;

namespace FreqScene;

public static class SettingsStore
{
    public static string FilePath { get; private set; } = Path.Combine(
        AppDataDirectory.Default,
        "settings.json");

    /// <summary>Points this store (and only this store) at a different directory; call before the first Load.</summary>
    public static void OverrideDirectory(string directory) =>
        FilePath = Path.Combine(directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = File.OpenRead(FilePath);
                return JsonSerializer.Deserialize(stream, PlaylistJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to defaults; a corrupt file must not stop the app from starting.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var stream = File.Create(FilePath);
            JsonSerializer.Serialize(stream, settings, PlaylistJsonContext.Default.AppSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
