using System.Text;
using System.Text.Json;
using BG3ModpackManager.Core.Lspk;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class ProfileExportTests
{
    public static async Task RunAllAsync()
    {
        await ExportProfileTo7z_PacksActiveAndVaultPaksDataGameAndJson_AndLeavesOriginalsIntactAsync();
        await ExportProfileTo7z_HandlesMissingModFilesGracefullyAsync();
        await ExportProfileTo7z_AutoIncludesLiveGeneratedAndBinMods_ExcludesVanillaAndLogsAsync();
        await ImportExportedModpack7z_ReconstructsProfileAndBackupsOriginalGameFilesForUninstallAsync();
        await ImportExportedModpack7z_VaultOnlyThenActivate_BackupsAndRestoresOriginalsAsync();
    }

    private static async Task ExportProfileTo7z_PacksActiveAndVaultPaksDataGameAndJson_AndLeavesOriginalsIntactAsync()
    {
        TestAssert.True(SevenZipHelper.FindExecutable() is not null, "7-Zip CLI must be installed to run ProfileExportTests.");

        using var sandbox = new TempSandbox();
        var originalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Active standard PAK
        string stdPakPath = Path.Combine(sandbox.Settings.ModsFolder, "StandardMod.pak");
        string stdUuid = Guid.NewGuid().ToString();
        TestPakBuilder.CreatePakV18(stdPakPath, new[]
        {
            ("Mods/StandardMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Standard Mod", stdUuid, "StandardMod")), TestPakBuilder.PayloadCompression.None),
            ("Public/StandardMod/Content/ok.txt", "std"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry stdMod = await CreateTrackedPakAsync(stdPakPath, ModLocation.Active, sandbox, originalHashes);

        // 2. Vaulted merged override PAK (listed in JSON + ActiveOverride)
        string mergedStagePath = Path.Combine(sandbox.Root, "stage_MergedOvr.pak");
        string mergedUuid = Guid.NewGuid().ToString();
        TestPakBuilder.CreatePakV18(mergedStagePath, new[]
        {
            ("Mods/MergedOvr/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Merged Override Mod", mergedUuid, "MergedOvr")), TestPakBuilder.PayloadCompression.None),
            ("Public/MergedOvr/Content/mod.txt", "own"u8.ToArray(), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/Assets/override.lsf", "ovr"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        PakInfo mergedInfo = LspkPakReader.ReadPakInfo(mergedStagePath);
        string mergedHash = await HashService.ComputeFileSha256Async(mergedStagePath);
        ModEntry mergedMod = ModLibraryService.CreatePakModEntry(mergedInfo, mergedHash, sandbox.Db, ModLocation.Vault);
        string mergedVaultPath = ModLibraryService.GetVaultPakPath(sandbox.Settings.VaultFolder, mergedMod);
        FileTransactionHelper.SafeMoveFile(mergedStagePath, mergedVaultPath);
        sandbox.Db.Mods.Add(mergedMod);
        originalHashes[mergedVaultPath] = mergedHash;

        // 3. Active standalone override PAK (HighlightEverything style: no meta.lsx, never in JSON Order)
        string ovrPakPath = Path.Combine(sandbox.Settings.ModsFolder, "HighlightEverything.pak");
        TestPakBuilder.CreatePakV18(ovrPakPath, new[]
        {
            ("Public/Shared/GUI/Widgets/highlight.xaml", "<xaml/>"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry ovrMod = await CreateTrackedPakAsync(ovrPakPath, ModLocation.Active, sandbox, originalHashes);

        // 4. Active loose Data mod
        string activeDataRel = "Generated/Public/Shared/Assets/makeup.dds";
        string activeDataFull = Path.Combine(sandbox.Settings.DataFolder, activeDataRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(activeDataFull)!);
        await File.WriteAllBytesAsync(activeDataFull, new byte[] { 10, 20, 30, 40, 50 });
        string activeDataSha = HashService.ComputeFileSha256(activeDataFull);
        originalHashes[activeDataFull] = activeDataSha;

        var looseDataMod = new ModEntry
        {
            Id = "loose-data-01",
            Kind = ModKind.LooseData,
            DisplayName = "Mari's Makeup",
            ContentHash = "comp-data-01",
            TotalSizeBytes = 5,
            IsDetectedOverride = true,
            Location = ModLocation.Active,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.DataFolder,
                    RelativePath = activeDataRel,
                    FileSizeBytes = 5,
                    Sha256 = activeDataSha
                }
            }
        };
        sandbox.Db.Mods.Add(looseDataMod);

        // 5. Vaulted loose Game mod + in-place .bg3mpm_orig backup on active disk (must NOT be exported)
        var looseGameMod = new ModEntry
        {
            Id = "loose-game-01",
            Kind = ModKind.LooseGame,
            DisplayName = "Native Mod Loader + WASD",
            ContentHash = "comp-game-01",
            TotalSizeBytes = 10,
            IsDetectedOverride = true,
            Location = ModLocation.Vault,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.GameFolder,
                    RelativePath = "bin/bink2w64.dll",
                    FileSizeBytes = 4,
                    Sha256 = "h1"
                },
                new()
                {
                    Target = TrackedFileTarget.GameFolder,
                    RelativePath = "bin/NativeMods/Wasd.dll",
                    FileSizeBytes = 6,
                    Sha256 = "h2"
                }
            }
        };
        foreach (TrackedFileRecord tf in looseGameMod.TrackedFiles)
        {
            string dstVault = ModLibraryService.GetVaultTrackedFilePath(sandbox.Settings.VaultFolder, looseGameMod, tf);
            Directory.CreateDirectory(Path.GetDirectoryName(dstVault)!);
            byte[] bytes = Encoding.UTF8.GetBytes("payload:" + tf.RelativePath);
            await File.WriteAllBytesAsync(dstVault, bytes);
            tf.FileSizeBytes = bytes.Length;
            tf.Sha256 = HashService.ComputeFileSha256(dstVault);
            originalHashes[dstVault] = tf.Sha256;
        }
        sandbox.Db.Mods.Add(looseGameMod);

        // Vanilla game file backup sitting in Game/bin/ that must NEVER end up in exported archive
        string origBackupPath = Path.Combine(sandbox.Settings.GameFolder, "bin", "bink2w64.dll.bg3mpm_orig");
        Directory.CreateDirectory(Path.GetDirectoryName(origBackupPath)!);
        await File.WriteAllBytesAsync(origBackupPath, new byte[] { 99, 98, 97 });
        originalHashes[origBackupPath] = HashService.ComputeFileSha256(origBackupPath);

        // 6. Hybrid mod (Active PAK + Active Data file)
        string hybridPakPath = Path.Combine(sandbox.Settings.ModsFolder, "HybridMod.pak");
        string hybridUuid = Guid.NewGuid().ToString();
        TestPakBuilder.CreatePakV18(hybridPakPath, new[]
        {
            ("Mods/HybridMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Hybrid Mod", hybridUuid, "HybridMod")), TestPakBuilder.PayloadCompression.None)
        });
        PakInfo hybridInfo = LspkPakReader.ReadPakInfo(hybridPakPath);
        string hybridPakSha = await HashService.ComputeFileSha256Async(hybridPakPath);
        originalHashes[hybridPakPath] = hybridPakSha;

        string hybridDataRel = "Public/HybridMod/test.txt";
        string hybridDataFull = Path.Combine(sandbox.Settings.DataFolder, hybridDataRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hybridDataFull)!);
        await File.WriteAllTextAsync(hybridDataFull, "hybrid-loose-content");
        string hybridDataSha = HashService.ComputeFileSha256(hybridDataFull);
        originalHashes[hybridDataFull] = hybridDataSha;

        var hybridMod = ModLibraryService.CreatePakModEntry(hybridInfo, hybridPakSha, sandbox.Db, ModLocation.Active);
        hybridMod.Kind = ModKind.Hybrid;
        hybridMod.PakSha256 = hybridPakSha;
        hybridMod.TrackedFiles.Add(new TrackedFileRecord
        {
            Target = TrackedFileTarget.DataFolder,
            RelativePath = hybridDataRel,
            FileSizeBytes = new FileInfo(hybridDataFull).Length,
            Sha256 = hybridDataSha
        });
        sandbox.Db.Mods.Add(hybridMod);

        var profile = new ModProfile
        {
            Name = "Better Combat: 2024/Test",
            ModIds = new List<string>
            {
                stdMod.Id,
                mergedMod.Id,
                ovrMod.Id,
                looseDataMod.Id,
                looseGameMod.Id,
                hybridMod.Id
            }
        };
        sandbox.Db.Profiles.Add(profile);

        // Verify preview
        ProfileExportPreview preview = ProfileExportService.BuildExportPreview(profile, sandbox.Settings, sandbox.Db);
        TestAssert.Equal(4, preview.PakModCount, "Preview PAK count");
        TestAssert.Equal(2, preview.LooseDataModCount, "Preview loose Data mod count");
        TestAssert.Equal(2, preview.LooseDataFileCount, "Preview loose Data file count");
        TestAssert.Equal(1, preview.LooseGameModCount, "Preview loose Game mod count");
        TestAssert.Equal(2, preview.LooseGameFileCount, "Preview loose Game file count");
        TestAssert.Equal(0, preview.MissingModCount, "No missing mods");
        TestAssert.Equal(0, preview.MissingFileCount, "No missing files");

        // Export to .7z
        string archiveOut = Path.Combine(sandbox.Root, "Exports", "BetterCombat.7z");
        var progressMessages = new List<string>();
        var syncProgress = new Progress<string>(msg => progressMessages.Add(msg));

        ProfileExportResult result = await ProfileExportService.ExportProfileTo7zAsync(
            profile,
            new ProfileExportOptions
            {
                OutputArchivePath = archiveOut,
                IncludePaks = true,
                IncludeLooseMods = true,
                IncludeOrderJson = true,
                IncludeManifestAndReadme = true,
                CompressionLevel = 1
            },
            sandbox.Settings,
            sandbox.Db,
            syncProgress);

        TestAssert.True(result.Success, "Export should succeed");
        TestAssert.True(File.Exists(archiveOut), "Output .7z archive must exist");
        TestAssert.True(result.ArchiveSizeBytes > 0, "Archive must be non-empty");
        TestAssert.Equal(4, result.ExportedPakCount, "Exported 4 PAKs");
        TestAssert.Equal(2, result.ExportedDataFileCount, "Exported 2 Data files");
        TestAssert.Equal(2, result.ExportedGameFileCount, "Exported 2 Game/bin files");
        TestAssert.True(result.IncludedOrderJson, "Included BG3MM JSON");
        TestAssert.Equal(0, result.MissingItems.Count, "No missing items");

        // Ensure every original source file (active & vault) is still intact with identical SHA-256
        foreach ((string origPath, string expectedSha) in originalHashes)
        {
            TestAssert.True(File.Exists(origPath), $"Original source file must remain intact: {origPath}");
            TestAssert.Equal(expectedSha, HashService.ComputeFileSha256(origPath), $"Original file hash must be unchanged: {origPath}");
        }

        // Round-trip inspect via ArchiveImportService (and check extracted files)
        ArchiveImportPlan inspected = await ArchiveImportService.InspectDropAsync(archiveOut, sandbox.Settings, new LibraryDatabase());
        try
        {
            var pakCandidates = inspected.Items.Where(i => i.TargetKind == ImportTargetKind.ModsPak).ToList();
            TestAssert.Equal(4, pakCandidates.Count, "Inspected 7z should contain 4 PAK candidates");

            var dataCandidates = inspected.Items.Where(i => i.TargetKind == ImportTargetKind.DataFolder).ToList();
            var dataPaths = dataCandidates.SelectMany(c => c.ContainedRelativePaths).ToList();
            TestAssert.True(dataPaths.Any(p => p.Contains("Generated/Public/Shared/Assets/makeup.dds", StringComparison.OrdinalIgnoreCase)), "Data makeup file preserved");
            TestAssert.True(dataPaths.Any(p => p.Contains("Public/HybridMod/test.txt", StringComparison.OrdinalIgnoreCase)), "Hybrid Data file preserved");

            var gameCandidates = inspected.Items.Where(i => i.TargetKind == ImportTargetKind.GameFolder).ToList();
            var gamePaths = gameCandidates.SelectMany(c => c.ContainedRelativePaths).ToList();
            TestAssert.True(gamePaths.Any(p => p.EndsWith("=>bin/bink2w64.dll", StringComparison.OrdinalIgnoreCase)), "Game bin/bink2w64.dll maps back to bin/bink2w64.dll");
            TestAssert.True(gamePaths.Any(p => p.EndsWith("=>bin/NativeMods/Wasd.dll", StringComparison.OrdinalIgnoreCase)), "Game bin/NativeMods/Wasd.dll maps cleanly");

            string[] stagedFiles = Directory.GetFiles(inspected.StagingDirectory, "*", SearchOption.AllDirectories);
            TestAssert.False(stagedFiles.Any(f => f.EndsWith(".bg3mpm_orig", StringComparison.OrdinalIgnoreCase)), "Backup .bg3mpm_orig must NOT be exported");

            string expectedJsonName = ProfileExportService.SanitizeFileName(profile.Name) + ".json";
            string? exportedJson = stagedFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), expectedJsonName, StringComparison.OrdinalIgnoreCase));
            TestAssert.True(exportedJson is not null, $"Exported JSON '{expectedJsonName}' must exist in archive");

            Bg3LoadOrderFile parsedOrder = Bg3LoadOrderService.LoadOrderJson(exportedJson!);
            TestAssert.Equal(3, parsedOrder.Order.Count, "JSON Order must contain 3 meta.lsx PAKs (excluding HighlightEverything and loose mods)");
            TestAssert.Equal(stdUuid, parsedOrder.Order[0].Uuid, "JSON order 0 matches StandardMod");
            TestAssert.Equal(mergedUuid, parsedOrder.Order[1].Uuid, "JSON order 1 matches MergedOverride");
            TestAssert.Equal(hybridUuid, parsedOrder.Order[2].Uuid, "JSON order 2 matches HybridMod");

            string? manifestPath = stagedFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), "profile_manifest.json", StringComparison.OrdinalIgnoreCase));
            TestAssert.True(manifestPath is not null, "profile_manifest.json must exist");
            ProfileExportManifest? manifest = JsonSerializer.Deserialize<ProfileExportManifest>(await File.ReadAllTextAsync(manifestPath!));
            TestAssert.True(manifest is not null, "Manifest deserializes cleanly");
            TestAssert.Equal(profile.Name, manifest!.ProfileName, "Manifest profile name matches");
            TestAssert.Equal(6, manifest.Mods.Count, "Manifest includes all 6 mod records");

            string? readmePath = stagedFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), "README_INSTALL.txt", StringComparison.OrdinalIgnoreCase));
            TestAssert.True(readmePath is not null && new FileInfo(readmePath).Length > 50, "README_INSTALL.txt exists and has instructions");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(inspected);
        }
    }

    private static async Task ExportProfileTo7z_HandlesMissingModFilesGracefullyAsync()
    {
        TestAssert.True(SevenZipHelper.FindExecutable() is not null, "7-Zip CLI must be installed to run ProfileExportTests.");

        using var sandbox = new TempSandbox();

        string presentPak = Path.Combine(sandbox.Settings.ModsFolder, "Present.pak");
        TestPakBuilder.CreatePakV18(presentPak, new[]
        {
            ("Mods/Present/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Present Mod", Guid.NewGuid().ToString(), "Present")), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry presentMod = await CreateTrackedPakAsync(presentPak, ModLocation.Active, sandbox, new Dictionary<string, string>());

        var missingFileMod = new ModEntry
        {
            Id = "missing-pak-01",
            Kind = ModKind.Pak,
            DisplayName = "Deleted Mod",
            PakFileName = "DoesNotExist.pak",
            ContentHash = "deadbeef",
            Location = ModLocation.Missing
        };
        sandbox.Db.Mods.Add(missingFileMod);

        var profile = new ModProfile
        {
            Name = "PartialProfile",
            ModIds = new List<string> { presentMod.Id, missingFileMod.Id, "unknown-id-99" }
        };
        sandbox.Db.Profiles.Add(profile);

        ProfileExportPreview preview = ProfileExportService.BuildExportPreview(profile, sandbox.Settings, sandbox.Db);
        TestAssert.Equal(1, preview.PakModCount, "1 present PAK in preview");
        TestAssert.Equal(1, preview.MissingModCount, "1 missing library record");
        TestAssert.Equal(1, preview.MissingFileCount, "1 missing PAK file");

        string out7z = Path.Combine(sandbox.Root, "PartialProfile.7z");
        ProfileExportResult res = await ProfileExportService.ExportProfileTo7zAsync(
            profile,
            new ProfileExportOptions
            {
                OutputArchivePath = out7z,
                IncludePaks = true,
                IncludeLooseMods = true,
                IncludeOrderJson = true,
                IncludeManifestAndReadme = true,
                CompressionLevel = 0
            },
            sandbox.Settings,
            sandbox.Db);

        TestAssert.True(res.Success, "Partial export should succeed cleanly");
        TestAssert.True(File.Exists(out7z), "Output archive should be created");
        TestAssert.Equal(1, res.ExportedPakCount, "Present PAK exported");
        TestAssert.Equal(2, res.MissingItems.Count, "Both missing mod ID and missing PAK file reported");

        ArchiveImportPlan inspected = await ArchiveImportService.InspectDropAsync(out7z, sandbox.Settings, new LibraryDatabase());
        try
        {
            string? manifestPath = Directory.GetFiles(inspected.StagingDirectory, "profile_manifest.json", SearchOption.AllDirectories).FirstOrDefault();
            TestAssert.True(manifestPath is not null, "Manifest present in partial export");
            ProfileExportManifest? manifest = JsonSerializer.Deserialize<ProfileExportManifest>(await File.ReadAllTextAsync(manifestPath!));
            TestAssert.True(manifest is not null, "Manifest deserializes");
            TestAssert.True(manifest!.Mods.Any(m => m.Id == missingFileMod.Id && m.IsMissing), "Missing mod flagged IsMissing=true in manifest");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(inspected);
        }
    }

    private static async Task<ModEntry> CreateTrackedPakAsync(
        string pakPath,
        ModLocation location,
        TempSandbox sandbox,
        Dictionary<string, string> hashes)
    {
        PakInfo info = LspkPakReader.ReadPakInfo(pakPath);
        string sha = await HashService.ComputeFileSha256Async(pakPath);
        ModEntry entry = ModLibraryService.CreatePakModEntry(info, sha, sandbox.Db, location);
        sandbox.Db.Mods.Add(entry);
        hashes[pakPath] = sha;
        return entry;
    }

    private static async Task ExportProfileTo7z_AutoIncludesLiveGeneratedAndBinMods_ExcludesVanillaAndLogsAsync()
    {
        TestAssert.True(SevenZipHelper.FindExecutable() is not null, "7-Zip CLI must be installed to run ProfileExportTests.");

        using var sandbox = new TempSandbox();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string stdPakPath = Path.Combine(sandbox.Settings.ModsFolder, "BasePak.pak");
        TestPakBuilder.CreatePakV18(stdPakPath, new[]
        {
            ("Mods/BasePak/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("BasePak", Guid.NewGuid().ToString(), "BasePak")), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry baseMod = await CreateTrackedPakAsync(stdPakPath, ModLocation.Active, sandbox, hashes);

        var profile = new ModProfile { Name = "LiveScanProfile", ModIds = new List<string> { baseMod.Id } };
        sandbox.Db.Profiles.Add(profile);

        // Seed live Data/Generated files (custom DDS + Editor gizmo that must be excluded)
        string customDds = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "makeup.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(customDds)!);
        await File.WriteAllBytesAsync(customDds, "CUSTOM_MAKEUP_DDS"u8.ToArray());

        string editorGizmo = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Engine", "Assets", "Editor", "gizmo.gr2");
        Directory.CreateDirectory(Path.GetDirectoryName(editorGizmo)!);
        await File.WriteAllBytesAsync(editorGizmo, "EDITOR_GIZMO"u8.ToArray());

        // Seed Game/bin mods + vanilla files + logs/backups
        string binDir = Path.Combine(sandbox.Settings.GameFolder, "bin");
        string nativeDir = Path.Combine(binDir, "NativeMods");
        Directory.CreateDirectory(nativeDir);

        await File.WriteAllBytesAsync(Path.Combine(nativeDir, "BG3WASD.dll"), "WASD_DLL"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(nativeDir, "BG3WASD.toml"), "enabled = true\n");
        await File.WriteAllTextAsync(Path.Combine(nativeDir, "BG3WASD.log"), "debug log\n");
        await File.WriteAllBytesAsync(Path.Combine(binDir, "DWrite.dll"), "SE_DWRITE"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(binDir, "ScriptExtenderSettings.json"), "{ \"CreateConsole\": false }");
        await File.WriteAllBytesAsync(Path.Combine(binDir, "bink2w64.dll"), "MODDED_NML_BINK"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(binDir, "bink2w64_original.dll"), "VANILLA_ORIG_BINK"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(binDir, "bink2w64.dll.bg3mpm_orig"), "BACKUP_DO_NOT_EXPORT"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(binDir, "bg3.exe"), "VANILLA_EXE"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(binDir, "SDL2.dll"), "VANILLA_SDL2"u8.ToArray());
        await File.WriteAllTextAsync(Path.Combine(binDir, "gold.log"), "vanilla gold log\n");

        ProfileExportPreview preview = ProfileExportService.BuildExportPreview(profile, sandbox.Settings, sandbox.Db);
        TestAssert.Equal(1, preview.LiveGeneratedFileCount, "Live Data/Generated excludes Editor gizmo and finds 1 custom file");
        TestAssert.True(preview.LiveBinModCount >= 3, "Detected NativeMods, ScriptExtender (DWrite), and bink2w64 NML groups");
        TestAssert.True(preview.LiveBinFileCount >= 5, "Detected live bin mod files");

        string out7z = Path.Combine(sandbox.Root, "LiveExport.7z");
        ProfileExportResult res = await ProfileExportService.ExportProfileTo7zAsync(
            profile,
            new ProfileExportOptions
            {
                OutputArchivePath = out7z,
                IncludePaks = true,
                IncludeLooseMods = true,
                IncludeGeneratedFolder = true,
                IncludeBinMods = true,
                IncludeOrderJson = true,
                IncludeManifestAndReadme = true,
                CompressionLevel = 1
            },
            sandbox.Settings,
            sandbox.Db);

        TestAssert.True(res.Success && File.Exists(out7z), "Live export archive created");

        List<string> entries = await SevenZipHelper.ListEntriesAsync(out7z);
        string Normalize(string p) => p.Replace('\\', '/');
        var normEntries = entries.Select(Normalize).ToList();

        TestAssert.True(normEntries.Any(e => e.EndsWith("Data/Generated/Public/Shared/makeup.dds", StringComparison.OrdinalIgnoreCase)), "Includes live Data/Generated/Public/Shared/makeup.dds");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/NativeMods/BG3WASD.dll", StringComparison.OrdinalIgnoreCase)), "Includes NativeMods/BG3WASD.dll");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/NativeMods/BG3WASD.toml", StringComparison.OrdinalIgnoreCase)), "Includes NativeMods/BG3WASD.toml");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/DWrite.dll", StringComparison.OrdinalIgnoreCase)), "Includes ScriptExtender DWrite.dll");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/ScriptExtenderSettings.json", StringComparison.OrdinalIgnoreCase)), "Includes ScriptExtenderSettings.json");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/bink2w64.dll", StringComparison.OrdinalIgnoreCase)), "Includes modded bink2w64.dll");
        TestAssert.True(normEntries.Any(e => e.EndsWith("Game/bin/bink2w64_original.dll", StringComparison.OrdinalIgnoreCase)), "Includes bink2w64_original.dll");

        TestAssert.False(normEntries.Any(e => e.Contains("bg3.exe", StringComparison.OrdinalIgnoreCase)), "Vanilla bg3.exe must never be exported");
        TestAssert.False(normEntries.Any(e => e.Contains("SDL2.dll", StringComparison.OrdinalIgnoreCase)), "Vanilla SDL2.dll must never be exported");
        TestAssert.False(normEntries.Any(e => e.EndsWith(".log", StringComparison.OrdinalIgnoreCase)), "Log files must not be exported");
        TestAssert.False(normEntries.Any(e => e.EndsWith(".bg3mpm_orig", StringComparison.OrdinalIgnoreCase)), ".bg3mpm_orig backup files must not be exported");
        TestAssert.False(normEntries.Any(e => e.Contains("Editor/gizmo.gr2", StringComparison.OrdinalIgnoreCase)), "Toolkit Editor gizmos must not be exported");
    }

    private static async Task ImportExportedModpack7z_ReconstructsProfileAndBackupsOriginalGameFilesForUninstallAsync()
    {
        TestAssert.True(SevenZipHelper.FindExecutable() is not null, "7-Zip CLI must be installed to run ProfileExportTests.");

        // 1. Source sandbox exports a complete pack (standard PAK + override PAK + Data/Generated + Game/bin)
        using var srcSandbox = new TempSandbox();
        var srcHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string stdUuid = Guid.NewGuid().ToString();
        string stdPakPath = Path.Combine(srcSandbox.Settings.ModsFolder, "StdPackMod.pak");
        TestPakBuilder.CreatePakV18(stdPakPath, new[]
        {
            ("Mods/StdPackMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("StdPackMod", stdUuid, "StdPackMod")), TestPakBuilder.PayloadCompression.None),
            ("Public/StdPackMod/ok.txt", "ok"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry stdMod = await CreateTrackedPakAsync(stdPakPath, ModLocation.Active, srcSandbox, srcHashes);

        string ovrPakPath = Path.Combine(srcSandbox.Settings.ModsFolder, "HighlightEverything.pak");
        TestPakBuilder.CreatePakV18(ovrPakPath, new[]
        {
            ("Public/Shared/GUI/hi.xaml", "<xaml/>"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry ovrMod = await CreateTrackedPakAsync(ovrPakPath, ModLocation.Active, srcSandbox, srcHashes);

        byte[] moddedMakeupBytes = "MODDED_MAKEUP_CONTENT"u8.ToArray();
        string srcMakeupPath = Path.Combine(srcSandbox.Settings.DataFolder, "Generated", "Public", "Shared", "makeup.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(srcMakeupPath)!);
        await File.WriteAllBytesAsync(srcMakeupPath, moddedMakeupBytes);

        byte[] moddedBinkBytes = "MODDED_BINK2W64_CONTENT"u8.ToArray();
        byte[] moddedWasdBytes = "MODDED_WASD_CONTENT"u8.ToArray();
        string srcBinDir = Path.Combine(srcSandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(Path.Combine(srcBinDir, "NativeMods"));
        await File.WriteAllBytesAsync(Path.Combine(srcBinDir, "bink2w64.dll"), moddedBinkBytes);
        await File.WriteAllBytesAsync(Path.Combine(srcBinDir, "bink2w64_original.dll"), "ORIGINAL_BINK_IN_SRC"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(srcBinDir, "NativeMods", "BG3WASD.dll"), moddedWasdBytes);

        var srcProfile = new ModProfile { Name = "Exported Pack", ModIds = new List<string> { stdMod.Id, ovrMod.Id } };
        srcSandbox.Db.Profiles.Add(srcProfile);

        string archivePath = Path.Combine(srcSandbox.Root, "ExportedPack.7z");
        ProfileExportResult exportRes = await ProfileExportService.ExportProfileTo7zAsync(
            srcProfile,
            new ProfileExportOptions { OutputArchivePath = archivePath, CompressionLevel = 1 },
            srcSandbox.Settings,
            srcSandbox.Db);
        TestAssert.True(exportRes.Success, "Source export succeeded");

        // 2. Destination sandbox pre-seeded with vanilla game files
        using var dstSandbox = new TempSandbox();
        var dstCache = new PakMetadataCache();
        string dstBink = Path.Combine(dstSandbox.Settings.GameFolder, "bin", "bink2w64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dstBink)!);
        await File.WriteAllBytesAsync(dstBink, "VANILLA_BINK2W64_ORIG"u8.ToArray());
        string vanillaBinkSha = await HashService.ComputeFileSha256Async(dstBink);

        string dstMakeup = Path.Combine(dstSandbox.Settings.DataFolder, "Generated", "Public", "Shared", "makeup.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(dstMakeup)!);
        await File.WriteAllBytesAsync(dstMakeup, "VANILLA_MAKEUP_ORIG"u8.ToArray());
        string vanillaMakeupSha = await HashService.ComputeFileSha256Async(dstMakeup);

        ModpackImportPlan plan = await ProfileImportService.InspectModpackArchiveAsync(archivePath, dstSandbox.Settings, dstSandbox.Db);
        try
        {
            TestAssert.True(plan.IsModpackArchive, "Archive detected as exported modpack");
            TestAssert.True(plan.Items.Count >= 4, "Plan discovers both PAKs plus Generated and Game/bin groups");
            TestAssert.True(plan.Items.SelectMany(i => i.ExistingOriginalFilesToBackup).Any(p => p.Contains("bink2w64.dll", StringComparison.OrdinalIgnoreCase)), "Detects pre-existing bink2w64.dll needing backup");
            TestAssert.True(plan.Items.SelectMany(i => i.ExistingOriginalFilesToBackup).Any(p => p.Contains("makeup.dds", StringComparison.OrdinalIgnoreCase)), "Detects pre-existing makeup.dds needing backup");

            ModpackImportResult importRes = await ProfileImportService.CommitModpackImportAsync(
                plan,
                new ModpackImportOptions
                {
                    ProfileName = "Imported Pack",
                    InstallActiveNow = true,
                    SetAsActiveProfile = true
                },
                dstSandbox.Settings,
                dstSandbox.Db,
                dstCache);

            TestAssert.Equal(0, importRes.Errors.Count, "Import completed without errors");
            TestAssert.True(importRes.CreatedProfile is not null, "Created profile");
            TestAssert.Equal("Imported Pack", importRes.CreatedProfile!.Name, "Profile name matches");
            TestAssert.Equal(importRes.CreatedProfile.Id, dstSandbox.Db.ActiveProfileId, "Imported profile set active");
            TestAssert.True(importRes.BackedUpOriginalGameFiles.Count >= 2, "Original bink2w64.dll and makeup.dds backed up");

            TestAssert.True(File.Exists(Path.Combine(dstSandbox.Settings.ModsFolder, "StdPackMod.pak")), "Standard PAK active");
            TestAssert.True(File.Exists(Path.Combine(dstSandbox.Settings.ModsFolder, "HighlightEverything.pak")), "Override PAK active");
            TestAssert.True(File.Exists(Path.Combine(dstSandbox.Settings.GameFolder, "bin", "NativeMods", "BG3WASD.dll")), "NativeMods/BG3WASD.dll active");

            string binkOrigBackup = dstBink + ".bg3mpm_orig";
            string makeupOrigBackup = dstMakeup + ".bg3mpm_orig";
            TestAssert.True(File.Exists(binkOrigBackup), "bink2w64.dll.bg3mpm_orig backup created");
            TestAssert.True(File.Exists(makeupOrigBackup), "makeup.dds.bg3mpm_orig backup created");
            TestAssert.EqualIgnoreCase(vanillaBinkSha, await HashService.ComputeFileSha256Async(binkOrigBackup), "bink backup has exact vanilla hash");
            TestAssert.EqualIgnoreCase(vanillaMakeupSha, await HashService.ComputeFileSha256Async(makeupOrigBackup), "makeup backup has exact vanilla hash");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(dstBink).Length, "Active bink2w64.dll is modded payload");
            TestAssert.Equal(moddedMakeupBytes.Length, (int)new FileInfo(dstMakeup).Length, "Active makeup.dds is modded payload");

            // Deactivate LooseGame (NML) -> restores vanilla bink2w64.dll; re-activate -> re-backups vanilla and installs modded bink2w64.dll
            ModEntry binkMod = dstSandbox.Db.Mods.Single(m => m.Kind == ModKind.LooseGame && m.TrackedFiles.Any(t => t.RelativePath.Contains("bink2w64.dll", StringComparison.OrdinalIgnoreCase)));
            ModEntry wasdMod = dstSandbox.Db.Mods.Single(m => m.Kind == ModKind.LooseGame && m.TrackedFiles.Any(t => t.RelativePath.Contains("BG3WASD.dll", StringComparison.OrdinalIgnoreCase)));
            ModLibraryService.MoveModToVault(binkMod, dstSandbox.Settings);
            TestAssert.False(File.Exists(binkOrigBackup), "Deactivating removes .bg3mpm_orig after restore");
            TestAssert.EqualIgnoreCase(vanillaBinkSha, await HashService.ComputeFileSha256Async(dstBink), "Vanilla bink2w64.dll restored while parked in Vault");

            ModLibraryService.ActivateModFromVault(binkMod, dstSandbox.Settings);
            TestAssert.True(File.Exists(binkOrigBackup), "Reactivating backs up vanilla bink2w64.dll again");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(dstBink).Length, "Modded bink2w64.dll active again");

            // Uninstall loose mods -> restores BOTH vanilla files byte-for-byte & removes helper/WASD files
            ModEntry dataMod = dstSandbox.Db.Mods.Single(m => m.Kind == ModKind.LooseData);
            ModLibraryService.ModUninstallResult gameUninstall = ModLibraryService.UninstallMod(binkMod, dstSandbox.Settings, dstSandbox.Db);
            _ = ModLibraryService.UninstallMod(wasdMod, dstSandbox.Settings, dstSandbox.Db);
            ModLibraryService.ModUninstallResult dataUninstall = ModLibraryService.UninstallMod(dataMod, dstSandbox.Settings, dstSandbox.Db);

            TestAssert.True(gameUninstall.RestoredOriginalFiles.Count >= 1, "Game uninstall restored original bink2w64.dll");
            TestAssert.True(dataUninstall.RestoredOriginalFiles.Count >= 1, "Data uninstall restored original makeup.dds");
            TestAssert.False(File.Exists(binkOrigBackup), "No .bg3mpm_orig left on bink2w64.dll");
            TestAssert.False(File.Exists(makeupOrigBackup), "No .bg3mpm_orig left on makeup.dds");
            TestAssert.False(File.Exists(Path.Combine(dstSandbox.Settings.GameFolder, "bin", "bink2w64_original.dll")), "Mod helper bink2w64_original.dll removed on uninstall");
            TestAssert.False(File.Exists(Path.Combine(dstSandbox.Settings.GameFolder, "bin", "NativeMods", "BG3WASD.dll")), "Modded WASD DLL removed on uninstall");
            TestAssert.EqualIgnoreCase(vanillaBinkSha, await HashService.ComputeFileSha256Async(dstBink), "Vanilla bink2w64.dll restored byte-for-byte after uninstall");
            TestAssert.EqualIgnoreCase(vanillaMakeupSha, await HashService.ComputeFileSha256Async(dstMakeup), "Vanilla makeup.dds restored byte-for-byte after uninstall");

            // Re-importing same archive reuses single-copy PAKs instead of duplicating
            ModpackImportPlan plan2 = await ProfileImportService.InspectModpackArchiveAsync(archivePath, dstSandbox.Settings, dstSandbox.Db);
            try
            {
                ModpackImportResult res2 = await ProfileImportService.CommitModpackImportAsync(
                    plan2,
                    new ModpackImportOptions { ProfileName = "Imported Pack 2", InstallActiveNow = false, SetAsActiveProfile = false },
                    dstSandbox.Settings,
                    dstSandbox.Db,
                    dstCache);
                TestAssert.True(res2.ReusedModCount >= 2, "Re-importing reuses existing library PAKs (single-copy deduplication)");
            }
            finally
            {
                ProfileImportService.CleanupStaging(plan2);
            }
        }
        finally
        {
            ProfileImportService.CleanupStaging(plan);
        }
    }

    private static async Task ImportExportedModpack7z_VaultOnlyThenActivate_BackupsAndRestoresOriginalsAsync()
    {
        TestAssert.True(SevenZipHelper.FindExecutable() is not null, "7-Zip CLI must be installed to run ProfileExportTests.");

        using var srcSandbox = new TempSandbox();
        var srcHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string stdPakPath = Path.Combine(srcSandbox.Settings.ModsFolder, "VaultTestMod.pak");
        TestPakBuilder.CreatePakV18(stdPakPath, new[]
        {
            ("Mods/VaultTestMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("VaultTestMod", Guid.NewGuid().ToString(), "VaultTestMod")), TestPakBuilder.PayloadCompression.None)
        });
        ModEntry pakMod = await CreateTrackedPakAsync(stdPakPath, ModLocation.Active, srcSandbox, srcHashes);

        string srcBinDir = Path.Combine(srcSandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(srcBinDir);
        byte[] moddedBinkBytes = "MODDED_VAULT_FIRST_BINK"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(srcBinDir, "bink2w64.dll"), moddedBinkBytes);
        await File.WriteAllBytesAsync(Path.Combine(srcBinDir, "bink2w64_original.dll"), "ORIGINAL_BINK_VAULT_SRC"u8.ToArray());

        var srcProfile = new ModProfile { Name = "VaultPack", ModIds = new List<string> { pakMod.Id } };
        srcSandbox.Db.Profiles.Add(srcProfile);

        string archivePath = Path.Combine(srcSandbox.Root, "VaultPack.7z");
        await ProfileExportService.ExportProfileTo7zAsync(
            srcProfile,
            new ProfileExportOptions { OutputArchivePath = archivePath, CompressionLevel = 1 },
            srcSandbox.Settings,
            srcSandbox.Db);

        using var dstSandbox = new TempSandbox();
        string dstBink = Path.Combine(dstSandbox.Settings.GameFolder, "bin", "bink2w64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dstBink)!);
        await File.WriteAllBytesAsync(dstBink, "VANILLA_STAY_UNTOUCHED"u8.ToArray());
        string vanillaSha = await HashService.ComputeFileSha256Async(dstBink);

        ModpackImportPlan plan = await ProfileImportService.InspectModpackArchiveAsync(archivePath, dstSandbox.Settings, dstSandbox.Db);
        try
        {
            ModpackImportResult res = await ProfileImportService.CommitModpackImportAsync(
                plan,
                new ModpackImportOptions
                {
                    ProfileName = "VaultOnlyPack",
                    InstallActiveNow = false,
                    SetAsActiveProfile = false
                },
                dstSandbox.Settings,
                dstSandbox.Db,
                new PakMetadataCache());

            TestAssert.Equal(0, res.Errors.Count, "Vault-only import succeeded");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(dstBink), "Vanilla bink2w64.dll remains untouched on Vault-only import");
            TestAssert.False(File.Exists(dstBink + ".bg3mpm_orig"), "No backup file created until mod is activated");
            TestAssert.True(dstSandbox.Db.Mods.All(m => m.Location == ModLocation.Vault), "All imported mods stored in Vault");

            foreach (ModEntry mod in dstSandbox.Db.Mods)
            {
                ModLibraryService.ActivateModFromVault(mod, dstSandbox.Settings);
            }

            TestAssert.True(File.Exists(dstBink + ".bg3mpm_orig"), "Activating from Vault creates .bg3mpm_orig backup");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(dstBink + ".bg3mpm_orig"), "Backup holds exact vanilla bink2w64.dll");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(dstBink).Length, "Active bink2w64.dll updated to modded payload");

            ModEntry binkMod = dstSandbox.Db.Mods.Single(m => m.Kind == ModKind.LooseGame);
            ModLibraryService.UninstallMod(binkMod, dstSandbox.Settings, dstSandbox.Db);
            TestAssert.False(File.Exists(dstBink + ".bg3mpm_orig"), "Uninstall consumes backup");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(dstBink), "Vanilla bink2w64.dll restored after Vault->Activate->Uninstall");
        }
        finally
        {
            ProfileImportService.CleanupStaging(plan);
        }
    }
}
