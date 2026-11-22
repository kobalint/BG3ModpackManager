using System.Diagnostics;
using BG3ModpackManager.Core.Models;

namespace BG3ModpackManager.Core.Services;

/// <summary>
/// Result of resolving the target Baldur's Gate 3 executable for launching.
/// </summary>
public sealed record Bg3LaunchResolution(
    bool Success,
    string ExecutablePath,
    string WorkingDirectory,
    string ExecutableFileName,
    Bg3LaunchRenderer Renderer,
    string? ErrorCode = null);

/// <summary>
/// Resolves and launches Baldur's Gate 3 (Vulkan or DirectX 11).
/// </summary>
public static class GameLauncherService
{
    public static string GetExecutableFileName(Bg3LaunchRenderer renderer)
        => renderer == Bg3LaunchRenderer.Vulkan ? "bg3.exe" : "bg3_dx11.exe";

    public static string ResolveGameFolder(AppSettings settings)
        => ResolveGameFolder(settings.GameFolder, settings.DataFolder);

    public static string ResolveGameFolder(string? gameFolder, string? dataFolder)
    {
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            return gameFolder.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dataFolder))
        {
            string trimmedData = dataFolder.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmedData);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return string.Empty;
    }

    public static Bg3LaunchResolution ResolveExecutable(AppSettings settings)
        => ResolveExecutable(settings.GameFolder, settings.DataFolder, settings.LaunchRenderer);

    public static Bg3LaunchResolution ResolveExecutable(
        string? gameFolder,
        string? dataFolder,
        Bg3LaunchRenderer renderer)
    {
        string exeName = GetExecutableFileName(renderer);
        string gameRoot = ResolveGameFolder(gameFolder, dataFolder);

        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return new Bg3LaunchResolution(false, string.Empty, string.Empty, exeName, renderer, "GameFolderNotSet");
        }

        if (!Directory.Exists(gameRoot))
        {
            return new Bg3LaunchResolution(false, gameRoot, string.Empty, exeName, renderer, "GameFolderMissing");
        }

        string primaryPath = Path.Combine(gameRoot, "bin", exeName);
        if (File.Exists(primaryPath))
        {
            return new Bg3LaunchResolution(true, primaryPath, Path.GetDirectoryName(primaryPath)!, exeName, renderer);
        }

        string directPath = Path.Combine(gameRoot, exeName);
        if (File.Exists(directPath))
        {
            return new Bg3LaunchResolution(true, directPath, Path.GetDirectoryName(directPath)!, exeName, renderer);
        }

        return new Bg3LaunchResolution(false, primaryPath, Path.Combine(gameRoot, "bin"), exeName, renderer, "ExecutableMissing");
    }

    public static Process? LaunchGame(Bg3LaunchResolution resolution)
    {
        if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.ExecutablePath) || !File.Exists(resolution.ExecutablePath))
        {
            throw new FileNotFoundException("Baldur's Gate 3 executable could not be found.", resolution.ExecutablePath);
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = resolution.ExecutablePath,
            WorkingDirectory = resolution.WorkingDirectory,
            UseShellExecute = true
        });
    }
}
