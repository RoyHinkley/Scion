using Scion.Common;

namespace Scion.Capture;

public static class ScionManager
{
    public static ScionPruneResult PruneConfirmedScions(
        string targetPath, int confirmationsRequired, Logger logger)
    {
        Directory.CreateDirectory(targetPath);
        List<DirectoryInfo> scions = new DirectoryInfo(targetPath)
            .EnumerateDirectories()
            .Where(d => ScionNaming.TryParse(d.Name, out _, out _))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = new List<string>();
        var failures = new List<ScionRemovalFailure>();
        foreach (DirectoryInfo scion in scions)
        {
            if (scion.EnumerateFiles("*.confirmed", SearchOption.TopDirectoryOnly).Count() < confirmationsRequired)
                continue;
            try
            {
                scion.Delete(recursive: true);
                removed.Add(scion.FullName);
                logger.WriteVerbose($"Removed confirmed scion {scion.FullName}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ScionRemovalFailure(scion.FullName, ex.Message));
                logger.Write($"Unable to remove confirmed scion {scion.FullName}: {ex.Message}");
            }
        }
        return new ScionPruneResult(removed, failures);
    }

    public static StagingScion CreateStaging(string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        string path = Path.Combine(targetPath, $".scion-building-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new StagingScion(path);
    }

    public static ScionDirectory Publish(StagingScion staging, string targetPath, DateTime localTime)
    {
        string baseName = ScionNaming.BaseName(localTime);
        string finalPath = Path.Combine(targetPath, baseName);
        int suffix = 2;
        while (Directory.Exists(finalPath))
            finalPath = Path.Combine(targetPath, $"{baseName}_{suffix++}");
        Directory.Move(staging.Path, finalPath);
        if (!ScionNaming.TryParse(Path.GetFileName(finalPath), out DateTime timestamp, out int parsedSuffix))
            throw new InvalidOperationException($"Published scion name could not be parsed: {finalPath}");
        return new ScionDirectory(finalPath, timestamp, parsedSuffix);
    }

    public static void DeleteStaging(StagingScion staging)
    {
        try
        {
            if (Directory.Exists(staging.Path))
                Directory.Delete(staging.Path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed record StagingScion(string Path);
public sealed record ScionRemovalFailure(string ScionPath, string ErrorMessage);
public sealed record ScionPruneResult(IReadOnlyList<string> RemovedScions, IReadOnlyList<ScionRemovalFailure> Failures);
