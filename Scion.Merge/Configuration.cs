using Scion.Common;

namespace Scion.Merge;

public sealed record MergeConfiguration(
    IReadOnlyList<string> ScionFolders,
    string RecoveryTree,
    string CollectorName,
    OutputMode Stdout,
    LogMode Log)
{
    public static MergeConfiguration Load(string path)
    {
        IniDocument document = IniDocument.Read(path);
        List<string> scionFolders = document.Lines("ScionFolders")
            .Select(line => line.Contains('=') ? line[(line.IndexOf('=') + 1)..].Trim() : line)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(PathUtility.NormalizeDirectory)
            .ToList();
        if (scionFolders.Count == 0)
            throw new InvalidDataException("[ScionFolders] must contain at least one Scion folder.");

        string? recoveryTree = document.Lines("RecoveryTree")
            .Select(line => line.Contains('=') ? line[(line.IndexOf('=') + 1)..].Trim() : line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(recoveryTree))
            throw new InvalidDataException("[RecoveryTree] must contain a destination path.");

        Dictionary<string, string> settings = document.Values("Settings");
        string collector = settings.GetValueOrDefault("CollectorName", Environment.MachineName).Trim().ToLowerInvariant();
        if (collector.Length == 0 || collector.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("CollectorName is not a valid filename component.");

        return new MergeConfiguration(
            scionFolders,
            PathUtility.NormalizeDirectory(recoveryTree),
            collector,
            ModeParser.ParseOutputMode(settings.GetValueOrDefault("stdout", "normal")),
            ModeParser.ParseLogMode(settings.GetValueOrDefault("log", "normal")));
    }

    public static string BootstrapContents
    {
        get
        {
            string protectedDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');
            string? secondDrive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName.TrimEnd('\\'))
                .FirstOrDefault(d => !d.Equals(protectedDrive, StringComparison.OrdinalIgnoreCase));
            string recoveryTree = Path.Combine((secondDrive ?? protectedDrive) + Path.DirectorySeparatorChar, "Recovery");
            string scionFolder = Path.Combine(protectedDrive + Path.DirectorySeparatorChar, "Scion");

            return
                "# scion-merge configuration" + Environment.NewLine +
                "# Scion folders are processed in the order listed." + Environment.NewLine +
                Environment.NewLine +
                "[ScionFolders]" + Environment.NewLine +
                scionFolder + Environment.NewLine +
                Environment.NewLine +
                "[RecoveryTree]" + Environment.NewLine +
                recoveryTree + Environment.NewLine +
                Environment.NewLine +
                "[Settings]" + Environment.NewLine +
                "stdout=normal" + Environment.NewLine +
                "log=normal" + Environment.NewLine;
        }
    }
}
