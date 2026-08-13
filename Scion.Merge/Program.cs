using Scion.Common;
using Scion.Merge;

return Run(args);

static int Run(string[] args)
{
    try
    {
        string collectorName = Environment.MachineName.ToLowerInvariant();
        Options options = Options.Parse(args);
        MergeFiles files = MergeFiles.BesideExecutable();
        IReadOnlyList<string> created = Bootstrapper.CreateMissingFiles(files);
        if (created.Count > 0)
        {
            Console.WriteLine("scion-merge created the following missing files:");
            foreach (string path in created) Console.WriteLine($"  {path}");
            Console.WriteLine("\nReview scion-merge.ini, then run scion-merge again.");
            return 0;
        }

        MergeConfiguration config = MergeConfiguration.Load(files.ConfigurationPath);
        OutputMode stdout = options.StdoutOverride ?? config.Stdout;
        bool verbose = stdout == OutputMode.Verbose;
        bool quiet = stdout == OutputMode.Quiet;
        var logger = new Logger(files.LogPath, config.Log);
        string recoveryTree = config.RecoveryTree;
        Directory.CreateDirectory(recoveryTree);

        var totals = new MergeTotals();
        foreach (string source in config.ScionFolders)
        {
            var sourceTotals = new MergeTotals();
            totals.SourcesExamined++;
            List<ScionDirectory> scions;
            try
            {
                scions = EnumerateScions(source);
                sourceTotals.SourcesExamined++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                totals.SourceFailures++;
                logger.Write($"Source unavailable {source}: {Describe(ex)}");
                if (!quiet) Console.WriteLine($"UNAVAILABLE  {source}: {ex.Message}");
                continue;
            }

            foreach (ScionDirectory scion in scions)
            {
                sourceTotals.ScionsExamined++;
                totals.ScionsExamined++;
                string confirmationPath = Path.Combine(scion.Path, collectorName + ".confirmed");
                if (File.Exists(confirmationPath))
                {
                    sourceTotals.ScionsAlreadyConfirmed++;
                    totals.ScionsAlreadyConfirmed++;
                    continue;
                }

                ScionMergeResult result = MergeScion(scion, recoveryTree, logger, verbose);
                sourceTotals.Add(result);
                totals.Add(result);
                if (result.Succeeded)
                {
                    try
                    {
                        CreateConfirmation(confirmationPath);
                        sourceTotals.ScionsConfirmed++;
                        totals.ScionsConfirmed++;
                        logger.Write($"Confirmed {scion.Path}; {result.FilesCopied} copied; {result.FilesSkipped} skipped; {result.BytesCopied} bytes.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                    {
                        result.Failures++;
                        sourceTotals.Failures++;
                        totals.Failures++;
                        sourceTotals.ScionsFailed++;
                        totals.ScionsFailed++;
                        logger.Write($"Unable to confirm {scion.Path}: {Describe(ex)}");
                    }
                }
                else
                {
                    sourceTotals.ScionsFailed++;
                    totals.ScionsFailed++;
                    logger.Write($"Not confirmed {scion.Path}; {result.Failures} failures.");
                }
            }

            if (!quiet)
                Console.WriteLine($"{source}: {sourceTotals.ScionsExamined} scions, {sourceTotals.FilesCopied} copied, {sourceTotals.FilesSkipped} skipped, {sourceTotals.Failures} failed");
        }

        logger.Write($"Merge complete; {totals.SourcesExamined} sources; {totals.SourceFailures} unavailable; {totals.ScionsExamined} scions; " +
            $"{totals.ScionsConfirmed} confirmed; {totals.FilesCopied} copied; {totals.FilesSkipped} skipped; {totals.Failures} failures; {totals.BytesCopied} bytes.");

        if (quiet)
        {
            Console.WriteLine($"{totals.FilesCopied} files copied and verified; {totals.ScionsConfirmed} scions confirmed; {totals.Failures + totals.SourceFailures} failures.");
        }
        else
        {
            Console.WriteLine("\nTotals:");
            Console.WriteLine($"Sources unavailable:       {totals.SourceFailures}");
            Console.WriteLine($"Scions examined:           {totals.ScionsExamined}");
            Console.WriteLine($"Already confirmed:         {totals.ScionsAlreadyConfirmed}");
            Console.WriteLine($"Newly confirmed:           {totals.ScionsConfirmed}");
            Console.WriteLine($"Scions failed:              {totals.ScionsFailed}");
            Console.WriteLine($"Files copied and verified: {totals.FilesCopied}");
            Console.WriteLine($"Files skipped:             {totals.FilesSkipped}");
            Console.WriteLine($"Copy failures:             {totals.Failures}");
            Console.WriteLine($"Bytes copied:              {totals.BytesCopied:N0}");
        }
        return totals.Failures + totals.SourceFailures == 0 ? 0 : 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static List<ScionDirectory> EnumerateScions(string source)
{
    return new DirectoryInfo(source).EnumerateDirectories()
        .Select(directory =>
        {
            if (!ScionNaming.TryParse(directory.Name, out DateTime timestamp, out int suffix)) return null;
            return new ScionDirectory(directory.FullName, timestamp, suffix);
        })
        .Where(s => s is not null)
        .Cast<ScionDirectory>()
        .OrderBy(s => s.Timestamp)
        .ThenBy(s => s.Suffix)
        .ToList();
}

static ScionMergeResult MergeScion(ScionDirectory scion, string recoveryTree, Logger logger, bool verbose)
{
    var result = new ScionMergeResult();
    List<string> files;
    try
    {
        // Only files beneath directories in the scion are mergeable. Top-level files
        // are metadata such as collector confirmation files and are intentionally ignored.
        files = new DirectoryInfo(scion.Path).EnumerateDirectories()
            .SelectMany(directory => directory.EnumerateFiles("*", SearchOption.AllDirectories))
            .Select(file => file.FullName)
            .ToList();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        result.Failures++;
        logger.Write($"Unable to enumerate {scion.Path}: {Describe(ex)}");
        return result;
    }

    foreach (string sourceFile in files)
    {
        result.FilesExamined++;
        string relative = Path.GetRelativePath(scion.Path, sourceFile);
        string destination;
        try
        {
            destination = PathUtility.MapRelativePath(recoveryTree, relative);
            FileInfo incoming = new(sourceFile);
            if (File.Exists(destination))
            {
                FileInfo existing = new(destination);
                int comparison = DateTime.Compare(incoming.LastWriteTimeUtc, existing.LastWriteTimeUtc);
                if (comparison < 0 || comparison == 0)
                {
                    result.FilesSkipped++;
                    if (comparison == 0 && incoming.Length != existing.Length)
                        logger.Write($"Equal-timestamp collision retained existing file: {destination}; incoming length {incoming.Length}, existing length {existing.Length}.");
                    else if (verbose)
                        logger.WriteVerbose($"Skipped {sourceFile}; destination is same age or newer.");
                    continue;
                }
            }

            VerifiedCopyResult copy = VerifiedFileCopier.Copy(sourceFile, destination);
            if (!copy.Succeeded)
            {
                result.Failures++;
                logger.Write($"Merge copy failed ({copy.FailureKind.ToString().ToLowerInvariant()}): {sourceFile} -> {destination}: {copy.ErrorMessage}");
                continue;
            }
            result.FilesCopied++;
            result.BytesCopied += copy.BytesCopied;
            logger.WriteVerbose($"Merged and verified {sourceFile} -> {destination}; SHA-256 {copy.Sha256}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            result.Failures++;
            logger.Write($"Unable to process {sourceFile}: {Describe(ex)}");
        }
    }
    return result;
}

static void CreateConfirmation(string finalPath)
{
    string temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
    File.WriteAllBytes(temporaryPath, []);
    File.Move(temporaryPath, finalPath, overwrite: false);
}

static string Describe(Exception ex) => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";

file sealed class ScionMergeResult
{
    public int FilesExamined { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int Failures { get; set; }
    public long BytesCopied { get; set; }
    public bool Succeeded => Failures == 0;
}

file sealed class MergeTotals
{
    public int SourcesExamined { get; set; }
    public int SourceFailures { get; set; }
    public int ScionsExamined { get; set; }
    public int ScionsAlreadyConfirmed { get; set; }
    public int ScionsConfirmed { get; set; }
    public int ScionsFailed { get; set; }
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int Failures { get; set; }
    public long BytesCopied { get; set; }
    public void Add(ScionMergeResult result)
    {
        FilesCopied += result.FilesCopied;
        FilesSkipped += result.FilesSkipped;
        Failures += result.Failures;
        BytesCopied += result.BytesCopied;
    }
    public void Add(MergeTotals other)
    {
        SourcesExamined += other.SourcesExamined;
        SourceFailures += other.SourceFailures;
        ScionsExamined += other.ScionsExamined;
        ScionsAlreadyConfirmed += other.ScionsAlreadyConfirmed;
        ScionsConfirmed += other.ScionsConfirmed;
        ScionsFailed += other.ScionsFailed;
        FilesCopied += other.FilesCopied;
        FilesSkipped += other.FilesSkipped;
        Failures += other.Failures;
        BytesCopied += other.BytesCopied;
    }
}

file sealed record Options(OutputMode? StdoutOverride)
{
    public static Options Parse(string[] args)
    {
        OutputMode? mode = null;
        foreach (string arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--verbose": case "-v": Set(ref mode, OutputMode.Verbose); break;
                case "--normal": case "-n": Set(ref mode, OutputMode.Normal); break;
                case "--quiet": case "-q": Set(ref mode, OutputMode.Quiet); break;
                case "--help": case "-h": case "/?": PrintUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown option: {arg}");
            }
        }
        return new Options(mode);
    }
    private static void Set(ref OutputMode? current, OutputMode requested)
    {
        if (current is not null && current != requested) throw new ArgumentException("Only one stdout override may be specified.");
        current = requested;
    }
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: scion-merge [--quiet | --normal | --verbose]");
        Console.WriteLine("Files are stored beside scion-merge.exe.");
    }
}
