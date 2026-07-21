namespace FreqScene;

public sealed class TextureFolderEntry
{
    public TextureFolderEntry(string fullPath)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name))
        {
            Name = fullPath;
        }
    }

    public string FullPath { get; }

    public string Name { get; }
}
