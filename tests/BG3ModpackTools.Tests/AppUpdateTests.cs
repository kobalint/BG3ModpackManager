using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.Tests;

internal static class AppUpdateTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    public static async Task RunAllAsync()
    {
        TestVersionComparison();
        await TestFetchAndCheckForUpdateWithMockHttpAsync();
        await TestDownloadStageValidationAndScriptGenerationAsync();
        TestStartupPreferenceCachingAndPersistence();
    }

    private static void TestVersionComparison()
    {
        TestAssert.True(AppUpdateService.IsNewerVersion("1.0.0", "v1.0.1"), "v1.0.1 > 1.0.0");
        TestAssert.True(AppUpdateService.IsNewerVersion("v1.0.0", "1.0.1"), "1.0.1 > v1.0.0");
        TestAssert.True(AppUpdateService.IsNewerVersion("1.0.0", "v1.1"), "v1.1 > 1.0.0");
        TestAssert.False(AppUpdateService.IsNewerVersion("1.0.0", "v1.0.0"), "v1.0.0 == 1.0.0");
        TestAssert.False(AppUpdateService.IsNewerVersion("1.0.0", "1.0.0.0"), "1.0.0.0 == 1.0.0");
        TestAssert.False(AppUpdateService.IsNewerVersion("1.0.0", "v0.9.9"), "v0.9.9 < 1.0.0");
        TestAssert.False(AppUpdateService.IsNewerVersion("1.0.0", "v1.0.0-beta.1"), "prerelease not newer than stable");
        TestAssert.True(AppUpdateService.IsNewerVersion("1.0.0-beta.1", "v1.0.0"), "stable newer than prerelease");
    }

    private static async Task TestFetchAndCheckForUpdateWithMockHttpAsync()
    {
        string api404 = "https://mock.github.local/404";
        using (var client404 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            AppUpdateCheckResult res404 = await AppUpdateService.CheckForUpdateAsync("1.0.0", client404, api404);
            TestAssert.False(res404.UpdateAvailable, "404 returns UpdateAvailable = false cleanly");
            TestAssert.True(res404.LatestRelease is null, "404 returns null LatestRelease");
        }

        string apiUrl = "https://mock.github.local/latest";
        string releaseJsonSame = JsonSerializer.Serialize(new
        {
            tag_name = "v1.0.0",
            name = "Initial Release",
            body = "Release body",
            html_url = "https://github.com/kobalint/BG3ModpackTools/releases/tag/v1.0.0",
            assets = new object[]
            {
                new { name = "BG3ModpackTools.zip", browser_download_url = "https://mock.github.local/dl/app.zip", size = 100 }
            }
        });

        using (var clientSame = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJsonSame, Encoding.UTF8, "application/json")
        })))
        {
            AppUpdateCheckResult resSame = await AppUpdateService.CheckForUpdateAsync("1.0.0", clientSame, apiUrl);
            TestAssert.False(resSame.UpdateAvailable, "Same tag v1.0.0 is not newer than 1.0.0");
        }

        string releaseJsonNew = JsonSerializer.Serialize(new
        {
            tag_name = "v1.2.0",
            name = "BG3 Modpack Tools 1.2.0",
            body = "- Added self-updater\n- Bug fixes",
            html_url = "https://github.com/kobalint/BG3ModpackTools/releases/tag/v1.2.0",
            assets = new object[]
            {
                new { name = "checksums.txt", browser_download_url = "https://mock.github.local/dl/checksums.txt", size = 32 },
                new { name = "OtherHelper.zip", browser_download_url = "https://mock.github.local/dl/other.zip", size = 500 },
                new { name = "BG3ModpackTools.exe", browser_download_url = "https://mock.github.local/dl/app.exe", size = 900 },
                new { name = "BG3ModpackTools.zip", browser_download_url = "https://mock.github.local/dl/BG3ModpackTools.zip", size = 700 }
            }
        });

        using (var clientNew = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJsonNew, Encoding.UTF8, "application/json")
        })))
        {
            AppUpdateCheckResult resNew = await AppUpdateService.CheckForUpdateAsync("1.0.0", clientNew, apiUrl);
            TestAssert.True(resNew.UpdateAvailable, "v1.2.0 > 1.0.0");
            TestAssert.True(resNew.LatestRelease is not null, "LatestRelease populated");
            TestAssert.Equal("BG3ModpackTools.zip", resNew.LatestRelease!.AssetName, "Prioritizes BG3ModpackTools.zip over other assets");
            TestAssert.Equal("https://mock.github.local/dl/BG3ModpackTools.zip", resNew.LatestRelease.DownloadUrl, "Download URL matches");
            TestAssert.Equal("- Added self-updater\n- Bug fixes", resNew.LatestRelease.ReleaseNotes, "Release notes parsed");
        }
    }

    private static async Task TestDownloadStageValidationAndScriptGenerationAsync()
    {
        using var sandbox = new TempSandbox();
        string appDir = Path.Combine(sandbox.Root, "100%Portable");
        Directory.CreateDirectory(appDir);
        string targetExe = Path.Combine(appDir, "BG3ModpackTools.exe");
        await File.WriteAllBytesAsync(targetExe, "MZ_OLD_VERSION"u8.ToArray());

        byte[] newExeBytes = "MZ_NEW_VERSION_PAYLOAD_1_2_0"u8.ToArray();
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry readme = zip.CreateEntry("README.txt");
                await using (Stream s = readme.Open())
                {
                    await s.WriteAsync("notes"u8.ToArray());
                }

                ZipArchiveEntry exe = zip.CreateEntry("BG3ModpackTools.exe");
                await using (Stream s = exe.Open())
                {
                    await s.WriteAsync(newExeBytes);
                }
            }

            zipBytes = ms.ToArray();
        }

        string zipUrl = "https://mock.github.local/dl/BG3ModpackTools.zip";
        string exeUrl = "https://mock.github.local/dl/BG3ModpackTools.exe";
        string badUrl = "https://mock.github.local/dl/corrupt.exe";

        using var httpClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            string u = req.RequestUri?.ToString() ?? string.Empty;
            if (u == zipUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            }

            if (u == exeUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newExeBytes) };
            }

            if (u == badUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<html>not-an-exe</html>"u8.ToArray()) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        // 1. Zip download & staging
        var releaseZip = new AppReleaseInfo("v1.2.0", "Release 1.2.0", "", "BG3ModpackTools.zip", zipUrl, zipBytes.Length, AppUpdateService.ReleasesPageUrl);
        StagedAppUpdate stagedZip = await AppUpdateService.DownloadAndStageUpdateAsync(releaseZip, targetExe, httpClient: httpClient);
        try
        {
            TestAssert.True(File.Exists(stagedZip.StagedNewExePath), "Staged .new executable written next to target");
            TestAssert.True((await File.ReadAllBytesAsync(stagedZip.StagedNewExePath)).SequenceEqual(newExeBytes), "Staged executable matches zip entry bytes");

            string script = AppUpdateService.BuildWindowsReplaceScriptContent(4242, stagedZip);
            TestAssert.True(script.Contains("chcp 65001 >NUL"), "Replace script configures UTF-8 codepage");
            TestAssert.True(script.Contains("100%%Portable"), "Percent signs in paths are escaped for batch variables");
            TestAssert.True(script.Contains("set \"PID=4242\""), "Replace script embeds target PID");
            TestAssert.True(script.Contains("tasklist /FI \"PID eq %PID%\" /NH /FO CSV"), "Replace script polls PID exit");
            TestAssert.True(script.Contains("move /y \"%TARGET%\" \"%OLDFILE%\""), "Replace script backs up target before swap");
            TestAssert.True(script.Contains("start \"\" \"%TARGET%\""), "Replace script relaunches updated exe");
        }
        finally
        {
            if (Directory.Exists(stagedZip.StagingDirectory))
            {
                Directory.Delete(stagedZip.StagingDirectory, recursive: true);
            }
        }

        // Cleanup removes .new and .old artifacts cleanly
        await File.WriteAllBytesAsync(targetExe + ".old", "OLD"u8.ToArray());
        AppUpdateService.CleanupStaleUpdateArtifacts(targetExe);
        TestAssert.False(File.Exists(targetExe + ".new"), "CleanupStaleUpdateArtifacts removes .new");
        TestAssert.False(File.Exists(targetExe + ".old"), "CleanupStaleUpdateArtifacts removes .old");

        // 2. Direct .exe download & staging
        var releaseExe = new AppReleaseInfo("v1.2.0", "Release 1.2.0", "", "BG3ModpackTools.exe", exeUrl, newExeBytes.Length, AppUpdateService.ReleasesPageUrl);
        StagedAppUpdate stagedExe = await AppUpdateService.DownloadAndStageUpdateAsync(releaseExe, targetExe, httpClient: httpClient);
        try
        {
            TestAssert.True((await File.ReadAllBytesAsync(stagedExe.StagedNewExePath)).SequenceEqual(newExeBytes), "Direct .exe update staged with exact bytes");
        }
        finally
        {
            AppUpdateService.CleanupStaleUpdateArtifacts(targetExe);
            if (Directory.Exists(stagedExe.StagingDirectory))
            {
                Directory.Delete(stagedExe.StagingDirectory, recursive: true);
            }
        }

        // 3. Rejects non-PE corrupt download
        var releaseBad = new AppReleaseInfo("v1.2.0", "Bad", "", "BG3ModpackTools.exe", badUrl, 24, AppUpdateService.ReleasesPageUrl);
        bool threwInvalidPe = false;
        try
        {
            await AppUpdateService.DownloadAndStageUpdateAsync(releaseBad, targetExe, httpClient: httpClient);
        }
        catch (InvalidDataException)
        {
            threwInvalidPe = true;
        }
        TestAssert.True(threwInvalidPe, "Rejects non-MZ corrupt payload");
        TestAssert.False(File.Exists(targetExe + ".new"), "Failed staging cleans up .new");

        // 4. Rejects dotnet.exe host target
        bool threwDotnet = false;
        try
        {
            AppUpdateService.ValidateSelfUpdateTarget(@"C:\Program Files\dotnet\dotnet.exe");
        }
        catch (InvalidOperationException)
        {
            threwDotnet = true;
        }
        TestAssert.True(threwDotnet, "ValidateSelfUpdateTarget refuses overwriting dotnet.exe");
    }

    private static void TestStartupPreferenceCachingAndPersistence()
    {
        using var sandbox = new TempSandbox();
        TestAssert.True(new AppSettings().CheckUpdatesOnStartup, "CheckUpdatesOnStartup defaults to true");

        var store = new JsonStore(Path.Combine(sandbox.Root, "upd_store"));
        var settings = new AppSettings
        {
            ModsFolder = sandbox.Settings.ModsFolder,
            GameFolder = sandbox.Settings.GameFolder,
            DataFolder = sandbox.Settings.DataFolder,
            VaultFolder = sandbox.Settings.VaultFolder,
            CheckUpdatesOnStartup = false
        };
        store.SaveSettings(settings);

        AppSettings reloaded = store.LoadSettings();
        TestAssert.False(reloaded.CheckUpdatesOnStartup, "Cached declined CheckUpdatesOnStartup = false persists across reload");

        AppSettings afterDetect = PathAutoDetector.DetectDefaults(reloaded);
        TestAssert.False(afterDetect.CheckUpdatesOnStartup, "PathAutoDetector preserves CheckUpdatesOnStartup = false");
    }
}
