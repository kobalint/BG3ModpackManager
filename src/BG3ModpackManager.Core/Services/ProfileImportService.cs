using System.IO.Compression;
using System.Text.Json;
using BG3ModpackManager.Core.Lspk;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.Core.Services;

/// <summary>
/// Inspects and imports exported .7z modpack archives (including PAKs, loose Data/Generated &amp; Game/bin mods,
/// manifest metadata, and BG3MM JSON load orders), reconstructing a full profile and renaming any replaced
/// original game files in place (.bg3mpm_orig) so every mod remains cleanly uninstallable.
/// </summary>
public static class ProfileImportService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> TopStructureDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mods", "Data", "Game", "bin", "NativeMods", "Generated", "Public", "Localization", "Video"
    };

    public static async Task<bool> IsExportedModpackArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        string ext = Path.GetExtension(archivePath);
        if (!ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        List<string> entries;
        try
        {
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive zip = ZipFile.OpenRead(archivePath);
                entries = zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .Select(e => e.FullName.Replace('\\', '/').Trim('/'))
                    .ToList();
            }
            else
            {
                entries = await SevenZipHelper.ListEntriesAsync(archivePath, cancellationToken);
            }
        }
        catch
        {
            return false;
        }

        if (entries.Count == 0)
        {
            return false;
        }

        bool hasManifest = entries.Any(e =>
            e.Equals("profile_manifest.json", StringComparison.OrdinalIgnoreCase) ||
            (e.EndsWith("/profile_manifest.json", StringComparison.OrdinalIgnoreCase) && e.Count(c => c == '/') == 1));
        if (hasManifest)
        {
            return true;
        }

        bool hasModsPak = entries.Any(e =>
            (e.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) || e.Contains("/Mods/", StringComparison.OrdinalIgnoreCase)) &&
            e.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));

        bool hasRootOrderJson = entries.Any(e =>
            e.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !e.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase) &&
            !e.Equals("info.json", StringComparison.OrdinalIgnoreCase) &&
            e.Count(c => c == '/') <= 1);

        return hasModsPak && hasRootOrderJson;
    }

    public static async Task<ModpackImportPlan> InspectModpackArchiveAsync(
        string archivePath,
        AppSettings settings,
        LibraryDatabase db,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive does not exist: {archivePath}", archivePath);
        }

        string fullSource = Path.GetFullPath(archivePath);
        string sourceName = Path.GetFileName(fullSource);
        string stagingDir = Path.Combine(Path.GetTempPath(), "BG3MPM_PackImport_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(stagingDir);

        var plan = new ModpackImportPlan
        {
            SourceArchivePath = fullSource,
            SourceName = sourceName,
            StagingDirectory = stagingDir,
            StagingOwnedByPlan = true
        };

        try
        {
            progress?.Report($"Extracting '{sourceName}'...");
            if (Path.GetExtension(fullSource).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractZipContainedAsync(fullSource, stagingDir, cancellationToken);
            }
            else
            {
                await SevenZipHelper.ExtractToDirectoryAsync(fullSource, stagingDir, cancellationToken);
            }

            string effectiveRoot = UnwrapOptionalContainer(stagingDir);
            string[] allFiles = Directory.GetFiles(effectiveRoot, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var fileByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in allFiles)
            {
                string rel = Path.GetRelativePath(effectiveRoot, f).Replace('\\', '/');
                fileByRel[rel] = f;
            }

            ProfileExportManifest? manifest = null;
            if (fileByRel.TryGetValue("profile_manifest.json", out string? manifestPath))
            {
                manifest = JsonSerializer.Deserialize<ProfileExportManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken),
                    ReadOptions);
                plan.HasManifest = manifest is not null;
            }

            string? orderJsonPath = allFiles.FirstOrDefault(f =>
            {
                string rel = Path.GetRelativePath(effectiveRoot, f).Replace('\\', '/');
                string name = Path.GetFileName(f);
                return !rel.Contains('/') &&
                       name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("profile_manifest.json", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("info.json", StringComparison.OrdinalIgnoreCase);
            });

            if (orderJsonPath is not null)
            {
                try
                {
                    Bg3LoadOrderFile orderFile = Bg3LoadOrderService.LoadOrderJson(orderJsonPath);
                    plan.OrderJsonUuids = orderFile.Order
                        .Select(o => o.Uuid)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .ToList();
                }
                catch (Exception ex)
                {
                    plan.Warnings.Add($"Could not parse load order JSON '{Path.GetFileName(orderJsonPath)}': {ex.Message}");
                }
            }

            plan.SuggestedProfileName = !string.IsNullOrWhiteSpace(manifest?.ProfileName)
                ? manifest.ProfileName
                : (orderJsonPath is not null
                    ? Path.GetFileNameWithoutExtension(orderJsonPath)
                    : Path.GetFileNameWithoutExtension(sourceName));
            if (string.IsNullOrWhiteSpace(plan.SuggestedProfileName))
            {
                plan.SuggestedProfileName = "Imported Modpack";
            }

            var consumedRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in allFiles)
            {
                string rel = Path.GetRelativePath(effectiveRoot, f).Replace('\\', '/');
                if (!rel.Contains('/') &&
                    (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     rel.Equals("README_INSTALL.txt", StringComparison.OrdinalIgnoreCase) ||
                     rel.StartsWith("readme", StringComparison.OrdinalIgnoreCase)))
                {
                    consumedRels.Add(rel);
                }
            }

            if (manifest is not null)
            {
                for (int i = 0; i < manifest.Mods.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProfileExportedModRecord mRec = manifest.Mods[i];
                    progress?.Report($"Inspecting {i + 1}/{manifest.Mods.Count}: {mRec.DisplayName}...");

                    string? pakRel = null;
                    string? pakStage = null;
                    if (!string.IsNullOrWhiteSpace(mRec.PakFileName))
                    {
                        string candidateRel = $"Mods/{mRec.PakFileName}";
                        if (fileByRel.TryGetValue(candidateRel, out string? foundPak) && !consumedRels.Contains(candidateRel))
                        {
                            pakRel = candidateRel;
                            pakStage = foundPak;
                            consumedRels.Add(candidateRel);
                        }
                    }

                    var looseStaged = new List<(TrackedFileTarget Target, string DestRel, string StagePath)>();
                    foreach (string dataRel in mRec.DataFiles)
                    {
                        string norm = dataRel.Replace('\\', '/').TrimStart('/');
                        string archiveRel = $"Data/{norm}";
                        if (fileByRel.TryGetValue(archiveRel, out string? found) && !consumedRels.Contains(archiveRel))
                        {
                            looseStaged.Add((TrackedFileTarget.DataFolder, norm, found));
                            consumedRels.Add(archiveRel);
                        }
                    }

                    foreach (string gameRel in mRec.GameFiles)
                    {
                        string norm = gameRel.Replace('\\', '/').TrimStart('/');
                        string archiveRel = $"Game/{norm}";
                        if (fileByRel.TryGetValue(archiveRel, out string? found) && !consumedRels.Contains(archiveRel))
                        {
                            looseStaged.Add((TrackedFileTarget.GameFolder, norm, found));
                            consumedRels.Add(archiveRel);
                        }
                    }

                    if (pakStage is null && looseStaged.Count == 0)
                    {
                        if (!mRec.IsMissing)
                        {
                            plan.Warnings.Add($"Manifest entry '{mRec.DisplayName}' had no files present inside archive.");
                        }

                        continue;
                    }

                    ModpackImportItem item = await BuildImportItemAsync(
                        mRec.DisplayName,
                        mRec.Kind,
                        mRec.OverrideKind,
                        pakRel,
                        mRec.PakFileName,
                        pakStage,
                        looseStaged,
                        settings,
                        db,
                        cancellationToken);

                    plan.Items.Add(item);
                }
            }

            // Sweep any remaining unstaged PAK / Data / Game files (e.g. manifest-less .7z or manually added files)
            await SweepRemainingFilesAsync(effectiveRoot, allFiles, consumedRels, plan, settings, db, cancellationToken);

            plan.IsModpackArchive = plan.HasManifest ||
                                    (plan.OrderJsonUuids.Count > 0 && plan.Items.Any(i => i.Kind is ModKind.Pak or ModKind.Hybrid));

            return plan;
        }
        catch
        {
            CleanupStaging(plan);
            throw;
        }
    }

    public static async Task<ModpackImportResult> CommitModpackImportAsync(
        ModpackImportPlan plan,
        ModpackImportOptions options,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ProfileName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(options));
        }

        if (options.InstallActiveNow)
        {
            GameProcessGuard.EnsureGameNotRunning($"importing modpack '{plan.SourceName}'");
        }

        var result = new ModpackImportResult();
        var profile = new ModProfile
        {
            Name = options.ProfileName.Trim()
        };

        List<ModpackImportItem> selected = plan.Items.Where(i => i.IsSelected).ToList();
        for (int i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModpackImportItem item = selected[i];
            progress?.Report($"Installing {i + 1}/{selected.Count}: {item.DisplayName}...");

            // Re-check DB cleanly in case multiple identical items or prior imports already added this hash.
            ModEntry? existing = !string.IsNullOrWhiteSpace(item.ExistingDuplicateModId)
                ? db.Mods.FirstOrDefault(m => m.Id == item.ExistingDuplicateModId)
                : null;
            existing ??= FindExistingDuplicate(item.CompositeHash, item.PakSha256, item.Kind, db);

            if (existing is not null)
            {
                result.ReusedModCount++;
                if (!profile.ModIds.Contains(existing.Id, StringComparer.OrdinalIgnoreCase))
                {
                    profile.ModIds.Add(existing.Id);
                }

                if (options.InstallActiveNow && existing.Location == ModLocation.Vault)
                {
                    if (!string.IsNullOrWhiteSpace(existing.PakFileName) &&
                        File.Exists(ModLibraryService.GetActivePakPath(settings, existing)))
                    {
                        string activePak = ModLibraryService.GetActivePakPath(settings, existing);
                        string? expectedPakSha = existing.EffectivePakSha256 ?? existing.ContentHash;
                        if (string.Equals(HashService.ComputeFileSha256(activePak), expectedPakSha, StringComparison.OrdinalIgnoreCase))
                        {
                            // Exact same PAK already sits in active Mods/: activate remaining loose parts or reconcile cleanly.
                            ModLibraryService.ActivateModFromVault(existing, settings);
                        }
                        else
                        {
                            result.Warnings.Add($"Reused '{existing.DisplayName}' from Vault, but '{existing.PakFileName}' is already occupied in Mods/.");
                        }
                    }
                    else
                    {
                        ModLibraryService.ActivateModFromVault(existing, settings);
                    }
                }

                continue;
            }

            ModEntry newMod = CreateNewModEntry(item, plan.SourceName, db, options.InstallActiveNow);

            if (!string.IsNullOrWhiteSpace(item.PakStagingFullPath) &&
                !string.IsNullOrWhiteSpace(newMod.PakFileName) &&
                File.Exists(item.PakStagingFullPath))
            {
                string dstPak = options.InstallActiveNow
                    ? ModLibraryService.GetActivePakPath(settings, newMod)
                    : ModLibraryService.GetVaultPakPath(settings.VaultFolder, newMod);

                if (File.Exists(dstPak))
                {
                    if (options.InstallActiveNow)
                    {
                        string activeSha = HashService.ComputeFileSha256(dstPak);
                        if (!string.Equals(activeSha, newMod.EffectivePakSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            ModLibraryService.BackupFileToVault(dstPak, Path.Combine("Mods", newMod.PakFileName), settings);
                        }
                    }

                    File.Delete(dstPak);
                }

                FileTransactionHelper.SafeMoveFile(item.PakStagingFullPath, dstPak);
                result.ImportedPakCount++;
            }

            foreach (ModpackImportLooseFile lf in item.LooseFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(lf.StagingFullPath))
                {
                    continue;
                }

                var tfRec = new TrackedFileRecord
                {
                    Target = lf.Target,
                    RelativePath = lf.DestRelativePath,
                    FileSizeBytes = lf.FileSizeBytes,
                    Sha256 = lf.Sha256
                };

                string dstFile = options.InstallActiveNow
                    ? ModLibraryService.GetActiveTrackedFilePath(settings, tfRec)
                    : ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, newMod, tfRec);

                if (File.Exists(dstFile))
                {
                    if (options.InstallActiveNow)
                    {
                        var renamed = new List<string>();
                        ModLibraryService.ReplaceActiveTrackedFileSafely(
                            dstFile,
                            tfRec,
                            lf.Sha256,
                            newMod,
                            settings,
                            db,
                            renamedOriginals: renamed);
                        result.BackedUpOriginalGameFiles.AddRange(renamed);
                    }
                    else
                    {
                        File.Delete(dstFile);
                    }
                }

                FileTransactionHelper.SafeMoveFile(lf.StagingFullPath, dstFile);
                if (lf.Target == TrackedFileTarget.DataFolder)
                {
                    result.ImportedDataFileCount++;
                }
                else
                {
                    result.ImportedGameFileCount++;
                }
            }

            newMod.Location = ModLibraryService.EvaluateModLocation(settings, newMod);
            db.Mods.Add(newMod);
            profile.ModIds.Add(newMod.Id);
            result.ImportedModCount++;
        }

        db.Profiles.Add(profile);
        if (options.InstallActiveNow && options.SetAsActiveProfile)
        {
            db.ActiveProfileId = profile.Id;
        }

        result.CreatedProfile = profile;
        await Task.CompletedTask;
        return result;
    }

    public static void CleanupStaging(ModpackImportPlan plan)
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
        }
    }

    private static async Task SweepRemainingFilesAsync(
        string effectiveRoot,
        string[] allFiles,
        HashSet<string> consumedRels,
        ModpackImportPlan plan,
        AppSettings settings,
        LibraryDatabase db,
        CancellationToken cancellationToken)
    {
        var extraPaks = new List<(string Rel, string Full)>();
        var extraDataByGroup = new SortedDictionary<string, List<(TrackedFileTarget, string, string)>>(StringComparer.OrdinalIgnoreCase);
        var extraGameByGroup = new SortedDictionary<string, List<(TrackedFileTarget, string, string)>>(StringComparer.OrdinalIgnoreCase);

        foreach (string fullPath in allFiles)
        {
            string rel = Path.GetRelativePath(effectiveRoot, fullPath).Replace('\\', '/');
            if (consumedRels.Contains(rel) || rel.Contains(".bg3mpm_orig", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (fullPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                extraPaks.Add((rel, fullPath));
                continue;
            }

            if (parts[0].Equals("Data", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                string destRel = string.Join('/', parts.Skip(1));
                string groupKey = parts[1].Equals("Generated", StringComparison.OrdinalIgnoreCase)
                    ? "Generated Assets (Data/Generated)"
                    : $"Data/{parts[1]}";
                AddToGroup(extraDataByGroup, groupKey, TrackedFileTarget.DataFolder, destRel, fullPath);
            }
            else if (parts[0].Equals("Game", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                string destRel = string.Join('/', parts.Skip(1));
                string groupKey = DetermineGameGroupName(destRel);
                AddToGroup(extraGameByGroup, groupKey, TrackedFileTarget.GameFolder, destRel, fullPath);
            }
            else if (parts[0].Equals("Generated", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                AddToGroup(extraDataByGroup, "Generated Assets (Data/Generated)", TrackedFileTarget.DataFolder, rel, fullPath);
            }
            else if (parts[0].Equals("bin", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                AddToGroup(extraGameByGroup, DetermineGameGroupName(rel), TrackedFileTarget.GameFolder, rel, fullPath);
            }
        }

        // Order swept PAKs by OrderJsonUuids first if present.
        var pakOrderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int idx = 0; idx < plan.OrderJsonUuids.Count; idx++)
        {
            pakOrderIndex.TryAdd(plan.OrderJsonUuids[idx], idx);
        }

        var pakItems = new List<(int SortOrder, ModpackImportItem Item)>();
        foreach (var (rel, fullPath) in extraPaks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModpackImportItem item = await BuildImportItemAsync(
                Path.GetFileNameWithoutExtension(fullPath),
                ModKind.Pak,
                OverrideKind.None,
                rel,
                Path.GetFileName(fullPath),
                fullPath,
                Array.Empty<(TrackedFileTarget, string, string)>(),
                settings,
                db,
                cancellationToken);

            int sortIdx = !string.IsNullOrWhiteSpace(item.Uuid) && pakOrderIndex.TryGetValue(item.Uuid, out int idx)
                ? idx
                : int.MaxValue;
            pakItems.Add((sortIdx, item));
        }

        foreach (var entry in pakItems.OrderBy(x => x.SortOrder).ThenBy(x => x.Item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            plan.Items.Add(entry.Item);
        }

        foreach (KeyValuePair<string, List<(TrackedFileTarget, string, string)>> kv in extraDataByGroup)
        {
            plan.Items.Add(await BuildImportItemAsync(
                kv.Key,
                ModKind.LooseData,
                OverrideKind.MergedOverride,
                null,
                null,
                null,
                kv.Value,
                settings,
                db,
                cancellationToken));
        }

        foreach (KeyValuePair<string, List<(TrackedFileTarget, string, string)>> kv in extraGameByGroup)
        {
            plan.Items.Add(await BuildImportItemAsync(
                kv.Key,
                ModKind.LooseGame,
                OverrideKind.MergedOverride,
                null,
                null,
                null,
                kv.Value,
                settings,
                db,
                cancellationToken));
        }
    }

    private static async Task<ModpackImportItem> BuildImportItemAsync(
        string fallbackDisplayName,
        ModKind kind,
        OverrideKind fallbackOverrideKind,
        string? pakArchiveRel,
        string? pakFileName,
        string? pakStagingPath,
        IReadOnlyList<(TrackedFileTarget Target, string DestRel, string StagePath)> looseFiles,
        AppSettings settings,
        LibraryDatabase db,
        CancellationToken cancellationToken)
    {
        PakInfo? pakInfo = null;
        string? pakSha = null;
        long totalSize = 0;
        var hashInputs = new List<(string Key, string Sha256)>();

        if (pakStagingPath is not null && File.Exists(pakStagingPath))
        {
            pakInfo = LspkPakReader.ReadPakInfo(pakStagingPath);
            pakSha = await HashService.ComputeFileSha256Async(pakStagingPath, cancellationToken);
            totalSize += pakInfo.FileSizeBytes;
            hashInputs.Add(($"PAK:{pakFileName ?? pakInfo.FileName}", pakSha));
        }

        var importLoose = new List<ModpackImportLooseFile>();
        var originalsToBackup = new List<string>();

        foreach ((TrackedFileTarget target, string destRel, string stagePath) in looseFiles)
        {
            long len = new FileInfo(stagePath).Length;
            string sha = await HashService.ComputeFileSha256Async(stagePath, cancellationToken);
            totalSize += len;
            hashInputs.Add(($"{target}:{destRel}", sha));

            bool willBackup = false;
            bool matchesDisk = false;
            string root = target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
            if (!string.IsNullOrWhiteSpace(root))
            {
                string diskPath = FileTransactionHelper.ResolveContainedPath(root, destRel);
                if (File.Exists(diskPath))
                {
                    string diskSha = HashService.ComputeFileSha256(diskPath);
                    if (string.Equals(diskSha, sha, StringComparison.OrdinalIgnoreCase))
                    {
                        matchesDisk = true;
                    }
                    else
                    {
                        willBackup = true;
                        originalsToBackup.Add(destRel);
                    }
                }
            }

            importLoose.Add(new ModpackImportLooseFile
            {
                Target = target,
                StagingFullPath = stagePath,
                DestRelativePath = destRel,
                FileSizeBytes = len,
                Sha256 = sha,
                WillBackupOriginal = willBackup,
                MatchesExistingOnDisk = matchesDisk
            });
        }

        ModKind actualKind = (pakStagingPath is not null, importLoose.Count > 0) switch
        {
            (true, true) => ModKind.Hybrid,
            (true, false) => ModKind.Pak,
            _ => importLoose[0].Target == TrackedFileTarget.DataFolder ? ModKind.LooseData : ModKind.LooseGame
        };

        string compositeHash = actualKind == ModKind.Pak && pakSha is not null
            ? pakSha
            : HashService.ComputeCompositeHash(hashInputs);

        ModEntry? existingDup = FindExistingDuplicate(compositeHash, pakSha, actualKind, db);

        string displayName = !string.IsNullOrWhiteSpace(pakInfo?.Name)
            ? pakInfo.Name
            : fallbackDisplayName;

        OverrideKind ovrKind = pakInfo is not null
            ? pakInfo.OverrideKind
            : fallbackOverrideKind;

        return new ModpackImportItem
        {
            DisplayName = displayName,
            Kind = actualKind,
            OverrideKind = ovrKind,
            IsSelected = true,
            PakArchiveRelativePath = pakArchiveRel,
            PakFileName = pakFileName ?? pakInfo?.FileName,
            PakStagingFullPath = pakStagingPath,
            PakInfo = pakInfo,
            PakSha256 = pakSha,
            LooseFiles = importLoose,
            TotalSizeBytes = totalSize,
            FileCount = (pakStagingPath is not null ? 1 : 0) + importLoose.Count,
            ExistingDuplicateModId = existingDup?.Id,
            ExistingDuplicateModName = existingDup?.DisplayName,
            Uuid = pakInfo?.Uuid,
            VersionString = pakInfo?.VersionString,
            CompositeHash = compositeHash,
            ExistingOriginalFilesToBackup = originalsToBackup
        };
    }

    private static ModEntry? FindExistingDuplicate(string? compositeHash, string? pakSha, ModKind kind, LibraryDatabase db)
    {
        if (!string.IsNullOrWhiteSpace(compositeHash))
        {
            ModEntry? byComposite = db.Mods.FirstOrDefault(m =>
                string.Equals(m.ContentHash, compositeHash, StringComparison.OrdinalIgnoreCase));
            if (byComposite is not null)
            {
                return byComposite;
            }
        }

        if (kind == ModKind.Pak && !string.IsNullOrWhiteSpace(pakSha))
        {
            return db.Mods.FirstOrDefault(m =>
                string.Equals(m.EffectivePakSha256, pakSha, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static ModEntry CreateNewModEntry(
        ModpackImportItem item,
        string sourceArchiveName,
        LibraryDatabase db,
        bool installActiveNow)
    {
        string contentHash = item.CompositeHash ?? item.PakSha256 ?? Guid.NewGuid().ToString("N");
        List<TrackedFileRecord> tracked = item.LooseFiles.Select(lf => new TrackedFileRecord
        {
            Target = lf.Target,
            RelativePath = lf.DestRelativePath,
            FileSizeBytes = lf.FileSizeBytes,
            Sha256 = lf.Sha256
        }).ToList();

        if (item.PakInfo is not null)
        {
            var entry = ModLibraryService.CreatePakModEntry(
                item.PakInfo,
                item.PakSha256!,
                db,
                installActiveNow ? ModLocation.Active : ModLocation.Vault,
                sourceArchiveName);

            entry.Kind = item.Kind;
            entry.ContentHash = contentHash;
            entry.TotalSizeBytes = item.TotalSizeBytes;
            entry.TrackedFiles = tracked;
            entry.BackupReplacedOriginals = true;
            if (tracked.Count > 0 && !entry.OverrideReasons.Contains("Includes loose BG3 Data/Game files"))
            {
                entry.OverrideReasons.Add("Includes loose BG3 Data/Game files");
            }

            return entry;
        }

        return new ModEntry
        {
            Id = HashService.GenerateUniqueModId(contentHash, db.Mods),
            Kind = item.Kind,
            DisplayName = item.DisplayName,
            ContentHash = contentHash,
            TotalSizeBytes = item.TotalSizeBytes,
            IsDetectedOverride = true,
            DetectedOverrideKind = OverrideKind.MergedOverride,
            OverrideReasons = new List<string>
            {
                item.Kind == ModKind.LooseData ? "Loose files in BG3 Data" : "Loose files in BG3 Game / bin"
            },
            TrackedFiles = tracked,
            BackupReplacedOriginals = true,
            Location = installActiveNow ? ModLocation.Active : ModLocation.Vault,
            SourceArchiveName = sourceArchiveName,
            ImportedAtUtc = DateTime.UtcNow
        };
    }

    private static string UnwrapOptionalContainer(string stagingDir)
    {
        string[] files = Directory.GetFiles(stagingDir, "*", SearchOption.TopDirectoryOnly);
        string[] dirs = Directory.GetDirectories(stagingDir, "*", SearchOption.TopDirectoryOnly);
        if (files.Length == 0 && dirs.Length == 1)
        {
            string name = Path.GetFileName(dirs[0]);
            if (!TopStructureDirs.Contains(name))
            {
                return dirs[0];
            }
        }

        return stagingDir;
    }

    private static void AddToGroup(
        SortedDictionary<string, List<(TrackedFileTarget, string, string)>> map,
        string groupKey,
        TrackedFileTarget target,
        string destRel,
        string fullPath)
    {
        if (!map.TryGetValue(groupKey, out var list))
        {
            list = new List<(TrackedFileTarget, string, string)>();
            map[groupKey] = list;
        }

        list.Add((target, destRel, fullPath));
    }

    private static string DetermineGameGroupName(string gameRel)
    {
        if (gameRel.StartsWith("bin/NativeMods/", StringComparison.OrdinalIgnoreCase))
        {
            string sub = gameRel["bin/NativeMods/".Length..];
            int slash = sub.IndexOf('/');
            string key = slash > 0 ? sub[..slash] : Path.GetFileNameWithoutExtension(sub);
            return $"{key} (bin/NativeMods)";
        }

        if (gameRel.Equals("bin/bink2w64.dll", StringComparison.OrdinalIgnoreCase) ||
            gameRel.Equals("bin/bink2w64_original.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "Native Mod Loader (bin)";
        }

        if (gameRel.Equals("bin/DWrite.dll", StringComparison.OrdinalIgnoreCase) ||
            gameRel.StartsWith("bin/ScriptExtender", StringComparison.OrdinalIgnoreCase))
        {
            return "BG3 Script Extender (bin)";
        }

        string top = gameRel.Split('/')[0];
        return $"Game/{top}";
    }

    private static async Task ExtractZipContainedAsync(string zipPath, string destinationDir, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string dst = FileTransactionHelper.ResolveContainedPath(destinationDir, entry.FullName);
            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using Stream src = entry.Open();
            await using var dstStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dstStream, cancellationToken);
        }
    }
}
