using System.Globalization;
using System.Text.RegularExpressions;

namespace Scion.Common;

public static partial class ScionNaming
{
    public const string Prefix = "scion_";
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static string BaseName(DateTime localTime) =>
        Prefix + localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static bool TryParse(string name, out DateTime timestamp, out int suffix) =>
        TryParseMatch(CurrentScionRegex().Match(name), out timestamp, out suffix);

    private static bool TryParseMatch(Match match, out DateTime timestamp, out int suffix)
    {
        timestamp = default;
        if (!match.Success || !DateTime.TryParseExact(
                match.Groups[1].Value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp))
        {
            suffix = 0;
            return false;
        }

        suffix = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : 1;
        return true;
    }

    [GeneratedRegex(@"^scion_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:_(\d+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CurrentScionRegex();
}

public sealed record ScionDirectory(string Path, DateTime Timestamp, int Suffix)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
