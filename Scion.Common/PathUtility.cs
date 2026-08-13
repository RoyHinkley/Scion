namespace Scion.Common;

public static class PathUtility
{
    public static string NormalizeDirectory(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    public static bool IsWithin(string candidatePath, string directoryPath)
    {
        string candidate = Path.GetFullPath(candidatePath);
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        return candidate.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string MapRelativePath(string root, string relativePath)
    {
        string destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(destination, root))
            throw new InvalidDataException($"Relative path escapes its destination root: {relativePath}");
        return destination;
    }
}
