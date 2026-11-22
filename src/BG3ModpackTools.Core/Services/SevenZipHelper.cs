using System.Diagnostics;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Locates and invokes a working 7-Zip CLI executable (7z.exe or 7za.exe) with tar.exe fallback.
/// </summary>
public static class SevenZipHelper
{
    public static string? FindExecutable()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "7za.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            foreach (string exeName in new[] { "7z.exe", "7za.exe" })
            {
                string full = Path.Combine(trimmed, exeName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    public static async Task<List<string>> ListEntriesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        string? sevenZip = FindExecutable();
        ProcessStartInfo psi;

        if (sevenZip is not null)
        {
            psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("l");
            psi.ArgumentList.Add("-slt");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(archivePath);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "tar.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-tf");
            psi.ArgumentList.Add(archivePath);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch archive tool '{psi.FileName}'.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to inspect archive '{Path.GetFileName(archivePath)}' (exit {proc.ExitCode}): {stderr.Trim()}".Trim());
        }

        var entries = new List<string>();
        if (sevenZip is not null)
        {
            bool pastHeader = false;
            foreach (string rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!pastHeader)
                {
                    if (rawLine.StartsWith("----------", StringComparison.Ordinal))
                    {
                        pastHeader = true;
                    }

                    continue;
                }

                if (rawLine.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    string entryPath = rawLine[7..].Trim().Replace('\\', '/');
                    if (!string.IsNullOrEmpty(entryPath))
                    {
                        entries.Add(entryPath);
                    }
                }
            }
        }
        else
        {
            foreach (string rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                entries.Add(rawLine.Trim().Replace('\\', '/'));
            }
        }

        return entries;
    }

    public static async Task ExtractToDirectoryAsync(string archivePath, string destinationDir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);
        string? sevenZip = FindExecutable();
        ProcessStartInfo psi;

        if (sevenZip is not null)
        {
            psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add($"-o{destinationDir}");
            psi.ArgumentList.Add("-y");
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "tar.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destinationDir);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch archive extractor '{psi.FileName}'.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        _ = await stdoutTask;
        string stderr = await stderrTask;
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0)
        {
            string hint = sevenZip is null
                ? "Install 7-Zip or convert the archive to .zip if Windows tar.exe cannot extract this format."
                : "7z.exe returned an error.";
            throw new InvalidOperationException($"Archive extraction failed (exit {proc.ExitCode}): {stderr.Trim()} {hint}".Trim());
        }

        foreach (string extracted in Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories))
        {
            if (!FileTransactionHelper.IsPathStrictlyInsideRoot(extracted, destinationDir))
            {
                throw new InvalidOperationException($"Extracted file escaped staging root: {extracted}");
            }
        }
    }
}
