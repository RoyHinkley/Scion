using System.ComponentModel;
using System.Globalization;
using System.Security.Principal;
using Scion.Common;
using Scion.Capture;

return Run(args);

static int Run(string[] args)
{
    CaptureFiles? files = null;
    Logger? logger = null;

    try
    {
        Options options = Options.Parse(args);
        files = options.ConfigurationPath is null
            ? CaptureFiles.BesideExecutable()
            : CaptureFiles.FromConfigurationPath(options.ConfigurationPath);
        logger = new Logger(files.LogPath, LogMode.Normal);
        BootstrapResult bootstrap = Bootstrapper.CreateMissingFiles(files);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("scion-capture runs only on Windows.");

        CaptureConfiguration configuration = CaptureConfiguration.Load(files.ConfigurationPath);
        logger = new Logger(files.LogPath, configuration.Log);
        OutputMode stdout = options.StdoutOverride ?? configuration.Stdout;
        bool verbose = stdout == OutputMode.Verbose;
        bool quiet = stdout == OutputMode.Quiet;
        if (!IsAdministrator() && !quiet)
            Console.Error.WriteLine("WARNING: Run scion-capture from an elevated console.");

        string volumeRoot = PathUtility.NormalizeDirectory(configuration.ProtectedDrive + Path.DirectorySeparatorChar);
        string scionFolder = PathUtility.NormalizeDirectory(configuration.ScionFolder);
        List<string> protectedFolders = configuration.ProtectedFolderPaths.Select(PathUtility.NormalizeDirectory).ToList();
        ValidatePaths(volumeRoot, protectedFolders, scionFolder);

        var createdFiles = new List<string>(bootstrap.CreatedFiles);
        bool stateCreated = false;
        bool stateInitializedFromJournal = false;

        NtfsChangeJournal? journal = null;
        JournalInfo info;
        DateTime infoQueryStartUtc = DateTime.UtcNow;
        try
        {
            journal = new NtfsChangeJournal(volumeRoot);
            infoQueryStartUtc = DateTime.UtcNow;
            info = journal.Query();

            if (!File.Exists(files.StatePath))
            {
                CheckpointStore.Save(
                    files.StatePath,
                    new JournalCheckpoint(info.JournalId, info.NextUsn, infoQueryStartUtc));
                createdFiles.Add(files.StatePath);
                stateCreated = true;
                stateInitializedFromJournal = true;
            }
        }
        catch (Exception ex)
        {
            journal?.Dispose();

            if (!File.Exists(files.StatePath))
            {
                File.WriteAllText(files.StatePath, string.Empty);

                createdFiles.Add(files.StatePath);
                stateCreated = true;
            }

            ReportCreatedFiles(
                createdFiles,
                stateCreated,
                stateInitializedFromJournal);

            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"The NTFS journal could not be queried: {ex.Message}");

            Console.Error.WriteLine(
                "An empty checkpoint was created.");

            throw;
        }

        using NtfsChangeJournal activeJournal = journal
            ?? throw new InvalidOperationException("The NTFS journal was not initialized.");
        {
            ReportCreatedFiles(createdFiles, stateCreated, stateInitializedFromJournal);

            foreach (string protectedFolder in protectedFolders)
                if (!Directory.Exists(protectedFolder))
                    throw new DirectoryNotFoundException($"Protected folder does not exist: {protectedFolder}");

            if (verbose)
            {
                Console.WriteLine($"Configuration:   {files.ConfigurationPath}");
                Console.WriteLine($"Protected drive: {configuration.ProtectedDrive}");
                Console.WriteLine($"Scion folder:    {scionFolder}");
                foreach (string folder in configuration.ProtectedFolders) Console.WriteLine($"Protected folder:{folder}");
                Console.WriteLine($"stdout:          {stdout.ToString().ToLowerInvariant()}");
                Console.WriteLine($"log:             {configuration.Log.ToString().ToLowerInvariant()}");
                Console.WriteLine($"Confirmations:   {configuration.ConfirmationsRequired}");
                Console.WriteLine($"Volume:          {activeJournal.VolumeRoot}");
                Console.WriteLine($"Journal ID:      0x{info.JournalId:X16}");
                Console.WriteLine($"First USN:       {info.FirstUsn}");
                Console.WriteLine($"Next USN:        {info.NextUsn}");
                Console.WriteLine($"Lowest valid USN:{info.LowestValidUsn}");
                Console.WriteLine($"Checkpoint file: {files.StatePath}");
                Console.WriteLine($"Log file:        {files.LogPath}\n");
            }

            JournalCheckpoint? checkpoint = CheckpointStore.Load(files.StatePath);
            if (checkpoint is null)
            {
                checkpoint = new JournalCheckpoint(info.JournalId, info.NextUsn, infoQueryStartUtc);
                CheckpointStore.Save(files.StatePath, checkpoint);
                stateInitializedFromJournal = true;
            }

            if (options.Reset is not null)
            {
                JournalCheckpoint original = checkpoint;
                ResetResolution reset = ResolveReset(
                    options.Reset,
                    activeJournal,
                    info,
                    infoQueryStartUtc);

                checkpoint = new JournalCheckpoint(
                    info.JournalId,
                    reset.NextUsn,
                    reset.CheckpointStartTimeUtc);
                CheckpointStore.Save(files.StatePath, checkpoint);

                string resetMessage = $"Checkpoint reset from USN {original.NextUsn} to USN {checkpoint.NextUsn}{reset.Description}.";
                logger.Write(resetMessage);
                if (!quiet)
                    Console.WriteLine(resetMessage);

                if (reset.PredatesRetainedHistory)
                {
                    string warning = $"Requested checkpoint predates retained journal history; using earliest available USN {checkpoint.NextUsn}.";
                    logger.Write(warning);
                    if (!quiet)
                        Console.WriteLine(warning);
                }
            }

            ScionPruneResult prune = ScionManager.PruneConfirmedScions(
                scionFolder,
                configuration.ConfirmationsRequired,
                logger);
            if (verbose)
            {
                foreach (string removed in prune.RemovedScions) Console.WriteLine($"REMOVED       {removed}");
                foreach (ScionRemovalFailure failure in prune.Failures) Console.WriteLine($"REMOVE FAILED {failure.ScionPath}: {failure.ErrorMessage}");
                if (prune.RemovedScions.Count > 0 || prune.Failures.Count > 0) Console.WriteLine();
            }

            StagingScion? staging = null;
            var copied = new List<CopyCandidate>();
            var sourceFailures = new List<CopyFailure>();
            var destinationFailures = new List<CopyFailure>();
            RecoveryScanResult? recoveryScan = null;
            JournalSelection? journalSelection = null;
            bool usedRecovery = false;
            JournalInfo currentInfo = activeJournal.Query();
            string? recoveryReason = options.Reset is null
                ? GetCheckpointInconsistency(checkpoint, currentInfo)
                : null;
            JournalCheckpoint finalCheckpoint;

            if (recoveryReason is null)
            {
                DateTime captureStartUtc = DateTime.UtcNow;
                try
                {
                    JournalReadResult journalResult = activeJournal.Read(checkpoint.JournalId, checkpoint.NextUsn, UsnReason.All);
                    journalSelection = SelectJournalCandidates(activeJournal, journalResult, protectedFolders, logger);

                    foreach (LocatedCandidate item in journalSelection.Eligible)
                    {
                        CopySourceFile(
                            item.Path,
                            volumeRoot,
                            scionFolder,
                            ref staging,
                            copied,
                            sourceFailures,
                            destinationFailures,
                            logger);
                    }

                    finalCheckpoint = new JournalCheckpoint(
                        journalResult.JournalId,
                        journalResult.NextUsn,
                        captureStartUtc);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1181)
                {
                    recoveryReason = "The stored checkpoint was trimmed from the USN journal while the journal was being read.";
                    finalCheckpoint = default!;
                }
            }
            else
            {
                finalCheckpoint = default!;
            }

            if (recoveryReason is not null)
            {
                usedRecovery = true;
                DateTime recoveryThresholdUtc = checkpoint.CheckpointStartTimeUtc;

                string recoveryMessage =
                    $"{recoveryReason} Recovering by scanning files written since {recoveryThresholdUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}, then catching up from a fresh journal position.";
                logger.Write(recoveryMessage);
                if (!quiet)
                    Console.WriteLine(recoveryMessage);

                DateTime recoveryBaselineTimeUtc = DateTime.UtcNow;
                JournalInfo recoveryInfo = activeJournal.Query();
                JournalCheckpoint recoveryBaseline = new(
                    recoveryInfo.JournalId,
                    recoveryInfo.NextUsn,
                    recoveryBaselineTimeUtc);

                recoveryScan = RecoveryScanner.Scan(
                    protectedFolders,
                    recoveryThresholdUtc,
                    path => CopySourceFile(
                        path,
                        volumeRoot,
                        scionFolder,
                        ref staging,
                        copied,
                        sourceFailures,
                        destinationFailures,
                        logger));

                foreach (RecoveryScanFailure failure in recoveryScan.Failures)
                    logger.Write($"Recovery scan failed to inspect {failure.Path}: {failure.ErrorMessage}");

                JournalReadResult catchupResult = activeJournal.Read(recoveryBaseline.JournalId, recoveryBaseline.NextUsn, UsnReason.All);
                journalSelection = SelectJournalCandidates(activeJournal, catchupResult, protectedFolders, logger);

                foreach (LocatedCandidate item in journalSelection.Eligible)
                {
                    CopySourceFile(
                        item.Path,
                        volumeRoot,
                        scionFolder,
                        ref staging,
                        copied,
                        sourceFailures,
                        destinationFailures,
                        logger);
                }

                finalCheckpoint = new JournalCheckpoint(
                    catchupResult.JournalId,
                    catchupResult.NextUsn,
                    recoveryBaselineTimeUtc);
            }

            bool checkpointAdvanced =
                destinationFailures.Count == 0 &&
                (recoveryScan?.Failures.Count ?? 0) == 0;

            ScionDirectory? published = null;
            if (staging is not null)
            {
                if (checkpointAdvanced && copied.Count > 0)
                    published = ScionManager.Publish(staging, scionFolder, DateTime.Now);
                else
                    ScionManager.DeleteStaging(staging);
            }

            if (checkpointAdvanced)
                CheckpointStore.Save(files.StatePath, finalCheckpoint);

            string scionText = published?.Name ?? "none";
            int journalRecords = journalSelection?.Result.Changes.Count ?? 0;
            int eligibleCount = journalSelection?.Eligible.Count ?? 0;
            string modeText = usedRecovery ? "recovery scan + journal catch-up" : "journal";
            string recoverySummary = recoveryScan is null
                ? string.Empty
                : $" recovery scanned {recoveryScan.FilesExamined} files; {recoveryScan.FilesQualifying} timestamp-qualified; {recoveryScan.Failures.Count} scan failures;";

            logger.Write(
                $"{modeText};{recoverySummary} {journalRecords} journal records; scion {scionText}; " +
                $"{eligibleCount} journal files to copy; {copied.Count} copied and verified; " +
                $"{sourceFailures.Count} source failures; {destinationFailures.Count} destination/verification failures; " +
                $"{prune.RemovedScions.Count} old scions removed; " +
                (checkpointAdvanced
                    ? $"checkpoint advanced to {finalCheckpoint.NextUsn}."
                    : $"checkpoint retained at {checkpoint.NextUsn}."));

            if (quiet)
            {
                Console.WriteLine($"{copied.Count} {(copied.Count == 1 ? "file" : "files")} copied and verified.");
                Console.WriteLine(checkpointAdvanced
                    ? $"Checkpoint advanced to USN {finalCheckpoint.NextUsn}."
                    : $"Checkpoint retained at USN {checkpoint.NextUsn}.");
                return checkpointAdvanced ? 0 : 2;
            }

            if (usedRecovery && recoveryScan is not null)
            {
                Console.WriteLine("Recovery directory scan:");
                Console.WriteLine($"    Directories visited:          {recoveryScan.DirectoriesVisited}");
                Console.WriteLine($"    Files examined:               {recoveryScan.FilesExamined}");
                Console.WriteLine($"    Files timestamp-qualified:    {recoveryScan.FilesQualifying}");
                Console.WriteLine($"    Scan failures:                {recoveryScan.Failures.Count}");
                Console.WriteLine($"    Peak pending directories:     {recoveryScan.PeakPendingDirectories}\n");
                Console.WriteLine("Journal catch-up:");
            }

            PrintJournalSelection(journalSelection);

            Console.WriteLine($"Source copy failures:              {sourceFailures.Count}");
            Console.WriteLine($"Destination/verification failures: {destinationFailures.Count}");
            Console.WriteLine($"Files copied and verified:         {copied.Count}");
            Console.WriteLine($"Scion:                             {published?.Path ?? "None"}");
            Console.WriteLine(checkpointAdvanced
                ? $"Checkpoint advanced to USN {finalCheckpoint.NextUsn}."
                : $"Checkpoint retained at USN {checkpoint.NextUsn}.");

            if (copied.Count > 0)
            {
                Console.WriteLine("\nFiles copied:");
                foreach (CopyCandidate candidate in copied)
                    Console.WriteLine(candidate.SourcePath);
            }

            if (sourceFailures.Count + destinationFailures.Count > 0)
            {
                Console.WriteLine("\nCopy failures:");
                foreach (CopyFailure failure in sourceFailures.Concat(destinationFailures))
                {
                    Console.WriteLine($"[{failure.FailureKind}] {failure.Candidate.SourcePath}");
                    Console.WriteLine($"    Target: {failure.Candidate.TargetPath}");
                    Console.WriteLine($"    Error:  {failure.ErrorMessage}");
                }
            }

            if (recoveryScan is not null && recoveryScan.Failures.Count > 0)
            {
                Console.WriteLine("\nRecovery scan failures:");
                foreach (RecoveryScanFailure failure in recoveryScan.Failures)
                {
                    Console.WriteLine(failure.Path);
                    Console.WriteLine($"    Error: {failure.ErrorMessage}");
                }
            }

            return checkpointAdvanced ? 0 : 2;
        }
    }
    catch (Exception ex)
    {
        try
        {
            logger?.Write($"ERROR: {ex.Message}");
        }
        catch
        {
            // Preserve the original failure when logging is unavailable.
        }

        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static JournalSelection SelectJournalCandidates(
    NtfsChangeJournal journal,
    JournalReadResult result,
    IReadOnlyList<string> protectedFolders,
    Logger logger)
{
    List<JournalCandidate> distinctIds = result.Changes
        .GroupBy(c => c.FileReferenceNumber)
        .Select(group =>
        {
            JournalChange latest = group.MaxBy(c => c.Usn)!;
            UsnReason reasons = group.Aggregate(UsnReason.None, (all, c) => all | c.Reason);
            return new JournalCandidate(
                latest.FileReferenceNumber,
                latest.ParentFileReferenceNumber,
                latest.Usn,
                latest.TimestampUtc,
                reasons,
                latest.Reason,
                latest.Attributes,
                latest.FileName);
        })
        .OrderBy(c => c.LatestUsn)
        .ToList();

    List<JournalCandidate> deletedIds = distinctIds.Where(c => c.IsDeleted).ToList();
    List<JournalCandidate> directoryIds = distinctIds.Where(c => !c.IsDeleted && c.IsDirectory).ToList();
    List<JournalCandidate> noActionIds = distinctIds.Where(c => !c.IsDeleted && !c.IsDirectory && !c.HasActionableChange).ToList();
    List<JournalCandidate> idsToResolve = distinctIds.Where(c => !c.IsDeleted && !c.IsDirectory && c.HasActionableChange).ToList();
    var located = new List<LocatedCandidate>();
    var resolutionFailures = new List<JournalCandidate>();

    foreach (JournalCandidate candidate in idsToResolve)
    {
        string? path = journal.ResolvePath(candidate.FileReferenceNumber);
        if (path is null)
        {
            resolutionFailures.Add(candidate);
            logger.WriteVerbose($"Unresolved FRN 0x{candidate.FileReferenceNumber:X16}: {candidate.LatestFileName}; {candidate.CombinedReasons}.");
            continue;
        }
        located.Add(new LocatedCandidate(candidate, path));
    }

    List<LocatedCandidate> outside = located.Where(c => !protectedFolders.Any(folder => PathUtility.IsWithin(c.Path, folder))).ToList();
    List<LocatedCandidate> eligible = located.Where(c => protectedFolders.Any(folder => PathUtility.IsWithin(c.Path, folder))).ToList();

    return new JournalSelection(
        result,
        distinctIds,
        deletedIds,
        directoryIds,
        noActionIds,
        idsToResolve,
        resolutionFailures,
        outside,
        eligible);
}

static void PrintJournalSelection(JournalSelection? selection)
{
    if (selection is null)
    {
        Console.WriteLine("Journal records read:              0");
        Console.WriteLine("Files to copy:                     0\n");
        return;
    }

    Console.WriteLine($"Journal records read:              {selection.Result.Changes.Count}");
    Console.WriteLine($"Distinct file IDs:                 {selection.DistinctIds.Count}\n");
    Console.WriteLine($"Eliminated using journal data:     {selection.DeletedIds.Count + selection.DirectoryIds.Count + selection.NoActionIds.Count}");
    Console.WriteLine($"    Deleted:                       {selection.DeletedIds.Count}");
    Console.WriteLine($"    Directories:                   {selection.DirectoryIds.Count}");
    Console.WriteLine($"    No actionable change:          {selection.NoActionIds.Count}");
    Console.WriteLine($"IDs requiring path resolution:     {selection.IdsToResolve.Count}\n");
    Console.WriteLine($"Eliminated after path resolution:  {selection.ResolutionFailures.Count + selection.Outside.Count}");
    Console.WriteLine($"    Resolution failures:           {selection.ResolutionFailures.Count}");
    Console.WriteLine($"    Outside source path:           {selection.Outside.Count}");
    Console.WriteLine($"Files to copy:                     {selection.Eligible.Count}\n");
}

static void CopySourceFile(
    string sourceFilePath,
    string volumeRoot,
    string scionFolder,
    ref StagingScion? staging,
    List<CopyCandidate> copied,
    List<CopyFailure> sourceFailures,
    List<CopyFailure> destinationFailures,
    Logger logger)
{
    staging ??= ScionManager.CreateStaging(scionFolder);

    string relative = Path.GetRelativePath(volumeRoot, sourceFilePath);
    string destination = PathUtility.MapRelativePath(staging.Path, relative);
    var candidate = new CopyCandidate(sourceFilePath, relative, destination);
    VerifiedCopyResult attempt = VerifiedFileCopier.Copy(sourceFilePath, destination);

    if (attempt.Succeeded)
    {
        copied.Add(candidate);
        logger.WriteVerbose($"Verified {sourceFilePath} to {destination}; SHA-256 {attempt.Sha256}.");
        return;
    }

    var failure = new CopyFailure(
        candidate,
        attempt.FailureKind,
        attempt.ErrorMessage ?? "Unknown error");
    logger.Write($"Copy failed ({attempt.FailureKind.ToString().ToLowerInvariant()}): {sourceFilePath} -> {destination}: {failure.ErrorMessage}");

    if (attempt.FailureKind is VerifiedCopyFailureKind.Destination or VerifiedCopyFailureKind.Verification)
        destinationFailures.Add(failure);
    else
        sourceFailures.Add(failure);
}

static ResetResolution ResolveReset(
    ResetRequest request,
    NtfsChangeJournal journal,
    JournalInfo info,
    DateTime infoQueryStartUtc)
{
    if (request.Kind == ResetKind.Now)
    {
        return new ResetResolution(
            info.NextUsn,
            infoQueryStartUtc,
            " at the current journal position",
            false);
    }

    if (request.Kind == ResetKind.Usn)
    {
        long requestedUsn = request.Usn
            ?? throw new InvalidOperationException("The reset USN was not supplied.");

        if (requestedUsn > info.NextUsn)
            throw new ArgumentOutOfRangeException(nameof(request), $"Reset USN {requestedUsn} is ahead of the current journal position {info.NextUsn}.");

        bool predates = requestedUsn < info.FirstUsn;
        long boundedUsn = predates ? info.FirstUsn : requestedUsn;
        (long resolvedUsn, DateTime? recordTimestampUtc) = journal.ResolveUsnAtOrAfter(info, boundedUsn);
        DateTime usnCheckpointTimeUtc = recordTimestampUtc ?? infoQueryStartUtc;

        string description = resolvedUsn == requestedUsn
            ? $" for requested USN {requestedUsn}"
            : $" for requested USN {requestedUsn} (resolved to retained record USN {resolvedUsn})";

        return new ResetResolution(
            resolvedUsn,
            usnCheckpointTimeUtc,
            description,
            predates);
    }

    DateTimeOffset requestedTime = request.Timestamp
        ?? throw new InvalidOperationException("The reset timestamp was not supplied.");
    if (requestedTime.UtcDateTime > DateTime.UtcNow)
        throw new ArgumentOutOfRangeException(nameof(request), "Reset timestamp is later than the current system time.");

    JournalCheckpointSearchResult found = journal.FindFirstAtOrAfter(info, requestedTime.UtcDateTime);
    string requestedText = requestedTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    return new ResetResolution(
        found.NextUsn,
        requestedTime.UtcDateTime,
        $" for requested time {requestedText}",
        found.PredatesRetainedHistory);
}

static void ReportCreatedFiles(
    IReadOnlyList<string> createdFiles,
    bool stateCreated,
    bool stateInitializedFromJournal)
{
    if (createdFiles.Count == 0)
        return;

    Console.WriteLine("scion-capture created the following missing files:");
    foreach (string path in createdFiles)
        Console.WriteLine($"  {path}");

    if (stateCreated)
    {
        Console.WriteLine(
            stateInitializedFromJournal
                ? "The checkpoint was initialized from the current NTFS journal."
                : "The NTFS journal could not be read; an empty checkpoint was created.");
    }
}

static string? GetCheckpointInconsistency(JournalCheckpoint checkpoint, JournalInfo info)
{
    if (checkpoint.JournalId != info.JournalId)
        return "The source volume journal does not match the stored checkpoint.";
    if (checkpoint.NextUsn < info.FirstUsn)
        return "The stored checkpoint is older than the journal's retained records.";
    if (checkpoint.NextUsn > info.NextUsn)
        return "The stored checkpoint is ahead of the current journal.";
    return null;
}

static bool IsAdministrator()
{
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static void ValidatePaths(string volumeRoot, IReadOnlyList<string> protectedFolders, string scionFolder)
{
    if (!string.Equals(Path.GetPathRoot(scionFolder), volumeRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("ScionFolder must be on ProtectedDrive.");
    foreach (string folder in protectedFolders)
        if (!string.Equals(Path.GetPathRoot(folder), volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Protected folder is not on ProtectedDrive: {folder}");
}

file sealed record CopyFailure(CopyCandidate Candidate, VerifiedCopyFailureKind FailureKind, string ErrorMessage);

file sealed record JournalSelection(
    JournalReadResult Result,
    IReadOnlyList<JournalCandidate> DistinctIds,
    IReadOnlyList<JournalCandidate> DeletedIds,
    IReadOnlyList<JournalCandidate> DirectoryIds,
    IReadOnlyList<JournalCandidate> NoActionIds,
    IReadOnlyList<JournalCandidate> IdsToResolve,
    IReadOnlyList<JournalCandidate> ResolutionFailures,
    IReadOnlyList<LocatedCandidate> Outside,
    IReadOnlyList<LocatedCandidate> Eligible);

file sealed record ResetResolution(
    long NextUsn,
    DateTime CheckpointStartTimeUtc,
    string Description,
    bool PredatesRetainedHistory);

file enum ResetKind
{
    Now,
    Timestamp,
    Usn
}

file sealed record ResetRequest(ResetKind Kind, DateTimeOffset? Timestamp = null, long? Usn = null);

file sealed record Options(OutputMode? StdoutOverride, ResetRequest? Reset, string? ConfigurationPath)
{
    public static Options Parse(string[] args)
    {
        OutputMode? mode = null;
        ResetRequest? reset = null;
        string? configurationPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--verbose":
                case "-v":
                    Set(ref mode, OutputMode.Verbose);
                    break;
                case "--normal":
                case "-n":
                    Set(ref mode, OutputMode.Normal);
                    break;
                case "--quiet":
                case "-q":
                    Set(ref mode, OutputMode.Quiet);
                    break;
                case "--config":
                    if (configurationPath is not null)
                        throw new ArgumentException("--config may be specified only once.");
                    if (++index >= args.Length)
                        throw new ArgumentException("--config requires an INI file path.");
                    configurationPath = args[index];
                    break;
                case "--reset":
                    if (reset is not null)
                        throw new ArgumentException("--reset may be specified only once.");

                    string? value = index + 1 < args.Length && !args[index + 1].StartsWith('-')
                        ? args[++index]
                        : null;
                    reset = ParseReset(value);
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return new Options(mode, reset, configurationPath);
    }

    private static ResetRequest ParseReset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("now", StringComparison.OrdinalIgnoreCase))
            return new ResetRequest(ResetKind.Now);

        string text = value.Trim();
        if (text.StartsWith("usn:", StringComparison.OrdinalIgnoreCase))
            text = text[4..];

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long usn))
        {
            if (usn < 0)
                throw new ArgumentException("Reset USN must be nonnegative.");
            return new ResetRequest(ResetKind.Usn, Usn: usn);
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out DateTimeOffset timestamp))
        {
            return new ResetRequest(ResetKind.Timestamp, Timestamp: timestamp);
        }

        throw new ArgumentException($"Invalid reset value: {value}");
    }

    private static void Set(ref OutputMode? current, OutputMode requested)
    {
        if (current is not null && current != requested)
            throw new ArgumentException("Only one stdout override may be specified.");
        current = requested;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: scion-capture [--config <ini-file>] [--quiet | --normal | --verbose] [--reset [now | <timestamp> | <usn>]]");
        Console.WriteLine("A timestamp without an offset is interpreted as local time.");
        Console.WriteLine("A numeric reset value is interpreted as a USN; use an ISO-like form for timestamps.");
        Console.WriteLine("Without --config, scion-capture.ini/.state/.log are stored beside scion-capture.exe.");
        Console.WriteLine("With --config, state and log files use the same base path as the specified INI file.");
    }
}
