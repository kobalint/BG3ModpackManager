using System.Text;
using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class ProfileSwitchTests
{
    public static async Task RunAllAsync()
    {
        await TestSharedZeroMoveAndUniqueTavSwapAndLooseDataPruningAsync();
        await TestOverridesAndDataOnlySwitchModeAsync();
        await TestModSettingsLsxSyncWithBackupAsync();
    }

    private static async Task TestSharedZeroMoveAndUniqueTavSwapAndLooseDataPruningAsync()
    {
        using var sandbox = new TempSandbox();
        var cache = new PakMetadataCache();

        string sharedUuid = "11111111-aaaa-bbbb-cccc-000000000001";
        string profAUuid = "11111111-aaaa-bbbb-cccc-000000000002";
        string ovrAUuid = "84199668-9978-4142-86b7-efba18e9b557";
        string profBUuid = "11111111-aaaa-bbbb-cccc-000000000003";
        string uniqueTavUuid = "4cb0fd40-7212-4fe9-8733-9a8dbc6637ae";

        // Active Mods for Profile A: Shared, ProfileA-only, OverrideA, KAZSTRA unique_tav.pak
        string sharedPath = Path.Combine(sandbox.Settings.ModsFolder, "SharedMod.pak");
        TestPakBuilder.CreatePakV18(sharedPath, new[]
        {
            ("Mods/SharedMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("SharedMod", sharedUuid, "SharedMod")), TestPakBuilder.PayloadCompression.None)
        });
        DateTime sharedInitialWrite = File.GetLastWriteTimeUtc(sharedPath);

        string profAPath = Path.Combine(sandbox.Settings.ModsFolder, "ProfileAOnly.pak");
        TestPakBuilder.CreatePakV18(profAPath, new[]
        {
            ("Mods/ProfileAOnly/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("ProfileAOnly", profAUuid, "ProfileAOnly")), TestPakBuilder.PayloadCompression.None)
        });

        string ovrAPath = Path.Combine(sandbox.Settings.ModsFolder, "ASTRL_Lifelike.pak");
        TestPakBuilder.CreatePakV18(ovrAPath, new[]
        {
            ("Mods/ASTRL_Lifelike/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("ASTRL Lifelike", ovrAUuid, "ASTRL_Lifelike")), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/CharacterCreationPresets/Skin.lsx", "skin"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        string kavtActivePath = Path.Combine(sandbox.Settings.ModsFolder, "unique_tav.pak");
        TestPakBuilder.CreatePakV18(kavtActivePath, new[]
        {
            ("Mods/unique_tav/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("KAVT - KAZSTRA Virtual Tav", uniqueTavUuid, "unique_tav")), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/Assets/kavt.lsx", "kavt_payload"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        string kavtHash = await HashService.ComputeFileSha256Async(kavtActivePath);

        // Adopt active PAKs
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);

        // Also register an active LooseData mod (inside Data/Generated/Public/Shared/Custom/a.dds and Data/Public/MyLoose/b.lsx)
        string protectedSharedDir = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared");
        string customDir = Path.Combine(protectedSharedDir, "Custom");
        Directory.CreateDirectory(customDir);
        string looseFile1 = Path.Combine(customDir, "a.dds");
        await File.WriteAllBytesAsync(looseFile1, new byte[] { 1, 2, 3 });
        ModEntry looseDataMod = await ModLibraryService.RegisterExistingLooseFolderAsync(customDir, TrackedFileTarget.DataFolder, "CustomLooseData", sandbox.Settings, sandbox.Db);

        // Put ProfileB-only PAK and Original unique_tav.pak into Vault (staged via temp -> BulkImport)
        string importStage = Path.Combine(sandbox.Root, "b_stage");
        TestPakBuilder.CreatePakV18(Path.Combine(importStage, "ProfileBOnly.pak"), new[]
        {
            ("Mods/ProfileBOnly/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("ProfileBOnly", profBUuid, "ProfileBOnly")), TestPakBuilder.PayloadCompression.None)
        });
        string origUniqueTmp = Path.Combine(importStage, "unique_tav.pak");
        TestPakBuilder.CreatePakV18(origUniqueTmp, new[]
        {
            ("Mods/unique_tav/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("unique_tav", uniqueTavUuid, "unique_tav")), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/Assets/orig_utav.lsx", "original_utav_payload"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        string origUniqueHash = await HashService.ComputeFileSha256Async(origUniqueTmp);
        TestAssert.False(string.Equals(kavtHash, origUniqueHash, StringComparison.OrdinalIgnoreCase), "KAVT and Orig unique_tav have different hashes");

        await ModLibraryService.BulkImportFromBackupFolderAsync(importStage, sandbox.Settings, sandbox.Db, cache, moveInsteadOfCopy: true);

        ModEntry sharedMod = sandbox.Db.Mods.Single(m => m.Uuid == sharedUuid);
        ModEntry profAMod = sandbox.Db.Mods.Single(m => m.Uuid == profAUuid);
        ModEntry ovrAMod = sandbox.Db.Mods.Single(m => m.Uuid == ovrAUuid);
        ModEntry kavtMod = sandbox.Db.Mods.Single(m => m.ContentHash == kavtHash);
        ModEntry profBMod = sandbox.Db.Mods.Single(m => m.Uuid == profBUuid);
        ModEntry origUtavMod = sandbox.Db.Mods.Single(m => m.ContentHash == origUniqueHash);

        var profileA = new ModProfile
        {
            Name = "Profile A",
            ModIds = new List<string> { sharedMod.Id, profAMod.Id, ovrAMod.Id, kavtMod.Id, looseDataMod.Id }
        };
        var profileB = new ModProfile
        {
            Name = "Profile B",
            ModIds = new List<string> { sharedMod.Id, profBMod.Id, origUtavMod.Id }
        };
        sandbox.Db.Profiles.Add(profileA);
        sandbox.Db.Profiles.Add(profileB);
        sandbox.Db.ActiveProfileId = profileA.Id;

        // Build plan A -> B
        SwitchPlan planToB = await ProfileSwitchService.BuildSwitchPlanAsync(profileB, sandbox.Settings, sandbox.Db, cache);
        TestAssert.True(planToB.CanExecute, "Switch A->B has no conflicts");
        TestAssert.Equal(1, planToB.SharedCount, "SharedMod stays in place (0 I/O)");
        TestAssert.Equal(4, planToB.DeactivateCount, "ProfileAOnly + OverrideA + KAVT + LooseData move to Vault");
        TestAssert.Equal(2, planToB.ActivateCount, "ProfileBOnly + Orig unique_tav activate from Vault");

        SwitchExecutionResult execToB = await ProfileSwitchService.ExecuteSwitchPlanAsync(planToB, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
        TestAssert.True(execToB.Success, "Switch A->B succeeded");

        // Verify SharedMod stayed in place untouched
        TestAssert.True(File.Exists(sharedPath), "SharedMod still active");
        TestAssert.Equal(sharedInitialWrite, File.GetLastWriteTimeUtc(sharedPath), "SharedMod timestamp unchanged");

        // Verify unique_tav.pak is now Original variant and KAVT is safely in Vault
        TestAssert.EqualIgnoreCase(origUniqueHash, await HashService.ComputeFileSha256Async(kavtActivePath), "Active unique_tav.pak is now Original variant");
        TestAssert.True(File.Exists(ModLibraryService.GetVaultPakPath(sandbox.Settings.VaultFolder, kavtMod)), "KAVT safely parked in Vault");

        // Verify LooseData moved to Vault, empty Custom dir pruned, protected Data/Generated/Public/Shared preserved!
        TestAssert.False(File.Exists(looseFile1), "LooseData file removed from Data/");
        TestAssert.False(Directory.Exists(customDir), "Empty Custom dir pruned");
        TestAssert.True(Directory.Exists(protectedSharedDir), "Protected Data/Generated/Public/Shared dir must be preserved");
        TestAssert.True(File.Exists(ModLibraryService.GetVaultTrackedFilePath(sandbox.Settings.VaultFolder, looseDataMod, looseDataMod.TrackedFiles[0])), "LooseData file parked in Vault");

        // Verify single-copy invariant across every mod
        AssertSingleCopyInvariant(sandbox);

        // Now switch B -> A and verify full restoration
        SwitchPlan planToA = await ProfileSwitchService.BuildSwitchPlanAsync(profileA, sandbox.Settings, sandbox.Db, cache);
        TestAssert.True(planToA.CanExecute, "Switch B->A has no conflicts");
        SwitchExecutionResult execToA = await ProfileSwitchService.ExecuteSwitchPlanAsync(planToA, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
        TestAssert.True(execToA.Success, "Switch B->A succeeded");

        TestAssert.EqualIgnoreCase(kavtHash, await HashService.ComputeFileSha256Async(kavtActivePath), "KAVT unique_tav.pak restored");
        TestAssert.True(File.Exists(looseFile1), "LooseData file restored to Data/");
        TestAssert.True(File.Exists(ovrAPath), "OverrideA restored");
        TestAssert.True(File.Exists(profAPath), "ProfileAOnly restored");
        TestAssert.False(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "ProfileBOnly.pak")), "ProfileBOnly moved back to Vault");
        AssertSingleCopyInvariant(sandbox);
    }

    private static async Task TestOverridesAndDataOnlySwitchModeAsync()
    {
        using var sandbox = new TempSandbox();
        sandbox.Settings.SwitchMode = SwitchMode.OverridesAndDataOnly;
        var cache = new PakMetadataCache();

        string normalPak = Path.Combine(sandbox.Settings.ModsFolder, "NormalExtra.pak");
        TestPakBuilder.CreatePakV18(normalPak, new[]
        {
            ("Mods/NormalExtra/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("NormalExtra", "22222222-1111-1111-1111-111111111111", "NormalExtra")), TestPakBuilder.PayloadCompression.None)
        });
        string overridePak = Path.Combine(sandbox.Settings.ModsFolder, "OverrideExtra.pak");
        TestPakBuilder.CreatePakV18(overridePak, new[]
        {
            ("Public/Shared/foo.lsx", "x"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);

        var emptyTargetProfile = new ModProfile { Name = "Clean Profile", ModIds = new List<string>() };
        sandbox.Db.Profiles.Add(emptyTargetProfile);

        SwitchPlan plan = await ProfileSwitchService.BuildSwitchPlanAsync(emptyTargetProfile, sandbox.Settings, sandbox.Db, cache);
        SwitchExecutionResult res = await ProfileSwitchService.ExecuteSwitchPlanAsync(plan, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
        TestAssert.True(res.Success, "OverridesAndDataOnly switch succeeded");

        TestAssert.True(File.Exists(normalPak), "Non-override PAK must stay in Mods/ under OverridesAndDataOnly mode");
        TestAssert.False(File.Exists(overridePak), "Override PAK must be moved to Vault under OverridesAndDataOnly mode");
    }

    private static async Task TestModSettingsLsxSyncWithBackupAsync()
    {
        using var sandbox = new TempSandbox();
        TestAssert.True(new AppSettings().SyncModSettingsLsxOnSwitch, "AppSettings.SyncModSettingsLsxOnSwitch defaults to true");
        TestAssert.True(sandbox.Settings.SyncModSettingsLsxOnSwitch, "Sandbox Settings inherits default SyncModSettingsLsxOnSwitch = true without manual flip");
        var cache = new PakMetadataCache();

        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.Settings.ModSettingsLsxPath)!);
        await File.WriteAllTextAsync(sandbox.Settings.ModSettingsLsxPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
              <version major="4" minor="8" revision="0" build="100" />
              <region id="ModuleSettings">
                <node id="root">
                  <children>
                    <node id="Mods">
                      <children>
                        <node id="ModuleShortDesc">
                          <attribute id="Folder" type="LSString" value="GustavX" />
                          <attribute id="MD5" type="LSString" value="abc" />
                          <attribute id="Name" type="LSString" value="GustavX" />
                          <attribute id="PublishHandle" type="uint64" value="0" />
                          <attribute id="UUID" type="guid" value="cb555efe-2d9e-131f-8195-a89329d218ea" />
                          <attribute id="Version64" type="int64" value="145241946983020728" />
                        </node>
                      </children>
                    </node>
                  </children>
                </node>
              </region>
            </save>
            """);

        string modUuid = "33333333-1111-1111-1111-111111111111";
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "SyncedMod.pak"), new[]
        {
            ("Mods/SyncedMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("SyncedMod", modUuid, "SyncedMod")), TestPakBuilder.PayloadCompression.None)
        });
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);
        ModEntry mod = sandbox.Db.Mods.Single();

        var profile = new ModProfile { Name = "LSX Profile", ModIds = new List<string> { mod.Id } };
        sandbox.Db.Profiles.Add(profile);

        SwitchPlan plan = await ProfileSwitchService.BuildSwitchPlanAsync(profile, sandbox.Settings, sandbox.Db, cache);
        SwitchExecutionResult res = await ProfileSwitchService.ExecuteSwitchPlanAsync(plan, sandbox.Settings, sandbox.Db, cache, sandbox.Store);
        TestAssert.True(res.Success, "Switch with default LSX sync succeeded");
        TestAssert.True(File.Exists(sandbox.Settings.ModSettingsLsxPath + ".bak"), "Backup .bak file must be created");

        List<ModSettingsEntry> lsxEntries = Bg3LoadOrderService.ReadModSettingsLsx(sandbox.Settings.ModSettingsLsxPath);
        TestAssert.Equal(1, lsxEntries.Count, "1 non-base mod written to modsettings.lsx by default");
        TestAssert.EqualIgnoreCase(modUuid, lsxEntries[0].Uuid, "Written UUID matches");

        // Verify one-time schema upgrade on legacy settings.json and respect for explicit uncheck on schema v2
        var store = new JsonStore(Path.Combine(sandbox.Root, "legacy_store"));
        Directory.CreateDirectory(store.DataDirectory);
        await File.WriteAllTextAsync(store.SettingsPath, """
            {
              "SyncModSettingsLsxOnSwitch": false
            }
            """);
        AppSettings upgraded = store.LoadSettings();
        TestAssert.True(upgraded.SyncModSettingsLsxOnSwitch, "Legacy settings.json without SettingsSchemaVersion upgrades SyncModSettingsLsxOnSwitch to true once");
        TestAssert.Equal(AppSettings.CurrentSettingsSchemaVersion, upgraded.SettingsSchemaVersion, "SettingsSchemaVersion upgraded to CurrentSettingsSchemaVersion");

        upgraded.SyncModSettingsLsxOnSwitch = false;
        store.SaveSettings(upgraded);
        AppSettings reloadedExplicitOff = store.LoadSettings();
        TestAssert.False(reloadedExplicitOff.SyncModSettingsLsxOnSwitch, "Explicit user opt-out on SettingsSchemaVersion 2 is preserved across reload");
    }

    private static void AssertSingleCopyInvariant(TempSandbox sandbox)
    {
        foreach (ModEntry mod in sandbox.Db.Mods)
        {
            if (!string.IsNullOrWhiteSpace(mod.PakFileName))
            {
                string activePak = ModLibraryService.GetActivePakPath(sandbox.Settings, mod);
                string vaultPak = ModLibraryService.GetVaultPakPath(sandbox.Settings.VaultFolder, mod);
                string expectedPakSha = mod.EffectivePakSha256 ?? mod.ContentHash;
                bool inActive = File.Exists(activePak) &&
                                string.Equals(HashService.ComputeFileSha256(activePak), expectedPakSha, StringComparison.OrdinalIgnoreCase);
                bool inVault = File.Exists(vaultPak);
                TestAssert.True(inActive ^ inVault, $"Mod '{mod.DisplayName}' must exist in EXACTLY one location (active={inActive}, vault={inVault}).");
            }

            foreach (TrackedFileRecord tf in mod.TrackedFiles)
            {
                string activeFile = ModLibraryService.GetActiveTrackedFilePath(sandbox.Settings, tf);
                string vaultFile = ModLibraryService.GetVaultTrackedFilePath(sandbox.Settings.VaultFolder, mod, tf);
                TestAssert.True(File.Exists(activeFile) ^ File.Exists(vaultFile), $"Tracked file '{tf.RelativePath}' must exist in EXACTLY one location.");
            }
        }
    }
}
