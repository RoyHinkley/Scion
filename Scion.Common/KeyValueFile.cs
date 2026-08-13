namespace Scion.Common;

public static class KeyValueFile
{
    public static Dictionary<string, string> ReadFlat(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException($"Invalid line in {path}: {rawLine}");

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }
}

public sealed class IniDocument
{
    private readonly Dictionary<string, List<string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static IniDocument Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        var document = new IniDocument();
        string section = string.Empty;
        document._sections[section] = [];

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0)
                    throw new InvalidDataException($"Empty section name in {path}: {rawLine}");
                document._sections.TryAdd(section, []);
                continue;
            }

            document._sections[section].Add(line);
        }

        return document;
    }

    public IReadOnlyList<string> Lines(string section) =>
        _sections.TryGetValue(section, out List<string>? lines) ? lines : [];

    public Dictionary<string, string> Values(string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(section))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException($"Expected key=value in [{section}]: {line}");
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }
}
