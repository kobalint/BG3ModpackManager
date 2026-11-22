using System.Text;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Tests;

internal static class LspkReaderTests
{
    public static void RunAll()
    {
        TestLz4MatchAndOverlapDecompression();
        TestV18CompressionFormatsAndVersionDecoding();
        TestV16FormatParsing();
        TestOverrideDetectionRules();
    }

    private static void TestLz4MatchAndOverlapDecompression()
    {
        // Encodes "A" literal + overlapping match (offset=1, matchLen=10 -> "AAAAAAAAAAA", total 11 bytes)
        // Token: 0x10 (1 literal, matchLen = 0+4 = 4? Wait: 1 literal + matchLen = 6+4 = 10 -> token = 0x16)
        byte[] encoded = new byte[] { 0x16, (byte)'A', 0x01, 0x00 };
        byte[] decoded = Lz4BlockDecoder.Decompress(encoded, 11);
        TestAssert.Equal("AAAAAAAAAAA", Encoding.ASCII.GetString(decoded), "LZ4 overlapping match decoding");
    }

    private static void TestV18CompressionFormatsAndVersionDecoding()
    {
        string temp = CreateTempDir();
        try
        {
            foreach (TestPakBuilder.PayloadCompression comp in Enum.GetValues<TestPakBuilder.PayloadCompression>())
            {
                string pakPath = Path.Combine(temp, $"test_{comp}.pak");
                string uuid = "26922ba9-6018-5252-075d-7ff2ba6ed879";
                string meta = TestPakBuilder.BuildMetaLsx(
                    name: $"TestMod_{comp}",
                    uuid: uuid,
                    folder: "TestModFolder",
                    version64: "72057594037927986", // 2.0.0.50
                    author: "BaldurTester");

                TestPakBuilder.CreatePakV18(pakPath, new[]
                {
                    ("Mods/TestModFolder/meta.lsx", Encoding.UTF8.GetBytes(meta), comp),
                    ("Public/TestModFolder/RootTemplates/sample.lsx", Encoding.UTF8.GetBytes("<save/>"), comp)
                });

                PakInfo info = LspkPakReader.ReadPakInfo(pakPath);
                TestAssert.True(info.ParseError is null, $"v18 {comp} should parse without error: {info.ParseError}");
                TestAssert.Equal((uint)18, info.LspkVersion, "LSPK version");
                TestAssert.True(info.HasMetaLsx, "HasMetaLsx");
                TestAssert.Equal($"TestMod_{comp}", info.Name, "Mod Name");
                TestAssert.EqualIgnoreCase(uuid, info.Uuid, "Mod UUID");
                TestAssert.Equal("TestModFolder", info.Folder, "Mod Folder");
                TestAssert.Equal("2.0.0.50", info.VersionString, "Version64 decoded string");
                TestAssert.Equal("BaldurTester", info.Author, "Author");
                TestAssert.False(info.IsDetectedOverride, $"Standard mod ({comp}) should not be flagged as override");
            }
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    private static void TestV16FormatParsing()
    {
        string temp = CreateTempDir();
        try
        {
            string pakPath = Path.Combine(temp, "legacy_v16.pak");
            string meta = TestPakBuilder.BuildMetaLsx("LegacyMod", "11111111-2222-3333-4444-555555555555", "LegacyMod", "36028799166447617");
            TestPakBuilder.CreatePakV16(pakPath, new[]
            {
                ("Mods/LegacyMod/meta.lsx", Encoding.UTF8.GetBytes(meta), TestPakBuilder.PayloadCompression.Zlib)
            });

            PakInfo info = LspkPakReader.ReadPakInfo(pakPath);
            TestAssert.True(info.ParseError is null, $"v16 should parse without error: {info.ParseError}");
            TestAssert.Equal((uint)16, info.LspkVersion, "LSPK v16");
            TestAssert.Equal("LegacyMod", info.Name, "v16 Name");
            TestAssert.Equal("1.0.1.1", info.VersionString, "v16 VersionString");
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    private static void TestOverrideDetectionRules()
    {
        string temp = CreateTempDir();
        try
        {
            // 1. Custom icons under Public/Game/GUI/Assets/... should NOT trigger override false positive
            string iconPak = Path.Combine(temp, "ValkranaLike.pak");
            string meta1 = TestPakBuilder.BuildMetaLsx("CustomIconsMod", "aaaaaaaa-1111-1111-1111-111111111111", "CustomIcons");
            TestPakBuilder.CreatePakV18(iconPak, new[]
            {
                ("Mods/CustomIcons/meta.lsx", Encoding.UTF8.GetBytes(meta1), TestPakBuilder.PayloadCompression.None),
                ("Public/Game/GUI/Assets/Tooltips/Icons/sword.dds", new byte[] { 1, 2, 3 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo iconInfo = LspkPakReader.ReadPakInfo(iconPak);
            TestAssert.False(iconInfo.IsDetectedOverride, "Public/Game/GUI/Assets icon mod must NOT be flagged as override");
            TestAssert.Equal(OverrideKind.None, iconInfo.OverrideKind, "Icon mod OverrideKind must be None");

            // 2. Generated/Public/Shared/ alone sets HasGeneratedSharedAssets without forcing override
            string genPak = Path.Combine(temp, "GeneratedOnly.pak");
            string meta2 = TestPakBuilder.BuildMetaLsx("GeneratedPatch", "bbbbbbbb-1111-1111-1111-111111111111", "GeneratedPatch");
            TestPakBuilder.CreatePakV18(genPak, new[]
            {
                ("Mods/GeneratedPatch/meta.lsx", Encoding.UTF8.GetBytes(meta2), TestPakBuilder.PayloadCompression.None),
                ("Generated/Public/Shared/Assets/unique_tav/scar.dds", new byte[] { 1, 2 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo genInfo = LspkPakReader.ReadPakInfo(genPak);
            TestAssert.False(genInfo.IsDetectedOverride, "Generated/Public/Shared alone must NOT flag IsDetectedOverride");
            TestAssert.Equal(OverrideKind.None, genInfo.OverrideKind, "Generated/Public/Shared alone OverrideKind must be None");
            TestAssert.True(genInfo.HasGeneratedSharedAssets, "HasGeneratedSharedAssets must be true");

            // 3. No meta.lsx => StandaloneOverride (HighlightEverything pattern)
            string noMetaPak = Path.Combine(temp, "HighlightEverything.pak");
            TestPakBuilder.CreatePakV18(noMetaPak, new[]
            {
                ("Public/Shared/RootTemplates/foo.lsf", new byte[] { 9, 9 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo noMetaInfo = LspkPakReader.ReadPakInfo(noMetaPak);
            TestAssert.True(noMetaInfo.IsDetectedOverride, "No meta.lsx + Public/Shared must flag override");
            TestAssert.Equal(OverrideKind.StandaloneOverride, noMetaInfo.OverrideKind, "HighlightEverything must be StandaloneOverride");

            // 4. Meta.lsx + Public/Shared + mod-own files => MergedOverride (Lifelike pattern)
            string sharedPak = Path.Combine(temp, "Lifelike.pak");
            string meta4 = TestPakBuilder.BuildMetaLsx("ASTRL Lifelike", "84199668-9978-4142-86b7-efba18e9b557", "ASTRL_Lifelike");
            TestPakBuilder.CreatePakV18(sharedPak, new[]
            {
                ("Mods/ASTRL_Lifelike/meta.lsx", Encoding.UTF8.GetBytes(meta4), TestPakBuilder.PayloadCompression.None),
                ("Public/Shared/CharacterCreationPresets/SkinColors.lsx", new byte[] { 4 }, TestPakBuilder.PayloadCompression.None),
                ("Public/ASTRL_Lifelike/Content/Skin.lsx", new byte[] { 8 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo sharedInfo = LspkPakReader.ReadPakInfo(sharedPak);
            TestAssert.True(sharedInfo.IsDetectedOverride, "Public/Shared must flag override");
            TestAssert.Equal(OverrideKind.MergedOverride, sharedInfo.OverrideKind, "ASTRL Lifelike must be MergedOverride");

            // 5. Meta.lsx + Mods/MainUI + Public/Game/Credits + BG3MCM files => MergedOverride (MCM pattern)
            string mcmPak = Path.Combine(temp, "MCM.pak");
            string meta5 = TestPakBuilder.BuildMetaLsx("Mod Configuration Menu", "755a8a72-407f-4f0d-9a33-274ac0f0b53d", "BG3MCM");
            TestPakBuilder.CreatePakV18(mcmPak, new[]
            {
                ("Mods/BG3MCM/meta.lsx", Encoding.UTF8.GetBytes(meta5), TestPakBuilder.PayloadCompression.None),
                ("Mods/BG3MCM/ScriptExtender/Config.json", new byte[] { 1 }, TestPakBuilder.PayloadCompression.None),
                ("Mods/MainUI/GUI/Pages/MainMenu.xaml", new byte[] { 5 }, TestPakBuilder.PayloadCompression.None),
                ("Public/Game/Credits/credits.txt", new byte[] { 6 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo mcmInfo = LspkPakReader.ReadPakInfo(mcmPak);
            TestAssert.True(mcmInfo.IsDetectedOverride, "Mods/MainUI + Public/Game/Credits must flag override");
            TestAssert.Equal(OverrideKind.MergedOverride, mcmInfo.OverrideKind, "MCM must be MergedOverride");

            // 6. Meta.lsx + Video/ + Public/Shared/GUI/placeholder.txt (no mod-own files) => StandaloneOverride (NoIntro pattern)
            string videoPak = Path.Combine(temp, "NoIntro.pak");
            string meta6 = TestPakBuilder.BuildMetaLsx("NoIntro", "7fa55404-7280-11ed-4c26-63116a161862", "NoIntro");
            TestPakBuilder.CreatePakV18(videoPak, new[]
            {
                ("Mods/NoIntroPak/meta.lsx", Encoding.UTF8.GetBytes(meta6), TestPakBuilder.PayloadCompression.None),
                ("Public/Shared/GUI/placeholder.txt", new byte[] { 2 }, TestPakBuilder.PayloadCompression.None),
                ("Video/Splash_Logo_Larian.bk2", new byte[] { 7 }, TestPakBuilder.PayloadCompression.None)
            });
            PakInfo videoInfo = LspkPakReader.ReadPakInfo(videoPak);
            TestAssert.True(videoInfo.IsDetectedOverride, "Video/ must flag override");
            TestAssert.Equal(OverrideKind.StandaloneOverride, videoInfo.OverrideKind, "NoIntro must be StandaloneOverride");
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "BG3MPM_LspkTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
