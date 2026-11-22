using System.IO.Compression;
using System.Text;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class ArchiveImportTests
{
    public static async Task RunAllAsync()
    {
        await TestWrappedPakAndIgnoreFilesImportAsync();
        await TestDataAndNativeGameImportAsync();
        await TestHybridSingleModImportAndDeduplicationAsync();
        await TestSevenZipMariDataImportAndPruningAsync();
        await TestNativeModLoaderOriginalBackupRestoreAndUninstallAsync();
        await TestReimportRepairsMissingAndOverwrittenLooseAndPakModsAsync();
        await TestDeeplyNestedBinAndGeneratedWithPakImportAsync();
        await TestImportAndDragReorderAutoSyncBg3MmJsonAndLsxAsync();
        await TestZipSlipRejectedAsync();
        TestDeletableArchiveExtensionDetection();
    }

    private static async Task TestWrappedPakAndIgnoreFilesImportAsync()
    {
        using var sandbox = new TempSandbox();
        string stagingSrc = Path.Combine(sandbox.Root, "src_wrapped");
        string pakPath = Path.Combine(stagingSrc, "MyCoolMod_v1", "MyCoolMod.pak");
        TestPakBuilder.CreatePakV18(pakPath, new[]
        {
            ("Mods/MyCoolMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("MyCoolMod", "12345678-1111-1111-1111-111111111111", "MyCoolMod")), TestPakBuilder.PayloadCompression.None)
        });
        File.WriteAllText(Path.Combine(stagingSrc, "MyCoolMod_v1", "info.json"), "{}");
        File.WriteAllText(Path.Combine(stagingSrc, "MyCoolMod_v1", "README.txt"), "hello");

        string zipPath = Path.Combine(sandbox.Root, "MyCoolMod.zip");
        ZipFile.CreateFromDirectory(stagingSrc, zipPath);

        ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
        try
        {
            TestAssert.Equal(1, plan.Items.Count(i => i.TargetKind == ImportTargetKind.ModsPak && i.IsSelected), "Should select 1 PAK");
            TestAssert.True(plan.Items.Where(i => i.TargetKind == ImportTargetKind.Ignore).All(i => !i.IsSelected), "Docs/info.json ignored by default");

            ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res.Errors.Count, "No import errors");
            TestAssert.Equal(1, res.ImportedModIds.Count, "1 mod imported");
            TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "MyCoolMod.pak")), "PAK copied to Mods/");
            TestAssert.False(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "info.json")), "info.json must not enter Mods/");

            // Re-import exact same zip -> must reuse single copy (0 new mods, 1 reused)
            ArchiveImportPlan plan2 = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
            try
            {
                TestAssert.True(plan2.Items.Single(i => i.TargetKind == ImportTargetKind.ModsPak).ExistingDuplicateModId is not null, "Duplicate flagged in preview");
                ArchiveImportResult res2 = await ArchiveImportService.CommitImportAsync(plan2, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
                TestAssert.Equal(0, res2.ImportedModIds.Count, "No duplicate mod created");
                TestAssert.Equal(1, res2.ReusedDuplicateModIds.Count, "Existing single copy reused");
                TestAssert.Equal(1, sandbox.Db.Mods.Count, "Library still holds exactly 1 copy");
            }
            finally
            {
                ArchiveImportService.CleanupStaging(plan2);
            }
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan);
        }
    }

    private static async Task TestDataAndNativeGameImportAsync()
    {
        using var sandbox = new TempSandbox();
        string zipPath = Path.Combine(sandbox.Root, "DataAndNative.zip");
        using (ZipArchive z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteZipEntry(z, "Data/Generated/Public/Shared/Assets/skin.dds", new byte[] { 1, 2, 3, 4 });
            WriteZipEntry(z, "Data/Public/CustomMod/Stats/stat.txt", "stat"u8.ToArray());
            WriteZipEntry(z, "bin/NativeMods/NativePlugin.dll", new byte[] { 9, 8, 7 });
        }

        // Pre-seed an unmanaged file to test automatic Vault backup.
        string unmanagedDest = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "skin.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(unmanagedDest)!);
        await File.WriteAllBytesAsync(unmanagedDest, new byte[] { 99 });

        ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
        try
        {
            ImportItemCandidate genCandidate = plan.Items.First(i => i.TargetKind == ImportTargetKind.DataFolder && i.DisplayName.Contains("Generated"));
            TestAssert.True(genCandidate.OverwritesUnmanagedFile, "Should detect overwrite of unmanaged file");
            TestAssert.True(plan.Items.Any(i => i.TargetKind == ImportTargetKind.GameFolder), "Should detect bin/NativeMods -> GameFolder");

            ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res.Errors.Count, "No errors importing Data+Game zip");
            TestAssert.Equal(1, res.BackedUpUnmanagedFiles.Count, "Original unmanaged file backed up before overwrite");
            TestAssert.Equal(4, (int)new FileInfo(unmanagedDest).Length, "New skin.dds written");
            TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "NativePlugin.dll")), "NativePlugin.dll placed in Game/bin/NativeMods");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan);
        }
    }

    private static async Task TestHybridSingleModImportAndDeduplicationAsync()
    {
        using var sandbox = new TempSandbox();
        string srcDir = Path.Combine(sandbox.Root, "hybrid_src");
        TestPakBuilder.CreatePakV18(Path.Combine(srcDir, "HybridMod.pak"), new[]
        {
            ("Mods/HybridMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("HybridMod", "99999999-1111-1111-1111-111111111111", "HybridMod")), TestPakBuilder.PayloadCompression.None)
        });
        string asset = Path.Combine(srcDir, "Generated", "Public", "Shared", "Custom", "tex.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        await File.WriteAllBytesAsync(asset, new byte[] { 10, 20, 30 });

        string zipPath = Path.Combine(sandbox.Root, "HybridMod.zip");
        ZipFile.CreateFromDirectory(srcDir, zipPath);

        ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
        try
        {
            TestAssert.True(plan.PackageAsSingleHybridMod, "1 PAK + loose files defaults to Hybrid");
            ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(1, res.ImportedModIds.Count, "Packaged as 1 Hybrid mod");
            ModEntry hybrid = sandbox.Db.Mods.Single();
            TestAssert.Equal(ModKind.Hybrid, hybrid.Kind, "ModKind.Hybrid");
            TestAssert.Equal(1, hybrid.TrackedFiles.Count, "1 tracked loose file");
            TestAssert.True(!string.IsNullOrEmpty(hybrid.PakSha256), "Hybrid mod tracks individual PakSha256");

            // Scanning active Mods/ must recognize Hybrid mod's .pak without re-adopting a duplicate ModEntry
            var (adopted, _) = await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, new PakMetadataCache());
            TestAssert.Equal(0, adopted, "ScanAndAdopt must not duplicate Hybrid mod's PAK");
            TestAssert.Equal(1, sandbox.Db.Mods.Count, "Library still has 1 Hybrid mod after scan");
            TestAssert.Equal(ModLocation.Active, hybrid.Location, "Hybrid remains Active");

            // Re-import exact same Hybrid archive -> single-copy deduplication!
            ArchiveImportPlan plan2 = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
            try
            {
                ArchiveImportResult res2 = await ArchiveImportService.CommitImportAsync(plan2, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
                TestAssert.Equal(0, res2.ImportedModIds.Count, "No duplicate Hybrid mod created");
                TestAssert.Equal(1, res2.ReusedDuplicateModIds.Count, "Hybrid mod reused");
                TestAssert.Equal(1, sandbox.Db.Mods.Count, "Still 1 Hybrid mod in library");
            }
            finally
            {
                ArchiveImportService.CleanupStaging(plan2);
            }
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan);
        }
    }

    private static async Task TestZipSlipRejectedAsync()
    {
        using var sandbox = new TempSandbox();
        string zipPath = Path.Combine(sandbox.Root, "evil.zip");
        using (ZipArchive z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteZipEntry(z, "../../escaped.txt", "evil"u8.ToArray());
        }

        bool threw = false;
        try
        {
            _ = await ArchiveImportService.InspectDropAsync(zipPath, sandbox.Settings, sandbox.Db);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        TestAssert.True(threw, "Zip Slip entry must throw InvalidOperationException");
    }

    private static async Task TestSevenZipMariDataImportAndPruningAsync()
    {
        string sevenZip = @"C:\Program Files\7-Zip\7z.exe";
        if (!File.Exists(sevenZip))
        {
            return;
        }

        using var sandbox = new TempSandbox();
        string srcRoot = Path.Combine(sandbox.Root, "mari_src");
        string ddsPath = Path.Combine(srcRoot, "Generated", "Public", "Shared", "Assets", "unique_tav", "FACE", "Skin_Atlas_Head_SHR_Makeup_A_MSK.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(ddsPath)!);
        await File.WriteAllBytesAsync(ddsPath, new byte[] { 0x44, 0x44, 0x53, 0x20, 1, 2, 3 });

        string archivePath = Path.Combine(sandbox.Root, "Mari_makeup_UT.7z");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = sevenZip,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add(Path.Combine(srcRoot, "Generated"));
        using (var proc = System.Diagnostics.Process.Start(psi)!)
        {
            await proc.WaitForExitAsync();
            TestAssert.Equal(0, proc.ExitCode, "7z archive creation must succeed");
        }

        // Pre-create protected Generated\Public\Shared so pruning preserves it
        string protectedDir = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared");
        Directory.CreateDirectory(protectedDir);

        ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(archivePath, sandbox.Settings, sandbox.Db);
        try
        {
            plan.CustomDisplayName = "Mari's Makeup - Unique Tav";
            TestAssert.Equal(1, plan.Items.Count(i => i.TargetKind == ImportTargetKind.DataFolder && i.IsSelected), ".7z Generated/ classified as DataFolder");

            ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res.Errors.Count, "No errors importing .7z");
            ModEntry mod = sandbox.Db.Mods.Single();
            TestAssert.Equal("Mari's Makeup - Unique Tav", mod.DisplayName, "CustomDisplayName preserved");

            string activeDds = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "unique_tav", "FACE", "Skin_Atlas_Head_SHR_Makeup_A_MSK.dds");
            TestAssert.True(File.Exists(activeDds), "Mari DDS installed into Data/Generated/Public/Shared/...");

            // Move to Vault -> file parked, empty subdirs pruned, protected dir intact
            ModLibraryService.MoveModToVault(mod, sandbox.Settings);
            TestAssert.False(File.Exists(activeDds), "Mari DDS removed from Data when parked in Vault");
            TestAssert.True(Directory.Exists(protectedDir), "Protected Generated/Public/Shared preserved");
            TestAssert.Equal(ModLocation.Vault, mod.Location, "Mod parked in Vault");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan);
        }
    }

    private static async Task TestNativeModLoaderOriginalBackupRestoreAndUninstallAsync()
    {
        using var sandbox = new TempSandbox();

        // Seed vanilla bink2w64.dll in Game/bin
        string binDir = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(binDir);
        string vanillaBink = Path.Combine(binDir, "bink2w64.dll");
        byte[] vanillaBytes = "VANILLA_BINK_ORIGINAL_CONTENT"u8.ToArray();
        await File.WriteAllBytesAsync(vanillaBink, vanillaBytes);
        string vanillaSha = await HashService.ComputeFileSha256Async(vanillaBink);

        // Create NativeModLoader zip replacing bin/bink2w64.dll + adding bin/bink2w64_original.dll
        string nmlZip = Path.Combine(sandbox.Root, "NativeModLoader.zip");
        byte[] moddedBinkBytes = "NATIVE_MOD_LOADER_SHIM"u8.ToArray();
        using (ZipArchive z = ZipFile.Open(nmlZip, ZipArchiveMode.Create))
        {
            WriteZipEntry(z, "bin/bink2w64.dll", moddedBinkBytes);
            WriteZipEntry(z, "bin/bink2w64_original.dll", vanillaBytes);
        }

        ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(nmlZip, sandbox.Settings, sandbox.Db);
        try
        {
            plan.CustomDisplayName = "Native Mod Loader";
            ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res.Errors.Count, "NativeModLoader import succeeded");
            TestAssert.Equal(1, res.RenamedOriginalFiles.Count, "Vanilla bink2w64.dll renamed in-place");

            ModEntry nmlMod = sandbox.Db.Mods.Single();
            TestAssert.Equal(1, nmlMod.ReplacedOriginalFiles.Count, "ReplacedOriginalFiles recorded");
            string backupOrig = Path.Combine(binDir, "bink2w64.dll.bg3mpm_orig");
            TestAssert.True(File.Exists(backupOrig), "Vanilla backup exists at bin/bink2w64.dll.bg3mpm_orig");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(backupOrig), "Backup contains exact vanilla DLL");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(vanillaBink).Length, "Active bink2w64.dll is now NativeModLoader");

            // Deactivate via ProfileSwitch (empty profile) -> must restore vanilla bink2w64.dll automatically!
            var emptyProfile = new ModProfile { Name = "Vanilla" };
            var nmlProfile = new ModProfile { Name = "With NML", ModIds = new List<string> { nmlMod.Id } };
            sandbox.Db.Profiles.Add(emptyProfile);
            sandbox.Db.Profiles.Add(nmlProfile);

            var cache = new PakMetadataCache();
            SwitchPlan toVanillaPlan = await ProfileSwitchService.BuildSwitchPlanAsync(emptyProfile, sandbox.Settings, sandbox.Db, cache);
            TestAssert.True(toVanillaPlan.CanExecute, "Switch to Vanilla has no conflicts");
            SwitchExecutionResult toVanillaExec = await ProfileSwitchService.ExecuteSwitchPlanAsync(toVanillaPlan, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
            TestAssert.True(toVanillaExec.Success, "Switch to Vanilla succeeded");
            TestAssert.False(File.Exists(backupOrig), "Backup file moved back to active bink2w64.dll");
            TestAssert.False(File.Exists(Path.Combine(binDir, "bink2w64_original.dll")), "bink2w64_original.dll parked in Vault");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(vanillaBink), "Vanilla bink2w64.dll restored!");

            // Switch back to With NML -> must re-backup vanilla bink2w64.dll without FileConflict!
            SwitchPlan toNmlPlan = await ProfileSwitchService.BuildSwitchPlanAsync(nmlProfile, sandbox.Settings, sandbox.Db, cache);
            TestAssert.True(toNmlPlan.CanExecute, "Switch back to NML must NOT raise FileConflict on vanilla bink2w64.dll");
            SwitchExecutionResult toNmlExec = await ProfileSwitchService.ExecuteSwitchPlanAsync(toNmlPlan, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
            TestAssert.True(toNmlExec.Success, "Switch back to NML succeeded");
            TestAssert.True(File.Exists(backupOrig), "Vanilla bink2w64.dll backed up again when NML reactivated");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(vanillaBink).Length, "NativeModLoader active again");

            // Clean Uninstall -> removes NML files & restores vanilla bink2w64.dll
            ModLibraryService.ModUninstallResult uninstall = ModLibraryService.UninstallMod(nmlMod, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(1, uninstall.RestoredOriginalFiles.Count, "Uninstall restored 1 original game file");
            TestAssert.Equal(0, sandbox.Db.Mods.Count, "Mod removed from Library");
            TestAssert.False(File.Exists(backupOrig), "Backup file consumed by restore");
            TestAssert.False(File.Exists(Path.Combine(binDir, "bink2w64_original.dll")), "Mod helper DLL removed");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(vanillaBink), "Vanilla bink2w64.dll intact after uninstall");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan);
        }
    }

    private static void TestDeletableArchiveExtensionDetection()
    {
        using var sandbox = new TempSandbox();
        string zip = Path.Combine(sandbox.Root, "a.zip");
        string sevenZ = Path.Combine(sandbox.Root, "b.7z");
        string pak = Path.Combine(sandbox.Root, "c.pak");
        File.WriteAllBytes(zip, Array.Empty<byte>());
        File.WriteAllBytes(sevenZ, Array.Empty<byte>());
        File.WriteAllBytes(pak, Array.Empty<byte>());

        TestAssert.True(ArchiveImportService.IsDeletableArchiveFile(zip), ".zip is deletable archive");
        TestAssert.True(ArchiveImportService.IsDeletableArchiveFile(sevenZ), ".7z is deletable archive");
        TestAssert.False(ArchiveImportService.IsDeletableArchiveFile(pak), ".pak is NOT an archive");
        TestAssert.False(ArchiveImportService.IsDeletableArchiveFile(sandbox.Root), "Folder is NOT an archive");
    }

    private static async Task TestReimportRepairsMissingAndOverwrittenLooseAndPakModsAsync()
    {
        using var sandbox = new TempSandbox();

        string binDir = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(binDir);
        string activeBink = Path.Combine(binDir, "bink2w64.dll");
        string activeBinkOrig = Path.Combine(binDir, "bink2w64_original.dll");
        string backupOrig = Path.Combine(binDir, "bink2w64.dll.bg3mpm_orig");

        byte[] vanillaBytes = "VANILLA_BINK_411136_BYTES_SIMULATED_LONGER_PAYLOAD"u8.ToArray();
        byte[] moddedBinkBytes = "MODDED_NML_232448"u8.ToArray();
        await File.WriteAllBytesAsync(activeBink, vanillaBytes);
        string vanillaSha = await HashService.ComputeFileSha256Async(activeBink);

        string nmlZip = Path.Combine(sandbox.Root, "NativeModLoader.zip");
        using (ZipArchive z = ZipFile.Open(nmlZip, ZipArchiveMode.Create))
        {
            WriteZipEntry(z, "bin/bink2w64.dll", moddedBinkBytes);
            WriteZipEntry(z, "bin/bink2w64_original.dll", vanillaBytes);
        }

        // 1. Initial install over vanilla bink2w64.dll
        ArchiveImportPlan plan1 = await ArchiveImportService.InspectDropAsync(nmlZip, sandbox.Settings, sandbox.Db);
        try
        {
            ArchiveImportResult res1 = await ArchiveImportService.CommitImportAsync(plan1, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res1.Errors.Count, "Initial NML import succeeds");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan1);
        }

        ModEntry nmlMod = sandbox.Db.Mods.Single();
        nmlMod.ReplacedOriginalFiles.Clear();

        // 2. Simulate Steam/GOG Verify Game Files + empty Vault:
        //    vanilla bink2w64.dll overwrites activeBink (different length), bink2w64_original.dll and .bg3mpm_orig are deleted.
        await File.WriteAllBytesAsync(activeBink, vanillaBytes);
        if (File.Exists(activeBinkOrig)) File.Delete(activeBinkOrig);
        if (File.Exists(backupOrig)) File.Delete(backupOrig);

        // EvaluateModLocation must NOT treat the vanilla bink2w64.dll (wrong size) as an active mod file.
        TestAssert.Equal(ModLocation.Missing, ModLibraryService.EvaluateModLocation(sandbox.Settings, nmlMod),
            "Vanilla bink2w64.dll with wrong file size must evaluate to Missing, not Partial/Active");

        // User-edited .toml config file must still evaluate as Active even if size changed.
        string nativeModsDir = Path.Combine(binDir, "NativeMods");
        Directory.CreateDirectory(nativeModsDir);
        string tomlPath = Path.Combine(nativeModsDir, "TestPlugin.toml");
        await File.WriteAllTextAsync(tomlPath, "enabled=true");
        var tomlMod = new ModEntry
        {
            Id = "toml-edit-test",
            Kind = ModKind.LooseGame,
            DisplayName = "Toml Test",
            Location = ModLocation.Active,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.GameFolder,
                    RelativePath = "bin/NativeMods/TestPlugin.toml",
                    FileSizeBytes = 12,
                    Sha256 = await HashService.ComputeFileSha256Async(tomlPath)
                }
            }
        };
        await File.WriteAllTextAsync(tomlPath, "enabled=false\n# edited by user");
        TestAssert.Equal(ModLocation.Active, ModLibraryService.EvaluateModLocation(sandbox.Settings, tomlMod),
            "User-edited .toml config file remains Active despite size change");

        // 3. Re-drop NativeModLoader.zip -> must detect overwrite of vanilla bink2w64.dll & reinstall both DLLs!
        ArchiveImportPlan plan2 = await ArchiveImportService.InspectDropAsync(nmlZip, sandbox.Settings, sandbox.Db);
        try
        {
            TestAssert.True(
                plan2.Items.Any(i => i.TargetKind == ImportTargetKind.GameFolder && i.OverwritesUnmanagedFile),
                "Preview flags vanilla bink2w64.dll overwrite when existing NML is Missing/Partial");

            ArchiveImportResult res2 = await ArchiveImportService.CommitImportAsync(plan2, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res2.Errors.Count, "Re-import of NML succeeds with 0 errors");
            TestAssert.Equal(0, res2.ImportedModIds.Count, "Reuses existing NML ModEntry without creating duplicate");
            TestAssert.Equal(1, res2.ReusedDuplicateModIds.Count, "NML reused once");
            TestAssert.Equal(1, sandbox.Db.Mods.Count, "Still 1 NML entry in library");
            TestAssert.Equal(1, res2.RenamedOriginalFiles.Count, "Vanilla bink2w64.dll backed up to .bg3mpm_orig on repair");
            TestAssert.True(File.Exists(backupOrig), "Backup file bink2w64.dll.bg3mpm_orig created");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(backupOrig), "Backup has exact vanilla DLL");
            TestAssert.Equal(moddedBinkBytes.Length, (int)new FileInfo(activeBink).Length, "Modded bink2w64.dll installed in bin/");
            TestAssert.True(File.Exists(activeBinkOrig), "bink2w64_original.dll installed in bin/");
            TestAssert.EqualIgnoreCase(vanillaSha, await HashService.ComputeFileSha256Async(activeBinkOrig), "bink2w64_original.dll intact in bin/");
            TestAssert.Equal(ModLocation.Active, nmlMod.Location, "NML Location repaired to Active");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan2);
        }

        // 4. Re-drop NativeModLoader.zip a third time while already Active -> zero redundant backups!
        ArchiveImportPlan plan3 = await ArchiveImportService.InspectDropAsync(nmlZip, sandbox.Settings, sandbox.Db);
        try
        {
            ArchiveImportResult res3 = await ArchiveImportService.CommitImportAsync(plan3, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, res3.Errors.Count, "Idempotent re-import succeeds");
            TestAssert.Equal(0, res3.RenamedOriginalFiles.Count, "Already-active NML creates no extra backup");
            TestAssert.Equal(ModLocation.Active, nmlMod.Location, "NML stays Active");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(plan3);
        }

        // 5. Missing PAK mod repaired on re-import
        string pakSrcDir = Path.Combine(sandbox.Root, "pak_repair_src");
        TestPakBuilder.CreatePakV18(Path.Combine(pakSrcDir, "RepairPak.pak"), new[]
        {
            ("Mods/RepairPak/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("RepairPak", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "RepairPak")), TestPakBuilder.PayloadCompression.None)
        });
        string pakZip = Path.Combine(sandbox.Root, "RepairPak.zip");
        ZipFile.CreateFromDirectory(pakSrcDir, pakZip);

        ArchiveImportPlan pakPlan1 = await ArchiveImportService.InspectDropAsync(pakZip, sandbox.Settings, sandbox.Db);
        try
        {
            await ArchiveImportService.CommitImportAsync(pakPlan1, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
        }
        finally
        {
            ArchiveImportService.CleanupStaging(pakPlan1);
        }

        string activePakPath = Path.Combine(sandbox.Settings.ModsFolder, "RepairPak.pak");
        ModEntry pakMod = sandbox.Db.Mods.Single(m => m.PakFileName == "RepairPak.pak");
        File.Delete(activePakPath);
        TestAssert.Equal(ModLocation.Missing, ModLibraryService.EvaluateModLocation(sandbox.Settings, pakMod), "Deleted PAK evaluates Missing");

        ArchiveImportPlan pakPlan2 = await ArchiveImportService.InspectDropAsync(pakZip, sandbox.Settings, sandbox.Db);
        try
        {
            ArchiveImportResult pakRes2 = await ArchiveImportService.CommitImportAsync(pakPlan2, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(1, pakRes2.ReusedDuplicateModIds.Count, "PAK entry reused");
            TestAssert.True(File.Exists(activePakPath), "Deleted PAK repaired into Mods/ on re-import");
            TestAssert.Equal(ModLocation.Active, pakMod.Location, "Repaired PAK evaluates Active");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(pakPlan2);
        }

        // 6. Missing Hybrid mod repaired on re-import
        string hybSrcDir = Path.Combine(sandbox.Root, "hyb_repair_src");
        TestPakBuilder.CreatePakV18(Path.Combine(hybSrcDir, "RepairHybrid.pak"), new[]
        {
            ("Mods/RepairHybrid/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("RepairHybrid", "11111111-2222-3333-4444-555555555555", "RepairHybrid")), TestPakBuilder.PayloadCompression.None)
        });
        string hybLooseSrc = Path.Combine(hybSrcDir, "Generated", "Public", "Shared", "Assets", "hyb.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(hybLooseSrc)!);
        await File.WriteAllBytesAsync(hybLooseSrc, new byte[] { 7, 8, 9, 10 });
        string hybZip = Path.Combine(sandbox.Root, "RepairHybrid.zip");
        ZipFile.CreateFromDirectory(hybSrcDir, hybZip);

        ArchiveImportPlan hybPlan1 = await ArchiveImportService.InspectDropAsync(hybZip, sandbox.Settings, sandbox.Db);
        try
        {
            await ArchiveImportService.CommitImportAsync(hybPlan1, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
        }
        finally
        {
            ArchiveImportService.CleanupStaging(hybPlan1);
        }

        ModEntry hybMod = sandbox.Db.Mods.Single(m => m.Kind == ModKind.Hybrid);
        string activeHybPak = Path.Combine(sandbox.Settings.ModsFolder, "RepairHybrid.pak");
        string activeHybDds = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "hyb.dds");
        File.Delete(activeHybPak);
        File.Delete(activeHybDds);
        TestAssert.Equal(ModLocation.Missing, ModLibraryService.EvaluateModLocation(sandbox.Settings, hybMod), "Deleted Hybrid files evaluate Missing");

        ArchiveImportPlan hybPlan2 = await ArchiveImportService.InspectDropAsync(hybZip, sandbox.Settings, sandbox.Db);
        try
        {
            ArchiveImportResult hybRes2 = await ArchiveImportService.CommitImportAsync(hybPlan2, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(1, hybRes2.ReusedDuplicateModIds.Count, "Hybrid entry reused");
            TestAssert.True(File.Exists(activeHybPak), "Deleted Hybrid PAK repaired");
            TestAssert.True(File.Exists(activeHybDds), "Deleted Hybrid loose DDS repaired");
            TestAssert.Equal(ModLocation.Active, hybMod.Location, "Repaired Hybrid evaluates Active");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(hybPlan2);
        }
    }

    private static async Task TestDeeplyNestedBinAndGeneratedWithPakImportAsync()
    {
        // Case A: Exact True Third-Person Camera layout (Hybrid + Separate modes)
        string srcRoot = Path.Combine(Path.GetTempPath(), "BG3MPM_TppSrc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(srcRoot);
        try
        {
            string tppZip = Path.Combine(srcRoot, "TrueThirdPersonCamera_v2.zip");
            string pakTemp = Path.Combine(srcRoot, "TrueThirdPersonCamera.pak");
            TestPakBuilder.CreatePakV18(pakTemp, new[]
            {
                ("Mods/TrueThirdPersonCamera/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("True Third-Person Camera", "6e84559a-1b7a-4441-bfc6-c2bae5538491", "TrueThirdPersonCamera")), TestPakBuilder.PayloadCompression.None)
            });
            byte[] pakBytes = await File.ReadAllBytesAsync(pakTemp);
            byte[] dllBytes = "TPP_DLL_PAYLOAD"u8.ToArray();

            using (ZipArchive z = ZipFile.Open(tppZip, ZipArchiveMode.Create))
            {
                WriteZipEntry(z, "1 - Main Game Folder Files/bin/NativeMods/TrueThirdPersonCamera.dll", dllBytes);
                WriteZipEntry(z, "2 - BG3 Mod Manager File/TrueThirdPersonCamera.pak", pakBytes);
                WriteZipEntry(z, "README.txt", "Install instructions"u8.ToArray());
            }

            // A1: Default Hybrid import
            using (var sandbox = new TempSandbox())
            {
                ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(tppZip, sandbox.Settings, sandbox.Db);
                try
                {
                    ImportItemCandidate pakCand = plan.Items.Single(i => i.TargetKind == ImportTargetKind.ModsPak);
                    ImportItemCandidate binCand = plan.Items.Single(i => i.TargetKind == ImportTargetKind.GameFolder);
                    ImportItemCandidate readmeCand = plan.Items.Single(i => i.TargetKind == ImportTargetKind.Ignore);

                    TestAssert.True(pakCand.IsSelected, "Nested .pak is auto-selected");
                    TestAssert.True(binCand.IsSelected, "Nested bin/ is auto-selected as GameFolder");
                    TestAssert.Equal("bin/NativeMods/TrueThirdPersonCamera.dll", binCand.DestinationRelativePath, "Wrapper prefix stripped from bin destination path");
                    TestAssert.False(readmeCand.IsSelected, "README.txt ignored by default");
                    TestAssert.True(plan.PackageAsSingleHybridMod, "1 PAK + nested bin/ defaults to PackageAsSingleHybridMod");

                    ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
                    TestAssert.Equal(0, res.Errors.Count, "Hybrid import of nested PAK+bin succeeds");
                    TestAssert.Equal(1, res.ImportedModIds.Count, "Imported as 1 Hybrid mod");

                    string activePak = Path.Combine(sandbox.Settings.ModsFolder, "TrueThirdPersonCamera.pak");
                    string activeDll = Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "TrueThirdPersonCamera.dll");
                    string wrongWrapperDir = Path.Combine(sandbox.Settings.GameFolder, "1 - Main Game Folder Files");
                    TestAssert.True(File.Exists(activePak), "PAK installed to Mods/");
                    TestAssert.True(File.Exists(activeDll), "DLL installed to Game/bin/NativeMods/ without wrapper prefix");
                    TestAssert.False(Directory.Exists(wrongWrapperDir), "Wrapper directory must NOT be created in Game folder");

                    // Vault round-trip on Hybrid mod moves both files out and back cleanly
                    ModEntry mod = sandbox.Db.Mods.Single();
                    ModLibraryService.MoveModToVault(mod, sandbox.Settings);
                    TestAssert.Equal(ModLocation.Vault, mod.Location, "Hybrid moves cleanly to Vault");
                    TestAssert.False(File.Exists(activePak), "Hybrid PAK removed from Mods/");
                    TestAssert.False(File.Exists(activeDll), "Hybrid DLL removed from Game/bin/NativeMods/");

                    ModLibraryService.ActivateModFromVault(mod, sandbox.Settings);
                    TestAssert.Equal(ModLocation.Active, mod.Location, "Hybrid reactivates from Vault");
                    TestAssert.True(File.Exists(activePak), "Hybrid PAK restored");
                    TestAssert.True(File.Exists(activeDll), "Hybrid DLL restored");
                }
                finally
                {
                    ArchiveImportService.CleanupStaging(plan);
                }
            }

            // A2: Separate (non-Hybrid) import from the same nested zip
            using (var sandbox = new TempSandbox())
            {
                ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(tppZip, sandbox.Settings, sandbox.Db);
                try
                {
                    plan.PackageAsSingleHybridMod = false;
                    ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
                    TestAssert.Equal(0, res.Errors.Count, "Separate import of nested PAK+bin succeeds");
                    TestAssert.Equal(2, res.ImportedModIds.Count, "Imported as 2 separate entries (Pak + LooseGame)");
                    TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "TrueThirdPersonCamera.pak")), "Separate PAK in Mods/");
                    TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "TrueThirdPersonCamera.dll")), "Separate DLL in Game/bin/NativeMods/");
                    TestAssert.Equal(1, sandbox.Db.Mods.Count(m => m.Kind == ModKind.Pak), "Library has 1 Pak entry");
                    TestAssert.Equal(1, sandbox.Db.Mods.Count(m => m.Kind == ModKind.LooseGame && m.TrackedFiles.Single().RelativePath == "bin/NativeMods/TrueThirdPersonCamera.dll"),
                        "Library has 1 LooseGame entry with stripped bin/NativeMods path");
                }
                finally
                {
                    ArchiveImportService.CleanupStaging(plan);
                }
            }
        }
        finally
        {
            if (Directory.Exists(srcRoot))
            {
                Directory.Delete(srcRoot, recursive: true);
            }
        }

        // Case B: Multi-level nested Generated/, NativeMods/, outer Public/.../Stats/Generated/Data/Armor.txt, Game/Data/..., and nested docs/images
        using (var sandbox = new TempSandbox())
        {
            string multiZip = Path.Combine(sandbox.Root, "NestedMultiLayout.zip");
            using (ZipArchive z = ZipFile.Open(multiZip, ZipArchiveMode.Create))
            {
                WriteZipEntry(z, "Optional/Deep/Wrapper/Generated/Public/Shared/Assets/skin.dds", new byte[] { 1, 2, 3 });
                WriteZipEntry(z, "Step1/Plugins/NativeMods/CameraPlugin.dll", new byte[] { 4, 5, 6 });
                WriteZipEntry(z, "Step2/Public/MyMod/Stats/Generated/Data/Armor.txt", "new entry \"X\""u8.ToArray());
                WriteZipEntry(z, "InstallHere/Game/Data/Generated/Public/Shared/Assets/extra.dds", new byte[] { 7, 8 });
                WriteZipEntry(z, "Optional/Deep/Wrapper/bin/README.txt", "ignore me"u8.ToArray());
                WriteZipEntry(z, "Step1/preview.png", new byte[] { 0x89, 0x50 });
            }

            ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(multiZip, sandbox.Settings, sandbox.Db);
            try
            {
                TestAssert.True(plan.Items.Any(i => i.TargetKind == ImportTargetKind.DataFolder && i.DestinationRelativePath == "Generated/Public/Shared/Assets/skin.dds" && i.IsSelected),
                    "Deeply nested Generated/ maps to Data/Generated/...");
                TestAssert.True(plan.Items.Any(i => i.TargetKind == ImportTargetKind.GameFolder && i.DestinationRelativePath == "bin/NativeMods/CameraPlugin.dll" && i.IsSelected),
                    "Deeply nested NativeMods/ maps to Game/bin/NativeMods/...");
                TestAssert.True(plan.Items.Any(i => i.TargetKind == ImportTargetKind.DataFolder && i.DestinationRelativePath == "Public/MyMod/Stats/Generated/Data/Armor.txt" && i.IsSelected),
                    "Outer Public/ takes precedence over inner Generated/Data/ for Stats/Generated/Data/Armor.txt");
                TestAssert.True(plan.Items.Any(i => i.TargetKind == ImportTargetKind.DataFolder && i.DestinationRelativePath == "Generated/Public/Shared/Assets/extra.dds" && i.IsSelected),
                    "Nested Game/Data/ maps cleanly to Data/");
                TestAssert.True(plan.Items.Where(i => i.TargetKind == ImportTargetKind.Ignore).Count() == 2 &&
                                plan.Items.Where(i => i.TargetKind == ImportTargetKind.Ignore).All(i => !i.IsSelected),
                    "Nested README.txt and preview.png are ignored");

                ArchiveImportResult res = await ArchiveImportService.CommitImportAsync(plan, null, installActiveNow: true, sandbox.Settings, sandbox.Db);
                TestAssert.Equal(0, res.Errors.Count, "Multi-layout nested import succeeds");
                TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "skin.dds")), "Nested skin.dds installed to Data/");
                TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "CameraPlugin.dll")), "Nested CameraPlugin.dll installed to Game/bin/NativeMods/");
                TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.DataFolder, "Public", "MyMod", "Stats", "Generated", "Data", "Armor.txt")), "Stats Armor.txt installed under Data/Public/");
                TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "extra.dds")), "Game/Data extra.dds installed to Data/");
                TestAssert.False(File.Exists(Path.Combine(sandbox.Settings.GameFolder, "bin", "README.txt")), "Nested bin/README.txt not installed");
            }
            finally
            {
                ArchiveImportService.CleanupStaging(plan);
            }
        }
    }

    private static async Task TestImportAndDragReorderAutoSyncBg3MmJsonAndLsxAsync()
    {
        using var sandbox = new TempSandbox();
        var cache = new PakMetadataCache();

        string uuidA = "aaaaaaaa-0001-0001-0001-000000000001";
        string uuidB = "bbbbbbbb-0002-0002-0002-000000000002";
        string uuidC = "cccccccc-0003-0003-0003-000000000003";

        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "ModA.pak"), new[]
        {
            ("Mods/ModA/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod A", uuidA, "ModA")), TestPakBuilder.PayloadCompression.None)
        });
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);
        ModEntry modA = sandbox.Db.Mods.Single(m => m.Uuid == uuidA);

        string loosePath = Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "CameraLoose.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
        await File.WriteAllBytesAsync(loosePath, new byte[] { 1, 2, 3, 4 });
        var looseMod = new ModEntry
        {
            Id = "loose-cam-01",
            Kind = ModKind.LooseGame,
            DisplayName = "Camera Loose",
            ContentHash = await HashService.ComputeFileSha256Async(loosePath),
            Location = ModLocation.Active,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.GameFolder,
                    RelativePath = "bin/NativeMods/CameraLoose.dll",
                    FileSizeBytes = 4,
                    Sha256 = await HashService.ComputeFileSha256Async(loosePath)
                }
            }
        };
        sandbox.Db.Mods.Add(looseMod);

        string orderJsonPath = Path.Combine(sandbox.Settings.OrdersFolder, "TestOrder.json");
        Bg3LoadOrderService.SaveOrderJson(orderJsonPath, new[]
        {
            new Bg3OrderEntry { Uuid = uuidA, Name = "Mod A" }
        });

        var profile = new ModProfile
        {
            Name = "Synced Setup",
            LinkedOrderJsonPath = orderJsonPath,
            ModIds = new List<string> { modA.Id, looseMod.Id }
        };
        sandbox.Db.Profiles.Add(profile);
        sandbox.Db.ActiveProfileId = profile.Id;
        ModLibraryService.LinkCompanionModsToOrderJson(sandbox.Db, orderJsonPath, profile.ModIds);

        // 1. Import Mod B directly into active linked profile -> must insert before looseMod and auto-write JSON + modsettings.lsx
        string srcB = Path.Combine(sandbox.Root, "import_b");
        TestPakBuilder.CreatePakV18(Path.Combine(srcB, "ModB.pak"), new[]
        {
            ("Mods/ModB/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod B", uuidB, "ModB")), TestPakBuilder.PayloadCompression.None)
        });
        string zipB = Path.Combine(sandbox.Root, "ModB.zip");
        ZipFile.CreateFromDirectory(srcB, zipB);

        ArchiveImportPlan planB = await ArchiveImportService.InspectDropAsync(zipB, sandbox.Settings, sandbox.Db);
        try
        {
            ArchiveImportResult resB = await ArchiveImportService.CommitImportAsync(planB, profile.Id, installActiveNow: true, sandbox.Settings, sandbox.Db);
            TestAssert.Equal(0, resB.Errors.Count, "Import ModB succeeds");
            TestAssert.Equal(orderJsonPath, resB.SyncedOrderJsonPath, "ArchiveImportResult records synced JSON path");
            TestAssert.Equal(2, resB.SyncedOrderJsonCount, "Synced JSON has 2 PAK entries");
            TestAssert.True(resB.SyncedModSettingsLsx, "ArchiveImportResult records synced modsettings.lsx");
        }
        finally
        {
            ArchiveImportService.CleanupStaging(planB);
        }

        ModEntry modB = sandbox.Db.Mods.Single(m => m.Uuid == uuidB);
        TestAssert.Equal(3, profile.ModIds.Count, "Profile now has 3 mods (ModA, ModB, looseMod)");
        TestAssert.Equal(modA.Id, profile.ModIds[0], "ModA at index 0");
        TestAssert.Equal(modB.Id, profile.ModIds[1], "Newly imported PAK inserted before trailing loose mod (index 1)");
        TestAssert.Equal(looseMod.Id, profile.ModIds[2], "Loose mod remains at the end");

        Bg3LoadOrderFile jsonAfterB = Bg3LoadOrderService.LoadOrderJson(orderJsonPath);
        TestAssert.Equal(2, jsonAfterB.Order.Count, "BG3MM JSON automatically updated on disk for F5 refresh");
        TestAssert.EqualIgnoreCase(uuidA, jsonAfterB.Order[0].Uuid, "JSON[0] == ModA");
        TestAssert.EqualIgnoreCase(uuidB, jsonAfterB.Order[1].Uuid, "JSON[1] == ModB");

        List<ModSettingsEntry> lsxAfterB = Bg3LoadOrderService.ReadModSettingsLsx(sandbox.Settings.ModSettingsLsxPath);
        TestAssert.Equal(2, lsxAfterB.Count, "modsettings.lsx automatically updated with ModB");
        TestAssert.EqualIgnoreCase(uuidB, lsxAfterB[1].Uuid, "LSX[1] == ModB");
        TestAssert.True(ModLibraryService.GetLinkedCompanionModIdsForOrderJson(sandbox.Db, orderJsonPath).Contains(looseMod.Id),
            "Companion loose mod link preserved alongside newly imported ModB");

        // 2. Import Mod C -> Add C, then test single & multi-select drag reorder + MoveProfileModsByDelta + sync
        string srcC = Path.Combine(sandbox.Root, "import_c");
        TestPakBuilder.CreatePakV18(Path.Combine(srcC, "ModC.pak"), new[]
        {
            ("Mods/ModC/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod C", uuidC, "ModC")), TestPakBuilder.PayloadCompression.None)
        });
        string zipC = Path.Combine(sandbox.Root, "ModC.zip");
        ZipFile.CreateFromDirectory(srcC, zipC);

        ArchiveImportPlan planC = await ArchiveImportService.InspectDropAsync(zipC, sandbox.Settings, sandbox.Db);
        try
        {
            await ArchiveImportService.CommitImportAsync(planC, profile.Id, installActiveNow: true, sandbox.Settings, sandbox.Db);
        }
        finally
        {
            ArchiveImportService.CleanupStaging(planC);
        }

        ModEntry modC = sandbox.Db.Mods.Single(m => m.Uuid == uuidC);
        // Current order: [ModA, ModB, ModC, looseMod]
        TestAssert.False(ModLibraryService.ReorderProfileMods(profile, new[] { modA.Id }, modA.Id, false), "Dropping mod onto itself is a no-op");
        TestAssert.True(ModLibraryService.ReorderProfileMods(profile, new[] { modB.Id, modC.Id }, modA.Id, insertAfter: false),
            "Multi-select block drag [ModB, ModC] before ModA succeeds");
        TestAssert.Equal(modB.Id, profile.ModIds[0], "After multi-drag: [0] == ModB");
        TestAssert.Equal(modC.Id, profile.ModIds[1], "After multi-drag: [1] == ModC");
        TestAssert.Equal(modA.Id, profile.ModIds[2], "After multi-drag: [2] == ModA");

        TestAssert.True(ModLibraryService.MoveProfileModsByDelta(profile, new[] { modA.Id }, -1), "MoveProfileModsByDelta shifts ModA up 1");
        TestAssert.Equal(modA.Id, profile.ModIds[1], "After MoveUp: ModA at index 1 ([ModB, ModA, ModC, loose])");

        ModLibraryService.LoadOrderSyncResult syncAfterReorder = ModLibraryService.WriteProfileLoadOrderFiles(profile, sandbox.Settings, sandbox.Db);
        TestAssert.True(syncAfterReorder.JsonWritten && syncAfterReorder.LsxWritten, "WriteProfileLoadOrderFiles updates both JSON and LSX");

        Bg3LoadOrderFile jsonReordered = Bg3LoadOrderService.LoadOrderJson(orderJsonPath);
        TestAssert.EqualIgnoreCase(uuidB, jsonReordered.Order[0].Uuid, "Reordered JSON[0] == ModB");
        TestAssert.EqualIgnoreCase(uuidA, jsonReordered.Order[1].Uuid, "Reordered JSON[1] == ModA");
        TestAssert.EqualIgnoreCase(uuidC, jsonReordered.Order[2].Uuid, "Reordered JSON[2] == ModC");

        List<ModSettingsEntry> lsxReordered = Bg3LoadOrderService.ReadModSettingsLsx(sandbox.Settings.ModSettingsLsxPath);
        TestAssert.EqualIgnoreCase(uuidB, lsxReordered[0].Uuid, "Reordered LSX[0] == ModB");
        TestAssert.EqualIgnoreCase(uuidA, lsxReordered[1].Uuid, "Reordered LSX[1] == ModA");
        TestAssert.EqualIgnoreCase(uuidC, lsxReordered[2].Uuid, "Reordered LSX[2] == ModC");

        // 3. Uninstall ModB -> removes from profile and updates linked JSON + modsettings.lsx automatically
        ModLibraryService.ModUninstallResult uninstallRes = ModLibraryService.UninstallMod(modB, sandbox.Settings, sandbox.Db);
        TestAssert.Equal(0, uninstallRes.Warnings.Count, "Uninstall ModB has no warnings");
        Bg3LoadOrderFile jsonAfterUninstall = Bg3LoadOrderService.LoadOrderJson(orderJsonPath);
        TestAssert.Equal(2, jsonAfterUninstall.Order.Count, "JSON has 2 entries after uninstalling ModB");
        TestAssert.EqualIgnoreCase(uuidA, jsonAfterUninstall.Order[0].Uuid, "After uninstall: JSON[0] == ModA");
        TestAssert.EqualIgnoreCase(uuidC, jsonAfterUninstall.Order[1].Uuid, "After uninstall: JSON[1] == ModC");

        List<ModSettingsEntry> lsxAfterUninstall = Bg3LoadOrderService.ReadModSettingsLsx(sandbox.Settings.ModSettingsLsxPath);
        TestAssert.Equal(2, lsxAfterUninstall.Count, "LSX updated automatically after uninstalling ModB");
        TestAssert.EqualIgnoreCase(uuidA, lsxAfterUninstall[0].Uuid, "After uninstall: LSX[0] == ModA");
        TestAssert.EqualIgnoreCase(uuidC, lsxAfterUninstall[1].Uuid, "After uninstall: LSX[1] == ModC");
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, byte[] data)
    {
        ZipArchiveEntry e = archive.CreateEntry(entryName);
        using Stream s = e.Open();
        s.Write(data, 0, data.Length);
    }
}
