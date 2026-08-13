namespace Scion.Capture;

public static class Bootstrapper
{
    public static BootstrapResult CreateMissingFiles(CaptureFiles files)
    {
        var created = new List<string>();
        bool configurationCreated = CreateIfMissing(files.ConfigurationPath, CaptureConfiguration.CreateBootstrapContents(), created);
        CreateIfMissing(files.LogPath, string.Empty, created);
        return new BootstrapResult(configurationCreated, created);
    }

    private static bool CreateIfMissing(string path, string contents, List<string> created)
    {
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, contents);
        created.Add(path);
        return true;
    }
}

public sealed record BootstrapResult(bool ConfigurationCreated, IReadOnlyList<string> CreatedFiles)
{
    public bool CreatedAny => CreatedFiles.Count > 0;
}

public sealed record CaptureFiles(string ConfigurationPath, string StatePath, string LogPath)
{
    public static CaptureFiles BesideExecutable() => FromConfigurationPath(Path.Combine(AppContext.BaseDirectory, "scion-capture.ini"));

    public static CaptureFiles FromConfigurationPath(string path)
    {
        string configurationPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(configurationPath) ?? AppContext.BaseDirectory;
        string baseName = Path.GetFileNameWithoutExtension(configurationPath);
        return new CaptureFiles(
            configurationPath,
            Path.Combine(directory, baseName + ".state"),
            Path.Combine(directory, baseName + ".log"));
    }
}
