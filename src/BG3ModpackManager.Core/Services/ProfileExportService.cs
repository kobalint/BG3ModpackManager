using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.Core.Services;

/// <summary>
/// Builds pre-flight previews and packages complete modpack profiles (PAKs, loose Data/Game mods,
/// BG3ModManager JSON load order, manifest, and install notes) into portable .7z archives.
/// </summary>
public static partial class ProfileExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex ProgressPercentRegex();

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "ModpackProfile";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string sanitized = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "ModpackProfile" : sanitized;
    }

    public static ProfileExportPreview BuildExportPreview(
        ModProfile profile,
        AppSettings settings,
        LibraryDatabase db)
    {
        string? sevenZip = SevenZipHelper.FindExecutable();
        var modMap = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var preview = new ProfileExportPreview
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            SevenZipAvailable = sevenZip is not null,
            SevenZipPath = sevenZip
        };

        var seenPakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDataRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGameRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string modId in profile.ModIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!modMap.TryGetValue(modId, out ModEntry? mod))
            {
                preview.MissingModCount++;
                preview.Warnings.Add($"Missing library mod record: {modId}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mod.PakFileName))
            {
                if (!seenPakNames.Add(mod.PakFileName))
                {
                    preview.Warnings.Add($"Duplicate PAK filename '{mod.PakFileName}' in profile; only the first entry can occupy Mods/.");
                }
                else if (TryResolvePakSourcePath(mod, settings, out string? pakPath))
                {
                    preview.PakModCount++;
                    preview.PakTotalBytes += new FileInfo(pakPath!).Length;
                }
                else
                {
                    preview.MissingFileCount++;
                    preview.Warnings.Add($"Missing PAK file on disk: {mod.PakFileName} ({mod.DisplayName})");
                }
            }

            bool hasData = false;
            bool hasGame = false;
            foreach (TrackedFileRecord tf in mod.TrackedFiles)
            {
                string normRel = tf.RelativePath.Replace('\\', '/');
                if (!TryResolveTrackedSourcePath(mod, tf, settings, out string? filePath))
                {
                    preview.MissingFileCount++;
                    preview.Warnings.Add($"Missing loose file: {tf.RelativePath} ({mod.DisplayName})");
                    continue;
                }

                long len = new FileInfo(filePath!).Length;
                if (tf.Target == TrackedFileTarget.DataFolder)
                {
                    seenDataRelPaths.Add(normRel);
                    hasData = true;
                    preview.LooseDataFileCount++;
                    preview.LooseDataTotalBytes += len;
                }
                else
                {
                    seenGameRelPaths.Add(normRel);
                    hasGame = true;
                    preview.LooseGameFileCount++;
                    preview.LooseGameTotalBytes += len;
                }
            }

            if (hasData)
            {
                preview.LooseDataModCount++;
            }

            if (hasGame)
            {
                preview.LooseGameModCount++;
            }
        }

        List<(string FullPath, string RelativePath, long Size)> liveGen = DiscoverUntrackedGeneratedFiles(settings, seenDataRelPaths);
        preview.LiveGeneratedFileCount = liveGen.Count;
        preview.LiveGeneratedTotalBytes = liveGen.Sum(f => f.Size);

        List<(string DisplayName, List<(string FullPath, string RelativePath, long Size)> Files)> liveBin =
            DiscoverUntrackedBinModGroups(settings, seenGameRelPaths);
        preview.LiveBinModCount = liveBin.Count;
        preview.LiveBinFileCount = liveBin.Sum(g => g.Files.Count);
        preview.LiveBinTotalBytes = liveBin.Sum(g => g.Files.Sum(f => f.Size));

        return preview;
    }

    public static async Task<ProfileExportResult> ExportProfileTo7zAsync(
        ModProfile profile,
        ProfileExportOptions options,
        AppSettings settings,
        LibraryDatabase db,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputArchivePath))
        {
            throw new ArgumentException("Output archive path cannot be empty.", nameof(options));
        }

        string sevenZipPath = SevenZipHelper.FindExecutable()
            ?? throw new InvalidOperationException(
                "7-Zip (7z.exe / 7za.exe) was not found. Install 7-Zip or place 7z.exe next to BG3ModpackManager.exe to export .7z archives.");

        string outputPath = Path.GetFullPath(options.OutputArchivePath.Trim());
        if (!outputPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".7z";
        }

        string outDir = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Invalid output path: {outputPath}");
        Directory.CreateDirectory(outDir);

        string stagingDir = Path.Combine(Path.GetTempPath(), "BG3MPM_Export_" + Guid.NewGuid().ToString("N")[..12]);
        string tempArchivePath = outputPath + ".bg3tmp.7z";

        var result = new ProfileExportResult
        {
            OutputArchivePath = outputPath
        };

        try
        {
            Directory.CreateDirectory(stagingDir);
            if (File.Exists(tempArchivePath))
            {
                File.Delete(tempArchivePath);
            }

            var modMap = db.Mods.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            var usedPakNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stagedDataRelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stagedGameRelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var orderEntries = new List<Bg3OrderEntry>();
            var manifestMods = new List<ProfileExportedModRecord>();
            int compressionLevel = Math.Clamp(options.CompressionLevel, 0, 9);

            List<string> uniqueModIds = profile.ModIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < uniqueModIds.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string modId = uniqueModIds[i];

                if (!modMap.TryGetValue(modId, out ModEntry? mod))
                {
                    string missingMsg = $"Missing library mod ID: {modId}";
                    result.MissingItems.Add(missingMsg);
                    result.Warnings.Add(missingMsg);
                    continue;
                }

                progress?.Report($"Staging {i + 1}/{uniqueModIds.Count}: {mod.DisplayName}...");

                var record = new ProfileExportedModRecord
                {
                    Id = mod.Id,
                    DisplayName = mod.DisplayName,
                    Kind = mod.Kind,
                    OverrideKind = mod.EffectiveOverrideKind,
                    Uuid = mod.Uuid,
                    VersionString = mod.VersionString,
                    PakFileName = mod.PakFileName,
                    ContentHash = mod.ContentHash
                };

                bool stagedAnythingForMod = false;
                bool missingSomethingForMod = false;

                if (options.IncludePaks && !string.IsNullOrWhiteSpace(mod.PakFileName))
                {
                    if (!usedPakNames.Add(mod.PakFileName))
                    {
                        string warn = $"Skipped duplicate PAK filename '{mod.PakFileName}' from '{mod.DisplayName}' cleanly to avoid overwriting.";
                        result.Warnings.Add(warn);
                        record.IsMissing = true;
                    }
                    else if (TryResolvePakSourcePath(mod, settings, out string? pakSrc))
                    {
                        string pakDst = FileTransactionHelper.ResolveContainedPath(Path.Combine(stagingDir, "Mods"), mod.PakFileName);
                        FileTransactionHelper.StageReadOnlyFile(pakSrc!, pakDst);
                        result.ExportedPakCount++;
                        stagedAnythingForMod = true;

                        if (!string.IsNullOrWhiteSpace(mod.Uuid) &&
                            mod.HasMetaLsx &&
                            mod.EffectiveOverrideKind != OverrideKind.StandaloneOverride)
                        {
                            orderEntries.Add(new Bg3OrderEntry
                            {
                                Uuid = mod.Uuid!,
                                Name = mod.DisplayName
                            });
                        }
                    }
                    else
                    {
                        missingSomethingForMod = true;
                        string missingPak = $"Missing PAK: {mod.PakFileName} ({mod.DisplayName})";
                        result.MissingItems.Add(missingPak);
                        result.Warnings.Add(missingPak);
                    }
                }

                if (options.IncludeLooseMods)
                {
                    foreach (TrackedFileRecord tf in mod.TrackedFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryResolveTrackedSourcePath(mod, tf, settings, out string? srcFile))
                        {
                            missingSomethingForMod = true;
                            string missingLoose = $"Missing loose file: {tf.RelativePath} ({mod.DisplayName})";
                            result.MissingItems.Add(missingLoose);
                            result.Warnings.Add(missingLoose);
                            continue;
                        }

                        string bucket = tf.Target == TrackedFileTarget.DataFolder ? "Data" : "Game";
                        string normRel = tf.RelativePath.Replace('\\', '/');
                        Dictionary<string, string> conflictMap = tf.Target == TrackedFileTarget.DataFolder
                            ? stagedDataRelPaths
                            : stagedGameRelPaths;

                        if (conflictMap.TryGetValue(normRel, out string? ownerName))
                        {
                            result.Warnings.Add($"Overlapping {bucket} path '{normRel}': '{mod.DisplayName}' replaced file from '{ownerName}' cleanly.");
                        }

                        string dstFile = FileTransactionHelper.ResolveContainedPath(Path.Combine(stagingDir, bucket), tf.RelativePath);
                        FileTransactionHelper.StageReadOnlyFile(srcFile!, dstFile);
                        conflictMap[normRel] = mod.DisplayName;
                        stagedAnythingForMod = true;

                        if (tf.Target == TrackedFileTarget.DataFolder)
                        {
                            result.ExportedDataFileCount++;
                            record.DataFiles.Add(normRel);
                        }
                        else
                        {
                            result.ExportedGameFileCount++;
                            record.GameFiles.Add(normRel);
                        }
                    }
                }

                if (missingSomethingForMod)
                {
                    record.IsMissing = true;
                }

                if (stagedAnythingForMod)
                {
                    result.ExportedModCount++;
                }

                manifestMods.Add(record);
            }

            if (options.IncludeGeneratedFolder)
            {
                List<(string FullPath, string RelativePath, long Size)> liveGen =
                    DiscoverUntrackedGeneratedFiles(settings, stagedDataRelPaths.Keys);
                if (liveGen.Count > 0)
                {
                    progress?.Report($"Staging {liveGen.Count} live Data/Generated files...");
                    var genRecord = new ProfileExportedModRecord
                    {
                        Id = "live-generated",
                        DisplayName = "Generated Assets (Data/Generated)",
                        Kind = ModKind.LooseData,
                        OverrideKind = OverrideKind.MergedOverride
                    };

                    foreach (var (fullPath, relPath, _) in liveGen)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string dstFile = FileTransactionHelper.ResolveContainedPath(Path.Combine(stagingDir, "Data"), relPath);
                        FileTransactionHelper.StageReadOnlyFile(fullPath, dstFile);
                        stagedDataRelPaths[relPath] = genRecord.DisplayName;
                        genRecord.DataFiles.Add(relPath);
                        result.ExportedDataFileCount++;
                    }

                    result.ExportedModCount++;
                    manifestMods.Add(genRecord);
                }
            }

            if (options.IncludeBinMods)
            {
                List<(string DisplayName, List<(string FullPath, string RelativePath, long Size)> Files)> liveBinGroups =
                    DiscoverUntrackedBinModGroups(settings, stagedGameRelPaths.Keys);
                for (int gIdx = 0; gIdx < liveBinGroups.Count; gIdx++)
                {
                    var group = liveBinGroups[gIdx];
                    progress?.Report($"Staging {group.DisplayName}...");
                    var binRecord = new ProfileExportedModRecord
                    {
                        Id = $"live-bin-{gIdx + 1}",
                        DisplayName = group.DisplayName,
                        Kind = ModKind.LooseGame,
                        OverrideKind = OverrideKind.MergedOverride
                    };

                    foreach (var (fullPath, relPath, _) in group.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string dstFile = FileTransactionHelper.ResolveContainedPath(Path.Combine(stagingDir, "Game"), relPath);
                        FileTransactionHelper.StageReadOnlyFile(fullPath, dstFile);
                        stagedGameRelPaths[relPath] = binRecord.DisplayName;
                        binRecord.GameFiles.Add(relPath);
                        result.ExportedGameFileCount++;
                    }

                    result.ExportedModCount++;
                    manifestMods.Add(binRecord);
                }
            }

            string safeProfileName = SanitizeFileName(profile.Name);

            if (options.IncludeOrderJson)
            {
                string jsonDst = Path.Combine(stagingDir, $"{safeProfileName}.json");
                Bg3LoadOrderService.SaveOrderJson(jsonDst, orderEntries);
                result.IncludedOrderJson = true;
            }

            if (options.IncludeManifestAndReadme)
            {
                var manifest = new ProfileExportManifest
                {
                    ProfileName = profile.Name,
                    ProfileId = profile.Id,
                    ExportedAtUtc = DateTime.UtcNow,
                    CompressionLevel = compressionLevel,
                    ExportedPakCount = result.ExportedPakCount,
                    ExportedDataFileCount = result.ExportedDataFileCount,
                    ExportedGameFileCount = result.ExportedGameFileCount,
                    Mods = manifestMods,
                    Warnings = result.Warnings.ToList()
                };

                string manifestDst = Path.Combine(stagingDir, "profile_manifest.json");
                await File.WriteAllTextAsync(manifestDst, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

                string readmeDst = Path.Combine(stagingDir, "README_INSTALL.txt");
                await File.WriteAllTextAsync(readmeDst, BuildReadmeContent(profile, safeProfileName, result), cancellationToken);
            }

            if (!Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories).Any())
            {
                throw new InvalidOperationException("No files were available to export into the .7z archive.");
            }

            progress?.Report("Compressing .7z archive (0%)...");
            await RunSevenZipPackAsync(sevenZipPath, stagingDir, tempArchivePath, compressionLevel, progress, cancellationToken);

            File.Move(tempArchivePath, outputPath, overwrite: true);
            result.ArchiveSizeBytes = new FileInfo(outputPath).Length;
            result.Success = true;
            return result;
        }
        finally
        {
            CleanupDirectoryQuietly(stagingDir);
            if (File.Exists(tempArchivePath))
            {
                try { File.Delete(tempArchivePath); } catch { }
            }
        }
    }

    private static bool TryResolvePakSourcePath(ModEntry mod, AppSettings settings, out string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModsFolder) && !string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            string active = ModLibraryService.GetActivePakPath(settings, mod);
            if (File.Exists(active))
            {
                sourcePath = active;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.VaultFolder) && !string.IsNullOrWhiteSpace(mod.PakFileName))
        {
            string vault = ModLibraryService.GetVaultPakPath(settings.VaultFolder, mod);
            if (File.Exists(vault))
            {
                sourcePath = vault;
                return true;
            }
        }

        sourcePath = null;
        return false;
    }

    private static bool TryResolveTrackedSourcePath(
        ModEntry mod,
        TrackedFileRecord tf,
        AppSettings settings,
        out string? sourcePath)
    {
        string activeRoot = tf.Target == TrackedFileTarget.DataFolder ? settings.DataFolder : settings.GameFolder;
        if (!string.IsNullOrWhiteSpace(activeRoot))
        {
            string active = ModLibraryService.GetActiveTrackedFilePath(settings, tf);
            if (File.Exists(active))
            {
                sourcePath = active;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.VaultFolder))
        {
            string vault = ModLibraryService.GetVaultTrackedFilePath(settings.VaultFolder, mod, tf);
            if (File.Exists(vault))
            {
                sourcePath = vault;
                return true;
            }
        }

        sourcePath = null;
        return false;
    }

    private static List<(string FullPath, string RelativePath, long Size)> DiscoverUntrackedGeneratedFiles(
        AppSettings settings,
        ICollection<string> alreadyTrackedDataRelPaths)
    {
        var results = new List<(string FullPath, string RelativePath, long Size)>();
        if (string.IsNullOrWhiteSpace(settings.DataFolder) || !Directory.Exists(settings.DataFolder))
        {
            return results;
        }

        string generatedRoot = Path.Combine(settings.DataFolder, "Generated");
        if (!Directory.Exists(generatedRoot))
        {
            return results;
        }

        foreach (string fullPath in Directory.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(settings.DataFolder, fullPath).Replace('\\', '/');
            if (rel.StartsWith("Generated/Public/Engine/Assets/Editor/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains(".bg3mpm_orig", StringComparison.OrdinalIgnoreCase) ||
                rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                alreadyTrackedDataRelPaths.Contains(rel))
            {
                continue;
            }

            results.Add((fullPath, rel, new FileInfo(fullPath).Length));
        }

        return results;
    }

    private static List<(string DisplayName, List<(string FullPath, string RelativePath, long Size)> Files)> DiscoverUntrackedBinModGroups(
        AppSettings settings,
        ICollection<string> alreadyTrackedGameRelPaths)
    {
        var groups = new List<(string DisplayName, List<(string FullPath, string RelativePath, long Size)> Files)>();
        if (string.IsNullOrWhiteSpace(settings.GameFolder) || !Directory.Exists(settings.GameFolder))
        {
            return groups;
        }

        string binRoot = Path.Combine(settings.GameFolder, "bin");
        if (!Directory.Exists(binRoot))
        {
            return groups;
        }

        // 1. bin/NativeMods/* plugins (excluding .log and .bg3mpm_orig)
        string nativeModsDir = Path.Combine(binRoot, "NativeMods");
        if (Directory.Exists(nativeModsDir))
        {
            var bySubGroup = new SortedDictionary<string, List<(string FullPath, string RelativePath, long Size)>>(StringComparer.OrdinalIgnoreCase);
            foreach (string fullPath in Directory.EnumerateFiles(nativeModsDir, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (fullPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains(".bg3mpm_orig", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string gameRel = Path.GetRelativePath(settings.GameFolder, fullPath).Replace('\\', '/');
                if (alreadyTrackedGameRelPaths.Contains(gameRel))
                {
                    continue;
                }

                string insideNative = Path.GetRelativePath(nativeModsDir, fullPath).Replace('\\', '/');
                int slash = insideNative.IndexOf('/');
                string groupKey = slash > 0
                    ? insideNative[..slash]
                    : Path.GetFileNameWithoutExtension(fullPath);

                if (!bySubGroup.TryGetValue(groupKey, out var list))
                {
                    list = new List<(string, string, long)>();
                    bySubGroup[groupKey] = list;
                }

                list.Add((fullPath, gameRel, new FileInfo(fullPath).Length));
            }

            foreach (KeyValuePair<string, List<(string FullPath, string RelativePath, long Size)>> kv in bySubGroup)
            {
                if (kv.Value.Count > 0)
                {
                    groups.Add(($"{kv.Key} (bin/NativeMods)", kv.Value));
                }
            }
        }

        // 2. Native Mod Loader (modded bink2w64.dll + bink2w64_original.dll)
        string binkPath = Path.Combine(binRoot, "bink2w64.dll");
        string binkOrigPath = Path.Combine(binRoot, "bink2w64_original.dll");
        string binkBackupPath = binkPath + ".bg3mpm_orig";
        if (File.Exists(binkPath))
        {
            bool isModdedBink = File.Exists(binkBackupPath);
            if (!isModdedBink && File.Exists(binkOrigPath))
            {
                string shaBink = HashService.ComputeFileSha256(binkPath);
                string shaOrig = HashService.ComputeFileSha256(binkOrigPath);
                isModdedBink = !string.Equals(shaBink, shaOrig, StringComparison.OrdinalIgnoreCase);
            }

            if (isModdedBink)
            {
                var nmlFiles = new List<(string FullPath, string RelativePath, long Size)>();
                string binkRel = "bin/bink2w64.dll";
                if (!alreadyTrackedGameRelPaths.Contains(binkRel))
                {
                    nmlFiles.Add((binkPath, binkRel, new FileInfo(binkPath).Length));
                }

                if (File.Exists(binkOrigPath))
                {
                    string binkOrigRel = "bin/bink2w64_original.dll";
                    if (!alreadyTrackedGameRelPaths.Contains(binkOrigRel))
                    {
                        nmlFiles.Add((binkOrigPath, binkOrigRel, new FileInfo(binkOrigPath).Length));
                    }
                }

                if (nmlFiles.Count > 0)
                {
                    groups.Add(("Native Mod Loader (bin)", nmlFiles));
                }
            }
        }

        // 3. BG3 Script Extender (DWrite.dll + ScriptExtender*.json)
        string dwritePath = Path.Combine(binRoot, "DWrite.dll");
        if (File.Exists(dwritePath))
        {
            var seFiles = new List<(string FullPath, string RelativePath, long Size)>();
            string dwriteRel = "bin/DWrite.dll";
            if (!alreadyTrackedGameRelPaths.Contains(dwriteRel))
            {
                seFiles.Add((dwritePath, dwriteRel, new FileInfo(dwritePath).Length));
            }

            foreach (string seCfg in Directory.GetFiles(binRoot, "ScriptExtender*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string cfgRel = Path.GetRelativePath(settings.GameFolder, seCfg).Replace('\\', '/');
                if (!alreadyTrackedGameRelPaths.Contains(cfgRel))
                {
                    seFiles.Add((seCfg, cfgRel, new FileInfo(seCfg).Length));
                }
            }

            if (seFiles.Count > 0)
            {
                groups.Add(("BG3 Script Extender (bin)", seFiles));
            }
        }

        return groups;
    }

    private static async Task RunSevenZipPackAsync(
        string sevenZipPath,
        string stagingDir,
        string tempArchivePath,
        int compressionLevel,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            WorkingDirectory = stagingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add("-t7z");
        psi.ArgumentList.Add($"-mx={compressionLevel}");
        psi.ArgumentList.Add("-mmt=on");
        psi.ArgumentList.Add("-bsp1");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(tempArchivePath);
        psi.ArgumentList.Add("*");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch 7-Zip at '{sevenZipPath}'.");

        using CancellationTokenRegistration ctr = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        int lastPercent = -1;
        char[] buffer = new char[256];
        var segment = new StringBuilder();
        while (true)
        {
            int read = await proc.StandardOutput.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    if (segment.Length > 0)
                    {
                        ReportPercentIfChanged(segment.ToString(), ref lastPercent, progress);
                        segment.Clear();
                    }
                }
                else
                {
                    segment.Append(ch);
                }
            }
        }

        if (segment.Length > 0)
        {
            ReportPercentIfChanged(segment.ToString(), ref lastPercent, progress);
        }

        string stderr = await stderrTask;
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0 || !File.Exists(tempArchivePath))
        {
            throw new InvalidOperationException($"7-Zip failed (exit code {proc.ExitCode}): {stderr.Trim()}".Trim());
        }
    }

    private static void ReportPercentIfChanged(string line, ref int lastPercent, IProgress<string>? progress)
    {
        if (progress is null)
        {
            return;
        }

        Match m = ProgressPercentRegex().Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
        {
            pct = Math.Clamp(pct, 0, 100);
            if (pct != lastPercent)
            {
                lastPercent = pct;
                progress.Report($"Compressing .7z archive ({pct}%)...");
            }
        }
    }

    private static string BuildReadmeContent(ModProfile profile, string safeProfileName, ProfileExportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BG3 Modpack Manager — Exported Profile: {profile.Name}");
        sb.AppendLine($"Exported (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Contents: {result.ExportedPakCount} PAK(s), {result.ExportedDataFileCount} Data file(s), {result.ExportedGameFileCount} Game/bin file(s)");
        sb.AppendLine();
        sb.AppendLine("Option A — Re-import using BG3 Modpack Manager:");
        sb.AppendLine("  1. Drag & drop this .7z archive onto the BG3 Modpack Manager window.");
        sb.AppendLine("  2. Choose whether to activate immediately or import into a profile.");
        if (result.IncludedOrderJson)
        {
            sb.AppendLine($"  3. Import '{safeProfileName}.json' for exact load order sorting if desired.");
        }
        sb.AppendLine();
        sb.AppendLine("Option B — Manual install layout inside this archive:");
        sb.AppendLine("  • Mods\\*.pak     -> %LOCALAPPDATA%\\Larian Studios\\Baldur's Gate 3\\Mods\\");
        sb.AppendLine("  • Data\\*         -> <BG3 Game Folder>\\Data\\");
        sb.AppendLine("  • Game\\*         -> <BG3 Game Folder>\\ (for example Game\\bin\\bink2w64.dll and Game\\bin\\NativeMods\\)");
        if (result.IncludedOrderJson)
        {
            sb.AppendLine($"  • {safeProfileName}.json -> BG3ModManager Load Order JSON (File -> Import Order)");
            sb.AppendLine("    Note: Standalone BG3MM Overrides (such as HighlightEverything or NoIntro) load directly from Mods\\ without being listed inside the JSON Order array.");
        }

        return sb.ToString();
    }

    private static void CleanupDirectoryQuietly(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
