using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Current local installation status of Norbyte's Baldur's Gate 3 Script Extender (BG3SE).
/// </summary>
public sealed record ScriptExtenderStatus(
    bool GameBinFolderAvailable,
    string BinFolderPath,
    string DWriteDllPath,
    bool IsInstalled,
    bool IsForeignDWriteDll,
    string? UpdaterVersion,
    string? RuntimeVersion,
    long? FileSizeBytes);

/// <summary>
/// Metadata describing the latest BG3 Script Extender release on GitHub.
/// </summary>
public sealed record ScriptExtenderReleaseInfo(
    string TagName,
    string ReleaseName,
    string AssetName,
    string DownloadUrl,
    long AssetSizeBytes,
    string HtmlUrl);

/// <summary>
/// Result of downloading and extracting BG3 Script Extender's DWrite.dll into BG3\bin.
/// </summary>
public sealed record ScriptExtenderInstallResult(
    string InstalledDllPath,
    string ReleaseTag,
    string ReleaseName,
    string? UpdaterVersion,
    string? BackedUpForeignDllPath);

/// <summary>
/// Result of removing BG3 Script Extender's DWrite.dll from BG3\bin.
/// </summary>
public sealed record ScriptExtenderUninstallResult(
    bool Removed,
    string? RestoredBackupPath);

/// <summary>
/// Inspects, downloads, installs, and removes Norbyte's Baldur's Gate 3 Script Extender (DWrite.dll)
/// into the BG3 bin/ directory cleanly, matching BG3ModManager behavior.
/// </summary>
public static class ScriptExtenderService
{
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/Norbyte/bg3se/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/Norbyte/bg3se/releases/latest";
    public const string DWriteFileName = "DWrite.dll";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static string ResolveBinFolder(AppSettings settings)
        => ResolveBinFolder(settings.GameFolder, settings.DataFolder);

    public static string ResolveBinFolder(string? gameFolder, string? dataFolder)
    {
        string gameRoot = GameLauncherService.ResolveGameFolder(gameFolder, dataFolder);
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return string.Empty;
        }

        string trimmed = gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), "bin", StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(Path.Combine(trimmed, "bg3.exe")) || File.Exists(Path.Combine(trimmed, "bg3_dx11.exe"))))
        {
            return trimmed;
        }

        return Path.Combine(trimmed, "bin");
    }

    public static ScriptExtenderStatus GetStatus(AppSettings settings, string? localAppDataOverride = null)
        => GetStatus(settings.GameFolder, settings.DataFolder, localAppDataOverride);

    public static ScriptExtenderStatus GetStatus(
        string? gameFolder,
        string? dataFolder,
        string? localAppDataOverride = null)
    {
        string binFolder = ResolveBinFolder(gameFolder, dataFolder);
        string dwritePath = string.IsNullOrWhiteSpace(binFolder)
            ? string.Empty
            : Path.Combine(binFolder, DWriteFileName);
        bool binExists = !string.IsNullOrWhiteSpace(binFolder) && Directory.Exists(binFolder);

        bool isInstalled = false;
        bool isForeign = false;
        string? updaterVersion = null;
        long? fileSize = null;

        if (!string.IsNullOrWhiteSpace(dwritePath) && File.Exists(dwritePath))
        {
            (isInstalled, isForeign, updaterVersion, fileSize) = InspectDWriteDll(dwritePath);
        }

        string? runtimeVersion = DetectLatestCachedRuntimeVersion(localAppDataOverride);

        return new ScriptExtenderStatus(
            GameBinFolderAvailable: binExists,
            BinFolderPath: binFolder,
            DWriteDllPath: dwritePath,
            IsInstalled: isInstalled,
            IsForeignDWriteDll: isForeign,
            UpdaterVersion: updaterVersion,
            RuntimeVersion: runtimeVersion,
            FileSizeBytes: fileSize);
    }

    public static async Task<ScriptExtenderReleaseInfo> FetchLatestReleaseAsync(
        HttpClient? httpClient = null,
        string? apiUrl = null,
        CancellationToken cancellationToken = default)
    {
        HttpClient client = httpClient ?? SharedHttpClient;
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl ?? LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("BG3ModpackTools/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string tagName = GetStringProp(root, "tag_name") ?? "latest";
        string releaseName = GetStringProp(root, "name") ?? tagName;
        string htmlUrl = GetStringProp(root, "html_url") ?? ReleasesPageUrl;

        if (!root.TryGetProperty("assets", out JsonElement assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Latest BG3 Script Extender release metadata does not contain any assets.");
        }

        ScriptExtenderReleaseInfo? fallbackZip = null;
        foreach (JsonElement asset in assetsEl.EnumerateArray())
        {
            string? assetName = GetStringProp(asset, "name");
            string? downloadUrl = GetStringProp(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(assetName) ||
                string.IsNullOrWhiteSpace(downloadUrl) ||
                !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long sizeBytes = asset.TryGetProperty("size", out JsonElement sizeEl) && sizeEl.TryGetInt64(out long s)
                ? s
                : 0;

            var candidate = new ScriptExtenderReleaseInfo(
                TagName: tagName,
                ReleaseName: releaseName,
                AssetName: assetName,
                DownloadUrl: downloadUrl,
                AssetSizeBytes: sizeBytes,
                HtmlUrl: htmlUrl);

            if (assetName.Contains("BG3SE-Updater", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            fallbackZip ??= candidate;
        }

        return fallbackZip ?? throw new InvalidOperationException(
            $"No .zip asset found in BG3 Script Extender release '{tagName}'.");
    }

    public static async Task<ScriptExtenderInstallResult> InstallFromZipStreamAsync(
        Stream zipStream,
        string binFolder,
        string releaseTag,
        string releaseName,
        CancellationToken cancellationToken = default)
    {
        GameProcessGuard.EnsureGameNotRunning("installing BG3 Script Extender");

        if (string.IsNullOrWhiteSpace(binFolder) || !Directory.Exists(binFolder))
        {
            throw new DirectoryNotFoundException($"BG3 bin folder not found: '{binFolder}'.");
        }

        string? tmpPath = null;
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            ZipArchiveEntry? dllEntry = archive.Entries.FirstOrDefault(e =>
            {
                if (string.IsNullOrEmpty(e.Name))
                {
                    return false;
                }

                string normalized = e.FullName.Replace('\\', '/');
                return string.Equals(Path.GetFileName(normalized), DWriteFileName, StringComparison.OrdinalIgnoreCase);
            });

            if (dllEntry is null)
            {
                throw new InvalidDataException($"The Script Extender archive did not contain '{DWriteFileName}'.");
            }

            tmpPath = Path.Combine(binFolder, $"{DWriteFileName}.bg3tmp_{Guid.NewGuid():N}");
            await using (Stream entryStream = dllEntry.Open())
            await using (FileStream outStream = new(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await entryStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
            }

            string targetPath = Path.Combine(binFolder, DWriteFileName);
            string? backedUpForeignDllPath = null;
            if (File.Exists(targetPath))
            {
                (bool isSe, _, _, _) = InspectDWriteDll(targetPath);
                if (!isSe)
                {
                    string backupPath = FileTransactionHelper.GenerateUniqueBackupPath(targetPath);
                    FileTransactionHelper.SafeMoveFile(targetPath, backupPath);
                    backedUpForeignDllPath = backupPath;
                }
            }

            File.Move(tmpPath, targetPath, overwrite: true);
            tmpPath = null;

            (_, _, string? updaterVersion, _) = InspectDWriteDll(targetPath);
            return new ScriptExtenderInstallResult(
                InstalledDllPath: targetPath,
                ReleaseTag: releaseTag,
                ReleaseName: releaseName,
                UpdaterVersion: updaterVersion,
                BackedUpForeignDllPath: backedUpForeignDllPath);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tmpPath) && File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    public static async Task<ScriptExtenderInstallResult> DownloadAndInstallAsync(
        AppSettings settings,
        ScriptExtenderReleaseInfo? release = null,
        IProgress<string>? progress = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        GameProcessGuard.EnsureGameNotRunning("installing BG3 Script Extender");

        string binFolder = ResolveBinFolder(settings);
        if (string.IsNullOrWhiteSpace(binFolder) || !Directory.Exists(binFolder))
        {
            throw new DirectoryNotFoundException($"BG3 bin folder not found: '{binFolder}'. Configure the BG3 Game Folder in Settings.");
        }

        if (release is null)
        {
            progress?.Report("Checking latest BG3 Script Extender release on GitHub...");
            release = await FetchLatestReleaseAsync(httpClient, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        HttpClient client = httpClient ?? SharedHttpClient;
        string tempZip = Path.Combine(Path.GetTempPath(), $"BG3SE_{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Report($"Downloading {release.AssetName} ({release.TagName})...");
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl))
            {
                request.Headers.UserAgent.ParseAdd("BG3ModpackTools/1.0");
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream netStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream fileStream = new(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await netStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report($"Extracting {DWriteFileName} to {binFolder}...");
            await using FileStream zipReadStream = new(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return await InstallFromZipStreamAsync(
                zipReadStream,
                binFolder,
                release.TagName,
                release.ReleaseName,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    public static ScriptExtenderUninstallResult UninstallScriptExtender(AppSettings settings)
    {
        GameProcessGuard.EnsureGameNotRunning("removing BG3 Script Extender");

        string binFolder = ResolveBinFolder(settings);
        if (string.IsNullOrWhiteSpace(binFolder))
        {
            return new ScriptExtenderUninstallResult(false, null);
        }

        string dwritePath = Path.Combine(binFolder, DWriteFileName);
        if (!File.Exists(dwritePath))
        {
            return new ScriptExtenderUninstallResult(false, null);
        }

        (bool isSe, _, _, _) = InspectDWriteDll(dwritePath);
        if (!isSe)
        {
            throw new InvalidOperationException(
                $"'{dwritePath}' does not appear to be BG3 Script Extender. Refusing to remove an unrelated DWrite.dll.");
        }

        File.Delete(dwritePath);

        string? restoredBackupPath = null;
        string origBackupPath = dwritePath + ".bg3mpm_orig";
        if (File.Exists(origBackupPath))
        {
            FileTransactionHelper.SafeMoveFile(origBackupPath, dwritePath);
            restoredBackupPath = dwritePath;
        }

        return new ScriptExtenderUninstallResult(true, restoredBackupPath);
    }

    private static (bool IsInstalled, bool IsForeign, string? UpdaterVersion, long? FileSize) InspectDWriteDll(string dwritePath)
    {
        long? size = null;
        try
        {
            size = new FileInfo(dwritePath).Length;
        }
        catch
        {
        }

        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(dwritePath);
            if (IsScriptExtenderVersionInfo(fvi))
            {
                string? version = string.IsNullOrWhiteSpace(fvi.FileVersion) ? null : fvi.FileVersion.Trim();
                return (true, false, version, size);
            }

            return (false, true, null, size);
        }
        catch (IOException)
        {
            // Locked by running BG3 process: treat existing DWrite.dll as installed rather than foreign.
            return (true, false, null, size);
        }
        catch (UnauthorizedAccessException)
        {
            return (true, false, null, size);
        }
        catch
        {
            return (false, true, null, size);
        }
    }

    private static bool IsScriptExtenderVersionInfo(FileVersionInfo fvi)
    {
        return (fvi.ProductName?.Contains("Script Extender", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (fvi.FileDescription?.Contains("Script Extender", StringComparison.OrdinalIgnoreCase) ?? false) ||
               string.Equals(fvi.CompanyName?.Trim(), "Norbyte", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectLatestCachedRuntimeVersion(string? localAppDataOverride)
    {
        try
        {
            string localAppData = string.IsNullOrWhiteSpace(localAppDataOverride)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localAppDataOverride;

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return null;
            }

            string seCacheDir = Path.Combine(localAppData, "BG3ScriptExtender", "ScriptExtender");
            if (!Directory.Exists(seCacheDir))
            {
                return null;
            }

            Version? highest = null;
            foreach (string subDir in Directory.EnumerateDirectories(seCacheDir))
            {
                string name = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                int sep = name.IndexOf('_');
                string verPart = sep > 0 ? name[..sep] : name;
                if (Version.TryParse(verPart, out Version? parsed) &&
                    (highest is null || parsed > highest))
                {
                    highest = parsed;
                }
            }

            return highest is not null ? $"v{highest.Major}" : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProp(JsonElement element, string propName)
    {
        return element.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
