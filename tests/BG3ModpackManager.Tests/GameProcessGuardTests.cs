using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class GameProcessGuardTests
{
    public static async Task RunAllAsync()
    {
        await TestVulkanAndDx11BlocksMutationsAsync();
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
}
