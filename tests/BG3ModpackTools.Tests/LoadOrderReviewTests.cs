using System.Text;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.Tests;

internal static class LoadOrderReviewTests
{
    public static async Task RunAllAsync()
    {
        using var sandbox = new TempSandbox();
        var cache = new PakMetadataCache();

        string mod1Uuid = "aaaaaaaa-0000-0000-0000-000000000001";
        string ovrUuid = "755a8a72-407f-4f0d-9a33-274ac0f0b53d";
        string vaultUuid = "bbbbbbbb-0000-0000-0000-000000000002";
        string missingUuid = "cccccccc-0000-0000-0000-000000000003";
        string uniqueTavUuid = "4cb0fd40-7212-4fe9-8733-9a8dbc6637ae";

        // Active normal mod
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "Normal.pak"), new[]
        {
            ("Mods/Normal/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("NormalMod", mod1Uuid, "Normal")), TestPakBuilder.PayloadCompression.None)
        });

        // Active listed merged override
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "MCM.pak"), new[]
        {
            ("Mods/BG3MCM/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("Mod Configuration Menu", ovrUuid, "BG3MCM")), TestPakBuilder.PayloadCompression.None),
            ("Mods/BG3MCM/ScriptExtender/Config.json", "cfg"u8.ToArray(), TestPakBuilder.PayloadCompression.None),
            ("Mods/MainUI/GUI/MainMenu.xaml", "ui"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        // BG3MM Standalone Overrides (HighlightEverything.pak + NoIntro.pak)
        string highlightPath = Path.Combine(sandbox.Settings.ModsFolder, "HighlightEverything.pak");
        TestPakBuilder.CreatePakV18(highlightPath, new[]
        {
            ("Public/Shared/RootTemplates/h.lsf", "hi"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "NoIntro.pak"), new[]
        {
            ("Mods/NoIntroPak/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("NoIntro", "7fa55404-7280-11ed-4c26-63116a161862", "NoIntro")), TestPakBuilder.PayloadCompression.None),
            ("Video/Splash.bk2", "vid"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        // Active BG3MM Inactive mod (standard mod sitting in Mods/, not in JSON or modsettings.lsx)
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "ExtraNormal.pak"), new[]
        {
            ("Mods/ExtraNormal/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("ExtraNormal", "dddddddd-0000-0000-0000-000000000004", "ExtraNormal")), TestPakBuilder.PayloadCompression.None)
        });

        // Active Original unique_tav.pak while JSON requests KAZSTRA KAVT (which is in Vault!)
        TestPakBuilder.CreatePakV18(Path.Combine(sandbox.Settings.ModsFolder, "unique_tav.pak"), new[]
        {
            ("Mods/unique_tav/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("unique_tav", uniqueTavUuid, "unique_tav")), TestPakBuilder.PayloadCompression.None),
            ("Public/unique_tav/Materials.lsx", "mat"u8.ToArray(), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/utav.lsf", "orig"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        // Vault mods: vaultUuid + KAZSTRA KAVT
        string vaultStage = Path.Combine(sandbox.Root, "vault_stage");
        TestPakBuilder.CreatePakV18(Path.Combine(vaultStage, "VaultMod.pak"), new[]
        {
            ("Mods/VaultMod/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("VaultMod", vaultUuid, "VaultMod")), TestPakBuilder.PayloadCompression.None)
        });
        TestPakBuilder.CreatePakV18(Path.Combine(vaultStage, "unique_tav.pak"), new[]
        {
            ("Mods/unique_tav/meta.lsx", Encoding.UTF8.GetBytes(TestPakBuilder.BuildMetaLsx("KAVT - KAZSTRA Virtual Tav", uniqueTavUuid, "unique_tav")), TestPakBuilder.PayloadCompression.None),
            ("Public/unique_tav/Materials.lsx", "kavt_mat"u8.ToArray(), TestPakBuilder.PayloadCompression.None),
            ("Public/Shared/kavt.lsf", "kavt"u8.ToArray(), TestPakBuilder.PayloadCompression.None)
        });

        await ModLibraryService.BulkImportFromBackupFolderAsync(vaultStage, sandbox.Settings, sandbox.Db, cache, moveInsteadOfCopy: true);

        string jsonPath = Path.Combine(sandbox.Settings.OrdersFolder, "TestOrder.json");
        Bg3LoadOrderService.SaveOrderJson(jsonPath, new[]
        {
            new Bg3OrderEntry { Uuid = mod1Uuid, Name = "NormalMod" },
            new Bg3OrderEntry { Uuid = ovrUuid, Name = "Mod Configuration Menu" },
            new Bg3OrderEntry { Uuid = vaultUuid, Name = "VaultMod" },
            new Bg3OrderEntry { Uuid = missingUuid, Name = "MissingMod" },
            new Bg3OrderEntry { Uuid = uniqueTavUuid, Name = "KAVT - KAZSTRA Virtual Tav" }
        });

        LoadOrderAuditReport report = await LoadOrderReviewService.AuditLoadOrderAsync(jsonPath, sandbox.Settings, sandbox.Db, cache);
        TestAssert.Equal(1, report.ActiveCount, "ActiveCount");
        TestAssert.Equal(1, report.ActiveOverrideCount, "ActiveOverrideCount");
        TestAssert.Equal(2, report.Bg3MmOverrideCount, "Bg3MmOverrideCount (HighlightEverything + NoIntro)");
        TestAssert.Equal(2, report.InVaultCount, "InVaultCount (VaultMod + KAVT matched by Name!)");
        TestAssert.Equal(1, report.MissingCount, "MissingCount");
        TestAssert.Equal(1, report.UnlistedOverrideCount, "UnlistedOverrideCount (Original unique_tav only)");
        TestAssert.Equal(1, report.UnlistedInactiveCount, "UnlistedInactiveCount (ExtraNormal)");

        LoadOrderReviewItem kavtRow = report.Items.First(i => i.JsonUuid == uniqueTavUuid);
        TestAssert.Equal(ReviewStatus.InVault, kavtRow.Status, "KAVT must be recognized as InVault rather than falsely satisfied by Original unique_tav");
        TestAssert.True(kavtRow.MultipleVariantsNote is not null, "MultipleVariantsNote populated");

        // Test QuarantineUnlistedPaksAsync (unlisted merged override only; leaves BG3MM Overrides & Inactive alone)
        int moved = await ModLibraryService.QuarantineUnlistedPaksAsync(report, includeUnlistedNonOverrides: false, sandbox.Settings, sandbox.Db, cache);
        TestAssert.Equal(1, moved, "1 unlisted merged override pak moved to Vault");
        TestAssert.True(File.Exists(highlightPath), "HighlightEverything (BG3MM Override) stays in Mods/ unless moved explicitly");
        TestAssert.False(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "unique_tav.pak")), "Original unique_tav moved out of Mods/");
        TestAssert.True(File.Exists(Path.Combine(sandbox.Settings.ModsFolder, "ExtraNormal.pak")), "ExtraNormal stayed in Mods/");

        // Test one-by-one AdoptAndMovePakToVaultAsync on HighlightEverything.pak
        ModEntry movedHighlight = await ModLibraryService.AdoptAndMovePakToVaultAsync(highlightPath, sandbox.Settings, sandbox.Db, cache);
        TestAssert.False(File.Exists(highlightPath), "HighlightEverything moved one-by-one out of Mods/");
        TestAssert.Equal(ModLocation.Vault, movedHighlight.Location, "HighlightEverything is now in Vault");

        // Test CaptureActiveProfileFromModSettingsAsync excludes BG3MM Inactive Mods (ExtraNormal.pak)
        await ModLibraryService.ScanAndAdoptActiveModsAsync(sandbox.Settings, sandbox.Db, cache);
        ModEntry normalMod = sandbox.Db.Mods.Single(m => m.Uuid == mod1Uuid);
        ModEntry mcmMod = sandbox.Db.Mods.Single(m => m.Uuid == ovrUuid);
        Bg3LoadOrderService.WriteModSettingsLsx(sandbox.Settings.ModSettingsLsxPath, new[] { normalMod, mcmMod });

        ModProfile captured = await ModLibraryService.CaptureActiveProfileFromModSettingsAsync(
            "Captured",
            useModSettingsLsx: true,
            includeStandaloneOverrides: true,
            includeActiveLooseMods: true,
            sandbox.Settings,
            sandbox.Db,
            cache);

        ModEntry extraNormal = sandbox.Db.Mods.Single(m => m.PakFileName == "ExtraNormal.pak");
        ModEntry noIntroMod = sandbox.Db.Mods.Single(m => m.PakFileName == "NoIntro.pak");
        TestAssert.False(captured.ModIds.Contains(extraNormal.Id), "Captured profile must exclude BG3MM Inactive mod (ExtraNormal.pak)");
        TestAssert.True(captured.ModIds.Contains(normalMod.Id), "Captured profile includes NormalMod");
        TestAssert.True(captured.ModIds.Contains(mcmMod.Id), "Captured profile includes MCM");
        TestAssert.True(captured.ModIds.Contains(noIntroMod.Id), "Captured profile includes active BG3MM Standalone Override (NoIntro.pak)");

        // Add active loose Data and Game mods, re-capture active profile, link a BG3MM JSON load order, and sync:
        string looseDataFile = Path.Combine(sandbox.Settings.DataFolder, "Generated", "Public", "Shared", "Assets", "TestTexture.DDS");
        Directory.CreateDirectory(Path.GetDirectoryName(looseDataFile)!);
        await File.WriteAllTextAsync(looseDataFile, "DDS-DATA");
        string looseDataSha = await HashService.ComputeFileSha256Async(looseDataFile);
        var looseDataMod = new ModEntry
        {
            Id = "loose-data-test01",
            DisplayName = "Mari Tattoos Loose Data",
            Kind = ModKind.LooseData,
            ContentHash = looseDataSha,
            Location = ModLocation.Active,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.DataFolder,
                    RelativePath = "Generated/Public/Shared/Assets/TestTexture.DDS",
                    Sha256 = looseDataSha,
                    FileSizeBytes = 8
                }
            }
        };
        sandbox.Db.Mods.Add(looseDataMod);

        string looseGameFile = Path.Combine(sandbox.Settings.GameFolder, "bin", "NativeMods", "BG3NativeCameraTweaks.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(looseGameFile)!);
        await File.WriteAllTextAsync(looseGameFile, "DLL-DATA");
        string looseGameSha = await HashService.ComputeFileSha256Async(looseGameFile);
        var looseGameMod = new ModEntry
        {
            Id = "loose-game-test02",
            DisplayName = "Native Camera Tweaks",
            Kind = ModKind.LooseGame,
            ContentHash = looseGameSha,
            Location = ModLocation.Active,
            TrackedFiles = new List<TrackedFileRecord>
            {
                new()
                {
                    Target = TrackedFileTarget.GameFolder,
                    RelativePath = "bin/NativeMods/BG3NativeCameraTweaks.dll",
                    Sha256 = looseGameSha,
                    FileSizeBytes = 8
                }
            }
        };
        sandbox.Db.Mods.Add(looseGameMod);

        ModProfile capturedWithLoose = await ModLibraryService.CaptureActiveProfileFromModSettingsAsync(
            "CapturedWithLoose",
            useModSettingsLsx: true,
            includeStandaloneOverrides: true,
            includeActiveLooseMods: true,
            sandbox.Settings,
            sandbox.Db,
            cache);

        TestAssert.Equal(capturedWithLoose.Id, sandbox.Db.ActiveProfileId, "CapturedWithLoose is ActiveProfileId");
        TestAssert.True(capturedWithLoose.ModIds.Contains(looseDataMod.Id), "CapturedWithLoose contains loose Data mod");
        TestAssert.True(capturedWithLoose.ModIds.Contains(looseGameMod.Id), "CapturedWithLoose contains loose Game mod");

        // Link a BG3MM JSON load order (which only lists normalMod + mcmMod, omitting standalone overrides & loose mods)
        string linkedJsonPath = Path.Combine(sandbox.Settings.OrdersFolder, "ActiveSetupOrder.json");
        Bg3LoadOrderService.SaveOrderJson(linkedJsonPath, new[]
        {
            new Bg3OrderEntry { Uuid = mod1Uuid, Name = "NormalMod" },
            new Bg3OrderEntry { Uuid = ovrUuid, Name = "BG3MCM" }
        });

        capturedWithLoose.LinkedOrderJsonPath = linkedJsonPath;
        ModLibraryService.LinkCompanionModsToOrderJson(sandbox.Db, linkedJsonPath, capturedWithLoose.ModIds);

        // Simulate clicking "Sync from Linked JSON" (which passes includeActiveStandaloneOverrides: false, includeActiveLooseMods: false)
        var (syncedProfile, _) = await ModLibraryService.CreateOrUpdateProfileFromJsonAsync(
            linkedJsonPath,
            capturedWithLoose.Name,
            includeActiveStandaloneOverrides: false,
            includeActiveLooseMods: false,
            sandbox.Settings,
            sandbox.Db,
            cache,
            existingProfile: capturedWithLoose);

        TestAssert.True(syncedProfile.ModIds.Contains(noIntroMod.Id), "Synced profile preserves standalone override (NoIntro.pak)");
        TestAssert.True(syncedProfile.ModIds.Contains(looseDataMod.Id), "Synced profile preserves loose Data mod at the end");
        TestAssert.True(syncedProfile.ModIds.Contains(looseGameMod.Id), "Synced profile preserves loose Game mod at the end");
        TestAssert.Equal(capturedWithLoose.Id, sandbox.Db.ActiveProfileId, "Synced profile remains recognized as ActiveProfileId");

        // Simulate a legacy profile that had its loose mods stripped before companion linking existed:
        sandbox.Db.JsonOrderCompanionModIds.Clear();
        capturedWithLoose.ModIds.RemoveAll(id => id == noIntroMod.Id || id == looseDataMod.Id || id == looseGameMod.Id);
        sandbox.Db.ActiveProfileId = null;

        ModLibraryService.ReconcileActiveProfileId(sandbox.Settings, sandbox.Db);
        TestAssert.True(capturedWithLoose.ModIds.Contains(noIntroMod.Id), "ReconcileActiveProfileId auto-recovers active standalone override");
        TestAssert.True(capturedWithLoose.ModIds.Contains(looseDataMod.Id), "ReconcileActiveProfileId auto-recovers active loose Data mod");
        TestAssert.True(capturedWithLoose.ModIds.Contains(looseGameMod.Id), "ReconcileActiveProfileId auto-recovers active loose Game mod");
        TestAssert.Equal(capturedWithLoose.Id, sandbox.Db.ActiveProfileId, "ReconcileActiveProfileId restores ActiveProfileId to matched profile");

        // Intentionally removing a loose mod and updating companion link must NOT re-add it on next sync:
        capturedWithLoose.ModIds.Remove(looseGameMod.Id);
        ModLibraryService.LinkCompanionModsToOrderJson(sandbox.Db, linkedJsonPath, capturedWithLoose.ModIds);

        await ModLibraryService.CreateOrUpdateProfileFromJsonAsync(
            linkedJsonPath,
            capturedWithLoose.Name,
            includeActiveStandaloneOverrides: false,
            includeActiveLooseMods: false,
            sandbox.Settings,
            sandbox.Db,
            cache,
            existingProfile: capturedWithLoose);

        TestAssert.True(capturedWithLoose.ModIds.Contains(looseDataMod.Id), "Remaining linked loose Data mod is kept after sync");
        TestAssert.False(capturedWithLoose.ModIds.Contains(looseGameMod.Id), "Intentionally removed loose Game mod is not re-added after sync");
    }
}
