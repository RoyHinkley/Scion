using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Scion.Capture;

public sealed class NtfsChangeJournal : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;

    private const int ErrorJournalEntryDeleted = 1181;
    private const int OutputBufferSize = 1024 * 1024;
    private const int UsnRecordV2FixedLength = 60;

    private readonly SafeFileHandle _volumeHandle;

    public NtfsChangeJournal(string driveRoot)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(driveRoot))
            ?? throw new ArgumentException("The path has no drive root.", nameof(driveRoot));

        if (root.Length < 2 || root[1] != ':')
            throw new ArgumentException("The path must be on a drive-letter volume.", nameof(driveRoot));

        VolumeRoot = root[..2];
        string volumePath = $@"\\.\{VolumeRoot}";

        _volumeHandle = CreateFileW(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (_volumeHandle.IsInvalid)
            throw NewWin32Exception($"Unable to open NTFS volume {volumePath}");
    }

    public string VolumeRoot { get; }

    public JournalInfo Query()
    {
        int size = Marshal.SizeOf<UsnJournalDataV0>();
        IntPtr output = Marshal.AllocHGlobal(size);

        try
        {
            if (!DeviceIoControl(
                    _volumeHandle,
                    FsctlQueryUsnJournal,
                    IntPtr.Zero,
                    0,
                    output,
                    (uint)size,
                    out _,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Unable to query the USN journal; " +
                    $"volume={VolumeRoot}; ioctl=0x{FsctlQueryUsnJournal:X8}; " +
                    $"outputBufferSize={size}; Win32Error={error} (0x{error:X8}): {new Win32Exception(error).Message}");
            }

            UsnJournalDataV0 data = Marshal.PtrToStructure<UsnJournalDataV0>(output);
            return new JournalInfo(
                data.UsnJournalId,
                data.FirstUsn,
                data.NextUsn,
                data.LowestValidUsn,
                data.MaxUsn,
                data.MaximumSize,
                data.AllocationDelta);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    public JournalCheckpointSearchResult FindFirstAtOrAfter(JournalInfo info, DateTime timestampUtc)
    {
        JournalInfo current = Query();
        if (info.JournalId != current.JournalId)
            throw new InvalidOperationException("The USN journal changed while resolving the reset checkpoint.");

        JournalChange? earliest = ReadFirstAtOrAfterUsn(info.JournalId, info.FirstUsn);
        if (earliest is null)
            return new JournalCheckpointSearchResult(info.NextUsn, false);

        if (timestampUtc < earliest.TimestampUtc)
            return new JournalCheckpointSearchResult(earliest.Usn, true);

        // FSCTL_READ_USN_JOURNAL requires a valid retained USN as its starting point;
        // arbitrary midpoint USNs are not suitable binary-search probes. Reset is rare,
        // so scan the retained journal linearly from FirstUsn and stop at the first match.
        JournalReadResult retained = Read(info.JournalId, info.FirstUsn, UsnReason.All);

        JournalChange? match = retained.Changes
            .FirstOrDefault(change => change.TimestampUtc >= timestampUtc);

        return match is null
            ? new JournalCheckpointSearchResult(info.NextUsn, false)
            : new JournalCheckpointSearchResult(match.Usn, false);
    }

    public (long Usn, DateTime? TimestampUtc) ResolveUsnAtOrAfter(JournalInfo info, long requestedUsn)
    {
        if (requestedUsn <= info.FirstUsn)
        {
            JournalChange? earliest = ReadFirstAtOrAfterUsn(info.JournalId, info.FirstUsn);
            return earliest is null
                ? (info.NextUsn, null)
                : (earliest.Usn, earliest.TimestampUtc);
        }

        if (requestedUsn >= info.NextUsn)
            return (info.NextUsn, null);

        JournalReadResult retained = Read(info.JournalId, info.FirstUsn, UsnReason.All);
        JournalChange? match = retained.Changes
            .FirstOrDefault(change => change.Usn >= requestedUsn);

        return match is null
            ? (info.NextUsn, null)
            : (match.Usn, match.TimestampUtc);
    }

    public JournalReadResult Read(ulong journalId, long startUsn, UsnReason reasonMask)
    {
        JournalInfo current = Query();

        if (journalId != current.JournalId)
        {
            throw new InvalidOperationException(
                "The requested journal ID belongs to a different USN journal instance; " +
                $"requestedJournalId=0x{journalId:X16}; {FormatJournalInfo(current)}");
        }

        if (startUsn < current.FirstUsn)
        {
            throw new InvalidOperationException(
                "The requested start USN is older than the earliest retained journal entry; " +
                $"requestedStartUsn={startUsn}; delta={current.FirstUsn - startUsn}; {FormatJournalInfo(current)}");
        }

        if (startUsn > current.NextUsn)
        {
            throw new InvalidOperationException(
                "The requested start USN is ahead of the current USN journal position; " +
                $"requestedStartUsn={startUsn}; delta={startUsn - current.NextUsn}; {FormatJournalInfo(current)}");
        }

        var changes = new List<JournalChange>();
        long nextUsn = startUsn;
        IntPtr input = Marshal.AllocHGlobal(Marshal.SizeOf<ReadUsnJournalDataV0>());
        IntPtr output = Marshal.AllocHGlobal(OutputBufferSize);

        try
        {
            int iteration = 0;
            while (true)
            {
                iteration++;
                var request = new ReadUsnJournalDataV0
                {
                    StartUsn = nextUsn,
                    ReasonMask = (uint)reasonMask,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalId = journalId
                };

                Marshal.StructureToPtr(request, input, false);

                if (!DeviceIoControl(
                        _volumeHandle,
                        FsctlReadUsnJournal,
                        input,
                        (uint)Marshal.SizeOf<ReadUsnJournalDataV0>(),
                        output,
                        OutputBufferSize,
                        out uint bytesReturned,
                        IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorJournalEntryDeleted)
                    {
                        throw CreateReadException(
                            "The saved checkpoint is no longer present in the USN journal",
                            error,
                            request,
                            current,
                            bytesReturned: 0,
                            iteration: iteration);
                    }

                    throw CreateReadException(
                        "Unable to read the USN journal",
                        error,
                        request,
                        current,
                        bytesReturned: 0,
                        iteration: iteration);
                }

                if (bytesReturned < sizeof(long))
                {
                    throw new InvalidDataException(
                        "The USN journal returned an undersized buffer; " +
                        $"bytesReturned={bytesReturned}; iteration={iteration}; requestedStartUsn={request.StartUsn}; {FormatJournalInfo(current)}");
                }

                long returnedNextUsn = Marshal.ReadInt64(output);
                int offset = sizeof(long);

                while ((uint)offset < bytesReturned)
                {
                    IntPtr recordPointer = IntPtr.Add(output, offset);
                    JournalChange change = ParseV2Record(recordPointer, bytesReturned - (uint)offset);
                    changes.Add(change);

                    uint recordLength = unchecked((uint)Marshal.ReadInt32(recordPointer));
                    offset += checked((int)recordLength);
                }

                if (returnedNextUsn == nextUsn || bytesReturned == sizeof(long))
                {
                    nextUsn = returnedNextUsn;
                    break;
                }

                nextUsn = returnedNextUsn;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }

        return new JournalReadResult(journalId, nextUsn, changes);
    }

    private JournalChange? ReadFirstAtOrAfterUsn(ulong journalId, long startUsn)
    {
        IntPtr input = Marshal.AllocHGlobal(Marshal.SizeOf<ReadUsnJournalDataV0>());
        IntPtr output = Marshal.AllocHGlobal(OutputBufferSize);

        try
        {
            var request = new ReadUsnJournalDataV0
            {
                StartUsn = startUsn,
                ReasonMask = (uint)UsnReason.All,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = journalId
            };

            Marshal.StructureToPtr(request, input, false);

            if (!DeviceIoControl(
                    _volumeHandle,
                    FsctlReadUsnJournal,
                    input,
                    (uint)Marshal.SizeOf<ReadUsnJournalDataV0>(),
                    output,
                    OutputBufferSize,
                    out uint bytesReturned,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Unable to read the first retained USN record; volume={VolumeRoot}; " +
                    $"requestedStartUsn={startUsn}; requestedJournalId=0x{journalId:X16}; " +
                    $"Win32Error={error} (0x{error:X8}): {new Win32Exception(error).Message}");
            }

            if (bytesReturned <= sizeof(long))
                return null;

            return ParseV2Record(
                IntPtr.Add(output, sizeof(long)),
                bytesReturned - sizeof(long));
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    public string? ResolvePath(ulong fileReferenceNumber)
    {
        var descriptor = new FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
            Type = FileIdType.FileIdType,
            FileId = unchecked((long)fileReferenceNumber)
        };

        using SafeFileHandle fileHandle = OpenFileById(
            _volumeHandle,
            ref descriptor,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics);

        if (fileHandle.IsInvalid)
            return null;

        uint capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(checked((int)capacity));
            uint length = GetFinalPathNameByHandleW(fileHandle, buffer, capacity, 0);

            if (length == 0)
                return null;

            if (length < capacity)
                return NormalizePath(buffer.ToString());

            capacity = length + 1;
        }
    }

    public void Dispose() => _volumeHandle.Dispose();

    private static JournalChange ParseV2Record(IntPtr pointer, uint availableBytes)
    {
        if (availableBytes < UsnRecordV2FixedLength)
            throw new InvalidDataException("The USN record is shorter than a V2 record header.");

        byte[] header = new byte[UsnRecordV2FixedLength];
        Marshal.Copy(pointer, header, 0, header.Length);
        ReadOnlySpan<byte> data = header;

        uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]);
        ushort minorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);

        if (recordLength < UsnRecordV2FixedLength || recordLength > availableBytes)
            throw new InvalidDataException($"Invalid USN record length: {recordLength}.");

        if (majorVersion != 2)
        {
            throw new NotSupportedException(
                $"Unsupported USN record version {majorVersion}.{minorVersion}; this prototype supports V2 only.");
        }

        ulong fileReference = BinaryPrimitives.ReadUInt64LittleEndian(data[8..16]);
        ulong parentReference = BinaryPrimitives.ReadUInt64LittleEndian(data[16..24]);
        long usn = BinaryPrimitives.ReadInt64LittleEndian(data[24..32]);
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(data[32..40]);
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]);
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[52..56]);
        ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[56..58]);
        ushort fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[58..60]);

        if ((uint)fileNameOffset + fileNameLength > recordLength)
            throw new InvalidDataException("The USN record contains an invalid file-name range.");

        string fileName = Marshal.PtrToStringUni(
            IntPtr.Add(pointer, fileNameOffset),
            fileNameLength / sizeof(char)) ?? string.Empty;

        DateTime timestampUtc = fileTime > 0
            ? DateTime.FromFileTimeUtc(fileTime)
            : DateTime.MinValue;

        return new JournalChange(
            fileReference,
            parentReference,
            usn,
            timestampUtc,
            (UsnReason)reason,
            (FileAttributes)attributes,
            fileName);
    }

    private static string NormalizePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";

        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];

        if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
            return path[extendedPrefix.Length..];

        return path;
    }

    private Win32Exception CreateReadException(
        string operation,
        int error,
        ReadUsnJournalDataV0 request,
        JournalInfo info,
        uint bytesReturned,
        int iteration)
    {
        string systemMessage = new Win32Exception(error).Message;
        string earliestRetained = FormatEarliestRetainedRecord(info);
        string message =
            $"{operation}; volume={VolumeRoot}; ioctl=0x{FsctlReadUsnJournal:X8}; " +
            $"iteration={iteration}; inputStructSize={Marshal.SizeOf<ReadUsnJournalDataV0>()}; " +
            $"outputBufferSize={OutputBufferSize}; bytesReturned={bytesReturned}; " +
            $"requestedStartUsn={request.StartUsn}; reasonMask=0x{request.ReasonMask:X8}; " +
            $"returnOnlyOnClose={request.ReturnOnlyOnClose}; timeout={request.Timeout}; " +
            $"bytesToWaitFor={request.BytesToWaitFor}; requestedJournalId=0x{request.UsnJournalId:X16}; " +
            $"{FormatJournalInfo(info)}; {earliestRetained}; " +
            $"Win32Error={error} (0x{error:X8}): {systemMessage}";

        return new Win32Exception(error, message);
    }


    private string FormatEarliestRetainedRecord(JournalInfo info)
    {
        try
        {
            JournalChange? earliest = ReadFirstAtOrAfterUsn(info.JournalId, info.FirstUsn);
            if (earliest is null)
                return "earliestRetainedRecord=unavailable";

            DateTime local = earliest.TimestampUtc.ToLocalTime();
            return
                $"earliestRetainedRecordUsn={earliest.Usn}; " +
                $"earliestRetainedRecordTimestampUtc={earliest.TimestampUtc:O}; " +
                $"earliestRetainedRecordTimestampLocal={local:O}; " +
                $"earliestRetainedRecordName={QuoteDiagnostic(earliest.FileName)}; " +
                $"earliestRetainedRecordReason=0x{(uint)earliest.Reason:X8}";
        }
        catch (Exception ex)
        {
            return $"earliestRetainedRecord=unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string QuoteDiagnostic(string value)
    {
        string escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string FormatJournalInfo(JournalInfo info)
    {
        return
            $"queriedJournalId=0x{info.JournalId:X16}; firstUsn={info.FirstUsn}; " +
            $"lowestValidUsn={info.LowestValidUsn}; nextUsn={info.NextUsn}; maxUsn={info.MaxUsn}; " +
            $"maximumSize={info.MaximumSize}; allocationDelta={info.AllocationDelta}";
    }

    private static Win32Exception NewWin32Exception(string message)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message}: {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    private enum FileIdType : uint
    {
        FileIdType = 0,
        ObjectIdType = 1,
        ExtendedFileIdType = 2,
        MaximumFileIdType = 3
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public FileIdType Type;
        [FieldOffset(8)] public long FileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
