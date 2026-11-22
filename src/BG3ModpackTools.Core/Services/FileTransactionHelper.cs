using System.Runtime.InteropServices;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Provides safe same-volume / cross-volume file moves, path containment validation,
/// and empty directory pruning that never deletes protected vanilla BG3 folders.
/// </summary>
public static class FileTransactionHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    public static readonly HashSet<string> DefaultProtectedRelativeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generated",
        @"Generated\Public",
        @"Generated\Public\Shared",
        @"Generated\Public\SharedDev",
        @"Generated\Public\Gustav",
        @"Generated\Public\GustavDev",
        "Public",
        @"Public\Shared",
        @"Public\SharedDev",
        @"Public\Gustav",
        @"Public\GustavDev",
        @"Public\GustavX",
        @"Public\Engine",
        @"Public\Game",
        "Mods",
        @"Mods\Shared",
        @"Mods\SharedDev",
        @"Mods\Gustav",
        @"Mods\GustavDev",
        @"Mods\GustavX",
        "Localization",
        @"Localization\English",
        "Editor",
        "Processed",
        "Projects",
        "Video",
        "bin",
        @"bin\NativeMods"
    };

    /// <summary>
    /// Moves a file safely without overwriting an existing destination, supporting both atomic
    /// same-volume renames and verified cross-volume copy-then-delete moves.
    /// </summary>
    public static void SafeMoveFile(string sourcePath, string destinationPath)
    {
        string fullSrc = Path.GetFullPath(sourcePath);
        string fullDst = Path.GetFullPath(destinationPath);

        if (!File.Exists(fullSrc))
        {
            throw new FileNotFoundException($"Source file does not exist: {fullSrc}", fullSrc);
        }

        if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(fullDst))
        {
            throw new IOException($"Destination file already exists: {fullDst}");
        }

        string? dstDir = Path.GetDirectoryName(fullDst);
        if (!string.IsNullOrEmpty(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }

        if (IsSameVolume(fullSrc, fullDst))
        {
            File.Move(fullSrc, fullDst, overwrite: false);
            return;
        }

        string tmpDst = fullDst + ".bg3tmp";
        try
        {
            long expectedSize = new FileInfo(fullSrc).Length;
            File.Copy(fullSrc, tmpDst, overwrite: true);
            long copiedSize = new FileInfo(tmpDst).Length;
            if (copiedSize != expectedSize)
            {
                throw new IOException($"Cross-volume copy size mismatch for {fullSrc} ({copiedSize} != {expectedSize}).");
            }

            File.Move(tmpDst, fullDst, overwrite: false);
            File.Delete(fullSrc);
        }
        catch
        {
            if (File.Exists(tmpDst))
            {
                try { File.Delete(tmpDst); } catch { }
            }

            throw;
        }
    }

    /// <summary>
    /// Generates a unique in-place backup path (e.g. file.dll.bg3mpm_orig) that does not collide with existing files.
    /// </summary>
    public static string GenerateUniqueBackupPath(string activeFilePath, string suffix = ".bg3mpm_orig")
    {
        string full = Path.GetFullPath(activeFilePath);
        string candidate = full + suffix;
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        int counter = 2;
        while (true)
        {
            string next = $"{full}{suffix}{counter}";
            if (!File.Exists(next))
            {
                return next;
            }

            counter++;
        }
    }

    public static void SafeCopyFile(string sourcePath, string destinationPath, bool overwrite = false)
    {
        string fullSrc = Path.GetFullPath(sourcePath);
        string fullDst = Path.GetFullPath(destinationPath);

        if (!File.Exists(fullSrc))
        {
            throw new FileNotFoundException($"Source file does not exist: {fullSrc}", fullSrc);
        }

        string? dstDir = Path.GetDirectoryName(fullDst);
        if (!string.IsNullOrEmpty(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }

        File.Copy(fullSrc, fullDst, overwrite);
    }

    /// <summary>
    /// Stages a read-only copy of a file for archive packing. Uses an NTFS hard link when source
    /// and destination share a volume so staging adds zero extra disk space; falls back to copying otherwise.
    /// </summary>
    public static bool StageReadOnlyFile(string sourcePath, string destinationPath)
    {
        string fullSrc = Path.GetFullPath(sourcePath);
        string fullDst = Path.GetFullPath(destinationPath);

        if (!File.Exists(fullSrc))
        {
            throw new FileNotFoundException($"Source file does not exist: {fullSrc}", fullSrc);
        }

        if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? dstDir = Path.GetDirectoryName(fullDst);
        if (!string.IsNullOrEmpty(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }

        if (File.Exists(fullDst))
        {
            File.Delete(fullDst);
        }

        if (OperatingSystem.IsWindows() && IsSameVolume(fullSrc, fullDst))
        {
            try
            {
                if (CreateHardLink(fullDst, fullSrc, IntPtr.Zero))
                {
                    return true;
                }
            }
            catch
            {
                // Fall back to regular copy if hard linking fails (e.g. non-NTFS volume or link limit).
            }
        }

        File.Copy(fullSrc, fullDst, overwrite: true);
        return false;
    }

    public static bool IsSameVolume(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        try
        {
            string? rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
            string? rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
            return !string.IsNullOrEmpty(rootA) &&
                   string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a relative path inside a root directory and throws if it escapes via traversal.
    /// </summary>
    public static string ResolveContainedPath(string rootDirectory, string relativePath)
    {
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(rootDirectory));
        string cleaned = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, cleaned));

        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{relativePath}' escapes root directory '{rootDirectory}'.");
        }

        return combined;
    }

    public static bool IsPathStrictlyInsideRoot(string candidatePath, string rootPath)
    {
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        string fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
               fullCandidate.Length > fullRoot.Length;
    }

    /// <summary>
    /// Removes empty directories walking upward from a deleted file up to (but never including) <paramref name="stopRoot"/>
    /// or any protected BG3 base directory.
    /// </summary>
    public static void PruneEmptyParentDirectories(
        string deletedFilePath,
        string stopRoot,
        ISet<string>? protectedRelativeDirs = null)
    {
        if (string.IsNullOrWhiteSpace(stopRoot) || !Directory.Exists(stopRoot))
        {
            return;
        }

        protectedRelativeDirs ??= DefaultProtectedRelativeDirs;
        string fullRoot = Path.GetFullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetDirectoryName(Path.GetFullPath(deletedFilePath));

        while (!string.IsNullOrEmpty(current) &&
               current.Length > fullRoot.Length &&
               IsPathStrictlyInsideRoot(current, fullRoot))
        {
            string rel = Path.GetRelativePath(fullRoot, current)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);

            if (protectedRelativeDirs.Contains(rel))
            {
                break;
            }

            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            try
            {
                Directory.Delete(current, recursive: false);
            }
            catch
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
