namespace Scion.Capture;

public static class RecoveryScanner
{
    public static RecoveryScanResult Scan(
        IEnumerable<string> rootPaths,
        DateTime thresholdUtc,
        Action<string> onQualifyingFile)
    {
        var pending = new Stack<DirectoryInfo>(rootPaths.Select(path => new DirectoryInfo(path)));
        int directoriesVisited = 0;
        int filesExamined = 0;
        int filesQualifying = 0;
        int peakPendingDirectories = pending.Count;
        var failures = new List<RecoveryScanFailure>();

        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            directoriesVisited++;
            try
            {
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    FileAttributes attributes;
                    try { attributes = entry.Attributes; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new RecoveryScanFailure(entry.FullName, ex.Message));
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push((DirectoryInfo)entry);
                            peakPendingDirectories = Math.Max(peakPendingDirectories, pending.Count);
                        }
                        continue;
                    }

                    filesExamined++;
                    try
                    {
                        if (entry.LastWriteTimeUtc >= thresholdUtc)
                        {
                            filesQualifying++;
                            onQualifyingFile(entry.FullName);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new RecoveryScanFailure(entry.FullName, ex.Message));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new RecoveryScanFailure(directory.FullName, ex.Message));
            }
        }

        return new RecoveryScanResult(directoriesVisited, filesExamined, filesQualifying, peakPendingDirectories, failures);
    }
}

public sealed record RecoveryScanFailure(string Path, string ErrorMessage);
public sealed record RecoveryScanResult(int DirectoriesVisited, int FilesExamined, int FilesQualifying, int PeakPendingDirectories, IReadOnlyList<RecoveryScanFailure> Failures);
