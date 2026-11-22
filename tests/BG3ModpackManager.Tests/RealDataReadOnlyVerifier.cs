using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

internal static class RealDataReadOnlyVerifier
{
    public static async Task<int> VerifyAsync(string jsonPath, string modsFolder)
    {
        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"JSON not found: {jsonPath}");
            return 1;
        }

        if (!Directory.Exists(modsFolder))
        {
            Console.Error.WriteLine($"Mods folder not found: {modsFolder}");
            return 1;
        }

        string tempVault = Path.Combine(Path.GetTempPath(), "BG3MPM_ReadOnly_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string? bg3Root = Path.GetDirectoryName(modsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string lsxPath = !string.IsNullOrWhiteSpace(bg3Root)
                ? Path.Combine(bg3Root, "PlayerProfiles", "Public", "modsettings.lsx")
                : string.Empty;

            var settings = new AppSettings
            {
                ModsFolder = modsFolder,
                VaultFolder = tempVault,
                ModSettingsLsxPath = lsxPath
            };
            var db = new LibraryDatabase();
            var cache = new PakMetadataCache();

            LoadOrderAuditReport report = await LoadOrderReviewService.AuditLoadOrderAsync(jsonPath, settings, db, cache);

            Console.WriteLine($"JSON: {report.JsonFilePath}");
            Console.WriteLine($"JSON Total: {report.JsonTotalCount}, Active: {report.ActiveCount}, ActiveOverrides: {report.ActiveOverrideCount}, Bg3MmOverrides: {report.Bg3MmOverrideCount}, InVault: {report.InVaultCount}, Missing: {report.MissingCount}, UnlistedOverrides: {report.UnlistedOverrideCount}, Inactive(BG3MM): {report.UnlistedInactiveCount}");

            Console.WriteLine("--- Detected Override PAKs ---");
            foreach (LoadOrderReviewItem item in report.Items.Where(i => i.IsOverride))
            {
                Console.WriteLine($"  [{item.Status} / {item.OverrideKind}] {item.PakFileName} | Name='{item.PakName}' | Reasons={string.Join("; ", item.OverrideReasons)}");
            }

            Console.WriteLine("--- BG3MM Overrides & Inactive Mods in Mods/ ---");
            foreach (LoadOrderReviewItem item in report.Items.Where(i => i.Status is ReviewStatus.Bg3MmOverride or ReviewStatus.UnlistedOverride or ReviewStatus.UnlistedInactive))
            {
                Console.WriteLine($"  [{item.Status}] {item.PakFileName} | Name='{item.PakName}'");
            }

            TestAssert.Equal(65, report.JsonTotalCount, "Expected 65 entries in Better Combat and 2024PHB.json");
            TestAssert.Equal(0, report.MissingCount, "Expected 0 missing mods");
            TestAssert.Equal(65, report.ActiveCount + report.ActiveOverrideCount, "Expected all 65 JSON mods active");
            TestAssert.Equal(5, report.Items.Count(i => i.IsOverride), "Expected exact 5 override mods in Mods/");
            TestAssert.Equal(2, report.Bg3MmOverrideCount, "Expected HighlightEverything.pak + NoIntro as Bg3MmOverride");
            TestAssert.Equal(0, report.UnlistedOverrideCount, "Expected 0 UnlistedOverride");
            TestAssert.Equal(3, report.UnlistedInactiveCount, "Expected 3 BG3MM Inactive mods");
            TestAssert.False(
                report.Items.Any(i => string.Equals(i.PakFileName, "Valkrana's Undead Encounters.pak", StringComparison.OrdinalIgnoreCase) && i.IsOverride),
                "Valkrana's Undead Encounters.pak must NOT be falsely flagged as an override");

            if (File.Exists(lsxPath))
            {
                ModProfile captured = await ModLibraryService.CaptureActiveProfileFromModSettingsAsync(
                    "ReadOnlyCapture",
                    useModSettingsLsx: true,
                    includeStandaloneOverrides: true,
                    includeActiveLooseMods: true,
                    settings,
                    db,
                    cache);

                TestAssert.Equal(67, captured.ModIds.Count, "Expected 65 active modsettings mods + 2 BG3MM Standalone Overrides = 67 (excluding 3 BG3MM Inactive mods)");
                Console.WriteLine($"CaptureActiveProfileFromModSettingsAsync (in-memory): {captured.ModIds.Count} mods (excluded 3 BG3MM Inactive mods).");
            }

            Console.WriteLine("READ-ONLY REAL-DATA VERIFICATION PASSED.");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(tempVault)) Directory.Delete(tempVault, true); } catch { }
        }
    }

    public static async Task<int> VerifyDownloadsAsync(string downloadsFolder)
    {
        if (!Directory.Exists(downloadsFolder))
        {
            Console.Error.WriteLine($"Downloads folder not found: {downloadsFolder}");
            return 1;
        }

        string tempSandbox = Path.Combine(Path.GetTempPath(), "BG3MPM_VerifyDL_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var settings = new AppSettings
            {
                ModsFolder = Path.Combine(tempSandbox, "Mods"),
                GameFolder = Path.Combine(tempSandbox, "Game"),
                DataFolder = Path.Combine(tempSandbox, "Game", "Data"),
                VaultFolder = Path.Combine(tempSandbox, "Vault")
            };
            Directory.CreateDirectory(settings.ModsFolder);
            Directory.CreateDirectory(settings.DataFolder);
            Directory.CreateDirectory(settings.VaultFolder);

            var expectedTargets = new (string Prefix, ImportTargetKind ExpectedTarget)[]
            {
                ("WASD ", ImportTargetKind.GameFolder),
                ("Native Camera Tweaks", ImportTargetKind.GameFolder),
                ("NativeModLoader", ImportTargetKind.GameFolder),
                ("Mari's tattoo - replacer", ImportTargetKind.DataFolder),
                ("Mari's tattoo - Unique Tav", ImportTargetKind.DataFolder),
                ("Mari's makeup - replacer", ImportTargetKind.DataFolder),
                ("Mari's makeup - Unique Tav", ImportTargetKind.DataFolder)
            };

            string[] files = Directory.GetFiles(downloadsFolder);
            var db = new LibraryDatabase();

            foreach ((string prefix, ImportTargetKind expectedTarget) in expectedTargets)
            {
                string? archivePath = files.FirstOrDefault(f =>
                    Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                TestAssert.True(archivePath is not null, $"Missing archive starting with '{prefix}' in Downloads");

                ArchiveImportPlan plan = await ArchiveImportService.InspectDropAsync(archivePath!, settings, db);
                try
                {
                    var activeItems = plan.Items.Where(i => i.IsSelected && i.TargetKind != ImportTargetKind.Ignore).ToList();
                    TestAssert.True(activeItems.Count > 0, $"{Path.GetFileName(archivePath)} produced 0 active items");
                    TestAssert.True(activeItems.All(i => i.TargetKind == expectedTarget),
                        $"{Path.GetFileName(archivePath)} expected {expectedTarget}, got {string.Join(", ", activeItems.Select(x => x.TargetKind))}");

                    Console.WriteLine($"[DOWNLOAD OK] {Path.GetFileName(archivePath)} -> {string.Join(", ", activeItems.Select(i => $"{i.TargetKind}:{i.DestinationRelativePath} ({i.FileCount} files)"))}");
                }
                finally
                {
                    ArchiveImportService.CleanupStaging(plan);
                }
            }

            Console.WriteLine("READ-ONLY DOWNLOAD ARCHIVES VERIFICATION PASSED.");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(tempSandbox)) Directory.Delete(tempSandbox, true); } catch { }
        }
    }
}
