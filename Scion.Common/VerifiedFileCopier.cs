using System.Security.Cryptography;

namespace Scion.Common;

public static class VerifiedFileCopier
{
    private const int BufferSize = 1024 * 1024;

    public static VerifiedCopyResult Copy(string sourcePath, string destinationPath)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
            return VerifiedCopyResult.DestinationFailure(
                new InvalidDataException($"Destination path has no parent directory: {destinationPath}"));

        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return VerifiedCopyResult.DestinationFailure(ex);
        }

        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.scion-{Guid.NewGuid():N}.tmp");

        FileStream source;
        DateTime sourceLastWriteUtc;
        try
        {
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);
            sourceLastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return VerifiedCopyResult.SourceFailure(ex);
        }

        using (source)
        {
            FileStream destination;
            try
            {
                destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.WriteThrough);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return VerifiedCopyResult.DestinationFailure(ex);
            }

            byte[] sourceHash;
            long bytesCopied = 0;
            using (destination)
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = source.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        TryDelete(temporaryPath);
                        return VerifiedCopyResult.SourceFailure(ex);
                    }

                    if (bytesRead == 0)
                        break;

                    hasher.AppendData(buffer, 0, bytesRead);
                    try
                    {
                        destination.Write(buffer, 0, bytesRead);
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        TryDelete(temporaryPath);
                        return VerifiedCopyResult.DestinationFailure(ex);
                    }

                    bytesCopied += bytesRead;
                }

                try
                {
                    destination.Flush(flushToDisk: true);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    TryDelete(temporaryPath);
                    return VerifiedCopyResult.DestinationFailure(ex);
                }

                sourceHash = hasher.GetHashAndReset();
            }

            byte[] destinationHash;
            try
            {
                using var destinationRead = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.SequentialScan);
                destinationHash = SHA256.HashData(destinationRead);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                TryDelete(temporaryPath);
                return VerifiedCopyResult.DestinationFailure(ex);
            }

            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            {
                TryDelete(temporaryPath);
                return VerifiedCopyResult.VerificationFailure(
                    Convert.ToHexString(sourceHash),
                    Convert.ToHexString(destinationHash));
            }

            try
            {
                File.SetLastWriteTimeUtc(temporaryPath, sourceLastWriteUtc);
                if (File.Exists(destinationPath)) File.SetAttributes(destinationPath, FileAttributes.Normal);
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                TryDelete(temporaryPath);
                return VerifiedCopyResult.DestinationFailure(ex);
            }

            return VerifiedCopyResult.Success(bytesCopied, Convert.ToHexString(sourceHash));
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original error.
        }
    }
}

public enum VerifiedCopyFailureKind
{
    None,
    Source,
    Destination,
    Verification
}

public sealed record VerifiedCopyResult(
    bool Succeeded,
    VerifiedCopyFailureKind FailureKind,
    string? ErrorMessage,
    long BytesCopied,
    string? Sha256)
{
    public static VerifiedCopyResult Success(long bytesCopied, string sha256) =>
        new(true, VerifiedCopyFailureKind.None, null, bytesCopied, sha256);

    public static VerifiedCopyResult SourceFailure(Exception ex) =>
        Failure(VerifiedCopyFailureKind.Source, Describe(ex));

    public static VerifiedCopyResult DestinationFailure(Exception ex) =>
        Failure(VerifiedCopyFailureKind.Destination, Describe(ex));

    public static VerifiedCopyResult VerificationFailure(string sourceHash, string destinationHash) =>
        Failure(
            VerifiedCopyFailureKind.Verification,
            $"SHA-256 mismatch: source {sourceHash}; destination {destinationHash}");

    private static VerifiedCopyResult Failure(VerifiedCopyFailureKind kind, string message) =>
        new(false, kind, message, 0, null);

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
}
