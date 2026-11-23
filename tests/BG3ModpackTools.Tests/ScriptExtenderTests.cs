using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.Tests;

internal static class ScriptExtenderTests
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
        TestResolveBinFolderAndStatusDetection();
        await TestFetchAndInstallFromZipAndForeignBackupAsync();
        await TestMissingDWriteZipAndProcessGuardAsync();
    }

    private static void TestResolveBinFolderAndStatusDetection()
    {
        using var sandbox = new TempSandbox();

        TestAssert.Equal(string.Empty, ScriptExtenderService.ResolveBinFolder(string.Empty, string.Empty), "Blank folders resolve to empty bin path");
        TestAssert.False(ScriptExtenderService.GetStatus(string.Empty, string.Empty).GameBinFolderAvailable, "Blank folders report GameBinFolderAvailable = false");

        string expectedBin = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(expectedBin);
        TestAssert.Equal(expectedBin, ScriptExtenderService.ResolveBinFolder(sandbox.Settings), "Game root resolves to Game\\bin");

        // Passing bin/ itself containing bg3_dx11.exe should not double-append \bin
        File.WriteAllBytes(Path.Combine(expectedBin, "bg3_dx11.exe"), "EXE"u8.ToArray());
        TestAssert.Equal(expectedBin, ScriptExtenderService.ResolveBinFolder(expectedBin, string.Empty), "bin folder with bg3_dx11.exe is not nested into bin\\bin");

        string fakeLocalAppData = Path.Combine(sandbox.Root, "LocalAppData");
        Directory.CreateDirectory(Path.Combine(fakeLocalAppData, "BG3ScriptExtender", "ScriptExtender", "22.0.0.0_abcdef"));
        Directory.CreateDirectory(Path.Combine(fakeLocalAppData, "BG3ScriptExtender", "ScriptExtender", "33.0.0.0_10efd553"));

        ScriptExtenderStatus emptyStatus = ScriptExtenderService.GetStatus(sandbox.Settings, fakeLocalAppData);
        TestAssert.True(emptyStatus.GameBinFolderAvailable, "Bin folder reported available");
        TestAssert.False(emptyStatus.IsInstalled, "Empty bin reports SE not installed");
        TestAssert.False(emptyStatus.IsForeignDWriteDll, "Empty bin reports no foreign DWrite.dll");
        TestAssert.Equal("v33", emptyStatus.RuntimeVersion, "Detects highest cached runtime version (v33)");

        // Foreign DWrite.dll in bin/
        string dwritePath = Path.Combine(expectedBin, "DWrite.dll");
        File.WriteAllBytes(dwritePath, "FOREIGN_DLL"u8.ToArray());
        ScriptExtenderStatus foreignStatus = ScriptExtenderService.GetStatus(sandbox.Settings, fakeLocalAppData);
        TestAssert.False(foreignStatus.IsInstalled, "Foreign DWrite.dll does not falsely report SE installed");
        TestAssert.True(foreignStatus.IsForeignDWriteDll, "Foreign DWrite.dll sets IsForeignDWriteDll");

        bool threwForeign = false;
        try
        {
            ScriptExtenderService.UninstallScriptExtender(sandbox.Settings);
        }
        catch (InvalidOperationException)
        {
            threwForeign = true;
        }
        TestAssert.True(threwForeign, "UninstallScriptExtender refuses to delete an unrelated foreign DWrite.dll");
        TestAssert.True(File.Exists(dwritePath), "Foreign DWrite.dll preserved on refused uninstall");
    }

    private static async Task TestFetchAndInstallFromZipAndForeignBackupAsync()
    {
        using var sandbox = new TempSandbox();
        string binFolder = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(binFolder);

        // Pre-existing non-SE DWrite.dll must be backed up as DWrite.dll.bg3mpm_orig
        string dwritePath = Path.Combine(binFolder, "DWrite.dll");
        byte[] oldForeignBytes = "OLD_FOREIGN_DWRITE"u8.ToArray();
        await File.WriteAllBytesAsync(dwritePath, oldForeignBytes);

        byte[] seDllBytes = "SYNTHETIC_NORBYTE_DWRITE_PAYLOAD_V33"u8.ToArray();
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry readme = zip.CreateEntry("README.txt");
                await using (Stream s = readme.Open())
                {
                    await s.WriteAsync("BG3SE"u8.ToArray());
                }

                ZipArchiveEntry dll = zip.CreateEntry("DWrite.dll");
                await using (Stream s = dll.Open())
                {
                    await s.WriteAsync(seDllBytes);
                }
            }

            zipBytes = ms.ToArray();
        }

        string apiUrl = "https://mock.github.local/repos/Norbyte/bg3se/releases/latest";
        string downloadUrl = "https://mock.github.local/download/v33/BG3SE-Updater-20261012.zip";
        string releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v33",
            name = "Script Extender (2026 Oct 12)",
            html_url = "https://github.com/Norbyte/bg3se/releases/tag/v33",
            assets = new object[]
            {
                new
                {
                    name = "OtherArchive.zip",
                    browser_download_url = "https://mock.github.local/download/other.zip",
                    size = 123
                },
                new
                {
                    name = "BG3SE-Updater-20261012.zip",
                    browser_download_url = downloadUrl,
                    size = zipBytes.Length
                }
            }
        });

        using var handler = new MockHttpMessageHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? string.Empty;
            if (url == apiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (url == downloadUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler);

        ScriptExtenderReleaseInfo release = await ScriptExtenderService.FetchLatestReleaseAsync(httpClient, apiUrl);
        TestAssert.Equal("v33", release.TagName, "Fetched tag name");
        TestAssert.Equal("BG3SE-Updater-20261012.zip", release.AssetName, "Selected BG3SE-Updater zip asset cleanly");
        TestAssert.Equal(downloadUrl, release.DownloadUrl, "Fetched asset download URL");
        TestAssert.Equal((long)zipBytes.Length, release.AssetSizeBytes, "Fetched asset size");

        ScriptExtenderInstallResult installResult = await ScriptExtenderService.DownloadAndInstallAsync(
            sandbox.Settings,
            release,
            httpClient: httpClient);

        TestAssert.Equal(dwritePath, installResult.InstalledDllPath, "Installed DWrite.dll path matches");
        TestAssert.True(!string.IsNullOrWhiteSpace(installResult.BackedUpForeignDllPath), "Recorded backup path for foreign DWrite.dll");
        TestAssert.True(File.Exists(installResult.BackedUpForeignDllPath!), "Foreign DWrite.dll backup exists");
        TestAssert.True((await File.ReadAllBytesAsync(installResult.BackedUpForeignDllPath!)).SequenceEqual(oldForeignBytes), "Foreign DWrite.dll backup preserved byte-for-byte");
        TestAssert.True((await File.ReadAllBytesAsync(dwritePath)).SequenceEqual(seDllBytes), "Extracted DWrite.dll matches release payload");
    }

    private static async Task TestMissingDWriteZipAndProcessGuardAsync()
    {
        using var sandbox = new TempSandbox();
        string binFolder = Path.Combine(sandbox.Settings.GameFolder, "bin");
        Directory.CreateDirectory(binFolder);

        // Zip missing DWrite.dll must throw InvalidDataException
        using var badZipStream = new MemoryStream();
        using (var zip = new ZipArchive(badZipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry e = zip.CreateEntry("NotDWrite.dll");
            await using Stream s = e.Open();
            await s.WriteAsync("NOPE"u8.ToArray());
        }

        badZipStream.Position = 0;
        bool threwMissingDll = false;
        try
        {
            await ScriptExtenderService.InstallFromZipStreamAsync(badZipStream, binFolder, "v33", "Test");
        }
        catch (InvalidDataException)
        {
            threwMissingDll = true;
        }
        TestAssert.True(threwMissingDll, "InstallFromZipStreamAsync rejects archives without DWrite.dll");

        // GameProcessGuard must block Script Extender installation while BG3 is running
        badZipStream.Position = 0;
        using IDisposable guard = GameProcessGuard.OverrideForTests(() =>
            new[] { new RunningGameProcess(4321, "bg3_dx11", "BG3 DirectX 11") });

        bool threwRunning = false;
        try
        {
            await ScriptExtenderService.InstallFromZipStreamAsync(badZipStream, binFolder, "v33", "Test");
        }
        catch (GameRunningException)
        {
            threwRunning = true;
        }
        TestAssert.True(threwRunning, "InstallFromZipStreamAsync blocks when BG3 is running");
    }
}
