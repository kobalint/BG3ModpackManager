using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.Core.Services;

/// <summary>
/// Builds dry-run switch plans and executes two-phase profile switching while leaving shared mods untouched.
/// </summary>
public static class ProfileSwitchService
{
    public static async Task<SwitchPlan> BuildSwitchPlanAsync(
        ModProfile targetProfile,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = new SwitchPlan
        {
            TargetProfileId = targetProfile.Id,
            TargetProfileName = targetProfile.Name,
            SwitchMode = settings.SwitchMode
        };

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var modByHash = db.Mods
            .Where(m => !string.IsNullOrEmpty(m.ContentHash))
            .ToDictionary(m => m.ContentHash, StringComparer.OrdinalIgnoreCase);

        // Scan live Mods folder so untracked .pak files are detected accurately before planning.
        var liveActivePaks = new Dictionary<string, (PakInfo Info, string Sha256)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && Directory.Exists(settings.ModsFolder))
        {
            string[] pakFiles = Directory.GetFiles(settings.ModsFolder, "*.pak", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < pakFiles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pakPath = pakFiles[i];
                progress?.Report($"Scanning active Mods ({i + 1}/{pakFiles.Length})...");
                (PakInfo info, string sha256, _) = await HashService.InspectPakCachedAsync(
                    pakPath,
                    cache,
                    computeHash: true,
                    cancellationToken);

                liveActivePaks[Path.GetFileName(pakPath)] = (info, sha256);
            }
        }

        // Resolve desired target mods.
        var targetMods = new List<ModEntry>();
        var seenTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string modId in targetProfile.ModIds)
        {
            if (!seenTargetIds.Add(modId))
            {
                continue;
            }

            if (modById.TryGetValue(modId, out ModEntry? mod))
            {
                targetMods.Add(mod);
            }
            else
            {
                plan.Actions.Add(new SwitchActionItem
                {
                    ActionType = SwitchActionType.MissingMod,
                    ModId = modId,
                    DisplayName = $"Unknown Mod ({modId})",
                    Reason = "Profile references a mod ID no longer present in the library."
                });
            }
        }

        // Conflict checks among target mods (e.g., two different unique_tav.pak variants or overlapping loose Data files).
        var targetPakNames = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var targetLoosePaths = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var conflictingModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModEntry mod in targetMods)
        {
            if (!string.IsNullOrWhiteSpace(mod.PakFileName))
            {
                if (targetPakNames.TryGetValue(mod.PakFileName, out ModEntry? other))
                {
                    conflictingModIds.Add(mod.Id);
                    conflictingModIds.Add(other.Id);
                    plan.Actions.Add(new SwitchActionItem
                    {
                        ActionType = SwitchActionType.FileConflict,
                        ModId = mod.Id,
                        DisplayName = mod.DisplayName,
                        PakFileName = mod.PakFileName,
                        SizeBytes = mod.TotalSizeBytes,
                        IsOverride = mod.EffectiveIsOverride,
                        Reason = $"Profile includes two mods with the same PAK filename '{mod.PakFileName}' ('{other.DisplayName}' and '{mod.DisplayName}')."
                    });
                }
                else
                {
                    targetPakNames[mod.PakFileName] = mod;
                }
            }

            foreach (TrackedFileRecord tf in mod.TrackedFiles)
            {
                string key = $"{tf.Target}:{tf.RelativePath.Replace('\\', '/')}";
                if (targetLoosePaths.TryGetValue(key, out ModEntry? other))
                {
                    conflictingModIds.Add(mod.Id);
                    plan.Actions.Add(new SwitchActionItem
                    {
                        ActionType = SwitchActionType.FileConflict,
                        ModId = mod.Id,
                        DisplayName = mod.DisplayName,
                        Reason = $"Target loose file '{tf.RelativePath}' conflicts with '{other.DisplayName}'."
                    });
                }
                else
                {
                    targetLoosePaths[key] = mod;
                }
            }
        }

        var targetSet = new HashSet<string>(targetMods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var targetHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry tm in targetMods)
        {
            if (!string.IsNullOrEmpty(tm.ContentHash))
            {
                targetHashes.Add(tm.ContentHash);
            }

            if (!string.IsNullOrEmpty(tm.EffectivePakSha256))
            {
                targetHashes.Add(tm.EffectivePakSha256);
            }
        }
        var incomingPakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Classify target mods: StayInPlace vs ActivateFromVault vs Missing.
        foreach (ModEntry mod in targetMods)
        {
            if (conflictingModIds.Contains(mod.Id))
            {
                continue;
            }

            string? expectedPakSha = mod.EffectivePakSha256;
            bool pakActiveMatchesHash = string.IsNullOrWhiteSpace(mod.PakFileName) ||
                (liveActivePaks.TryGetValue(mod.PakFileName, out var activePair) &&
                 (expectedPakSha is not null
                     ? string.Equals(activePair.Sha256, expectedPakSha, StringComparison.OrdinalIgnoreCase)
                     : !HasConflictingActivePak(mod, liveActivePaks)));

            bool allTrackedActive = mod.TrackedFiles.All(tf =>
                (string.IsNullOrWhiteSpace(settings.VaultFolder) ||
                 !File.Exists(ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, mod, tf))) &&
                File.Exists(ModLibraryService.GetActiveTrackedFilePath(settings, tf)));
            bool pakPresent = string.IsNullOrWhiteSpace(mod.PakFileName) ||
                              (!string.IsNullOrWhiteSpace(settings.ModsFolder) && File.Exists(ModLibraryService.GetActivePakPath(settings, mod)));

            bool isHybridOrPakActive = mod.Kind == ModKind.Hybrid
                ? pakPresent && allTrackedActive && pakActiveMatchesHash
                : pakActiveMatchesHash && allTrackedActive;

            if (isHybridOrPakActive)
            {
                plan.Actions.Add(new SwitchActionItem
                {
                    ActionType = SwitchActionType.StayInPlace,
                    ModId = mod.Id,
                    DisplayName = mod.DisplayName,
                    PakFileName = mod.PakFileName,
                    TrackedFileCount = mod.TrackedFiles.Count,
                    SizeBytes = mod.TotalSizeBytes,
                    IsOverride = mod.EffectiveIsOverride,
                    Reason = "Already active (shared mod — no files moved)"
                });
                continue;
            }

            // Can we satisfy it from Vault (or partial Vault + Active)?
            bool pakReadyInVaultOrActive = string.IsNullOrWhiteSpace(mod.PakFileName) ||
                File.Exists(ModLibraryService.GetVaultPakPath(settings.VaultFolder, mod)) ||
                (!HasConflictingActivePak(mod, liveActivePaks) && pakPresent);

            bool trackedReadyInVaultOrActive = mod.TrackedFiles.All(tf =>
                File.Exists(ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, mod, tf)) ||
                File.Exists(ModLibraryService.GetActiveTrackedFilePath(settings, tf)));

            if (pakReadyInVaultOrActive && trackedReadyInVaultOrActive)
            {
                if (!string.IsNullOrWhiteSpace(mod.PakFileName) && !pakPresent)
                {
                    incomingPakNames.Add(mod.PakFileName);
                }
                else if (!string.IsNullOrWhiteSpace(mod.PakFileName) && HasConflictingActivePak(mod, liveActivePaks))
                {
                    incomingPakNames.Add(mod.PakFileName);
                }

                // Check if an unmanaged file on disk blocks an incoming tracked file.
                bool unmanagedBlocked = false;
                bool willRenameOriginalBackup = false;
                foreach (TrackedFileRecord tf in mod.TrackedFiles)
                {
                    string activePath = ModLibraryService.GetActiveTrackedFilePath(settings, tf);
                    if (File.Exists(activePath))
                    {
                        string vaultPath = ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, mod, tf);
                        if (!File.Exists(vaultPath))
                        {
                            continue;
                        }

                        // If it exists in BOTH Vault and Active, check whether another tracked mod owns the Active copy.
                        bool ownedByAnotherActiveMod = db.Mods.Any(other =>
                            other.Id != mod.Id &&
                            other.Location == ModLocation.Active &&
                            other.TrackedFiles.Any(otf =>
                                otf.Target == tf.Target &&
                                string.Equals(otf.RelativePath, tf.RelativePath, StringComparison.OrdinalIgnoreCase)));

                        if (!ownedByAnotherActiveMod)
                        {
                            bool canRenameBackup = mod.BackupReplacedOriginals ||
                                mod.ReplacedOriginalFiles.Any(r =>
                                    r.Target == tf.Target &&
                                    string.Equals(r.RelativePath, tf.RelativePath, StringComparison.OrdinalIgnoreCase));

                            if (canRenameBackup)
                            {
                                willRenameOriginalBackup = true;
                                continue;
                            }

                            plan.Actions.Add(new SwitchActionItem
                            {
                                ActionType = SwitchActionType.FileConflict,
                                ModId = mod.Id,
                                DisplayName = mod.DisplayName,
                                Reason = $"Unmanaged file already exists at '{activePath}'."
                            });
                            unmanagedBlocked = true;
                            break;
                        }
                    }
                }

                if (!unmanagedBlocked)
                {
                    string activateReason = mod.TrackedFiles.Count > 0
                        ? (willRenameOriginalBackup
                            ? $"Activate from Vault ({mod.TrackedFiles.Count} Data/Game files, backs up existing game file)"
                            : $"Activate from Vault ({mod.TrackedFiles.Count} Data/Game files)")
                        : "Activate from Vault";

                    plan.Actions.Add(new SwitchActionItem
                    {
                        ActionType = SwitchActionType.ActivateFromVault,
                        ModId = mod.Id,
                        DisplayName = mod.DisplayName,
                        PakFileName = mod.PakFileName,
                        TrackedFileCount = mod.TrackedFiles.Count,
                        SizeBytes = mod.TotalSizeBytes,
                        IsOverride = mod.EffectiveIsOverride,
                        Reason = activateReason,
                        SourcePath = !string.IsNullOrWhiteSpace(mod.PakFileName)
                            ? ModLibraryService.GetVaultPakPath(settings.VaultFolder, mod)
                            : null,
                        DestinationPath = !string.IsNullOrWhiteSpace(mod.PakFileName)
                            ? ModLibraryService.GetActivePakPath(settings, mod)
                            : null
                    });
                }
            }
            else
            {
                plan.Actions.Add(new SwitchActionItem
                {
                    ActionType = SwitchActionType.MissingMod,
                    ModId = mod.Id,
                    DisplayName = mod.DisplayName,
                    PakFileName = mod.PakFileName,
                    TrackedFileCount = mod.TrackedFiles.Count,
                    SizeBytes = mod.TotalSizeBytes,
                    IsOverride = mod.EffectiveIsOverride,
                    Reason = "Files missing from both active folders and Vault."
                });
            }
        }

        // Check every tracked mod currently on disk that is NOT in the target profile.
        var handledActivePakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in targetMods)
        {
            if (!string.IsNullOrWhiteSpace(mod.PakFileName))
            {
                if (liveActivePaks.ContainsKey(mod.PakFileName) &&
                    !HasConflictingActivePak(mod, liveActivePaks))
                {
                    handledActivePakNames.Add(mod.PakFileName);
                }
            }
        }

        foreach (ModEntry mod in db.Mods)
        {
            if (targetSet.Contains(mod.Id))
            {
                continue;
            }

            bool hasActivePak = !string.IsNullOrWhiteSpace(mod.PakFileName) &&
                                liveActivePaks.ContainsKey(mod.PakFileName) &&
                                !HasConflictingActivePak(mod, liveActivePaks);

            bool hasActiveTrackedFiles = mod.TrackedFiles.Any(tf =>
                (string.IsNullOrWhiteSpace(settings.VaultFolder) ||
                 !File.Exists(ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, mod, tf))) &&
                File.Exists(ModLibraryService.GetActiveTrackedFilePath(settings, tf)));

            if (!hasActivePak && !hasActiveTrackedFiles)
            {
                continue;
            }

            if (hasActivePak && !string.IsNullOrWhiteSpace(mod.PakFileName))
            {
                handledActivePakNames.Add(mod.PakFileName);
            }

            bool filenameCollidesWithIncoming = !string.IsNullOrWhiteSpace(mod.PakFileName) &&
                                                incomingPakNames.Contains(mod.PakFileName);

            bool shouldMoveToVault =
                mod.TrackedFiles.Count > 0 ||
                mod.EffectiveIsOverride ||
                settings.SwitchMode == SwitchMode.FullProfile ||
                filenameCollidesWithIncoming;

            if (shouldMoveToVault)
            {
                string reason = filenameCollidesWithIncoming
                    ? $"Filename collision with target profile '{mod.PakFileName}'"
                    : mod.TrackedFiles.Count > 0 && mod.EffectiveIsOverride
                        ? $"Override + {mod.TrackedFiles.Count} Data/Game files not in target profile"
                        : mod.TrackedFiles.Count > 0
                            ? $"Data/Game loose files ({mod.TrackedFiles.Count}) not in target profile"
                            : mod.EffectiveIsOverride
                                ? "Override mod not in target profile"
                                : "Not in target profile (Full Profile mode)";

                plan.Actions.Add(new SwitchActionItem
                {
                    ActionType = SwitchActionType.MoveToVault,
                    ModId = mod.Id,
                    DisplayName = mod.DisplayName,
                    PakFileName = mod.PakFileName,
                    TrackedFileCount = mod.TrackedFiles.Count,
                    SizeBytes = mod.TotalSizeBytes,
                    IsOverride = mod.EffectiveIsOverride,
                    Reason = reason,
                    SourcePath = !string.IsNullOrWhiteSpace(mod.PakFileName)
                        ? ModLibraryService.GetActivePakPath(settings, mod)
                        : null,
                    DestinationPath = !string.IsNullOrWhiteSpace(mod.PakFileName)
                        ? ModLibraryService.GetVaultPakPath(settings.VaultFolder, mod)
                        : null
                });
            }
        }

        // Check any untracked .pak currently sitting in ModsFolder.
        foreach ((string fileName, (PakInfo info, string sha256)) in liveActivePaks)
        {
            if (handledActivePakNames.Contains(fileName))
            {
                continue;
            }

            if (targetHashes.Contains(sha256))
            {
                continue;
            }

            bool filenameCollidesWithIncoming = incomingPakNames.Contains(fileName);
            bool shouldAdoptToVault =
                info.IsDetectedOverride ||
                settings.SwitchMode == SwitchMode.FullProfile ||
                filenameCollidesWithIncoming;

            if (shouldAdoptToVault)
            {
                string reason = info.IsDetectedOverride
                    ? "Untracked active Override PAK — adopt & park in Vault"
                    : filenameCollidesWithIncoming
                        ? "Untracked PAK shares filename with target profile — adopt & park in Vault"
                        : "Untracked active PAK — adopt & park in Vault";

                plan.Actions.Add(new SwitchActionItem
                {
                    ActionType = SwitchActionType.AdoptUntrackedToVault,
                    DisplayName = string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileNameWithoutExtension(fileName) : info.Name,
                    PakFileName = fileName,
                    SizeBytes = info.FileSizeBytes,
                    IsOverride = info.IsDetectedOverride,
                    Reason = reason,
                    SourcePath = info.FilePath
                });
            }
        }

        return plan;
    }

    public static async Task<SwitchExecutionResult> ExecuteSwitchPlanAsync(
        SwitchPlan plan,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        JsonStore store,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new SwitchExecutionResult();
        GameProcessGuard.EnsureGameNotRunning($"switching to profile '{plan.TargetProfileName}'");

        if (!plan.CanExecute)
        {
            result.Success = false;
            result.Errors.Add("Switch aborted: resolve file conflicts first.");
            return result;
        }

        var modById = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        // Phase 1: Move outgoing mods (and untracked overrides/colliding PAKs) TO Vault BEFORE moving incoming mods IN.
        var outgoing = plan.Actions
            .Where(a => a.ActionType is SwitchActionType.MoveToVault or SwitchActionType.AdoptUntrackedToVault)
            .ToList();

        for (int i = 0; i < outgoing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwitchActionItem action = outgoing[i];
            progress?.Report($"Moving to Vault ({i + 1}/{outgoing.Count}): {action.DisplayName}");

            try
            {
                if (action.ActionType == SwitchActionType.MoveToVault && action.ModId is not null && modById.TryGetValue(action.ModId, out ModEntry? existingMod))
                {
                    ModLibraryService.MoveModToVault(existingMod, settings);
                    result.MovedToVaultCount++;
                    result.LogLines.Add($"-> Vault: {existingMod.DisplayName}");
                }
                else if (action.ActionType == SwitchActionType.AdoptUntrackedToVault && !string.IsNullOrWhiteSpace(action.SourcePath) && File.Exists(action.SourcePath))
                {
                    (PakInfo info, string sha256, _) = await HashService.InspectPakCachedAsync(
                        action.SourcePath,
                        cache,
                        computeHash: true,
                        cancellationToken);

                    ModEntry? adoptedMod = db.Mods.FirstOrDefault(m => string.Equals(m.ContentHash, sha256, StringComparison.OrdinalIgnoreCase));
                    if (adoptedMod is null)
                    {
                        adoptedMod = ModLibraryService.CreatePakModEntry(info, sha256, db, ModLocation.Active);
                        db.Mods.Add(adoptedMod);
                        modById[adoptedMod.Id] = adoptedMod;
                    }

                    ModLibraryService.MoveModToVault(adoptedMod, settings);
                    result.MovedToVaultCount++;
                    result.LogLines.Add($"-> Adopted & Vault: {adoptedMod.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed moving '{action.DisplayName}' to Vault: {ex.Message}");
            }
        }

        // Persist mid-switch progress after Phase 1.
        store.SaveLibrary(db);

        if (result.Errors.Count > 0)
        {
            result.Success = false;
            return result;
        }

        // Phase 2: Activate incoming mods FROM Vault.
        var incoming = plan.Actions
            .Where(a => a.ActionType == SwitchActionType.ActivateFromVault)
            .ToList();

        for (int i = 0; i < incoming.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwitchActionItem action = incoming[i];
            progress?.Report($"Activating ({i + 1}/{incoming.Count}): {action.DisplayName}");

            try
            {
                if (action.ModId is not null && modById.TryGetValue(action.ModId, out ModEntry? mod))
                {
                    ModLibraryService.ActivateModFromVault(mod, settings);
                    result.ActivatedCount++;
                    result.LogLines.Add($"<- Active: {mod.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed activating '{action.DisplayName}': {ex.Message}");
            }
        }

        result.StayedInPlaceCount = plan.SharedCount;

        if (result.Errors.Count == 0)
        {
            db.ActiveProfileId = plan.TargetProfileId;
            ModLibraryService.ReconcileDiskState(settings, db);
            store.SaveLibrary(db);

            if (settings.SyncModSettingsLsxOnSwitch && !string.IsNullOrWhiteSpace(settings.ModSettingsLsxPath))
            {
                ModProfile? targetProfile = db.Profiles.FirstOrDefault(p => p.Id == plan.TargetProfileId);
                if (targetProfile is not null)
                {
                    var orderedMods = targetProfile.ModIds
                        .Select(id => modById.TryGetValue(id, out ModEntry? m) ? m : null)
                        .Where(m => m is not null)
                        .Cast<ModEntry>()
                        .ToList();

                    Bg3LoadOrderService.WriteModSettingsLsx(settings.ModSettingsLsxPath, orderedMods);
                    result.LogLines.Add($"Synced modsettings.lsx ({orderedMods.Count(m => m.HasMetaLsx)} XML entries).");
                }
            }

            result.Success = true;
        }
        else
        {
            store.SaveLibrary(db);
            result.Success = false;
        }

        return result;
    }

    private static bool HasConflictingActivePak(
        ModEntry mod,
        Dictionary<string, (PakInfo Info, string Sha256)> liveActivePaks)
    {
        if (string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            return false;
        }

        if (!liveActivePaks.TryGetValue(mod.PakFileName, out var activePair))
        {
            return false;
        }

        string? expectedPakSha = mod.EffectivePakSha256;
        if (!string.IsNullOrEmpty(expectedPakSha))
        {
            return !string.Equals(activePair.Sha256, expectedPakSha, StringComparison.OrdinalIgnoreCase);
        }

        // Legacy fallback when PakSha256 was not yet populated on a Hybrid entry.
        bool uuidMatches = string.IsNullOrWhiteSpace(mod.Uuid) ||
                           string.Equals(activePair.Info.Uuid, mod.Uuid, StringComparison.OrdinalIgnoreCase);
        bool nameMatches = string.Equals(
            activePair.Info.Name ?? Path.GetFileNameWithoutExtension(activePair.Info.FileName),
            mod.DisplayName,
            StringComparison.OrdinalIgnoreCase);

        return !(uuidMatches && nameMatches);
    }
}
