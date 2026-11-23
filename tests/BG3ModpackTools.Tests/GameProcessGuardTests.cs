using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.Tests;

internal static class GameProcessGuardTests
{
    public static async Task RunAllAsync()
    {
        await TestVulkanAndDx11BlocksMutationsAsync();
        await TestActiveLoadOrderWritesBlockedWhileGameRunningAsync();
        TestGameLauncherResolution();
    }

    private static async Task TestVulkanAndDx11BlocksMutationsAsync()
    {
        using var sandbox = new TempSandbox();
        var cache = new PakMetadataCache();

        string activePak = Path.Combine(sandbox.Settings.ModsFolder, "Sample.pak");
        TestPakBuilder.CreatePakV18(activePak, new[]
        {
            ("Mods/Sample/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Sample", "11111111-1111-1111-1111-111111111111", "Sample")), TestPakBuilder.PayloadCompression.None)
        });
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);
        ModEntry mod = sandbox.Db.Mods.Single();

        var emptyProfile = new ModProfile { Name = "Empty" };
        sandbox.Db.Profiles.Add(emptyProfile);
        SwitchPlan plan = await ProfileSwitchService.BuildSwitchPlanAsync(emptyProfile, sandbox.Settings, sandbox.Db, cache);

        // 1. Mock Vulkan bg3.exe running
        using (GameProcessGuard.OverrideForTests(() => new[]
               {
                   new RunningGameProcess(1234, "bg3", "Baldur's Gate 3 (Vulkan - bg3.exe)")
               }))
        {
            TestAssert.True(GameProcessGuard.IsGameRunning(), "Vulkan process must be detected as running");

            await AssertThrowsGameRunningAsync(() => ProfileSwitchService.ExecuteSwitchPlanAsync(plan, sandbox.Settings, sandbox.Db, cache, sandbox.Store));
            AssertThrowsGameRunning(() => ModLibraryService.MoveModToVault(mod, sandbox.Settings));
            AssertThrowsGameRunning(() => ModLibraryService.UninstallMod(mod, sandbox.Settings, sandbox.Db));
            TestAssert.True(File.Exists(activePak), "Active PAK must remain untouched when guard blocks operation");
        }

        // 2. Mock DirectX 11 bg3_dx11.exe running during active CommitImportAsync
        string srcZip = Path.Combine(sandbox.Root, "test_import.zip");
        using (var z = System.IO.Compression.ZipFile.Open(srcZip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("bin/NativeMods/dummy.dll");
            using var s = e.Open();
            s.Write(new byte[] { 1, 2, 3 });
        }

        ArchiveImportPlan importPlan = await ArchiveImportService.InspectDropAsync(srcZip, sandbox.Settings, sandbox.Db);
        try
        {
            using (GameProcessGuard.OverrideForTests(() => new[]
                   {
                       new RunningGameProcess(5678, "bg3_dx11", "Baldur's Gate 3 (DirectX 11 - bg3_dx11.exe)")
                   }))
            {
                TestAssert.True(GameProcessGuard.IsGameRunning(), "DX11 process must be detected as running");
                await AssertThrowsGameRunningAsync(() => ArchiveImportService.CommitImportAsync(importPlan, null, installActiveNow: true, sandbox.Settings, sandbox.Db));
                TestAssert.False(File.Exists(Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "dummy.dll")), "Active import must not write files while DX11 is running");
            }
        }
        finally
        {
            ArchiveImportService.CleanupStaging(importPlan);
        }

        // 3. When game is closed (mock empty), move to Vault succeeds cleanly
        using (GameProcessGuard.OverrideForTests(Array.Empty<RunningGameProcess>))
        {
            TestAssert.False(GameProcessGuard.IsGameRunning(), "Game should report not running");
            SwitchExecutionResult res = await ProfileSwitchService.ExecuteSwitchPlanAsync(plan, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
            TestAssert.True(res.Success, "Switch succeeds once BG3 is closed");
            TestAssert.False(File.Exists(activePak), "PAK moved to Vault after BG3 closed");
        }
    }

    private static async Task TestActiveLoadOrderWritesBlockedWhileGameRunningAsync()
    {
        using var sandbox = new TempSandbox();
        var cache = new PakMetadataCache();

        string uuidA = "aaaaaaaa-9999-1111-1111-000000000001";
        string uuidB = "bbbbbbbb-9999-2222-2222-000000000002";
        string uuidC = "cccccccc-9999-3333-3333-000000000003";
        string uuidV1 = "dddddddd-9999-4444-4444-000000000004";
        string uuidV2 = "eeeeeeee-9999-5555-5555-000000000005";

        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "ModA.pak"), new[]
        {
            ("Mods/ModA/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod A", uuidA, "ModA")), TestPakBuilder.PayloadCompression.None)
        });
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "ModB.pak"), new[]
        {
            ("Mods/ModB/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod B", uuidB, "ModB")), TestPakBuilder.PayloadCompression.None)
        });
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);
        ModEntry modA = sandbox.Db.Mods.Single(m => m.Uuid == uuidA);
        ModEntry modB = sandbox.Db.Mods.Single(m => m.Uuid == uuidB);

        string vaultStage = Path.Combine(sandbox.Root, "vault_stage");
        TestPakBuilder.CreatePakV18(Path.Combine(vaultStage, "Vault1.pak"), new[]
        {
            ("Mods/Vault1/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Vault 1", uuidV1, "Vault1")), TestPakBuilder.PayloadCompression.None)
        });
        TestPakBuilder.CreatePakV18(Path.Combine(vaultStage, "Vault2.pak"), new[]
        {
            ("Mods/Vault2/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Vault 2", uuidV2, "Vault2")), TestPakBuilder.PayloadCompression.None)
        });
        await ModLibraryService.BulkImportFromBackupFolderAsync(vaultStage, sandbox.Settings, sandbox.Db, cache, moveInsteadOfCopy: true);
        ModEntry vault1 = sandbox.Db.Mods.Single(m => m.Uuid == uuidV1);
        ModEntry vault2 = sandbox.Db.Mods.Single(m => m.Uuid == uuidV2);

        string activeJsonPath = Path.Combine(sandbox.Settings.OrdersFolder, "ActiveOrder.json");
        var activeProfile = new ModProfile
        {
            Name = "Active Setup",
            LinkedOrderJsonPath = activeJsonPath,
            ModIds = new List<string> { modA.Id }
        };

        string inactiveJsonPath = Path.Combine(sandbox.Settings.OrdersFolder, "InactiveOrder.json");
        var inactiveProfile = new ModProfile
        {
            Name = "Inactive Vaulted Setup",
            LinkedOrderJsonPath = inactiveJsonPath,
            ModIds = new List<string> { vault1.Id, vault2.Id }
        };

        sandbox.Db.Profiles.Add(activeProfile);
        sandbox.Db.Profiles.Add(inactiveProfile);
        sandbox.Db.ActiveProfileId = activeProfile.Id;

        ModLibraryService.WriteProfileLoadOrderFiles(activeProfile, sandbox.Settings, sandbox.Db);
        string baselineJsonText = await File.ReadAllTextAsync(activeJsonPath);
        string baselineLsxText = await File.ReadAllTextAsync(sandbox.Settings.ModSettingsLsxPath);

        string importDir = Path.Combine(sandbox.Root, "import_c");
        TestPakBuilder.CreatePakV18(Path.Combine(importDir, "ModC.pak"), new[]
        {
            ("Mods/ModC/meta.lsx", System.Text.Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod C", uuidC, "ModC")), TestPakBuilder.PayloadCompression.None)
        });
        string zipC = Path.Combine(sandbox.Root, "ModC.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(importDir, zipC);
        ArchiveImportPlan importPlanC = await ArchiveImportService.InspectDropAsync(zipC, sandbox.Settings, sandbox.Db);

        try
        {
            using (GameProcessGuard.OverrideForTests(() => new[]
                   {
                       new RunningGameProcess(2468, "bg3", "Baldur's Gate 3 (Vulkan - bg3.exe)")
                   }))
            {
                // 1. Direct WriteModSettingsLsx blocked
                AssertThrowsGameRunning(() => Bg3LoadOrderService.WriteModSettingsLsx(sandbox.Settings.ModSettingsLsxPath, new[] { modA, modB }));
                TestAssert.Equal(baselineLsxText, await File.ReadAllTextAsync(sandbox.Settings.ModSettingsLsxPath), "modsettings.lsx untouched while blocked");

                // 2. WriteProfileLoadOrderFiles on active profile blocked (blocks both JSON and LSX)
                activeProfile.ModIds.Add(modB.Id);
                AssertThrowsGameRunning(() => ModLibraryService.WriteProfileLoadOrderFiles(activeProfile, sandbox.Settings, sandbox.Db));
                TestAssert.Equal(baselineJsonText, await File.ReadAllTextAsync(activeJsonPath), "Active linked JSON untouched while BG3 running");
                TestAssert.Equal(baselineLsxText, await File.ReadAllTextAsync(sandbox.Settings.ModSettingsLsxPath), "modsettings.lsx untouched while BG3 running");

                // 3. Forced active load order write from a non-active profile blocked while BG3 running
                AssertThrowsGameRunning(() => ModLibraryService.WriteProfileLoadOrderFiles(
                    inactiveProfile,
                    sandbox.Settings,
                    sandbox.Db,
                    writeLinkedJson: true,
                    writeModSettingsLsxIfActive: true,
                    onlyActiveModsInJson: true,
                    forceWriteModSettingsLsx: true));

                // 4. Vault-only import assigned to active profile blocked while BG3 running
                await AssertThrowsGameRunningAsync(() => ArchiveImportService.CommitImportAsync(
                    importPlanC,
                    activeProfile.Id,
                    installActiveNow: false,
                    sandbox.Settings,
                    sandbox.Db));
                TestAssert.False(sandbox.Db.Mods.Any(m => string.Equals(m.Uuid, uuidC, StringComparison.OrdinalIgnoreCase)), "ModC not imported into library when active order sync blocked");
                TestAssert.Equal(2, activeProfile.ModIds.Count, "ModC not added to active profile when blocked");
                TestAssert.Equal(baselineJsonText, await File.ReadAllTextAsync(activeJsonPath), "Active linked JSON untouched on blocked import");

                // 5. Completely inactive profile with its own separate JSON and no LSX write STILL allowed while BG3 running
                inactiveProfile.ModIds.Remove(vault1.Id);
                ModLibraryService.LoadOrderSyncResult inactiveRes = ModLibraryService.WriteProfileLoadOrderFiles(
                    inactiveProfile,
                    sandbox.Settings,
                    sandbox.Db,
                    writeLinkedJson: true,
                    writeModSettingsLsxIfActive: true,
                    onlyActiveModsInJson: false,
                    forceWriteModSettingsLsx: false);
                TestAssert.True(inactiveRes.JsonWritten, "Separate inactive profile still allowed to write its own JSON while BG3 runs");
                TestAssert.False(inactiveRes.LsxWritten, "Inactive profile does not touch modsettings.lsx");
                TestAssert.Equal(baselineLsxText, await File.ReadAllTextAsync(sandbox.Settings.ModSettingsLsxPath), "Active modsettings.lsx remains untouched");
            }

            // 6. Once game is closed, active load order writes succeed cleanly
            using (GameProcessGuard.OverrideForTests(Array.Empty<RunningGameProcess>))
            {
                ModLibraryService.LoadOrderSyncResult okSync = ModLibraryService.WriteProfileLoadOrderFiles(activeProfile, sandbox.Settings, sandbox.Db);
                TestAssert.True(okSync.JsonWritten && okSync.LsxWritten, "Active JSON and modsettings.lsx update once BG3 closes");

                ArchiveImportResult importOk = await ArchiveImportService.CommitImportAsync(
                    importPlanC,
                    activeProfile.Id,
                    installActiveNow: false,
                    sandbox.Settings,
                    sandbox.Db);
                TestAssert.Equal(0, importOk.Errors.Count, "Vault-only import assigned to active profile succeeds once BG3 closes");
                TestAssert.Equal(2, Bg3LoadOrderService.LoadOrderJson(activeJsonPath).Order.Count, "Vaulted ModC excluded from active JSON order while remaining in profile");
                TestAssert.Equal(3, activeProfile.ModIds.Count, "Active profile now references ModA, ModB, ModC");
            }
        }
        finally
        {
            ArchiveImportService.CleanupStaging(importPlanC);
        }
    }

    private static void AssertThrowsGameRunning(Action action)
    {
        bool threw = false;
        try
        {
            action();
        }
        catch (GameRunningException)
        {
            threw = true;
        }

        TestAssert.True(threw, "Expected GameRunningException");
    }

    private static async Task AssertThrowsGameRunningAsync(Func<Task> action)
    {
        bool threw = false;
        try
        {
            await action();
        }
        catch (GameRunningException)
        {
            threw = true;
        }

        TestAssert.True(threw, "Expected GameRunningException");
    }

    private static void TestGameLauncherResolution()
    {
        using var sandbox = new TempSandbox();

        TestAssert.Equal("bg3_dx11.exe", GameLauncherService.GetExecutableFileName(Bg3LaunchRenderer.DirectX11), "DX11 executable name");
        TestAssert.Equal("bg3.exe", GameLauncherService.GetExecutableFileName(Bg3LaunchRenderer.Vulkan), "Vulkan executable name");

        // Missing folder when both GameFolder and DataFolder are blank
        Bg3LaunchResolution unsetRes = GameLauncherService.ResolveExecutable("", "", Bg3LaunchRenderer.DirectX11);
        TestAssert.False(unsetRes.Success, "Unset folder must fail resolution");
        TestAssert.Equal("GameFolderNotSet", unsetRes.ErrorCode, "ErrorCode GameFolderNotSet");

        // Fallback to parent of DataFolder when GameFolder is blank
        string derivedRoot = GameLauncherService.ResolveGameFolder("", sandbox.Settings.DataFolder);
        TestAssert.Equal(Path.GetFullPath(sandbox.Settings.GameFolder), Path.GetFullPath(derivedRoot), "ResolveGameFolder derives parent of DataFolder");

        // Non-existent GameFolder
        string missingDir = Path.Combine(sandbox.Root, "NoSuchGameDir");
        Bg3LaunchResolution missingDirRes = GameLauncherService.ResolveExecutable(missingDir, "", Bg3LaunchRenderer.Vulkan);
        TestAssert.False(missingDirRes.Success, "Missing folder fails resolution");
        TestAssert.Equal("GameFolderMissing", missingDirRes.ErrorCode, "ErrorCode GameFolderMissing");

        // Valid GameFolder without executables yet
        Bg3LaunchResolution missingExeRes = GameLauncherService.ResolveExecutable(sandbox.Settings.GameFolder, sandbox.Settings.DataFolder, Bg3LaunchRenderer.Vulkan);
        TestAssert.False(missingExeRes.Success, "Missing bg3.exe fails resolution");
        TestAssert.Equal("ExecutableMissing", missingExeRes.ErrorCode, "ErrorCode ExecutableMissing");

        // Seed both bg3.exe and bg3_dx11.exe under GameFolder/bin/
        string binDir = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(binDir);
        string vkExe = Path.Combine(binDir, "bg3.exe");
        string dxExe = Path.Combine(binDir, "bg3_dx11.exe");
        File.WriteAllText(vkExe, "VK");
        File.WriteAllText(dxExe, "DX11");

        Bg3LaunchResolution vkRes = GameLauncherService.ResolveExecutable(sandbox.Settings.GameFolder, sandbox.Settings.DataFolder, Bg3LaunchRenderer.Vulkan);
        TestAssert.True(vkRes.Success, "Vulkan executable resolves");
        TestAssert.Equal(vkExe, vkRes.ExecutablePath, "Resolves bg3.exe");
        TestAssert.Equal(binDir, vkRes.WorkingDirectory, "WorkingDirectory is bin folder");

        Bg3LaunchResolution dxRes = GameLauncherService.ResolveExecutable("", sandbox.Settings.DataFolder, Bg3LaunchRenderer.DirectX11);
        TestAssert.True(dxRes.Success, "DX11 executable resolves via DataFolder fallback");
        TestAssert.Equal(dxExe, dxRes.ExecutablePath, "Resolves bg3_dx11.exe");

        // Direct bin-folder configuration fallback
        Bg3LaunchResolution binAsRootRes = GameLauncherService.ResolveExecutable(binDir, "", Bg3LaunchRenderer.Vulkan);
        TestAssert.True(binAsRootRes.Success, "Resolves executable when GameFolder points directly at bin/");
        TestAssert.Equal(vkExe, binAsRootRes.ExecutablePath, "Direct bin path resolves bg3.exe");

        // Settings persistence round-trip & legacy default deserialization
        var store = new JsonStore(Path.Combine(sandbox.Root, "settings_store"));
        AppSettings toSave = new()
        {
            GameFolder = sandbox.Settings.GameFolder,
            LaunchRenderer = Bg3LaunchRenderer.Vulkan
        };
        store.SaveSettings(toSave);
        AppSettings loaded = store.LoadSettings();
        TestAssert.Equal(Bg3LaunchRenderer.Vulkan, loaded.LaunchRenderer, "LaunchRenderer Vulkan persists across Save/Load");

        File.WriteAllText(store.SettingsPath, "{ \"GameFolder\": \"C:\\\\Games\\\\BG3\" }");
        AppSettings legacyLoaded = store.LoadSettings();
        TestAssert.Equal(Bg3LaunchRenderer.DirectX11, legacyLoaded.LaunchRenderer, "Legacy settings default LaunchRenderer to DirectX11");
    }
}
