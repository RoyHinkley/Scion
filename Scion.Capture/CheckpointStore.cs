using Scion.Common;
using System.Globalization;

namespace Scion.Capture;

public static class CheckpointStore
{
    public static string CreateContents(JournalCheckpoint checkpoint)
    {
        return
            $"JournalId=0x{checkpoint.JournalId:X16}{Environment.NewLine}" +
            $"NextUsn={checkpoint.NextUsn.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}" +
            $"CheckpointStartTimeUtc={checkpoint.CheckpointStartTimeUtc.ToString("O", CultureInfo.InvariantCulture)}{Environment.NewLine}";
    }

    public static JournalCheckpoint? Load(string path)
    {
        Dictionary<string, string> values = KeyValueFile.ReadFlat(path);

        bool hasJournalId = values.TryGetValue("JournalId", out string? journalIdText);
        bool hasNextUsn = values.TryGetValue("NextUsn", out string? nextUsnText);
        bool hasCheckpointTime = values.TryGetValue("CheckpointStartTimeUtc", out string? checkpointTimeText);

        if (!hasJournalId && !hasNextUsn && !hasCheckpointTime)
            return null;

        if (!hasJournalId || !hasNextUsn || !hasCheckpointTime)
            throw new InvalidDataException($"Checkpoint file is incomplete: {path}");

        bool journalIdBlank = string.IsNullOrWhiteSpace(journalIdText);
        bool nextUsnBlank = string.IsNullOrWhiteSpace(nextUsnText);
        bool checkpointTimeBlank = string.IsNullOrWhiteSpace(checkpointTimeText);

        if (journalIdBlank && nextUsnBlank && checkpointTimeBlank)
            return null;

        if (journalIdBlank || nextUsnBlank || checkpointTimeBlank)
            throw new InvalidDataException($"Checkpoint file is incomplete: {path}");

        ulong journalId = ParseJournalId(journalIdText!);
        long nextUsn = long.Parse(nextUsnText!, CultureInfo.InvariantCulture);
        DateTime checkpointStartTimeUtc = DateTime.ParseExact(
            checkpointTimeText!,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return new JournalCheckpoint(journalId, nextUsn, checkpointStartTimeUtc);
    }
    
    public static void Save(string path, JournalCheckpoint checkpoint)
    {
        string temporaryPath = path + ".new";
        string contents = CreateContents(checkpoint);

        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static ulong ParseJournalId(string value)
    {
        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
}
