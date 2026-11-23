using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Metadata for a published BG3ModpackTools release on GitHub.
/// </summary>
public sealed record AppReleaseInfo(
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string? AssetName,
    string? DownloadUrl,
    long AssetSizeBytes,
    string HtmlUrl);

/// <summary>
/// Result of comparing the running BG3ModpackTools version against the latest GitHub release.
/// </summary>
public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    AppReleaseInfo? LatestRelease,
    bool UpdateAvailable);

/// <summary>
/// Validated staged update payload ready for executable replacement upon app shutdown.
/// </summary>
public sealed record StagedAppUpdate(
    string TargetExePath,
    string StagedNewExePath,
    string StagingDirectory,
    string ReleaseTag);

/// <summary>
/// Checks GitHub Releases for newer versions of BG3ModpackTools, downloads and validates
/// portable executables/archives, and launches a hidden self-replace script after shutdown.
/// </summary>
public static class AppUpdateService
{
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/kobalint/BG3ModpackTools/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/kobalint/BG3ModpackTools/releases/latest";
    public const string ExecutableFileName = "BG3ModpackTools.exe";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static string GetCurrentAppVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AppUpdateService).Assembly;
        string? infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            string cleaned = (plusIdx > 0 ? infoVer[..plusIdx] : infoVer).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        Version? asmVer = assembly.GetName().Version;
        if (asmVer is not null)
        {
            return asmVer.Revision > 0
                ? $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}.{asmVer.Revision}"
                : $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}";
        }

        return "1.0.0";
    }

    public static bool TryParseNormalizedVersion(string? raw, out Version version, out bool isPrerelease)
    {
        version = new Version(0, 0, 0, 0);
        isPrerelease = false;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..].Trim();
        }

        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
        {
            s = s[..plusIdx];
        }

        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            isPrerelease = dashIdx + 1 < s.Length;
            s = s[..dashIdx];
        }

        if (!Version.TryParse(s, out Version? parsed))
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        return true;
    }

    public static bool IsNewerVersion(string currentVersion, string candidateTagOrVersion)
    {
        if (!TryParseNormalizedVersion(currentVersion, out Version curVer, out bool curPre) ||
            !TryParseNormalizedVersion(candidateTagOrVersion, out Version candVer, out bool candPre))
        {
            return false;
        }

        int cmp = candVer.CompareTo(curVer);
        if (cmp != 0)
        {
            return cmp > 0;
        }

        return curPre && !candPre;
    }

    public static async Task<AppReleaseInfo?> FetchLatestReleaseAsync(
        HttpClient? httpClient = null,
        string? apiUrl = null,
        CancellationToken cancellationToken = default)
    {
        HttpClient client = httpClient ?? SharedHttpClient;
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl ?? LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("BG3ModpackTools/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string tagName = GetStringProp(root, "tag_name") ?? "0.0.0";
        string releaseName = GetStringProp(root, "name") ?? tagName;
        string releaseNotes = GetStringProp(root, "body") ?? string.Empty;
        string htmlUrl = GetStringProp(root, "html_url") ?? ReleasesPageUrl;

        string? bestAssetName = null;
        string? bestDownloadUrl = null;
        long bestSizeBytes = 0;
        int bestPriority = int.MaxValue;

        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsEl.EnumerateArray())
            {
                string? name = GetStringProp(asset, "name");
                string? url = GetStringProp(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                bool isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                bool isExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (!isZip && !isExe)
                {
                    continue;
                }

                bool matchesAppName = name.Contains("BG3ModpackTools", StringComparison.OrdinalIgnoreCase);
                int priority = (matchesAppName, isZip, isExe) switch
                {
                    (true, true, _) => 0,
                    (true, _, true) => 1,
                    (false, true, _) => 2,
                    _ => 3
                };

                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestAssetName = name;
                    bestDownloadUrl = url;
                    bestSizeBytes = asset.TryGetProperty("size", out JsonElement sizeEl) && sizeEl.TryGetInt64(out long s)
                        ? s
                        : 0;
                }
            }
        }

        return new AppReleaseInfo(
            TagName: tagName,
            ReleaseName: releaseName,
            ReleaseNotes: releaseNotes,
            AssetName: bestAssetName,
            DownloadUrl: bestDownloadUrl,
            AssetSizeBytes: bestSizeBytes,
            HtmlUrl: htmlUrl);
    }

    public static async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        string? currentVersionOverride = null,
        HttpClient? httpClient = null,
        string? apiUrl = null,
        CancellationToken cancellationToken = default)
    {
        string current = string.IsNullOrWhiteSpace(currentVersionOverride)
            ? GetCurrentAppVersion()
            : currentVersionOverride.Trim();

        AppReleaseInfo? latest = await FetchLatestReleaseAsync(httpClient, apiUrl, cancellationToken).ConfigureAwait(false);
        bool available = latest is not null && IsNewerVersion(current, latest.TagName);
        return new AppUpdateCheckResult(current, latest, available);
    }

    public static void ValidateSelfUpdateTarget(string? targetExePath)
    {
        if (string.IsNullOrWhiteSpace(targetExePath))
        {
            throw new InvalidOperationException("Cannot resolve the target executable path for self-updating.");
        }

        string trimmed = targetExePath.Trim();
        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Self-update target must be a .exe file: '{trimmed}'.");
        }

        if (string.Equals(Path.GetFileName(trimmed), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Self-update cannot overwrite dotnet.exe when running via 'dotnet run'. Download the release from GitHub instead.");
        }
    }

    public static async Task<StagedAppUpdate> DownloadAndStageUpdateAsync(
        AppReleaseInfo release,
        string? targetExePathOverride = null,
        IProgress<string>? progress = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        string? rawTarget = targetExePathOverride ?? Environment.ProcessPath;
        ValidateSelfUpdateTarget(rawTarget);
        string targetExe = Path.GetFullPath(rawTarget!.Trim());

        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            throw new InvalidOperationException($"Release '{release.TagName}' does not have a downloadable .zip or .exe asset.");
        }

        string stagingDir = Path.Combine(Path.GetTempPath(), $"BG3ModpackTools_Update_{Guid.NewGuid():N}");
        string stagedNewExe = targetExe + ".new";
        Directory.CreateDirectory(stagingDir);

        try
        {
            string downloadName = string.IsNullOrWhiteSpace(release.AssetName)
                ? "BG3ModpackTools_update.bin"
                : Path.GetFileName(release.AssetName);
            string downloadedFile = Path.Combine(stagingDir, downloadName);

            progress?.Report($"Downloading {downloadName} ({release.TagName})...");
            HttpClient client = httpClient ?? SharedHttpClient;
            using (var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl))
            {
                request.Headers.UserAgent.ParseAdd("BG3ModpackTools/1.0");
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream netStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream outStream = new(downloadedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await netStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
            }

            string candidateExe;
            bool isZip = downloadName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         release.DownloadUrl.Split('?', '#')[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            if (isZip)
            {
                progress?.Report($"Extracting {ExecutableFileName}...");
                await using FileStream zipFs = new(downloadedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                using var archive = new ZipArchive(zipFs, ZipArchiveMode.Read);

                ZipArchiveEntry? exeEntry = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .OrderByDescending(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), ExecutableFileName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (exeEntry is null)
                {
                    throw new InvalidDataException($"Update archive '{downloadName}' did not contain an executable.");
                }

                candidateExe = Path.Combine(stagingDir, ExecutableFileName);
                await using Stream entryStream = exeEntry.Open();
                await using FileStream extractedFs = new(candidateExe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await entryStream.CopyToAsync(extractedFs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                candidateExe = downloadedFile;
            }

            VerifyWindowsPeHeader(candidateExe);

            string? targetDir = Path.GetDirectoryName(targetExe);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(candidateExe, stagedNewExe, overwrite: true);
            return new StagedAppUpdate(targetExe, stagedNewExe, stagingDir, release.TagName);
        }
        catch
        {
            TryDeleteFile(stagedNewExe);
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    public static string BuildWindowsReplaceScriptContent(int processId, StagedAppUpdate staged)
    {
        string target = EscapeBatchValue(staged.TargetExePath);
        string newFile = EscapeBatchValue(staged.StagedNewExePath);
        string oldFile = EscapeBatchValue(staged.TargetExePath + ".old");
        string stageDir = EscapeBatchValue(staged.StagingDirectory);

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >NUL");
        sb.AppendLine($"set \"TARGET={target}\"");
        sb.AppendLine($"set \"NEWFILE={newFile}\"");
        sb.AppendLine($"set \"OLDFILE={oldFile}\"");
        sb.AppendLine($"set \"STAGEDIR={stageDir}\"");
        sb.AppendLine($"set \"PID={processId}\"");
        sb.AppendLine("set /a WAIT=0");
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" /NH /FO CSV 2>NUL | findstr /C:\",\\\"%PID%\\\",\" >NUL");
        sb.AppendLine("if errorlevel 1 goto replaceloop");
        sb.AppendLine("set /a WAIT+=1");
        sb.AppendLine("if %WAIT% GEQ 30 goto replaceloop");
        sb.AppendLine("ping -n 2 127.0.0.1 >NUL");
        sb.AppendLine("goto waitloop");
        sb.AppendLine(":replaceloop");
        sb.AppendLine("set /a TRIES=0");
        sb.AppendLine(":tryreplace");
        sb.AppendLine("if exist \"%OLDFILE%\" del /f /q \"%OLDFILE%\" >NUL 2>&1");
        sb.AppendLine("if exist \"%TARGET%\" move /y \"%TARGET%\" \"%OLDFILE%\" >NUL 2>&1");
        sb.AppendLine("if exist \"%TARGET%\" goto retrywait");
        sb.AppendLine("move /y \"%NEWFILE%\" \"%TARGET%\" >NUL 2>&1");
        sb.AppendLine("if not exist \"%TARGET%\" goto rollback");
        sb.AppendLine("if exist \"%OLDFILE%\" del /f /q \"%OLDFILE%\" >NUL 2>&1");
        sb.AppendLine("if exist \"%STAGEDIR%\" rmdir /s /q \"%STAGEDIR%\" >NUL 2>&1");
        sb.AppendLine("start \"\" \"%TARGET%\"");
        sb.AppendLine("goto cleanup");
        sb.AppendLine(":rollback");
        sb.AppendLine("if exist \"%OLDFILE%\" move /y \"%OLDFILE%\" \"%TARGET%\" >NUL 2>&1");
        sb.AppendLine(":retrywait");
        sb.AppendLine("set /a TRIES+=1");
        sb.AppendLine("if %TRIES% GEQ 10 goto cleanup");
        sb.AppendLine("ping -n 2 127.0.0.1 >NUL");
        sb.AppendLine("goto tryreplace");
        sb.AppendLine(":cleanup");
        sb.AppendLine("del /f /q \"%~f0\" >NUL 2>&1");
        return sb.ToString();
    }

    public static void LaunchSelfReplaceScript(StagedAppUpdate staged, int? processIdOverride = null)
    {
        int pid = processIdOverride ?? Environment.ProcessId;
        string scriptPath = Path.Combine(Path.GetTempPath(), $"BG3ModpackTools_ApplyUpdate_{Guid.NewGuid():N}.cmd");
        string script = BuildWindowsReplaceScriptContent(pid, staged);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void CleanupStaleUpdateArtifacts(string? targetExePathOverride = null)
    {
        try
        {
            string? targetExe = targetExePathOverride ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(targetExe))
            {
                return;
            }

            string fullTarget = Path.GetFullPath(targetExe.Trim());
            TryDeleteFile(fullTarget + ".old");
            TryDeleteFile(fullTarget + ".new");
        }
        catch
        {
            // Never allow startup cleanup to interfere with normal launch.
        }
    }

    private static void VerifyWindowsPeHeader(string exePath)
    {
        Span<byte> header = stackalloc byte[2];
        using FileStream fs = File.OpenRead(exePath);
        int read = fs.Read(header);
        if (read < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            throw new InvalidDataException($"Downloaded file '{Path.GetFileName(exePath)}' is not a valid Windows executable.");
        }
    }

    private static string EscapeBatchValue(string value) => value.Replace("%", "%%");

    private static string? GetStringProp(JsonElement element, string propName)
    {
        return element.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
