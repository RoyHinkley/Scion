namespace Scion.Common;

public sealed class Logger
{
    private readonly string _path;
    private readonly LogMode _mode;

    public Logger(string path, LogMode mode)
    {
        _path = path;
        _mode = mode;
    }

    public void Write(string message)
    {
        if (_mode != LogMode.None)
            Append(message);
    }

    public void WriteVerbose(string message)
    {
        if (_mode == LogMode.Verbose)
            Append(message);
    }

    private void Append(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        File.AppendAllText(_path, line + Environment.NewLine);
    }
}
