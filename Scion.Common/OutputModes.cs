namespace Scion.Common;

public enum OutputMode
{
    Quiet,
    Normal,
    Verbose
}

public enum LogMode
{
    None,
    Normal,
    Verbose
}

public static class ModeParser
{
    public static OutputMode ParseOutputMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "quiet" => OutputMode.Quiet,
            "normal" => OutputMode.Normal,
            "verbose" => OutputMode.Verbose,
            _ => throw new InvalidDataException("stdout must be quiet, normal, or verbose.")
        };

    public static LogMode ParseLogMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "none" => LogMode.None,
            "normal" => LogMode.Normal,
            "verbose" => LogMode.Verbose,
            _ => throw new InvalidDataException("log must be none, normal, or verbose.")
        };
}
