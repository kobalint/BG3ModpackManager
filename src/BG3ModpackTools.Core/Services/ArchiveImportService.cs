using System.Diagnostics;
using System.IO.Compression;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Stages, classifies, deduplicates, and installs dropped NexusMods .zip/.7z/.rar archives,
/// bare .pak files, and unpacked mod folders into Mods/, Data/, Game/, or the Vault.
/// </summary>
public static class ArchiveImportService
{
    private static readonly HashSet<string> RootDataDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generated",
        "Public",
        "Localization",
        "Video"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".url", ".html", ".htm"
    };

    private static readonly HashSet<string> GameBinExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".toml", ".ini", ".cfg", ".asi"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".xz"
    };

    public static bool IsDeletableArchiveFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return ArchiveExtensions.Contains(Path.GetExtension(path));
    }

    public static async Task<ArchiveImportPlan> InspectDropAsync(
        string sourcePath,
        AppSettings settings,
        LibraryDatabase db,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
        }

        string fullSource = Path.GetFullPath(sourcePath);
        string stagingDir = Path.Combine(Path.GetTempPath(), "BG3MPM_Import_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(stagingDir);

        var plan = new ArchiveImportPlan
        {
            SourcePath = fullSource,
            SourceName = Path.GetFileName(fullSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StagingDirectory = stagingDir,
            StagingOwnedByPlan = true
        };

        try
        {
            if (Directory.Exists(fullSource))
            {
                CopyDirectoryContents(fullSource, stagingDir, cancellationToken);
            }
            else if (File.Exists(fullSource))
            {
                string ext = Path.GetExtension(fullSource).ToLowerInvariant();
                if (ext == ".pak")
                {
                    FileTransactionHelper.SafeCopyFile(fullSource, Path.Combine(stagingDir, Path.GetFileName(fullSource)));
                }
                else if (ext == ".zip")
                {
                    ExtractZipSafely(fullSource, stagingDir, cancellationToken);
                }
                else if (ext is ".7z" or ".rar" or ".tar" or ".gz" or ".xz")
                {
                    await ExtractExternalArchiveAsync(fullSource, stagingDir, cancellationToken);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported file type '{ext}'. Drop a .zip, .7z, .rar, .pak, or folder.");
                }
            }
            else
            {
                throw new FileNotFoundException($"Dropped path does not exist: {fullSource}", fullSource);
            }

            await PopulateCandidatesAsync(plan, settings, db, cancellationToken);
            return plan;
        }
        catch
        {
            CleanupStaging(plan);
            throw;
        }
    }

    public static async Task<ArchiveImportResult> CommitImportAsync(
        ArchiveImportPlan plan,
        string? assignToProfileId,
        bool installActiveNow,
        AppSettings settings,
        LibraryDatabase db,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ArchiveImportResult();
        if (installActiveNow)
        {
            GameProcessGuard.EnsureGameNotRunning($"installing '{plan.SourceName}' into active folders");
        }

        var selected = plan.Items.Where(i => i.IsSelected && i.TargetKind != ImportTargetKind.Ignore).ToList();
        if (selected.Count == 0)
        {
            result.Warnings.Add("No items were selected for import.");
            return result;
        }

        List<ImportItemCandidate> pakCandidates = selected.Where(i => i.TargetKind == ImportTargetKind.ModsPak).ToList();
        List<ImportItemCandidate> looseCandidates = selected.Where(i => i.TargetKind is ImportTargetKind.DataFolder or ImportTargetKind.GameFolder).ToList();

        // Validate distinct PAK filenames among selected items.
        var pakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportItemCandidate p in pakCandidates)
        {
            string fileName = Path.GetFileName(p.DestinationRelativePath);
            if (!pakNames.Add(fileName))
            {
                result.Errors.Add($"Multiple selected PAKs share filename '{fileName}'. Deselect duplicate variants first.");
            }
        }

        if (result.Errors.Count > 0)
        {
            return result;
        }

        ModProfile? profile = !string.IsNullOrWhiteSpace(assignToProfileId)
            ? db.Profiles.FirstOrDefault(p => p.Id == assignToProfileId)
            : null;

        if (profile is not null &&
            ModLibraryService.WouldModifyActiveLoadOrder(
                profile,
                settings,
                db,
                writeLinkedJson: true,
                writeModSettingsLsxIfActive: installActiveNow))
        {
            GameProcessGuard.EnsureGameNotRunning($"updating active profile '{profile.Name}' load order");
        }

        bool combineIntoHybrid = plan.PackageAsSingleHybridMod &&
                                 pakCandidates.Count == 1 &&
                                 looseCandidates.Count > 0 &&
                                 string.IsNullOrEmpty(pakCandidates[0].ExistingDuplicateModId);

        if (combineIntoHybrid)
        {
            (ModEntry hybrid, bool reused) = await InstallOrReuseHybridModAsync(
                pakCandidates[0],
                looseCandidates,
                plan,
                installActiveNow,
                settings,
                db,
                result,
                progress,
                cancellationToken);

            if (reused)
            {
                result.ReusedDuplicateModIds.Add(hybrid.Id);
            }
            else
            {
                db.Mods.Add(hybrid);
                result.ImportedModIds.Add(hybrid.Id);
            }

            AssignToProfile(profile, hybrid.Id, db);
            SyncAssignedProfileLoadOrder(profile, installActiveNow, settings, db, result);
            return result;
        }

        // Import PAKs individually (with strict deduplication).
        foreach (ImportItemCandidate pakItem in pakCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Importing {pakItem.DisplayName}...");

            if (!string.IsNullOrWhiteSpace(pakItem.ExistingDuplicateModId))
            {
                ModEntry? existing = db.Mods.FirstOrDefault(m => m.Id == pakItem.ExistingDuplicateModId);
                if (existing is not null)
                {
                    result.ReusedDuplicateModIds.Add(existing.Id);
                    AssignToProfile(profile, existing.Id, db);

                    string activePath = ModLibraryService.GetActivePakPath(settings, existing);
                    string vaultPath = string.IsNullOrWhiteSpace(settings.VaultFolder)
                        ? string.Empty
                        : ModLibraryService.GetVaultPakPath(settings.VaultFolder, existing);

                    if (installActiveNow)
                    {
                        if (existing.Location == ModLocation.Vault)
                        {
                            if (!File.Exists(activePath))
                            {
                                ModLibraryService.ActivateModFromVault(existing, settings);
                            }
                            else
                            {
                                result.Warnings.Add($"Reused '{existing.DisplayName}' from library, but '{existing.PakFileName}' is already occupied in Mods/.");
                            }
                        }
                        else if (!File.Exists(activePath) && (string.IsNullOrWhiteSpace(vaultPath) || !File.Exists(vaultPath)))
                        {
                            FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, activePath);
                            existing.Location = ModLibraryService.EvaluateModLocation(settings, existing);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(vaultPath) && !File.Exists(vaultPath) && !File.Exists(activePath))
                    {
                        FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, vaultPath);
                        existing.Location = ModLibraryService.EvaluateModLocation(settings, existing);
                    }

                    continue;
                }
            }

            PakInfo info = pakItem.PakInfo ?? LspkPakReader.ReadPakInfo(pakItem.StagingFullPath);
            string sha256 = pakItem.ContentHash ?? await HashService.ComputeFileSha256Async(pakItem.StagingFullPath, cancellationToken);
            ModLocation targetLoc = installActiveNow ? ModLocation.Active : ModLocation.Vault;
            ModEntry newMod = ModLibraryService.CreatePakModEntry(info, sha256, db, targetLoc, plan.SourceName);

            string dstPath = installActiveNow
                ? ModLibraryService.GetActivePakPath(settings, newMod)
                : ModLibraryService.GetVaultPakPath(settings.VaultFolder, newMod);

            if (File.Exists(dstPath))
            {
                if (installActiveNow)
                {
                    // Never lose the mod or clobber another PAK with the same filename: park in Vault if active slot is occupied.
                    BackupFileToVault(dstPath, Path.Combine("Mods", newMod.PakFileName!), settings, result);
                    File.Delete(dstPath);
                }
                else
                {
                    File.Delete(dstPath);
                }
            }

            FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, dstPath);
            newMod.Location = ModLibraryService.EvaluateModLocation(settings, newMod);
            db.Mods.Add(newMod);
            result.ImportedModIds.Add(newMod.Id);
            AssignToProfile(profile, newMod.Id, db);
        }

        // Import remaining Data/Game loose files as tracked loose mod(s) grouped by target.
        foreach (IGrouping<ImportTargetKind, ImportItemCandidate> group in looseCandidates.GroupBy(c => c.TargetKind))
        {
            TrackedFileTarget target = group.Key == ImportTargetKind.DataFolder
                ? TrackedFileTarget.DataFolder
                : TrackedFileTarget.GameFolder;

            (ModEntry looseMod, bool reused) = await InstallLooseModAsync(
                group.ToList(),
                target,
                plan,
                installActiveNow,
                settings,
                db,
                result,
                progress,
                cancellationToken);

            if (reused)
            {
                result.ReusedDuplicateModIds.Add(looseMod.Id);
                AssignToProfile(profile, looseMod.Id, db);
            }
            else
            {
                db.Mods.Add(looseMod);
                result.ImportedModIds.Add(looseMod.Id);
                AssignToProfile(profile, looseMod.Id, db);
            }
        }

        SyncAssignedProfileLoadOrder(profile, installActiveNow, settings, db, result);
        return result;
    }

    public static void CleanupStaging(ArchiveImportPlan plan)
    {
        if (!plan.StagingOwnedByPlan || string.IsNullOrWhiteSpace(plan.StagingDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(plan.StagingDirectory))
            {
                Directory.Delete(plan.StagingDirectory, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup failure is non-fatal.
        }
    }

    private static async Task<(ModEntry Entry, bool Reused)> InstallOrReuseHybridModAsync(
        ImportItemCandidate pakItem,
        List<ImportItemCandidate> looseItems,
        ArchiveImportPlan plan,
        bool installActiveNow,
        AppSettings settings,
        LibraryDatabase db,
        ArchiveImportResult result,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"Inspecting hybrid mod {pakItem.DisplayName}...");

        PakInfo info = pakItem.PakInfo ?? LspkPakReader.ReadPakInfo(pakItem.StagingFullPath);
        string pakSha = pakItem.ContentHash ?? await HashService.ComputeFileSha256Async(pakItem.StagingFullPath, cancellationToken);

        var trackedRecords = new List<TrackedFileRecord>();
        var stagedMoves = new List<(string StagePath, TrackedFileRecord Record)>();
        var hashInputs = new List<(string Key, string Sha256)>
        {
            ($"PAK:{info.FileName}", pakSha)
        };

        string effectiveRoot = GetUnwrappedRoot(plan.StagingDirectory);
        long totalSize = info.FileSizeBytes;

        foreach (ImportItemCandidate loose in looseItems)
        {
            TrackedFileTarget target = loose.TargetKind == ImportTargetKind.DataFolder
                ? TrackedFileTarget.DataFolder
                : TrackedFileTarget.GameFolder;

            foreach (var relPath in EnumerateSourceAndDestPaths(loose))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string srcFile = Path.Combine(effectiveRoot, relPath.SourceRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(srcFile))
                {
                    continue;
                }

                long len = new FileInfo(srcFile).Length;
                string sha = await HashService.ComputeFileSha256Async(srcFile, cancellationToken);
                var rec = new TrackedFileRecord
                {
                    Target = target,
                    RelativePath = relPath.DestRelative,
                    FileSizeBytes = len,
                    Sha256 = sha
                };
                trackedRecords.Add(rec);
                stagedMoves.Add((srcFile, rec));
                hashInputs.Add(($"{target}:{relPath.DestRelative}", sha));
                totalSize += len;
            }
        }

        string compositeHash = HashService.ComputeCompositeHash(hashInputs);
        ModEntry? existingDup = db.Mods.FirstOrDefault(m => string.Equals(m.ContentHash, compositeHash, StringComparison.OrdinalIgnoreCase));
        if (existingDup is not null)
        {
            string? activePak = !string.IsNullOrWhiteSpace(existingDup.PakFileName)
                ? ModLibraryService.GetActivePakPath(settings, existingDup)
                : null;
            string? vaultPak = !string.IsNullOrWhiteSpace(existingDup.PakFileName) && !string.IsNullOrWhiteSpace(settings.VaultFolder)
                ? ModLibraryService.GetVaultPakPath(settings.VaultFolder, existingDup)
                : null;

            if (installActiveNow)
            {
                if (existingDup.Location == ModLocation.Vault)
                {
                    bool activePakOccupied = activePak is not null && File.Exists(activePak);
                    if (!activePakOccupied)
                    {
                        ModLibraryService.ActivateModFromVault(existingDup, settings);
                    }
                    else
                    {
                        result.Warnings.Add($"Reused '{existingDup.DisplayName}' from Vault, but destination slot is currently occupied.");
                    }
                }

                if (activePak is not null && (vaultPak is null || !File.Exists(vaultPak)))
                {
                    bool activePakMatches = File.Exists(activePak) &&
                                            string.Equals(HashService.ComputeFileSha256(activePak), pakSha, StringComparison.OrdinalIgnoreCase);
                    if (!activePakMatches)
                    {
                        if (File.Exists(activePak))
                        {
                            BackupFileToVault(activePak, Path.Combine("Mods", existingDup.PakFileName!), settings, result);
                            File.Delete(activePak);
                        }

                        FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, activePak);
                    }
                }

                RepairExistingTrackedFiles(existingDup, stagedMoves, installActiveNow: true, settings, db, result);
            }
            else
            {
                if (vaultPak is not null && !File.Exists(vaultPak) && (activePak is null || !File.Exists(activePak)))
                {
                    FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, vaultPak);
                }

                RepairExistingTrackedFiles(existingDup, stagedMoves, installActiveNow: false, settings, db, result);
            }

            existingDup.Location = ModLibraryService.EvaluateModLocation(settings, existingDup);
            return (existingDup, true);
        }

        var hybrid = new ModEntry
        {
            Id = HashService.GenerateUniqueModId(compositeHash, db.Mods),
            Kind = ModKind.Hybrid,
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
            PakSha256 = pakSha,
            ContentHash = compositeHash,
            TotalSizeBytes = totalSize,
            HasMetaLsx = info.HasMetaLsx,
            IsDetectedOverride = info.IsDetectedOverride,
            DetectedOverrideKind = info.OverrideKind,
            OverrideReasons = info.OverrideReasons.ToList(),
            HasGeneratedSharedAssets = info.HasGeneratedSharedAssets,
            TrackedFiles = trackedRecords,
            SourceArchiveName = plan.SourceName,
            ImportedAtUtc = DateTime.UtcNow
        };

        if (trackedRecords.Count > 0 && !hybrid.OverrideReasons.Contains("Includes loose BG3 Data/Game files"))
        {
            hybrid.OverrideReasons.Add("Includes loose BG3 Data/Game files");
        }

        string dstPak = installActiveNow
            ? ModLibraryService.GetActivePakPath(settings, hybrid)
            : ModLibraryService.GetVaultPakPath(settings.VaultFolder, hybrid);

        if (File.Exists(dstPak))
        {
            if (installActiveNow)
            {
                BackupFileToVault(dstPak, Path.Combine("Mods", hybrid.PakFileName!), settings, result);
            }

            File.Delete(dstPak);
        }

        FileTransactionHelper.SafeMoveFile(pakItem.StagingFullPath, dstPak);

        foreach ((string stageFile, TrackedFileRecord rec) in stagedMoves)
        {
            string dstFile = installActiveNow
                ? ModLibraryService.GetActiveTrackedFilePath(settings, rec)
                : ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, hybrid, rec);

            if (File.Exists(dstFile))
            {
                if (installActiveNow)
                {
                    ModLibraryService.ReplaceActiveTrackedFileSafely(
                        dstFile,
                        rec,
                        rec.Sha256,
                        hybrid,
                        settings,
                        db,
                        result.BackedUpUnmanagedFiles,
                        result.RenamedOriginalFiles);
                }
                else
                {
                    File.Delete(dstFile);
                }
            }

            FileTransactionHelper.SafeMoveFile(stageFile, dstFile);
        }

        hybrid.Location = ModLibraryService.EvaluateModLocation(settings, hybrid);
        return (hybrid, false);
    }

    private static async Task<(ModEntry Entry, bool Reused)> InstallLooseModAsync(
        List<ImportItemCandidate> candidates,
        TrackedFileTarget target,
        ArchiveImportPlan plan,
        bool installActiveNow,
        AppSettings settings,
        LibraryDatabase db,
        ArchiveImportResult result,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string effectiveRoot = GetUnwrappedRoot(plan.StagingDirectory);
        var trackedRecords = new List<TrackedFileRecord>();
        var stagedMoves = new List<(string StagePath, TrackedFileRecord Record)>();
        var hashInputs = new List<(string Key, string Sha256)>();
        long totalSize = 0;

        foreach (ImportItemCandidate c in candidates)
        {
            progress?.Report($"Hashing {c.DisplayName}...");
            foreach (var rel in EnumerateSourceAndDestPaths(c))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string srcFile = Path.Combine(effectiveRoot, rel.SourceRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(srcFile))
                {
                    continue;
                }

                long len = new FileInfo(srcFile).Length;
                string sha = await HashService.ComputeFileSha256Async(srcFile, cancellationToken);
                var rec = new TrackedFileRecord
                {
                    Target = target,
                    RelativePath = rel.DestRelative,
                    FileSizeBytes = len,
                    Sha256 = sha
                };
                trackedRecords.Add(rec);
                stagedMoves.Add((srcFile, rec));
                hashInputs.Add(($"{target}:{rel.DestRelative}", sha));
                totalSize += len;
            }
        }

        string compositeHash = HashService.ComputeCompositeHash(hashInputs);
        ModEntry? duplicate = db.Mods.FirstOrDefault(m => string.Equals(m.ContentHash, compositeHash, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            if (installActiveNow && duplicate.Location == ModLocation.Vault)
            {
                ModLibraryService.ActivateModFromVault(duplicate, settings);
            }

            RepairExistingTrackedFiles(duplicate, stagedMoves, installActiveNow, settings, db, result);
            duplicate.Location = ModLibraryService.EvaluateModLocation(settings, duplicate);
            return (duplicate, true);
        }

        string baseName = !string.IsNullOrWhiteSpace(plan.CustomDisplayName)
            ? plan.CustomDisplayName.Trim()
            : Path.GetFileNameWithoutExtension(plan.SourceName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = plan.SourceName;
        }

        var looseMod = new ModEntry
        {
            Id = HashService.GenerateUniqueModId(compositeHash, db.Mods),
            Kind = target == TrackedFileTarget.DataFolder ? ModKind.LooseData : ModKind.LooseGame,
            DisplayName = !string.IsNullOrWhiteSpace(plan.CustomDisplayName)
                ? baseName
                : $"{baseName} ({(target == TrackedFileTarget.DataFolder ? "Data" : "Game")})",
            ContentHash = compositeHash,
            TotalSizeBytes = totalSize,
            IsDetectedOverride = true,
            OverrideReasons = new List<string> { $"Loose files in {(target == TrackedFileTarget.DataFolder ? "BG3 Data" : "BG3 Game")}" },
            TrackedFiles = trackedRecords,
            BackupReplacedOriginals = true,
            SourceArchiveName = plan.SourceName,
            ImportedAtUtc = DateTime.UtcNow
        };

        foreach ((string stageFile, TrackedFileRecord rec) in stagedMoves)
        {
            string dstFile = installActiveNow
                ? ModLibraryService.GetActiveTrackedFilePath(settings, rec)
                : ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, looseMod, rec);

            if (File.Exists(dstFile))
            {
                if (installActiveNow)
                {
                    ModLibraryService.ReplaceActiveTrackedFileSafely(
                        dstFile,
                        rec,
                        rec.Sha256,
                        looseMod,
                        settings,
                        db,
                        result.BackedUpUnmanagedFiles,
                        result.RenamedOriginalFiles);
                }
                else
                {
                    File.Delete(dstFile);
                }
            }

            FileTransactionHelper.SafeMoveFile(stageFile, dstFile);
        }

        looseMod.Location = ModLibraryService.EvaluateModLocation(settings, looseMod);
        return (looseMod, false);
    }

    private static void RepairExistingTrackedFiles(
        ModEntry existingMod,
        List<(string StagePath, TrackedFileRecord Record)> stagedMoves,
        bool installActiveNow,
        AppSettings settings,
        LibraryDatabase db,
        ArchiveImportResult result)
    {
        foreach ((string stageFile, TrackedFileRecord rec) in stagedMoves)
        {
            string activeFile = ModLibraryService.GetActiveTrackedFilePath(settings, rec);
            string vaultFile = string.IsNullOrWhiteSpace(settings.VaultFolder)
                ? string.Empty
                : ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, existingMod, rec);

            bool activeMatches = File.Exists(activeFile) &&
                                 string.Equals(HashService.ComputeFileSha256(activeFile), rec.Sha256, StringComparison.OrdinalIgnoreCase);

            if (installActiveNow)
            {
                if (activeMatches)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(vaultFile) && File.Exists(vaultFile))
                {
                    if (File.Exists(activeFile))
                    {
                        ModLibraryService.ReplaceActiveTrackedFileSafely(
                            activeFile,
                            rec,
                            rec.Sha256,
                            existingMod,
                            settings,
                            db,
                            result.BackedUpUnmanagedFiles,
                            result.RenamedOriginalFiles);
                    }

                    FileTransactionHelper.SafeMoveFile(vaultFile, activeFile);
                    string bucket = rec.Target == TrackedFileTarget.DataFolder ? "data" : "game";
                    string modRoot = Path.Combine(settings.VaultFolder, bucket, existingMod.Id);
                    FileTransactionHelper.PruneEmptyParentDirectories(vaultFile, modRoot);
                    if (Directory.Exists(modRoot) && !Directory.EnumerateFileSystemEntries(modRoot).Any())
                    {
                        Directory.Delete(modRoot, recursive: false);
                    }
                }
                else
                {
                    if (File.Exists(activeFile))
                    {
                        ModLibraryService.ReplaceActiveTrackedFileSafely(
                            activeFile,
                            rec,
                            rec.Sha256,
                            existingMod,
                            settings,
                            db,
                            result.BackedUpUnmanagedFiles,
                            result.RenamedOriginalFiles);
                    }

                    existingMod.BackupReplacedOriginals = true;
                    FileTransactionHelper.SafeMoveFile(stageFile, activeFile);
                }
            }
            else if (!string.IsNullOrWhiteSpace(vaultFile) && !File.Exists(vaultFile) && !activeMatches)
            {
                FileTransactionHelper.SafeMoveFile(stageFile, vaultFile);
            }
        }
    }

    private static async Task PopulateCandidatesAsync(
        ArchiveImportPlan plan,
        AppSettings settings,
        LibraryDatabase db,
        CancellationToken cancellationToken)
    {
        string effectiveRoot = GetUnwrappedRoot(plan.StagingDirectory);
        string[] allFiles = Directory.GetFiles(effectiveRoot, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingByHash = db.Mods
            .Where(m => !string.IsNullOrEmpty(m.ContentHash))
            .ToDictionary(m => m.ContentHash, StringComparer.OrdinalIgnoreCase);

        var seenPakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var looseGroups = new Dictionary<string, (ImportTargetKind Target, List<(string SrcRel, string DestRel, long Size)> Files)>(StringComparer.OrdinalIgnoreCase);

        foreach (string fullFile in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relPath = Path.GetRelativePath(effectiveRoot, fullFile).Replace('\\', '/');
            long size = new FileInfo(fullFile).Length;

            if (fullFile.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                PakInfo pakInfo = LspkPakReader.ReadPakInfo(fullFile);
                string sha256 = await HashService.ComputeFileSha256Async(fullFile, cancellationToken);
                existingByHash.TryGetValue(sha256, out ModEntry? existingDup);

                string pakName = Path.GetFileName(fullFile);
                bool duplicateInSameArchive = !seenPakNames.Add(pakName);

                string? warning = null;
                if (duplicateInSameArchive)
                {
                    warning = $"Another PAK named '{pakName}' is already selected in this archive.";
                }
                else if (existingDup is not null)
                {
                    warning = $"Identical to existing library mod '{existingDup.DisplayName}' (single copy will be reused).";
                }
                else if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && File.Exists(Path.Combine(settings.ModsFolder, pakName)))
                {
                    warning = $"A different '{pakName}' already exists in Mods/ (will be backed up if overwritten).";
                }

                plan.Items.Add(new ImportItemCandidate
                {
                    DisplayName = string.IsNullOrWhiteSpace(pakInfo.Name) ? Path.GetFileNameWithoutExtension(pakName) : pakInfo.Name,
                    ArchiveRelativePath = relPath,
                    TargetKind = ImportTargetKind.ModsPak,
                    DestinationRelativePath = pakName,
                    IsSelected = !duplicateInSameArchive,
                    FileSizeBytes = size,
                    FileCount = 1,
                    StagingFullPath = fullFile,
                    PakInfo = pakInfo,
                    ContentHash = sha256,
                    ExistingDuplicateModId = existingDup?.Id,
                    ExistingDuplicateModName = existingDup?.DisplayName,
                    WarningMessage = warning
                });

                continue;
            }

            (ImportTargetKind target, string groupKey, string destRel) = ClassifyNonPakFile(relPath);

            if (!looseGroups.TryGetValue(groupKey, out var grp))
            {
                grp = (target, new List<(string, string, long)>());
                looseGroups[groupKey] = grp;
            }

            grp.Files.Add((relPath, destRel, size));
        }

        foreach ((string groupKey, var grp) in looseGroups.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string sampleDest = grp.Files[0].DestRel;
            bool isIgnored = grp.Target == ImportTargetKind.Ignore;

            bool overwritesUnmanaged = false;
            if (!isIgnored)
            {
                string targetRoot = grp.Target == ImportTargetKind.DataFolder ? settings.DataFolder : settings.GameFolder;
                if (!string.IsNullOrWhiteSpace(targetRoot))
                {
                    foreach (var (_, destRel, _) in grp.Files)
                    {
                        string destFull = FileTransactionHelper.ResolveContainedPath(targetRoot, destRel);
                        if (File.Exists(destFull))
                        {
                            TrackedFileTarget targetEnum = grp.Target == ImportTargetKind.DataFolder
                                ? TrackedFileTarget.DataFolder
                                : TrackedFileTarget.GameFolder;

                            bool tracked = db.Mods.Any(m =>
                                ModLibraryService.EvaluateModLocation(settings, m) == ModLocation.Active &&
                                m.TrackedFiles.Any(tf =>
                                    tf.Target == targetEnum &&
                                    string.Equals(tf.RelativePath, destRel, StringComparison.OrdinalIgnoreCase) &&
                                    (string.IsNullOrWhiteSpace(tf.Sha256) ||
                                     (new FileInfo(destFull).Length == tf.FileSizeBytes &&
                                      string.Equals(HashService.ComputeFileSha256(destFull), tf.Sha256, StringComparison.OrdinalIgnoreCase)))));
                            if (!tracked)
                            {
                                overwritesUnmanaged = true;
                                break;
                            }
                        }
                    }
                }
            }

            string? warn = null;
            if (overwritesUnmanaged)
            {
                warn = "Replaces existing file(s) — originals will be renamed (.bg3mpm_orig) and restored on disable/uninstall.";
            }
            else if (grp.Target == ImportTargetKind.GameFolder && grp.Files.All(f => !f.SrcRel.Contains('/')))
            {
                warn = $"Mapped root file to BG3 Game/{sampleDest}. Verify destination before importing.";
            }
            else if (!isIgnored &&
                     grp.Files.Any(f => !string.Equals(f.SrcRel, f.DestRel, StringComparison.OrdinalIgnoreCase)))
            {
                string targetLabel = grp.Files.Count == 1
                    ? sampleDest
                    : GetCommonDestDirectoryPrefix(grp.Files.Select(f => f.DestRel));
                warn = $"Installs to: {targetLabel}";
            }

            plan.Items.Add(new ImportItemCandidate
            {
                DisplayName = groupKey,
                ArchiveRelativePath = grp.Files.Count == 1 ? grp.Files[0].SrcRel : groupKey,
                TargetKind = grp.Target,
                DestinationRelativePath = grp.Files.Count == 1 ? sampleDest : groupKey,
                IsSelected = !isIgnored,
                FileSizeBytes = grp.Files.Sum(f => f.Size),
                FileCount = grp.Files.Count,
                StagingFullPath = effectiveRoot,
                OverwritesUnmanagedFile = overwritesUnmanaged,
                WarningMessage = warn,
                ContainedRelativePaths = grp.Files.Select(f => $"{f.SrcRel}=>{f.DestRel}").ToList()
            });
        }

        // Default PackageAsSingleHybridMod only when there is 1 PAK and >=1 non-ignored loose group.
        plan.PackageAsSingleHybridMod =
            plan.Items.Count(i => i.TargetKind == ImportTargetKind.ModsPak && i.IsSelected) == 1 &&
            plan.Items.Any(i => i.TargetKind is ImportTargetKind.DataFolder or ImportTargetKind.GameFolder && i.IsSelected);
    }

    private static (ImportTargetKind Target, string GroupKey, string DestRel) ClassifyNonPakFile(string relPath)
    {
        string[] parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (ImportTargetKind.Ignore, relPath, relPath);
        }

        string fileName = parts[^1];
        string ext = Path.GetExtension(fileName);

        // Reject documentation and preview images at any nesting depth.
        // Non-readme .txt files remain eligible so game data files (e.g. Stats/Generated/Data/*.txt) still match.
        bool isDocOrMedia = fileName.Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("readme", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(ext) && IgnoredExtensions.Contains(ext) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase));
        if (isDocOrMedia)
        {
            string docGroup = parts.Length == 1 ? $"Docs/Metadata ({fileName})" : $"Docs/Metadata ({parts[0]})";
            return (ImportTargetKind.Ignore, docGroup, relPath);
        }

        // Scan segments left-to-right so outer BG3 roots (Data/, Game/, Public/, Generated/, bin/, NativeMods/)
        // take precedence over identically named inner asset subfolders (e.g., Public/Mod/Stats/Generated/Data/).
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string seg = parts[i];
            string prefix = i > 0 ? string.Join('/', parts.Take(i)) + "/" : string.Empty;

            if (seg.Equals("Data", StringComparison.OrdinalIgnoreCase))
            {
                string destRel = string.Join('/', parts.Skip(i + 1));
                string group = i + 1 < parts.Length - 1
                    ? $"{prefix}Data/{parts[i + 1]}/"
                    : $"{prefix}Data/";
                return (ImportTargetKind.DataFolder, group, destRel);
            }

            if (seg.Equals("Game", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 2 < parts.Length && parts[i + 1].Equals("Data", StringComparison.OrdinalIgnoreCase))
                {
                    string dataDestRel = string.Join('/', parts.Skip(i + 2));
                    string dataGroup = i + 2 < parts.Length - 1
                        ? $"{prefix}Game/Data/{parts[i + 2]}/"
                        : $"{prefix}Game/Data/";
                    return (ImportTargetKind.DataFolder, dataGroup, dataDestRel);
                }

                string gameDestRel = string.Join('/', parts.Skip(i + 1));
                string gameGroup = i + 1 < parts.Length - 1
                    ? $"{prefix}Game/{parts[i + 1]}/"
                    : $"{prefix}Game/";
                return (ImportTargetKind.GameFolder, gameGroup, gameDestRel);
            }

            if (RootDataDirs.Contains(seg))
            {
                string destRel = string.Join('/', parts.Skip(i));
                return (ImportTargetKind.DataFolder, $"{prefix}{seg}/", destRel);
            }

            if (seg.Equals("Mods", StringComparison.OrdinalIgnoreCase) &&
                (i == 0 || (parts.Length - i >= 3 && !IgnoredExtensions.Contains(ext))))
            {
                string destRel = string.Join('/', parts.Skip(i));
                return (ImportTargetKind.DataFolder, $"{prefix}Mods/{parts[i + 1]}/", destRel);
            }

            if (seg.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                string destRel = string.Join('/', parts.Skip(i));
                return (ImportTargetKind.GameFolder, $"{prefix}bin/", destRel);
            }

            if (seg.Equals("NativeMods", StringComparison.OrdinalIgnoreCase))
            {
                string destRel = "bin/" + string.Join('/', parts.Skip(i));
                return (ImportTargetKind.GameFolder, $"{prefix}bin/NativeMods/", destRel);
            }
        }

        if (parts.Length == 1)
        {
            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                IgnoredExtensions.Contains(ext))
            {
                return (ImportTargetKind.Ignore, $"Docs/Metadata ({fileName})", fileName);
            }

            if (GameBinExtensions.Contains(ext))
            {
                return (ImportTargetKind.GameFolder, $"bin/{fileName}", $"bin/{fileName}");
            }
        }

        if (IgnoredExtensions.Contains(ext))
        {
            return (ImportTargetKind.Ignore, $"Docs/Metadata ({parts[0]})", relPath);
        }

        return (ImportTargetKind.Ignore, $"Other ({parts[0]})", relPath);
    }

    private static string GetCommonDestDirectoryPrefix(IEnumerable<string> destRelPaths)
    {
        List<string[]> dirs = destRelPaths
            .Select(p =>
            {
                string[] segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return segs.Length > 1 ? segs[..^1] : Array.Empty<string>();
            })
            .ToList();

        if (dirs.Count == 0 || dirs[0].Length == 0)
        {
            return destRelPaths.FirstOrDefault() ?? string.Empty;
        }

        int commonLen = dirs[0].Length;
        foreach (string[] d in dirs.Skip(1))
        {
            int limit = Math.Min(commonLen, d.Length);
            int matched = 0;
            while (matched < limit && string.Equals(dirs[0][matched], d[matched], StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }

            commonLen = matched;
            if (commonLen == 0)
            {
                break;
            }
        }

        return commonLen > 0
            ? string.Join('/', dirs[0].Take(commonLen)) + "/"
            : dirs[0][0] + "/";
    }

    private static IEnumerable<(string SourceRelative, string DestRelative)> EnumerateSourceAndDestPaths(
        ImportItemCandidate candidate)
    {
        foreach (string encoded in candidate.ContainedRelativePaths)
        {
            int sep = encoded.IndexOf("=>", StringComparison.Ordinal);
            if (sep > 0)
            {
                string srcRel = encoded[..sep];
                string destRel = encoded[(sep + 2)..];
                yield return (srcRel, destRel);
            }
        }
    }

    private static string GetUnwrappedRoot(string stagingDir)
    {
        string current = stagingDir;
        for (int depth = 0; depth < 2; depth++)
        {
            string[] files = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly);
            string[] dirs = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);

            if (files.Length == 0 && dirs.Length == 1)
            {
                string singleDirName = Path.GetFileName(dirs[0]);
                if (!singleDirName.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
                    !singleDirName.Equals("Game", StringComparison.OrdinalIgnoreCase) &&
                    !RootDataDirs.Contains(singleDirName) &&
                    !singleDirName.Equals("Mods", StringComparison.OrdinalIgnoreCase) &&
                    !singleDirName.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    !singleDirName.Equals("NativeMods", StringComparison.OrdinalIgnoreCase))
                {
                    current = dirs[0];
                    continue;
                }
            }

            break;
        }

        return current;
    }

    private static void ExtractZipSafely(string zipPath, string destinationDir, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (normalized.Split('/').Any(seg => seg == ".."))
            {
                throw new InvalidOperationException($"Unsafe archive entry path rejected: {entry.FullName}");
            }

            string targetPath = FileTransactionHelper.ResolveContainedPath(destinationDir, normalized);
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static Task ExtractExternalArchiveAsync(string archivePath, string destinationDir, CancellationToken cancellationToken)
        => SevenZipHelper.ExtractToDirectoryAsync(archivePath, destinationDir, cancellationToken);

    private static void CopyDirectoryContents(string sourceDir, string destDir, CancellationToken cancellationToken)
    {
        foreach (string srcFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(sourceDir, srcFile);
            string dst = FileTransactionHelper.ResolveContainedPath(destDir, rel);
            FileTransactionHelper.SafeCopyFile(srcFile, dst, overwrite: true);
        }
    }

    private static void BackupFileToVault(
        string existingFilePath,
        string relativePath,
        AppSettings settings,
        ArchiveImportResult result)
        => ModLibraryService.BackupFileToVault(existingFilePath, relativePath, settings, result.BackedUpUnmanagedFiles);

    private static void AssignToProfile(ModProfile? profile, string modId, LibraryDatabase db)
    {
        if (profile is null)
        {
            return;
        }

        if (!profile.ModIds.Contains(modId, StringComparer.OrdinalIgnoreCase))
        {
            ModLibraryService.InsertModIntoProfile(profile, modId, db);
            if (!string.IsNullOrWhiteSpace(profile.LinkedOrderJsonPath))
            {
                ModLibraryService.LinkCompanionModsToOrderJson(db, profile.LinkedOrderJsonPath, profile.ModIds);
            }
        }
    }

    private static void SyncAssignedProfileLoadOrder(
        ModProfile? profile,
        bool installActiveNow,
        AppSettings settings,
        LibraryDatabase db,
        ArchiveImportResult result)
    {
        if (profile is null || (result.ImportedModIds.Count == 0 && result.ReusedDuplicateModIds.Count == 0))
        {
            return;
        }

        try
        {
            ModLibraryService.LoadOrderSyncResult sync = ModLibraryService.WriteProfileLoadOrderFiles(
                profile,
                settings,
                db,
                writeLinkedJson: true,
                writeModSettingsLsxIfActive: installActiveNow);

            result.SyncedOrderJsonPath = sync.JsonPath;
            result.SyncedOrderJsonCount = sync.JsonEntryCount;
            result.SyncedModSettingsLsx = sync.LsxWritten;
            result.SyncedModSettingsLsxCount = sync.LsxEntryCount;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Imported mod(s), but failed updating load order files: {ex.Message}");
        }
    }
}
