namespace Scion.Capture;

public sealed record JournalCheckpoint(ulong JournalId, long NextUsn, DateTime CheckpointStartTimeUtc);

public sealed record JournalInfo(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    ulong MaximumSize,
    ulong AllocationDelta);

public sealed record JournalChange(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    long Usn,
    DateTime TimestampUtc,
    UsnReason Reason,
    FileAttributes Attributes,
    string FileName);

public sealed record JournalCheckpointSearchResult(
    long NextUsn,
    bool PredatesRetainedHistory);

public sealed record JournalReadResult(
    ulong JournalId,
    long NextUsn,
    IReadOnlyList<JournalChange> Changes);

public sealed record JournalCandidate(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    long LatestUsn,
    DateTime LatestTimestampUtc,
    UsnReason CombinedReasons,
    UsnReason LatestReason,
    FileAttributes Attributes,
    string LatestFileName)
{
    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
    public bool IsDeleted => LatestReason.HasFlag(UsnReason.FileDelete);
    public bool HasActionableChange => (CombinedReasons & ~UsnReason.Close) != UsnReason.None;
}

public sealed record LocatedCandidate(
    JournalCandidate Journal,
    string Path);

public sealed record CopyCandidate(
    string SourcePath,
    string RelativePath,
    string TargetPath);


[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,
    All = 0xFFFFFFFF
}
