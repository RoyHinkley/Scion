namespace Scion.Merge;

public sealed record MergeFiles(string ConfigurationPath, string LogPath)
{
    public static MergeFiles BesideExecutable()
    {
        string directory = AppContext.BaseDirectory;
        return new MergeFiles(
            Path.Combine(directory, "scion-merge.ini"),
            Path.Combine(directory, "scion-merge.log"));
    }
}

public static class Bootstrapper
{
    public static IReadOnlyList<string> CreateMissingFiles(MergeFiles files)
    {
        var created = new List<string>();
        if (!File.Exists(files.ConfigurationPath))
        {
            File.WriteAllText(files.ConfigurationPath, MergeConfiguration.BootstrapContents);
            created.Add(files.ConfigurationPath);
        }
        if (!File.Exists(files.LogPath))
        {
            File.WriteAllText(files.LogPath, string.Empty);
            created.Add(files.LogPath);
        }
        return created;
    }
}
