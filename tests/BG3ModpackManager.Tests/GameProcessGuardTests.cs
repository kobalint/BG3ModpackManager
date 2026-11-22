using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class GameProcessGuardTests
{
    public static async Task RunAllAsync()
    {
        await TestVulkanAndDx11BlocksMutationsAsync();
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
