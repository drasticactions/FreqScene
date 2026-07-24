namespace FreqScene;

public static class AppDataDirectory
{
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "FreqScene");
}
