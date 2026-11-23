using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Manages the single-copy Mod Library, Vault storage, disk reconciliation, bulk backup imports,
/// and profile creation/synchronization from BG3ModManager JSON load orders.
/// </summary>
public static class ModLibraryService
{
    private static readonly HashSet<string> EditableConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".toml", ".ini", ".cfg", ".json", ".txt", ".xml"
    };

    public static string GetActivePakPath(AppSettings settings, ModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            throw new InvalidOperationException($"Mod '{mod.DisplayName}' has no PAK filename.");
        }

        return Path.Combine(settings.ModsFolder, mod.PakFileName);
    }

    public static string GetVaultPakPath(string vaultFolder, ModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            throw new InvalidOperationException($"Mod '{mod.DisplayName}' has no PAK filename.");
        }

        return Path.Combine(vaultFolder, "paks", mod.Id, mod.PakFileName);
    }

    public static string GetActiveTrackedFilePath(AppSettings settings, TrackedFileRecord record)
    {
        string root = record.Target == TrackedFileTarget.DataFolder
            ? settings.DataFolder
            : settings.GameFolder;

        return FileTransactionHelper.ResolveContainedPath(root, record.RelativePath);
    }

    public static string GetVaultTrackedFilePath(string vaultFolder, ModEntry mod, TrackedFileRecord record)
    {
        string bucket = record.Target == TrackedFileTarget.DataFolder ? "data" : "game";
        string modRoot = Path.Combine(vaultFolder, bucket, mod.Id);
        return FileTransactionHelper.ResolveContainedPath(modRoot, record.RelativePath);
    }

    /// <summary>
    /// Builds a ModEntry from a PAK on disk (using already computed PakInfo and SHA-256).
    /// </summary>
    public static ModEntry CreatePakModEntry(
        PakInfo info,
        string sha256,
        LibraryDatabase db,
        ModLocation location,
        string? sourceArchiveName = null)
    {
        return new ModEntry
        {
            Id = HashService.GenerateUniqueModId(sha256, db.Mods),
            Kind = ModKind.Pak,
            DisplayName = string.IsNullOrWhiteSpace(info.Name)
                ? Path.GetFileNameWithoutExtension(info.FileName)
                : info.Name,
            Uuid = info.Uuid,
            FolderName = info.Folder,
            Author = info.Author,
            Version64 = info.Version64,
            VersionString = info.VersionString,
            Md5 = info.Md5,
            PublishHandle = info.PublishHandle,
            PakFileName = info.FileName,
            PakSha256 = sha256,
            ContentHash = sha256,
            TotalSizeBytes = info.FileSizeBytes,
            HasMetaLsx = info.HasMetaLsx,
            IsDetectedOverride = info.IsDetectedOverride,
            DetectedOverrideKind = info.OverrideKind,
            OverrideReasons = info.OverrideReasons.ToList(),
            HasGeneratedSharedAssets = info.HasGeneratedSharedAssets,
            Location = location,
            SourceArchiveName = sourceArchiveName,
            ImportedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Scans the configured Mods folder, adopts any untracked .pak files into the Library in-place,
    /// and reconciles on-disk locations for all existing Library entries.
    /// </summary>
    public static async Task<(int AdoptedCount, int UpdatedCount)> ScanAndAdoptActiveModsAsync(
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int adopted = 0;
        if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && Directory.Exists(settings.ModsFolder))
        {
            string[] pakFiles = Directory.GetFiles(settings.ModsFolder, "*.pak", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var byPakHash = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in db.Mods.OrderByDescending(x => x.Location == ModLocation.Active))
            {
                string? pakHash = m.EffectivePakSha256;
                if (!string.IsNullOrEmpty(pakHash))
                {
                    byPakHash.TryAdd(pakHash, m);
                }

                if (!string.IsNullOrEmpty(m.ContentHash))
                {
                    byPakHash.TryAdd(m.ContentHash, m);
                }
            }

            for (int i = 0; i < pakFiles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pakPath = pakFiles[i];
                progress?.Report($"Scanning Mods folder ({i + 1}/{pakFiles.Length}): {Path.GetFileName(pakPath)}");

                (PakInfo info, string sha256, _) = await HashService.InspectPakCachedAsync(
                    pakPath,
                    cache,
                    computeHash: true,
                    cancellationToken);

                if (byPakHash.TryGetValue(sha256, out ModEntry? existing))
                {
                    RefreshPakMetadata(existing, info);
                    if (existing.TrackedFiles.Count == 0)
                    {
                        existing.Location = ModLocation.Active;
                    }
                }
                else
                {
                    ModEntry entry = CreatePakModEntry(info, sha256, db, ModLocation.Active);
                    db.Mods.Add(entry);
                    byPakHash[sha256] = entry;
                    adopted++;
                }
            }
        }

        int updated = ReconcileDiskState(settings, db);
        return (adopted, updated);
    }

    /// <summary>
    /// Checks whether every ModEntry's payload currently resides in Active folders, Vault, Partial, or Missing.
    /// </summary>
    public static int ReconcileDiskState(AppSettings settings, LibraryDatabase db)
    {
        int changed = 0;
        foreach (ModEntry mod in db.Mods)
        {
            ModLocation computed = EvaluateModLocation(settings, mod);
            if (mod.Location != computed)
            {
                mod.Location = computed;
                changed++;
            }
        }

        ReconcileActiveProfileId(settings, db);
        return changed;
    }

    public static ModLocation EvaluateModLocation(AppSettings settings, ModEntry mod)
    {
        int expectedParts = 0;
        int activeParts = 0;
        int vaultParts = 0;

        if (!string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            expectedParts++;
            if (!string.IsNullOrWhiteSpace(settings.VaultFolder) && File.Exists(GetVaultPakPath(settings.VaultFolder, mod)))
            {
                vaultParts++;
            }
            else if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && File.Exists(GetActivePakPath(settings, mod)))
            {
                activeParts++;
            }
        }

        foreach (TrackedFileRecord tf in mod.TrackedFiles)
        {
            expectedParts++;
            string activeRoot = tf.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
            if (!string.IsNullOrWhiteSpace(settings.VaultFolder) && File.Exists(GetVaultTrackedFilePath(settings.VaultFolder, mod, tf)))
            {
                vaultParts++;
            }
            else if (!string.IsNullOrWhiteSpace(activeRoot))
            {
                string activePath = GetActiveTrackedFilePath(settings, tf);
                if (File.Exists(activePath))
                {
                    bool sizeMatches = tf.FileSizeBytes <= 0 ||
                                       EditableConfigExtensions.Contains(Path.GetExtension(tf.RelativePath)) ||
                                       new FileInfo(activePath).Length == tf.FileSizeBytes;
                    if (sizeMatches)
                    {
                        activeParts++;
                    }
                }
            }
        }

        if (expectedParts == 0)
        {
            return ModLocation.Missing;
        }

        if (activeParts == expectedParts)
        {
            return ModLocation.Active;
        }

        if (vaultParts == expectedParts)
        {
            return ModLocation.Vault;
        }

        if (activeParts == 0 && vaultParts == 0)
        {
            return ModLocation.Missing;
        }

        return ModLocation.Partial;
    }

    /// <summary>
    /// Imports unique .pak files from an existing backup folder (such as Mods-off) into the Vault,
    /// skipping exact content-hash duplicates so only one copy exists.
    /// </summary>
    public static async Task<(int ImportedCount, int SkippedDuplicateCount, List<string> Errors)> BulkImportFromBackupFolderAsync(
        string sourceFolder,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        bool moveInsteadOfCopy = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");
        }

        if (string.IsNullOrWhiteSpace(settings.VaultFolder))
        {
            throw new InvalidOperationException("Vault folder is not configured.");
        }

        Directory.CreateDirectory(settings.VaultFolder);

        string[] pakFiles = Directory.GetFiles(sourceFolder, "*.pak", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingHashes = new HashSet<string>(
            db.Mods.Select(m => m.ContentHash).Where(h => !string.IsNullOrEmpty(h)),
            StringComparer.OrdinalIgnoreCase);

        int imported = 0;
        int duplicates = 0;
        var errors = new List<string>();

        for (int i = 0; i < pakFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string srcPath = pakFiles[i];
            string fileName = Path.GetFileName(srcPath);
            progress?.Report($"Checking {fileName} ({i + 1}/{pakFiles.Length})...");

            try
            {
                (PakInfo info, string sha256, _) = await HashService.InspectPakCachedAsync(
                    srcPath,
                    cache,
                    computeHash: true,
                    cancellationToken);

                if (existingHashes.Contains(sha256))
                {
                    duplicates++;
                    continue;
                }

                ModEntry entry = CreatePakModEntry(info, sha256, db, ModLocation.Vault, sourceArchiveName: Path.GetFileName(sourceFolder));
                string dstPath = GetVaultPakPath(settings.VaultFolder, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);

                if (moveInsteadOfCopy)
                {
                    FileTransactionHelper.SafeMoveFile(srcPath, dstPath);
                }
                else
                {
                    FileTransactionHelper.SafeCopyFile(srcPath, dstPath, overwrite: false);
                }

                db.Mods.Add(entry);
                existingHashes.Add(sha256);
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}: {ex.Message}");
            }
        }

        return (imported, duplicates, errors);
    }

    /// <summary>
    /// Registers an existing folder inside the BG3 Data or Game directory as a tracked loose mod in-place.
    /// </summary>
    public static async Task<ModEntry> RegisterExistingLooseFolderAsync(
        string folderPath,
        TrackedFileTarget target,
        string displayName,
        AppSettings settings,
        LibraryDatabase db,
        CancellationToken cancellationToken = default)
    {
        string fullFolder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullFolder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {fullFolder}");
        }

        // Redirect Data subdirectories automatically if the user picked Data inside GameFolder.
        if (target == TrackedFileTarget.GameFolder &&
            !string.IsNullOrWhiteSpace(settings.DataFolder) &&
            (string.Equals(fullFolder, Path.GetFullPath(settings.DataFolder), StringComparison.OrdinalIgnoreCase) ||
             FileTransactionHelper.IsPathStrictlyInsideRoot(fullFolder, settings.DataFolder)))
        {
            target = TrackedFileTarget.DataFolder;
        }

        string root = target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException($"Target root folder ({target}) is not configured or missing.");
        }

        if (!FileTransactionHelper.IsPathStrictlyInsideRoot(fullFolder, root))
        {
            throw new InvalidOperationException($"Selected folder '{fullFolder}' must be strictly inside '{root}'.");
        }

        string relativeFolder = Path.GetRelativePath(root, fullFolder)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

        if (FileTransactionHelper.DefaultProtectedRelativeDirs.Contains(relativeFolder))
        {
            throw new InvalidOperationException(
                $"'{relativeFolder}' is a protected vanilla BG3 directory. Select a mod-specific subfolder inside it instead.");
        }

        string[] files = Directory.GetFiles(fullFolder, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException("Selected folder contains no files.");
        }

        var tracked = new List<TrackedFileRecord>(files.Length);
        var hashPairs = new List<(string Key, string Sha256)>(files.Length);
        long totalSize = 0;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            long len = new FileInfo(file).Length;
            string sha = await HashService.ComputeFileSha256Async(file, cancellationToken);

            tracked.Add(new TrackedFileRecord
            {
                Target = target,
                RelativePath = rel,
                FileSizeBytes = len,
                Sha256 = sha
            });

            hashPairs.Add(($"{target}:{rel}", sha));
            totalSize += len;
        }

        string compositeHash = HashService.ComputeCompositeHash(hashPairs);
        ModEntry? existing = db.Mods.FirstOrDefault(m => string.Equals(m.ContentHash, compositeHash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var entry = new ModEntry
        {
            Id = HashService.GenerateUniqueModId(compositeHash, db.Mods),
            Kind = target == TrackedFileTarget.DataFolder ? ModKind.LooseData : ModKind.LooseGame,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(fullFolder) : displayName.Trim(),
            ContentHash = compositeHash,
            TotalSizeBytes = totalSize,
            IsDetectedOverride = true,
            OverrideReasons = new List<string> { $"Loose files in {(target == TrackedFileTarget.DataFolder ? "BG3 Data" : "BG3 Game")}" },
            TrackedFiles = tracked,
            Location = ModLocation.Active,
            ImportedAtUtc = DateTime.UtcNow
        };

        db.Mods.Add(entry);
        return entry;
    }

    public sealed record ModUninstallResult(
        int RemovedFilesCount,
        List<string> RestoredOriginalFiles,
        List<string> Warnings);

    public static void MoveModToVault(ModEntry mod, AppSettings settings)
    {
        GameProcessGuard.EnsureGameNotRunning($"moving '{mod.DisplayName}' to Vault");

        if (!string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            string activePak = GetActivePakPath(settings, mod);
            string vaultPak = GetVaultPakPath(settings.VaultFolder, mod);
            if (File.Exists(activePak) && !File.Exists(vaultPak))
            {
                FileTransactionHelper.SafeMoveFile(activePak, vaultPak);
            }
        }

        var touchedDirs = new List<(string ActiveFile, string StopRoot)>();
        foreach (TrackedFileRecord tf in mod.TrackedFiles)
        {
            string activeFile = GetActiveTrackedFilePath(settings, tf);
            string vaultFile = GetVaultTrackedFilePath(settings.VaultFolder, mod, tf);
            if (File.Exists(activeFile) && !File.Exists(vaultFile))
            {
                FileTransactionHelper.SafeMoveFile(activeFile, vaultFile);
                string stopRoot = tf.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
                touchedDirs.Add((activeFile, stopRoot));
            }
        }

        // Restore any in-place renamed original game files now that the mod's files are cleared.
        foreach (ReplacedOriginalFileRecord orig in mod.ReplacedOriginalFiles)
        {
            string root = orig.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string activeOrig = FileTransactionHelper.ResolveContainedPath(root, orig.RelativePath);
            string backupOrig = FileTransactionHelper.ResolveContainedPath(root, orig.BackupRelativePath);
            if (File.Exists(backupOrig) && !File.Exists(activeOrig))
            {
                FileTransactionHelper.SafeMoveFile(backupOrig, activeOrig);
                orig.IsCurrentlyRestored = true;
            }
        }

        foreach ((string activeFile, string stopRoot) in touchedDirs)
        {
            FileTransactionHelper.PruneEmptyParentDirectories(activeFile, stopRoot);
        }

        mod.Location = EvaluateModLocation(settings, mod);
    }

    /// <summary>
    /// Renames a pre-existing original game file in place (e.g. file.dll -> file.dll.bg3mpm_orig) and records it
    /// on <paramref name="mod"/> so deactivating or uninstalling automatically restores the original file.
    /// </summary>
    public static ReplacedOriginalFileRecord BackupOriginalTrackedFileInPlace(
        ModEntry mod,
        TrackedFileRecord tf,
        string existingActiveFile,
        AppSettings settings,
        string? precomputedSha256 = null)
    {
        string root = tf.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
        string backupFull = FileTransactionHelper.GenerateUniqueBackupPath(existingActiveFile);
        long origSize = new FileInfo(existingActiveFile).Length;
        string origSha = precomputedSha256 ?? HashService.ComputeFileSha256(existingActiveFile);
        FileTransactionHelper.SafeMoveFile(existingActiveFile, backupFull);
        string backupRel = Path.GetRelativePath(root, backupFull).Replace('\\', '/');

        ReplacedOriginalFileRecord? existingRec = mod.ReplacedOriginalFiles.FirstOrDefault(r =>
            r.Target == tf.Target &&
            string.Equals(r.RelativePath, tf.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (existingRec is not null)
        {
            existingRec.BackupRelativePath = backupRel;
            existingRec.FileSizeBytes = origSize;
            existingRec.Sha256 = origSha;
            existingRec.IsCurrentlyRestored = false;
            return existingRec;
        }

        var record = new ReplacedOriginalFileRecord
        {
            Target = tf.Target,
            RelativePath = tf.RelativePath,
            BackupRelativePath = backupRel,
            FileSizeBytes = origSize,
            Sha256 = origSha,
            IsCurrentlyRestored = false
        };
        mod.ReplacedOriginalFiles.Add(record);
        return record;
    }

    public static void ActivateModFromVault(ModEntry mod, AppSettings settings)
    {
        GameProcessGuard.EnsureGameNotRunning($"activating '{mod.DisplayName}' from Vault");

        if (!string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            string vaultPak = GetVaultPakPath(settings.VaultFolder, mod);
            string activePak = GetActivePakPath(settings, mod);
            if (File.Exists(vaultPak))
            {
                FileTransactionHelper.SafeMoveFile(vaultPak, activePak);
                string paksModDir = Path.GetDirectoryName(vaultPak)!;
                if (Directory.Exists(paksModDir) && !Directory.EnumerateFileSystemEntries(paksModDir).Any())
                {
                    Directory.Delete(paksModDir, recursive: false);
                }
            }
        }

        foreach (TrackedFileRecord tf in mod.TrackedFiles)
        {
            string vaultFile = GetVaultTrackedFilePath(settings.VaultFolder, mod, tf);
            string activeFile = GetActiveTrackedFilePath(settings, tf);
            if (File.Exists(vaultFile))
            {
                if (File.Exists(activeFile))
                {
                    string activeSha = HashService.ComputeFileSha256(activeFile);
                    string vaultSha = HashService.ComputeFileSha256(vaultFile);

                    if (string.Equals(activeSha, vaultSha, StringComparison.OrdinalIgnoreCase))
                    {
                        // Identical file already present on disk: replace without creating a redundant self-backup.
                        File.Delete(activeFile);
                    }
                    else
                    {
                        bool hasPriorBackup = mod.ReplacedOriginalFiles.Any(r =>
                            r.Target == tf.Target &&
                            string.Equals(r.RelativePath, tf.RelativePath, StringComparison.OrdinalIgnoreCase));

                        if (hasPriorBackup || mod.BackupReplacedOriginals)
                        {
                            _ = BackupOriginalTrackedFileInPlace(mod, tf, activeFile, settings, activeSha);
                        }
                    }
                }

                FileTransactionHelper.SafeMoveFile(vaultFile, activeFile);
                string bucket = tf.Target == TrackedFileTarget.DataFolder ? "data" : "game";
                string modRoot = Path.Combine(settings.VaultFolder, bucket, mod.Id);
                FileTransactionHelper.PruneEmptyParentDirectories(vaultFile, modRoot);
                if (Directory.Exists(modRoot) && !Directory.EnumerateFileSystemEntries(modRoot).Any())
                {
                    Directory.Delete(modRoot, recursive: false);
                }
            }
        }

        mod.Location = EvaluateModLocation(settings, mod);
    }

    /// <summary>
    /// Safely prepares an active destination path before moving a tracked loose mod file into it:
    /// backs up files owned by another tracked mod into Vault/_backups, deletes exact SHA-256 duplicates cleanly,
    /// and renames differing existing/original game files in place (.bg3mpm_orig) so uninstall restores them.
    /// </summary>
    public static void ReplaceActiveTrackedFileSafely(
        string dstFile,
        TrackedFileRecord rec,
        string incomingSha256,
        ModEntry importingMod,
        AppSettings settings,
        LibraryDatabase db,
        List<string>? backedUpFiles = null,
        List<string>? renamedOriginals = null)
    {
        if (!File.Exists(dstFile))
        {
            return;
        }

        bool ownedByOtherMod = db.Mods.Any(m =>
            !string.Equals(m.Id, importingMod.Id, StringComparison.OrdinalIgnoreCase) &&
            m.TrackedFiles.Any(tf =>
                tf.Target == rec.Target &&
                string.Equals(tf.RelativePath, rec.RelativePath, StringComparison.OrdinalIgnoreCase)));

        if (ownedByOtherMod)
        {
            BackupFileToVault(dstFile, rec.RelativePath, settings, backedUpFiles);
            File.Delete(dstFile);
            return;
        }

        string existingSha = HashService.ComputeFileSha256(dstFile);
        if (string.Equals(existingSha, incomingSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(dstFile);
            return;
        }

        ReplacedOriginalFileRecord origRec = BackupOriginalTrackedFileInPlace(importingMod, rec, dstFile, settings, existingSha);
        string targetRoot = rec.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
        renamedOriginals?.Add($"{rec.RelativePath} -> {origRec.BackupRelativePath}");
        backedUpFiles?.Add(FileTransactionHelper.ResolveContainedPath(targetRoot, origRec.BackupRelativePath));
    }

    public static void BackupFileToVault(
        string existingFilePath,
        string relativePath,
        AppSettings settings,
        List<string>? recordedBackupPaths = null)
    {
        if (string.IsNullOrWhiteSpace(settings.VaultFolder))
        {
            return;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string backupRoot = Path.Combine(settings.VaultFolder, "_backups", stamp);
        string backupPath = FileTransactionHelper.ResolveContainedPath(backupRoot, relativePath);
        FileTransactionHelper.SafeCopyFile(existingFilePath, backupPath, overwrite: true);
        recordedBackupPaths?.Add(backupPath);
    }

    /// <summary>
    /// Completely uninstalls a mod from Active folders and Vault, restores any renamed original game files,
    /// prunes empty mod directories, and removes its references from all profiles and the Library.
    /// </summary>
    public static ModUninstallResult UninstallMod(ModEntry mod, AppSettings settings, LibraryDatabase db)
    {
        GameProcessGuard.EnsureGameNotRunning($"uninstalling '{mod.DisplayName}'");

        int removedCount = 0;
        var restoredOriginals = new List<string>();
        var warnings = new List<string>();
        var pruneTargets = new List<(string FilePath, string StopRoot)>();

        if (!string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            if (!string.IsNullOrWhiteSpace(settings.ModsFolder))
            {
                string activePak = GetActivePakPath(settings, mod);
                string expectedPakSha = mod.EffectivePakSha256 ?? mod.ContentHash;
                if (File.Exists(activePak) &&
                    string.Equals(HashService.ComputeFileSha256(activePak), expectedPakSha, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(activePak);
                    removedCount++;
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.VaultFolder))
            {
                string vaultPak = GetVaultPakPath(settings.VaultFolder, mod);
                if (File.Exists(vaultPak))
                {
                    File.Delete(vaultPak);
                    removedCount++;
                    pruneTargets.Add((vaultPak, settings.VaultFolder));
                }
            }
        }

        foreach (TrackedFileRecord tf in mod.TrackedFiles)
        {
            string root = tf.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
            if (!string.IsNullOrWhiteSpace(root))
            {
                string activeFile = GetActiveTrackedFilePath(settings, tf);
                if (File.Exists(activeFile))
                {
                    File.Delete(activeFile);
                    removedCount++;
                    pruneTargets.Add((activeFile, root));
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.VaultFolder))
            {
                string vaultFile = GetVaultTrackedFilePath(settings.VaultFolder, mod, tf);
                if (File.Exists(vaultFile))
                {
                    File.Delete(vaultFile);
                    removedCount++;
                    pruneTargets.Add((vaultFile, settings.VaultFolder));
                }
            }
        }

        foreach (ReplacedOriginalFileRecord orig in mod.ReplacedOriginalFiles)
        {
            string root = orig.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string activeOrig = FileTransactionHelper.ResolveContainedPath(root, orig.RelativePath);
            string backupOrig = FileTransactionHelper.ResolveContainedPath(root, orig.BackupRelativePath);
            if (!File.Exists(backupOrig))
            {
                continue;
            }

            if (!File.Exists(activeOrig))
            {
                FileTransactionHelper.SafeMoveFile(backupOrig, activeOrig);
                orig.IsCurrentlyRestored = true;
                restoredOriginals.Add(orig.RelativePath);
            }
            else
            {
                warnings.Add($"Could not restore '{orig.RelativePath}' because a file already exists there (backup preserved at '{orig.BackupRelativePath}').");
            }
        }

        foreach ((string path, string stopRoot) in pruneTargets)
        {
            FileTransactionHelper.PruneEmptyParentDirectories(path, stopRoot);
        }

        var affectedProfiles = new List<ModProfile>();
        foreach (ModProfile p in db.Profiles)
        {
            if (p.ModIds.RemoveAll(id => string.Equals(id, mod.Id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                p.UpdatedAtUtc = DateTime.UtcNow;
                affectedProfiles.Add(p);
            }
        }

        db.Mods.Remove(mod);

        foreach (ModProfile affectedProfile in affectedProfiles)
        {
            try
            {
                WriteProfileLoadOrderFiles(affectedProfile, settings, db);
            }
            catch (Exception ex)
            {
                warnings.Add($"Updated profile '{affectedProfile.Name}', but failed syncing load order files: {ex.Message}");
            }
        }

        return new ModUninstallResult(removedCount, restoredOriginals, warnings);
    }

    /// <summary>
    /// Adopts and moves every UnlistedOverride (or all unlisted PAKs if requested) from Mods/ into the Vault.
    /// </summary>
    public static async Task<int> QuarantineUnlistedPaksAsync(
        LoadOrderAuditReport report,
        bool includeUnlistedNonOverrides,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        CancellationToken cancellationToken = default)
    {
        GameProcessGuard.EnsureGameNotRunning("moving unlisted PAKs to Vault");
        var targets = report.Items
            .Where(item =>
                item.Status == ReviewStatus.UnlistedOverride ||
                (includeUnlistedNonOverrides && item.Status == ReviewStatus.UnlistedInactive))
            .Where(item => !string.IsNullOrWhiteSpace(item.PakFullPath) && File.Exists(item.PakFullPath))
            .ToList();

        int moved = 0;
        foreach (LoadOrderReviewItem item in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AdoptAndMovePakToVaultAsync(item.PakFullPath!, settings, db, cache, cancellationToken);
            moved++;
        }

        return moved;
    }

    public static async Task<ModEntry> AdoptAndMovePakToVaultAsync(
        string pakFullPath,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        CancellationToken cancellationToken = default)
    {
        GameProcessGuard.EnsureGameNotRunning($"moving '{Path.GetFileName(pakFullPath)}' to Vault");
        if (!File.Exists(pakFullPath))
        {
            throw new FileNotFoundException($"PAK file not found: {pakFullPath}", pakFullPath);
        }

        (PakInfo info, string sha256, _) = await HashService.InspectPakCachedAsync(pakFullPath, cache, computeHash: true, cancellationToken);
        ModEntry? mod = db.Mods.FirstOrDefault(m =>
            string.Equals(m.EffectivePakSha256, sha256, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.ContentHash, sha256, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
        {
            mod = CreatePakModEntry(info, sha256, db, ModLocation.Active);
            db.Mods.Add(mod);
        }

        MoveModToVault(mod, settings);
        return mod;
    }

    /// <summary>
    /// Creates a profile representing the currently active BG3 setup, excluding BG3MM Inactive Mods when using modsettings.lsx.
    /// </summary>
    public static async Task<ModProfile> CaptureActiveProfileFromModSettingsAsync(
        string profileName,
        bool useModSettingsLsx,
        bool includeStandaloneOverrides,
        bool includeActiveLooseMods,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ScanAndAdoptActiveModsAsync(settings, db, cache, progress, cancellationToken);

        var activeMods = db.Mods.Where(m => m.Location == ModLocation.Active).ToList();
        var orderedIds = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasLsx = useModSettingsLsx &&
                      !string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath) &&
                      File.Exists(settings.ModSettingsLsxPath);

        if (hasLsx)
        {
            List<ModSettingsEntry> lsx = Bg3LoadOrderService.ReadModSettingsLsx(settings.ModSettingsLsxPath);
            var activeByUuid = activeMods
                .Where(m => !string.IsNullOrWhiteSpace(m.Uuid))
                .GroupBy(m => m.Uuid!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (ModSettingsEntry lsxEntry in lsx)
            {
                if (!activeByUuid.TryGetValue(lsxEntry.Uuid, out List<ModEntry>? candidates) || candidates.Count == 0)
                {
                    continue;
                }

                ModEntry chosen = candidates.FirstOrDefault(m =>
                    string.Equals(m.DisplayName, lsxEntry.Name, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];

                if (added.Add(chosen.Id))
                {
                    orderedIds.Add(chosen.Id);
                }
            }

            if (includeStandaloneOverrides)
            {
                foreach (ModEntry mod in activeMods
                             .Where(m => m.Kind is ModKind.Pak or ModKind.Hybrid &&
                                         m.EffectiveOverrideKind == OverrideKind.StandaloneOverride)
                             .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    if (added.Add(mod.Id))
                    {
                        orderedIds.Add(mod.Id);
                    }
                }
            }

            if (includeActiveLooseMods)
            {
                foreach (ModEntry mod in activeMods
                             .Where(m => m.Kind is ModKind.LooseData or ModKind.LooseGame)
                             .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    if (added.Add(mod.Id))
                    {
                        orderedIds.Add(mod.Id);
                    }
                }
            }
        }
        else
        {
            foreach (ModEntry remaining in activeMods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (added.Add(remaining.Id))
                {
                    orderedIds.Add(remaining.Id);
                }
            }
        }

        var profile = new ModProfile
        {
            Name = string.IsNullOrWhiteSpace(profileName) ? "Current Setup" : profileName.Trim(),
            ModIds = orderedIds
        };

        db.Profiles.Add(profile);
        db.ActiveProfileId = profile.Id;

        if (!string.IsNullOrWhiteSpace(settings.LastSelectedOrderJsonPath) &&
            File.Exists(settings.LastSelectedOrderJsonPath) &&
            !HasExplicitCompanionLinkForJson(db, settings.LastSelectedOrderJsonPath))
        {
            try
            {
                Bg3LoadOrderFile lastOrder = Bg3LoadOrderService.LoadOrderJson(settings.LastSelectedOrderJsonPath);
                var jsonUuids = lastOrder.Order
                    .Select(e => e.Uuid)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .ToList();
                var capturedUuids = orderedIds
                    .Select(id => db.Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                    .Where(m => m is not null && !IsJsonCompanionMod(m) && !string.IsNullOrWhiteSpace(m.Uuid))
                    .Select(m => m!.Uuid!)
                    .ToList();

                if (jsonUuids.Count > 0 && jsonUuids.SequenceEqual(capturedUuids, StringComparer.OrdinalIgnoreCase))
                {
                    LinkCompanionModsToOrderJson(db, settings.LastSelectedOrderJsonPath, profile.ModIds);
                }
            }
            catch
            {
                // Ignore invalid last selected JSON while capturing active profile.
            }
        }

        return profile;
    }

    /// <summary>
    /// Creates a new profile or updates an existing linked profile from a BG3ModManager JSON load order,
    /// disambiguating variants that share a UUID by matching JSON Name first, while preserving and linking
    /// companion loose file mods and standalone overrides that BG3ModManager omits from its JSON.
    /// </summary>
    public static async Task<(ModProfile Profile, List<string> MissingJsonEntries)> CreateOrUpdateProfileFromJsonAsync(
        string jsonPath,
        string profileName,
        bool includeActiveStandaloneOverrides,
        bool includeActiveLooseMods,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        ModProfile? existingProfile = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ScanAndAdoptActiveModsAsync(settings, db, cache, progress, cancellationToken);

        Bg3LoadOrderFile order = Bg3LoadOrderService.LoadOrderJson(jsonPath);
        var resolvedIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        var modsByUuid = db.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.Uuid))
            .GroupBy(m => m.Uuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (Bg3OrderEntry entry in order.Order)
        {
            if (!modsByUuid.TryGetValue(entry.Uuid, out List<ModEntry>? candidates) || candidates.Count == 0)
            {
                missing.Add($"{entry.Name} ({entry.Uuid})");
                continue;
            }

            ModEntry chosen = ChooseProfileCandidate(entry, candidates);
            if (seenIds.Add(chosen.Id))
            {
                resolvedIds.Add(chosen.Id);
            }
        }

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        List<string> linkedCompanions = GetLinkedCompanionModIdsForOrderJson(db, jsonPath, existingProfile);
        bool hadExplicitJsonLink = HasExplicitCompanionLinkForJson(db, jsonPath);

        if (existingProfile is not null)
        {
            // Syncing an existing profile from its linked BG3MM JSON: always keep companion loose mods & standalone overrides.
            foreach (string companionId in linkedCompanions)
            {
                if (seenIds.Add(companionId))
                {
                    resolvedIds.Add(companionId);
                }
            }
        }
        else
        {
            // Importing/creating a new profile from a BG3MM JSON: include linked companion mods respecting the dialog options.
            foreach (string companionId in linkedCompanions)
            {
                if (!modById.TryGetValue(companionId, out ModEntry? compMod))
                {
                    continue;
                }

                bool isLoose = compMod.Kind is ModKind.LooseData or ModKind.LooseGame || compMod.TrackedFiles.Count > 0;
                if (isLoose && !includeActiveLooseMods)
                {
                    continue;
                }

                if (!isLoose && !includeActiveStandaloneOverrides)
                {
                    continue;
                }

                if (seenIds.Add(compMod.Id))
                {
                    resolvedIds.Add(compMod.Id);
                }
            }
        }

        bool autoRecoverActiveCompanions =
            existingProfile is not null &&
            !hadExplicitJsonLink &&
            linkedCompanions.Count == 0 &&
            DoesPakListMatchActiveDiskOrder(resolvedIds, settings, db);

        if (includeActiveStandaloneOverrides || autoRecoverActiveCompanions)
        {
            foreach (ModEntry mod in db.Mods
                         .Where(m =>
                             m.Location == ModLocation.Active &&
                             m.EffectiveOverrideKind == OverrideKind.StandaloneOverride &&
                             m.Kind is ModKind.Pak or ModKind.Hybrid)
                         .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (seenIds.Add(mod.Id))
                {
                    resolvedIds.Add(mod.Id);
                }
            }
        }

        if (includeActiveLooseMods || autoRecoverActiveCompanions)
        {
            foreach (ModEntry mod in db.Mods
                         .Where(m =>
                             m.Location == ModLocation.Active &&
                             m.Kind is ModKind.LooseData or ModKind.LooseGame)
                         .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (seenIds.Add(mod.Id))
                {
                    resolvedIds.Add(mod.Id);
                }
            }
        }

        ModProfile profile = existingProfile ?? new ModProfile
        {
            Name = string.IsNullOrWhiteSpace(profileName)
                ? Path.GetFileNameWithoutExtension(jsonPath)
                : profileName.Trim()
        };

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            profile.Name = profileName.Trim();
        }

        profile.LinkedOrderJsonPath = jsonPath;
        profile.LastSyncedFromJsonUtc = DateTime.UtcNow;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.ModIds = resolvedIds;

        if (existingProfile is null)
        {
            db.Profiles.Add(profile);
        }

        LinkCompanionModsToOrderJson(db, jsonPath, resolvedIds);
        ReconcileActiveProfileId(settings, db, preferredProfileId: profile.Id);

        return (profile, missing);
    }

    /// <summary>
    /// Returns true if a mod is omitted from BG3ModManager's exported .json load order array
    /// (LooseData, LooseGame, StandaloneOverride PAKs, or PAKs lacking meta.lsx / UUID).
    /// </summary>
    public static bool IsJsonCompanionMod(ModEntry mod)
        => mod.Kind is ModKind.LooseData or ModKind.LooseGame ||
           mod.EffectiveOverrideKind == OverrideKind.StandaloneOverride ||
           !mod.HasMetaLsx ||
           string.IsNullOrWhiteSpace(mod.Uuid);

    public static bool HasExplicitCompanionLinkForJson(LibraryDatabase db, string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || db.JsonOrderCompanionModIds is null)
        {
            return false;
        }

        (string fullKey, string fileKey) = GetOrderJsonKeys(jsonPath);
        return (!string.IsNullOrEmpty(fullKey) && db.JsonOrderCompanionModIds.ContainsKey(fullKey)) ||
               (!string.IsNullOrEmpty(fileKey) && db.JsonOrderCompanionModIds.ContainsKey(fileKey));
    }

    /// <summary>
    /// Persistently associates all companion mods (loose file mods and standalone overrides) in
    /// <paramref name="profileModIds"/> with the specified BG3ModManager load order .json file.
    /// </summary>
    public static void LinkCompanionModsToOrderJson(
        LibraryDatabase db,
        string? jsonPath,
        IEnumerable<string> profileModIds)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return;
        }

        db.JsonOrderCompanionModIds ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var companionIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in profileModIds)
        {
            if (modById.TryGetValue(id, out ModEntry? mod) && IsJsonCompanionMod(mod) && seen.Add(mod.Id))
            {
                companionIds.Add(mod.Id);
            }
        }

        (string fullKey, string fileKey) = GetOrderJsonKeys(jsonPath);
        if (!string.IsNullOrEmpty(fullKey))
        {
            db.JsonOrderCompanionModIds[fullKey] = new List<string>(companionIds);
        }

        if (!string.IsNullOrEmpty(fileKey))
        {
            db.JsonOrderCompanionModIds[fileKey] = new List<string>(companionIds);
        }
    }

    /// <summary>
    /// Retrieves companion mod IDs (loose file mods and standalone overrides) linked to <paramref name="jsonPath"/>
    /// or already present on <paramref name="existingProfile"/>.
    /// </summary>
    public static List<string> GetLinkedCompanionModIdsForOrderJson(
        LibraryDatabase db,
        string? jsonPath,
        ModProfile? existingProfile = null)
    {
        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddCompanion(string id)
        {
            if (modById.TryGetValue(id, out ModEntry? mod) && IsJsonCompanionMod(mod) && seen.Add(mod.Id))
            {
                result.Add(mod.Id);
            }
        }

        bool hasExplicitLink = HasExplicitCompanionLinkForJson(db, jsonPath);
        if (hasExplicitLink && db.JsonOrderCompanionModIds is not null && !string.IsNullOrWhiteSpace(jsonPath))
        {
            (string fullKey, string fileKey) = GetOrderJsonKeys(jsonPath);
            if (!string.IsNullOrEmpty(fullKey) &&
                db.JsonOrderCompanionModIds.TryGetValue(fullKey, out List<string>? byFull) &&
                byFull is not null)
            {
                foreach (string id in byFull)
                {
                    TryAddCompanion(id);
                }
            }
            else if (!string.IsNullOrEmpty(fileKey) &&
                     db.JsonOrderCompanionModIds.TryGetValue(fileKey, out List<string>? byFile) &&
                     byFile is not null)
            {
                foreach (string id in byFile)
                {
                    TryAddCompanion(id);
                }
            }
        }

        if (existingProfile is not null)
        {
            foreach (string id in existingProfile.ModIds)
            {
                TryAddCompanion(id);
            }
        }

        if (!hasExplicitLink && !string.IsNullOrWhiteSpace(jsonPath))
        {
            (string fullKey, string fileKey) = GetOrderJsonKeys(jsonPath);
            foreach (ModProfile other in db.Profiles)
            {
                if (string.IsNullOrWhiteSpace(other.LinkedOrderJsonPath))
                {
                    continue;
                }

                (string otherFull, string otherFile) = GetOrderJsonKeys(other.LinkedOrderJsonPath);
                if ((!string.IsNullOrEmpty(fullKey) && string.Equals(fullKey, otherFull, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(fileKey) && string.Equals(fileKey, otherFile, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (string id in other.ModIds)
                    {
                        TryAddCompanion(id);
                    }
                }
            }
        }

        return result;
    }

    public static bool DoesPakListMatchActiveDiskOrder(
        IReadOnlyList<string> candidateModIds,
        AppSettings settings,
        LibraryDatabase db)
    {
        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var profileOrderablePaks = new List<ModEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in candidateModIds)
        {
            if (!seenIds.Add(id))
            {
                continue;
            }

            if (!modById.TryGetValue(id, out ModEntry? mod))
            {
                return false;
            }

            if (!IsJsonCompanionMod(mod))
            {
                if (mod.Location != ModLocation.Active)
                {
                    return false;
                }

                profileOrderablePaks.Add(mod);
            }
        }

        var activeOrderablePaksOnDisk = db.Mods
            .Where(m => m.Location == ModLocation.Active && !IsJsonCompanionMod(m))
            .ToList();

        if (profileOrderablePaks.Count == 0)
        {
            return activeOrderablePaksOnDisk.Count == 0;
        }

        // Check 1: Exact set match with all active orderable PAKs currently in Mods/
        var profilePakIdSet = new HashSet<string>(profileOrderablePaks.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (profilePakIdSet.Count == activeOrderablePaksOnDisk.Count &&
            activeOrderablePaksOnDisk.All(m => profilePakIdSet.Contains(m.Id)))
        {
            return true;
        }

        // Check 2: Match against modsettings.lsx active load order UUID sequence (supports OverridesAndCollisionsOnly mode)
        if (!string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath) && File.Exists(settings.ModSettingsLsxPath))
        {
            try
            {
                List<ModSettingsEntry> lsx = Bg3LoadOrderService.ReadModSettingsLsx(settings.ModSettingsLsxPath);
                var activeByUuid = activeOrderablePaksOnDisk
                    .Where(m => !string.IsNullOrWhiteSpace(m.Uuid))
                    .GroupBy(m => m.Uuid!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                var lsxResolvedIds = new List<string>();
                var lsxSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModSettingsEntry lsxEntry in lsx)
                {
                    if (!activeByUuid.TryGetValue(lsxEntry.Uuid, out List<ModEntry>? candidates) || candidates.Count == 0)
                    {
                        continue;
                    }

                    ModEntry chosen = candidates.FirstOrDefault(m =>
                        string.Equals(m.DisplayName, lsxEntry.Name, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];

                    if (lsxSeen.Add(chosen.Id))
                    {
                        lsxResolvedIds.Add(chosen.Id);
                    }
                }

                var profileIds = profileOrderablePaks.Select(m => m.Id).ToList();
                if (lsxResolvedIds.Count > 0 &&
                    lsxResolvedIds.Count == profileIds.Count &&
                    lsxResolvedIds.SequenceEqual(profileIds, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore unreadable modsettings.lsx during active profile matching.
            }
        }

        return false;
    }

    /// <summary>
    /// Auto-heals linked JSON profiles that were missing companion loose/standalone mods prior to companion linking,
    /// and reconciles <see cref="LibraryDatabase.ActiveProfileId"/> against the currently active mods on disk.
    /// </summary>
    public static void ReconcileActiveProfileId(
        AppSettings settings,
        LibraryDatabase db,
        string? preferredProfileId = null)
    {
        if (db.Profiles.Count == 0)
        {
            db.ActiveProfileId = null;
            return;
        }

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var activeCompanionsOnDisk = db.Mods
            .Where(m => m.Location == ModLocation.Active && IsJsonCompanionMod(m))
            .OrderBy(m => m.Kind is ModKind.LooseData or ModKind.LooseGame ? 1 : 0)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Step 1: Ensure every profile with a LinkedOrderJsonPath has its companion link initialized or synchronized.
        ModProfile? lastAutoHealedProfile = null;
        foreach (ModProfile p in db.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.LinkedOrderJsonPath))
            {
                continue;
            }

            bool hasCompanionsInProfile = p.ModIds.Any(id => modById.TryGetValue(id, out ModEntry? m) && IsJsonCompanionMod(m));
            bool hasExplicitLink = HasExplicitCompanionLinkForJson(db, p.LinkedOrderJsonPath);

            if (!hasCompanionsInProfile && !hasExplicitLink)
            {
                // Profile was linked/synced before companion linking existed and has 0 companion mods:
                // if its PAKs match active disk order (or another profile with the exact same PAK sequence), recover them.
                if (activeCompanionsOnDisk.Count > 0 && DoesPakListMatchActiveDiskOrder(p.ModIds, settings, db))
                {
                    var existingSet = new HashSet<string>(p.ModIds, StringComparer.OrdinalIgnoreCase);
                    foreach (ModEntry comp in activeCompanionsOnDisk)
                    {
                        if (existingSet.Add(comp.Id))
                        {
                            p.ModIds.Add(comp.Id);
                        }
                    }

                    LinkCompanionModsToOrderJson(db, p.LinkedOrderJsonPath, p.ModIds);
                    lastAutoHealedProfile = p;
                }
                else
                {
                    List<string> pPakIds = p.ModIds
                        .Where(id => modById.TryGetValue(id, out ModEntry? m) && !IsJsonCompanionMod(m))
                        .ToList();

                    if (pPakIds.Count > 0)
                    {
                        ModProfile? donor = db.Profiles.FirstOrDefault(other =>
                            other.Id != p.Id &&
                            other.ModIds.Any(id => modById.TryGetValue(id, out ModEntry? m) && IsJsonCompanionMod(m)) &&
                            other.ModIds
                                .Where(id => modById.TryGetValue(id, out ModEntry? m) && !IsJsonCompanionMod(m))
                                .SequenceEqual(pPakIds, StringComparer.OrdinalIgnoreCase));

                        if (donor is not null)
                        {
                            var existingSet = new HashSet<string>(p.ModIds, StringComparer.OrdinalIgnoreCase);
                            foreach (string id in donor.ModIds)
                            {
                                if (modById.TryGetValue(id, out ModEntry? m) && IsJsonCompanionMod(m) && existingSet.Add(m.Id))
                                {
                                    p.ModIds.Add(m.Id);
                                }
                            }

                            LinkCompanionModsToOrderJson(db, p.LinkedOrderJsonPath, p.ModIds);
                            lastAutoHealedProfile = p;
                        }
                    }
                }
            }
            else if (hasCompanionsInProfile && !hasExplicitLink)
            {
                LinkCompanionModsToOrderJson(db, p.LinkedOrderJsonPath, p.ModIds);
            }
        }

        // Step 2: Evaluate which profiles match the active disk setup.
        var activeCompanionIdSet = new HashSet<string>(activeCompanionsOnDisk.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        bool IsFullDiskMatch(ModProfile p)
        {
            if (p.ModIds.Count == 0)
            {
                return db.Mods.All(m => m.Location != ModLocation.Active);
            }

            if (!DoesPakListMatchActiveDiskOrder(p.ModIds, settings, db))
            {
                return false;
            }

            var profileCompanionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in p.ModIds)
            {
                if (!modById.TryGetValue(id, out ModEntry? mod))
                {
                    return false;
                }

                if (mod.Location != ModLocation.Active)
                {
                    return false;
                }

                if (IsJsonCompanionMod(mod))
                {
                    profileCompanionIds.Add(mod.Id);
                }
            }

            return profileCompanionIds.SetEquals(activeCompanionIdSet);
        }

        // If a specific preferredProfileId was just synced/linked/imported and matches active disk state, mark it active.
        if (!string.IsNullOrWhiteSpace(preferredProfileId))
        {
            ModProfile? preferred = db.Profiles.FirstOrDefault(p => string.Equals(p.Id, preferredProfileId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null && IsFullDiskMatch(preferred))
            {
                db.ActiveProfileId = preferred.Id;
                return;
            }
        }

        if (lastAutoHealedProfile is not null && IsFullDiskMatch(lastAutoHealedProfile))
        {
            ModProfile? cur = db.Profiles.FirstOrDefault(p => string.Equals(p.Id, db.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
            if (cur is null || !IsFullDiskMatch(cur) || lastAutoHealedProfile.UpdatedAtUtc >= cur.UpdatedAtUtc)
            {
                db.ActiveProfileId = lastAutoHealedProfile.Id;
                return;
            }
        }

        // If current ActiveProfileId still exists and matches the active disk state, keep it.
        if (!string.IsNullOrWhiteSpace(db.ActiveProfileId))
        {
            ModProfile? currentActive = db.Profiles.FirstOrDefault(p => string.Equals(p.Id, db.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
            if (currentActive is not null && IsFullDiskMatch(currentActive))
            {
                return;
            }
        }

        // Otherwise, if any profile matches the active disk state (preferring JSON-linked profiles, then most recently updated), select it.
        ModProfile? bestMatch = db.Profiles
            .Where(IsFullDiskMatch)
            .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.LinkedOrderJsonPath))
            .ThenByDescending(p => p.UpdatedAtUtc)
            .FirstOrDefault();

        if (bestMatch is not null)
        {
            db.ActiveProfileId = bestMatch.Id;
        }
    }

    private static (string FullPathKey, string FileNameKey) GetOrderJsonKeys(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return (string.Empty, string.Empty);
        }

        string trimmed = jsonPath.Trim();
        string fullKey;
        try
        {
            fullKey = Path.GetFullPath(trimmed).Replace('/', '\\');
        }
        catch
        {
            fullKey = trimmed.Replace('/', '\\');
        }

        string fileName = Path.GetFileName(trimmed);
        string fileKey = string.IsNullOrWhiteSpace(fileName) ? string.Empty : $"file:{fileName}";
        return (fullKey, fileKey);
    }

    public sealed record LoadOrderSyncResult(
        bool JsonWritten,
        string? JsonPath,
        int JsonEntryCount,
        bool LsxWritten,
        string? LsxPath,
        int LsxEntryCount);

    /// <summary>
    /// Inserts a mod into a profile, placing orderable PAK/Hybrid mods right before any trailing
    /// loose/standalone companion mods and appending loose/standalone mods at the end.
    /// </summary>
    public static void InsertModIntoProfile(ModProfile profile, string modId, LibraryDatabase db)
    {
        if (profile.ModIds.Contains(modId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        if (modById.TryGetValue(modId, out ModEntry? insertedMod) && !IsJsonCompanionMod(insertedMod))
        {
            int insertIndex = profile.ModIds.Count;
            while (insertIndex > 0)
            {
                string prevId = profile.ModIds[insertIndex - 1];
                if (modById.TryGetValue(prevId, out ModEntry? prevMod) && IsJsonCompanionMod(prevMod))
                {
                    insertIndex--;
                }
                else
                {
                    break;
                }
            }

            profile.ModIds.Insert(insertIndex, modId);
        }
        else
        {
            profile.ModIds.Add(modId);
        }

        profile.UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Moves one or more selected mods (preserving their relative order) immediately before or after <paramref name="targetModId"/>.
    /// </summary>
    public static bool ReorderProfileMods(
        ModProfile profile,
        IReadOnlyList<string> modIdsToMove,
        string targetModId,
        bool insertAfter)
    {
        if (modIdsToMove.Count == 0 || string.IsNullOrWhiteSpace(targetModId))
        {
            return false;
        }

        var moveSet = new HashSet<string>(modIdsToMove, StringComparer.OrdinalIgnoreCase);
        if (moveSet.Contains(targetModId))
        {
            return false;
        }

        var moving = profile.ModIds.Where(id => moveSet.Contains(id)).ToList();
        if (moving.Count == 0)
        {
            return false;
        }

        var remaining = profile.ModIds.Where(id => !moveSet.Contains(id)).ToList();
        int targetIdx = remaining.FindIndex(id => string.Equals(id, targetModId, StringComparison.OrdinalIgnoreCase));
        if (targetIdx < 0)
        {
            return false;
        }

        int insertIdx = insertAfter ? targetIdx + 1 : targetIdx;
        remaining.InsertRange(insertIdx, moving);

        if (remaining.SequenceEqual(profile.ModIds, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        profile.ModIds = remaining;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Shifts one or more selected mods up (delta &lt; 0) or down (delta &gt; 0) by one position.
    /// </summary>
    public static bool MoveProfileModsByDelta(
        ModProfile profile,
        IReadOnlyList<string> modIdsToMove,
        int delta)
    {
        if (delta == 0 || modIdsToMove.Count == 0 || profile.ModIds.Count <= 1)
        {
            return false;
        }

        var moveSet = new HashSet<string>(modIdsToMove, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(profile.ModIds);

        if (delta < 0)
        {
            for (int i = 1; i < list.Count; i++)
            {
                if (moveSet.Contains(list[i]) && !moveSet.Contains(list[i - 1]))
                {
                    (list[i], list[i - 1]) = (list[i - 1], list[i]);
                }
            }
        }
        else
        {
            for (int i = list.Count - 2; i >= 0; i--)
            {
                if (moveSet.Contains(list[i]) && !moveSet.Contains(list[i + 1]))
                {
                    (list[i], list[i + 1]) = (list[i + 1], list[i]);
                }
            }
        }

        if (list.SequenceEqual(profile.ModIds, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        profile.ModIds = list;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="jsonPath"/> matches the currently active profile's linked BG3MM JSON path.
    /// </summary>
    public static bool IsPathMatchingActiveProfileJson(string? jsonPath, LibraryDatabase db)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || string.IsNullOrWhiteSpace(db.ActiveProfileId))
        {
            return false;
        }

        ModProfile? activeProfile = db.Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, db.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        if (activeProfile is null || string.IsNullOrWhiteSpace(activeProfile.LinkedOrderJsonPath))
        {
            return false;
        }

        (string candidateFull, _) = GetOrderJsonKeys(jsonPath);
        (string activeFull, _) = GetOrderJsonKeys(activeProfile.LinkedOrderJsonPath);
        return !string.IsNullOrEmpty(candidateFull) &&
               string.Equals(candidateFull, activeFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if syncing <paramref name="profile"/> with the given options would write the active profile's
    /// BG3MM load order JSON or active BG3 modsettings.lsx file.
    /// </summary>
    public static bool WouldModifyActiveLoadOrder(
        ModProfile profile,
        AppSettings settings,
        LibraryDatabase db,
        bool writeLinkedJson = true,
        bool writeModSettingsLsxIfActive = true,
        bool forceWriteModSettingsLsx = false)
    {
        bool isProfileActive = string.Equals(db.ActiveProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);
        bool willWriteLsx = writeModSettingsLsxIfActive &&
                            settings.SyncModSettingsLsxOnSwitch &&
                            !string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath) &&
                            (forceWriteModSettingsLsx || isProfileActive);
        bool willWriteJson = writeLinkedJson && !string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath);
        bool targetsActiveJson = willWriteJson &&
                                 (isProfileActive ||
                                  forceWriteModSettingsLsx ||
                                  IsPathMatchingActiveProfileJson(profile.LinkedOrderJsonPath, db));

        return willWriteLsx || targetsActiveJson;
    }

    /// <summary>
    /// Writes a profile's linked BG3ModManager .json load order (updating companion mod links)
    /// and updates BG3's modsettings.lsx if the profile is active (or forced) and LSX syncing is enabled.
    /// </summary>
    public static LoadOrderSyncResult WriteProfileLoadOrderFiles(
        ModProfile profile,
        AppSettings settings,
        LibraryDatabase db,
        bool writeLinkedJson = true,
        bool writeModSettingsLsxIfActive = true,
        bool? onlyActiveModsInJson = null,
        bool forceWriteModSettingsLsx = false)
    {
        if (WouldModifyActiveLoadOrder(
                profile,
                settings,
                db,
                writeLinkedJson,
                writeModSettingsLsxIfActive,
                forceWriteModSettingsLsx))
        {
            GameProcessGuard.EnsureGameNotRunning($"updating active load order for '{profile.Name}'");
        }

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var seenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderableMods = new List<ModEntry>();

        foreach (string modId in profile.ModIds)
        {
            if (!modById.TryGetValue(modId, out ModEntry? mod))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(mod.Uuid) ||
                !mod.HasMetaLsx ||
                mod.EffectiveOverrideKind == OverrideKind.StandaloneOverride)
            {
                continue;
            }

            if (seenUuids.Add(mod.Uuid))
            {
                orderableMods.Add(mod);
            }
        }

        var activeOrderableMods = orderableMods
            .Where(m => m.Location == ModLocation.Active)
            .ToList();
        bool isProfileActive = string.Equals(db.ActiveProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);
        bool useActiveOnlyForJson = onlyActiveModsInJson ?? (isProfileActive || activeOrderableMods.Count > 0);

        bool jsonWritten = false;
        string? jsonPath = null;
        int jsonCount = 0;

        if (writeLinkedJson && !string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath))
        {
            List<ModEntry> jsonSourceMods = useActiveOnlyForJson ? activeOrderableMods : orderableMods;
            var entries = jsonSourceMods
                .Select(m => new Bg3OrderEntry { Uuid = m.Uuid!, Name = m.DisplayName })
                .ToList();
            Bg3LoadOrderService.SaveOrderJson(profile.LinkedOrderJsonPath, entries);
            profile.LastSyncedFromJsonUtc = DateTime.UtcNow;
            LinkCompanionModsToOrderJson(db, profile.LinkedOrderJsonPath, profile.ModIds);

            jsonWritten = true;
            jsonPath = profile.LinkedOrderJsonPath;
            jsonCount = entries.Count;
        }

        bool lsxWritten = false;
        string? lsxPath = null;
        int lsxCount = 0;

        if (writeModSettingsLsxIfActive &&
            settings.SyncModSettingsLsxOnSwitch &&
            !string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath) &&
            (forceWriteModSettingsLsx || isProfileActive))
        {
            Bg3LoadOrderService.WriteModSettingsLsx(settings.ModSettingsLsxPath, activeOrderableMods);

            lsxWritten = true;
            lsxPath = settings.ModSettingsLsxPath;
            lsxCount = activeOrderableMods.Count;
        }

        return new LoadOrderSyncResult(jsonWritten, jsonPath, jsonCount, lsxWritten, lsxPath, lsxCount);
    }

    private static ModEntry ChooseProfileCandidate(Bg3OrderEntry entry, List<ModEntry> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        ModEntry? exactName = candidates
            .OrderByDescending(m => m.Location == ModLocation.Active)
            .ThenByDescending(m => ulong.TryParse(m.Version64, out ulong v) ? v : 0UL)
            .FirstOrDefault(m => string.Equals(m.DisplayName, entry.Name, StringComparison.OrdinalIgnoreCase));

        return exactName ?? candidates
            .OrderByDescending(m => m.Location == ModLocation.Active)
            .ThenByDescending(m => ulong.TryParse(m.Version64, out ulong v) ? v : 0UL)
            .First();
    }

    private static void RefreshPakMetadata(ModEntry existing, PakInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            existing.DisplayName = info.Name;
        }

        existing.Uuid = info.Uuid ?? existing.Uuid;
        existing.FolderName = info.Folder ?? existing.FolderName;
        existing.Author = info.Author ?? existing.Author;
        existing.Version64 = info.Version64 ?? existing.Version64;
        existing.VersionString = info.VersionString ?? existing.VersionString;
        existing.Md5 = info.Md5 ?? existing.Md5;
        existing.PublishHandle = info.PublishHandle ?? existing.PublishHandle;
        existing.HasMetaLsx = info.HasMetaLsx;
        existing.IsDetectedOverride = info.IsDetectedOverride;
        existing.DetectedOverrideKind = info.OverrideKind;
        existing.OverrideReasons = info.OverrideReasons.ToList();
        existing.HasGeneratedSharedAssets = info.HasGeneratedSharedAssets;
    }
}
