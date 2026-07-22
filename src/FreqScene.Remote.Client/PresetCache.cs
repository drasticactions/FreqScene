namespace FreqScene.Remote.Client;

public sealed class PresetCache(string directory)
{
    public byte[]? TryGet(string presetId)
    {
        var path = PathFor(presetId);
        if (path is null)
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Store(string presetId, byte[] content)
    {
        var path = PathFor(presetId);
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache misses are always recoverable via the server.
        }
    }

    private string? PathFor(string presetId) =>
        presetId.Length > 0 && presetId.All(char.IsAsciiHexDigit)
            ? Path.Combine(directory, presetId + ".milk")
            : null;
}
