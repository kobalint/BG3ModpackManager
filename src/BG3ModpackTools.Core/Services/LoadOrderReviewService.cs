using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Cross-checks a BG3ModManager load order JSON against the live Mods folder, Vault library,
/// and active modsettings.lsx.
/// </summary>
public static class LoadOrderReviewService
{
    public static async Task<LoadOrderAuditReport> AuditLoadOrderAsync(
        string jsonPath,
        AppSettings settings,
        LibraryDatabase db,
        PakMetadataCache cache,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"Load order JSON not found: {jsonPath}", jsonPath);
        }

        Bg3LoadOrderFile orderFile = Bg3LoadOrderService.LoadOrderJson(jsonPath);
        var activePaks = new List<(PakInfo Info, string Sha256, ModEntry? KnownMod)>();
        bool cacheDirty = false;

        if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && Directory.Exists(settings.ModsFolder))
        {
            string[] pakFiles = Directory.GetFiles(settings.ModsFolder, "*.pak", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var modByHash = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in db.Mods.OrderByDescending(x => x.Location == ModLocation.Active))
            {
                string? pakHash = m.EffectivePakSha256;
                if (!string.IsNullOrEmpty(pakHash))
                {
                    modByHash.TryAdd(pakHash, m);
                }

                if (!string.IsNullOrEmpty(m.ContentHash))
                {
                    modByHash.TryAdd(m.ContentHash, m);
                }
            }

            for (int i = 0; i < pakFiles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pakPath = pakFiles[i];
                progress?.Report($"Inspecting {Path.GetFileName(pakPath)} ({i + 1}/{pakFiles.Length})...");

                (PakInfo info, string sha256, bool updated) = await HashService.InspectPakCachedAsync(
                    pakPath,
                    cache,
                    computeHash: true,
                    cancellationToken);

                cacheDirty |= updated;
                modByHash.TryGetValue(sha256, out ModEntry? known);
                activePaks.Add((info, sha256, known));
            }
        }

        var vaultModsByUuid = db.Mods
            .Where(m => m.Kind is ModKind.Pak or ModKind.Hybrid && !string.IsNullOrWhiteSpace(m.Uuid))
            .GroupBy(m => m.Uuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var matchedActivePakPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var report = new LoadOrderAuditReport
        {
            JsonFilePath = jsonPath,
            JsonTotalCount = orderFile.Order.Count
        };

        for (int i = 0; i < orderFile.Order.Count; i++)
        {
            Bg3OrderEntry entry = orderFile.Order[i];
            int orderIndex = i + 1;

            List<(PakInfo Info, string Sha256, ModEntry? KnownMod)> activeMatches = activePaks
                .Where(p => !string.IsNullOrWhiteSpace(p.Info.Uuid) &&
                            string.Equals(p.Info.Uuid, entry.Uuid, StringComparison.OrdinalIgnoreCase))
                .ToList();

            vaultModsByUuid.TryGetValue(entry.Uuid, out List<ModEntry>? libraryMatches);
            libraryMatches ??= new List<ModEntry>();

            var distinctHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var am in activeMatches)
            {
                distinctHashes.Add(am.Sha256);
            }

            foreach (ModEntry lm in libraryMatches)
            {
                if (!string.IsNullOrEmpty(lm.ContentHash))
                {
                    distinctHashes.Add(lm.ContentHash);
                }
            }

            string? variantNote = distinctHashes.Count > 1
                ? $"{distinctHashes.Count} distinct variants share this UUID"
                : null;

            // Choose the exact candidate: prefer exact Name match across active + library (vital for KAVT vs Original unique_tav).
            var selectedActive = SelectBestActiveMatch(entry, activeMatches, libraryMatches);
            if (selectedActive.HasValue)
            {
                var (info, _, knownMod) = selectedActive.Value;
                matchedActivePakPaths.Add(info.FilePath);
                bool isOverride = knownMod?.EffectiveIsOverride ?? info.IsDetectedOverride;
                OverrideKind overrideKind = knownMod?.EffectiveOverrideKind ?? info.OverrideKind;
                ReviewStatus status = isOverride ? ReviewStatus.ActiveOverride : ReviewStatus.Active;

                report.Items.Add(new LoadOrderReviewItem
                {
                    OrderIndex = orderIndex,
                    JsonName = entry.Name,
                    JsonUuid = entry.Uuid,
                    MatchedModId = knownMod?.Id,
                    PakFileName = info.FileName,
                    PakFullPath = info.FilePath,
                    PakName = info.Name ?? info.FileName,
                    PakUuid = info.Uuid,
                    VersionString = info.VersionString,
                    FileSizeBytes = info.FileSizeBytes,
                    IsOverride = isOverride,
                    OverrideKind = overrideKind,
                    OverrideReasons = info.OverrideReasons.ToList(),
                    Status = status,
                    MultipleVariantsNote = variantNote
                });

                if (status == ReviewStatus.ActiveOverride)
                {
                    report.ActiveOverrideCount++;
                }
                else
                {
                    report.ActiveCount++;
                }

                continue;
            }

            ModEntry? vaultMatch = SelectBestVaultMatch(entry, libraryMatches, settings);
            if (vaultMatch is not null)
            {
                report.Items.Add(new LoadOrderReviewItem
                {
                    OrderIndex = orderIndex,
                    JsonName = entry.Name,
                    JsonUuid = entry.Uuid,
                    MatchedModId = vaultMatch.Id,
                    PakFileName = vaultMatch.PakFileName,
                    PakFullPath = !string.IsNullOrWhiteSpace(vaultMatch.PakFileName)
                        ? ModLibraryService.GetVaultPakPath(settings.VaultFolder, vaultMatch)
                        : null,
                    PakName = vaultMatch.DisplayName,
                    PakUuid = vaultMatch.Uuid,
                    VersionString = vaultMatch.VersionString,
                    FileSizeBytes = vaultMatch.TotalSizeBytes,
                    IsOverride = vaultMatch.EffectiveIsOverride,
                    OverrideKind = vaultMatch.EffectiveOverrideKind,
                    OverrideReasons = vaultMatch.OverrideReasons.ToList(),
                    Status = ReviewStatus.InVault,
                    MultipleVariantsNote = variantNote
                });

                report.InVaultCount++;
                continue;
            }

            report.Items.Add(new LoadOrderReviewItem
            {
                OrderIndex = orderIndex,
                JsonName = entry.Name,
                JsonUuid = entry.Uuid,
                Status = ReviewStatus.Missing,
                MultipleVariantsNote = variantNote
            });

            report.MissingCount++;
        }

        // Report every active .pak in Mods/ not matched by any JSON entry.
        foreach (var (info, _, knownMod) in activePaks)
        {
            if (matchedActivePakPaths.Contains(info.FilePath))
            {
                continue;
            }

            bool isOverride = knownMod?.EffectiveIsOverride ?? info.IsDetectedOverride;
            OverrideKind overrideKind = knownMod?.EffectiveOverrideKind ?? info.OverrideKind;

            ReviewStatus status;
            string jsonColLabel;
            if (overrideKind == OverrideKind.StandaloneOverride)
            {
                status = ReviewStatus.Bg3MmOverride;
                jsonColLabel = "(BG3MM Overrides)";
                report.Bg3MmOverrideCount++;
            }
            else if (isOverride)
            {
                status = ReviewStatus.UnlistedOverride;
                jsonColLabel = "(Inactive in JSON — Overrides Base Game!)";
                report.UnlistedOverrideCount++;
            }
            else
            {
                status = ReviewStatus.UnlistedInactive;
                jsonColLabel = "(Inactive in BG3MM)";
                report.UnlistedInactiveCount++;
            }

            report.Items.Add(new LoadOrderReviewItem
            {
                OrderIndex = null,
                JsonName = jsonColLabel,
                JsonUuid = string.Empty,
                MatchedModId = knownMod?.Id,
                PakFileName = info.FileName,
                PakFullPath = info.FilePath,
                PakName = info.Name ?? Path.GetFileNameWithoutExtension(info.FileName),
                PakUuid = info.Uuid,
                VersionString = info.VersionString,
                FileSizeBytes = info.FileSizeBytes,
                IsOverride = isOverride,
                OverrideKind = overrideKind,
                OverrideReasons = info.OverrideReasons.ToList(),
                Status = status
            });
        }

        EvaluateModSettingsComparison(orderFile, settings.ModSettingsLsxPath, report);
        return report;
    }

    internal static (PakInfo Info, string Sha256, ModEntry? KnownMod)? SelectBestActiveMatch(
        Bg3OrderEntry entry,
        List<(PakInfo Info, string Sha256, ModEntry? KnownMod)> activeMatches,
        List<ModEntry> libraryMatches)
    {
        if (activeMatches.Count == 0)
        {
            return null;
        }

        var activeExactName = activeMatches
            .FirstOrDefault(m => string.Equals(m.Info.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (activeExactName.Info is not null)
        {
            return activeExactName;
        }

        // If a Vault variant matches the exact JSON Name while an active variant has a completely different Name
        // (e.g., Original unique_tav active vs KAVT in Vault), do NOT falsely claim the wrong variant is active.
        bool vaultHasExactNameMatch = libraryMatches.Any(lm =>
            lm.Location == ModLocation.Vault &&
            !activeMatches.Any(a => string.Equals(a.Sha256, lm.ContentHash, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(lm.DisplayName, entry.Name, StringComparison.OrdinalIgnoreCase));

        if (vaultHasExactNameMatch)
        {
            return null;
        }

        return activeMatches
            .OrderByDescending(m => ParseVersionNumeric(m.Info.Version64))
            .ThenBy(m => m.Info.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    internal static ModEntry? SelectBestVaultMatch(
        Bg3OrderEntry entry,
        List<ModEntry> libraryMatches,
        AppSettings settings)
    {
        var vaultAvailable = libraryMatches
            .Where(m => m.Location == ModLocation.Vault &&
                        !string.IsNullOrWhiteSpace(m.PakFileName) &&
                        File.Exists(ModLibraryService.GetVaultPakPath(settings.VaultFolder, m)))
            .ToList();

        if (vaultAvailable.Count == 0)
        {
            return null;
        }

        ModEntry? exactName = vaultAvailable
            .FirstOrDefault(m => string.Equals(m.DisplayName, entry.Name, StringComparison.OrdinalIgnoreCase));

        return exactName ?? vaultAvailable
            .OrderByDescending(m => ParseVersionNumeric(m.Version64))
            .First();
    }

    private static ulong ParseVersionNumeric(string? v64)
    {
        return ulong.TryParse(v64, out ulong val) ? val : 0UL;
    }

    private static void EvaluateModSettingsComparison(Bg3LoadOrderFile orderFile, string modSettingsLsxPath, LoadOrderAuditReport report)
    {
        if (string.IsNullOrWhiteSpace(modSettingsLsxPath) || !File.Exists(modSettingsLsxPath))
        {
            report.ModSettingsLsxMatchesJson = null;
            report.ModSettingsLsxSummary = "modsettings.lsx not found";
            return;
        }

        try
        {
            List<ModSettingsEntry> lsxMods = Bg3LoadOrderService.ReadModSettingsLsx(modSettingsLsxPath);
            List<string> jsonUuids = orderFile.Order.Select(o => o.Uuid).ToList();
            List<string> lsxUuids = lsxMods.Select(m => m.Uuid).ToList();

            bool exactOrderMatch = jsonUuids.Count == lsxUuids.Count &&
                                   jsonUuids.Zip(lsxUuids, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x);

            report.ModSettingsLsxMatchesJson = exactOrderMatch;
            report.ModSettingsLsxSummary = exactOrderMatch
                ? $"modsettings.lsx matches JSON order ({lsxUuids.Count} mods)"
                : $"modsettings.lsx has {lsxUuids.Count} active mods vs {jsonUuids.Count} in JSON";
        }
        catch (Exception ex)
        {
            report.ModSettingsLsxMatchesJson = null;
            report.ModSettingsLsxSummary = $"Could not read modsettings.lsx: {ex.Message}";
        }
    }
}
