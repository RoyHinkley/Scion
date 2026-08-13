using System.Runtime.InteropServices;
using Scion.Common;

namespace Scion.Capture;

public sealed record CaptureConfiguration(
    string ProtectedDrive,
    IReadOnlyList<string> ProtectedFolders,
    string ScionFolder,
    OutputMode Stdout,
    LogMode Log,
    int ConfirmationsRequired)
{
    public static CaptureConfiguration Load(string path)
    {
        IniDocument document = IniDocument.Read(path);
        Dictionary<string, string> values = document.Values(string.Empty);
        string drive = NormalizeDrive(Require(values, "ProtectedDrive"));
        string scionFolder = PathUtility.NormalizeDirectory(Require(values, "ScionFolder"));
        List<string> folders = NormalizeProtectedFolders(document.Lines("ProtectedFolders"), drive, scionFolder);
        if (folders.Count == 0)
            throw new InvalidDataException("[ProtectedFolders] must contain at least one folder after normalization.");

        RewriteProtectedFoldersIfNeeded(path, folders);
        Dictionary<string, string> settings = document.Values("Settings");
        return new CaptureConfiguration(
            drive,
            folders,
            scionFolder,
            ModeParser.ParseOutputMode(settings.GetValueOrDefault("stdout", "normal")),
            ModeParser.ParseLogMode(settings.GetValueOrDefault("log", "normal")),
            ParseNonnegativeInt(settings, "ConfirmationsRequired", 2));
    }

    public static string CreateBootstrapContents()
    {
        string drive = DefaultProtectedDrive();
        string scionFolder = Path.Combine(drive + Path.DirectorySeparatorChar, "Scion");
        List<string> folders = DefaultProtectedFolders(drive, scionFolder);

        return
            "# scion-capture configuration" + Environment.NewLine +
            Environment.NewLine +
            $"ProtectedDrive={drive}" + Environment.NewLine +
            $"ScionFolder={scionFolder}" + Environment.NewLine +
            Environment.NewLine +
            "# Full paths from the protected drive root, without the drive designation." + Environment.NewLine +
            "# This section is normalized by scion-capture: duplicates and redundant" + Environment.NewLine +
            "# nested folders are removed, folders that would include ScionFolder are" + Environment.NewLine +
            "# removed, and the remaining entries are sorted." + Environment.NewLine +
            "[ProtectedFolders]" + Environment.NewLine +
            string.Join(Environment.NewLine, folders) + Environment.NewLine +
            Environment.NewLine +
            "[Settings]" + Environment.NewLine +
            "# A scion is removed after this many *.confirmed files exist in its root." + Environment.NewLine +
            "ConfirmationsRequired=1" + Environment.NewLine +
            Environment.NewLine +
            "# stdout: quiet, normal, verbose" + Environment.NewLine +
            "stdout=normal" + Environment.NewLine +
            Environment.NewLine +
            "# log: none, normal, verbose" + Environment.NewLine +
            "log=normal" + Environment.NewLine;
    }

    public IEnumerable<string> ProtectedFolderPaths =>
        ProtectedFolders.Select(folder => ToFullPath(ProtectedDrive, folder));

    public static string ToFullPath(string drive, string folder) =>
        PathUtility.NormalizeDirectory(drive + folder.Replace('/', Path.DirectorySeparatorChar));

    private static List<string> DefaultProtectedFolders(string drive, string scionFolder)
    {
        var candidates = new List<string>();
        foreach (Guid id in KnownFolderIds)
        {
            string? path = TryGetKnownFolderPath(id);
            if (path is not null && IsOnDrive(path, drive))
                candidates.Add(ToDriveRelative(path, drive));
        }

        string data = Path.Combine(drive + Path.DirectorySeparatorChar, "Data");
        if (Directory.Exists(data))
            candidates.Add(@"\Data");

        return NormalizeProtectedFolders(candidates, drive, scionFolder);
    }

    private static List<string> NormalizeProtectedFolders(IEnumerable<string> lines, string drive, string scionFolder)
    {
        var candidates = lines
            .Select(line => line.Contains('=') ? line[(line.IndexOf('=') + 1)..].Trim() : line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(NormalizeProtectedFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder.Length)
            .ThenBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>();
        foreach (string folder in candidates)
        {
            string fullPath = ToFullPath(drive, folder);
            if (PathUtility.IsWithin(scionFolder, fullPath))
                continue;
            if (result.Any(parent => PathUtility.IsWithin(fullPath, ToFullPath(drive, parent))))
                continue;
            result.Add(folder);
        }

        return result.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeProtectedFolder(string folder)
    {
        string value = folder.Trim().Replace('/', '\\');
        if (value.Length == 0 || value[0] != '\\')
            throw new InvalidDataException($"Protected folder must be a full path from the drive root without a drive designation: {folder}");
        if (value.Length >= 2 && value[1] == '\\')
            throw new InvalidDataException($"Protected folder is not a drive-relative path: {folder}");
        if (value.Contains(':'))
            throw new InvalidDataException($"Protected folder must not contain a drive designation: {folder}");
        return value.Length > 1 ? value.TrimEnd('\\') : value;
    }

    private static string NormalizeDrive(string value)
    {
        string drive = value.Trim().TrimEnd('\\', '/');
        if (drive.Length != 2 || !char.IsLetter(drive[0]) || drive[1] != ':')
            throw new InvalidDataException("ProtectedDrive must be a drive designation such as C:.");
        return char.ToUpperInvariant(drive[0]) + ":";
    }

    private static string DefaultProtectedDrive()
    {
        string? root = Path.GetPathRoot(Environment.SystemDirectory);
        return NormalizeDrive(root ?? @"C:\");
    }

    private static bool IsOnDrive(string path, string drive) =>
        string.Equals(Path.GetPathRoot(path)?.TrimEnd('\\', '/'), drive, StringComparison.OrdinalIgnoreCase);

    private static string ToDriveRelative(string path, string drive)
    {
        string normalized = PathUtility.NormalizeDirectory(path);
        return normalized[drive.Length..];
    }

    private static void RewriteProtectedFoldersIfNeeded(string path, IReadOnlyList<string> folders)
    {
        string[] lines = File.ReadAllLines(path);
        int start = Array.FindIndex(lines, line => line.Trim().Equals("[ProtectedFolders]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            throw new InvalidDataException("Configuration section [ProtectedFolders] is required.");
        int end = start + 1;
        while (end < lines.Length && !(lines[end].TrimStart().StartsWith('[') && lines[end].TrimEnd().EndsWith(']')))
            end++;

        List<string> existing = lines[(start + 1)..end]
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            .ToList();
        if (existing.SequenceEqual(folders, StringComparer.Ordinal))
            return;

        var output = new List<string>();
        output.AddRange(lines[..(start + 1)]);
        output.AddRange(folders);
        output.AddRange(lines[end..]);
        File.WriteAllLines(path, output);
    }

    private static string Require(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Configuration setting {key} is required.");

    private static int ParseNonnegativeInt(Dictionary<string, string> values, string key, int defaultValue)
    {
        string value = values.GetValueOrDefault(key, defaultValue.ToString());
        if (!int.TryParse(value, out int result) || result < 0)
            throw new InvalidDataException($"Configuration setting {key} must be a nonnegative integer.");
        return result;
    }

    private static string? TryGetKnownFolderPath(Guid id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        int result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr path);
        if (result != 0) return null;
        try { return Marshal.PtrToStringUni(path); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    private static readonly Guid[] KnownFolderIds =
    [
        new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), // Desktop
        new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), // Documents
        new("33E28130-4E1E-4676-835A-98395C3BC3BB"), // Pictures
        new("4BD8D571-6D19-48D3-BE97-422220080E43"), // Music
        new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), // Videos
        new("374DE290-123F-4565-9164-39C4925E467B")  // Downloads
    ];

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
