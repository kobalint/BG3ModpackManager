using System.Diagnostics;
using System.Windows;
using BG3ModpackTools.App.Helpers;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.App.Services;

/// <summary>
/// UI workflow for automatic startup update prompts (caching declined choice)
/// and manual update checks with explicit confirmation dialogs.
/// </summary>
public static class AppUpdateUiHelper
{
    public static async Task CheckForUpdatesOnStartupAsync(
        AppState state,
        Window? owner,
        CancellationToken cancellationToken = default)
    {
        AppUpdateService.CleanupStaleUpdateArtifacts();

        if (!state.Settings.CheckUpdatesOnStartup)
        {
            return;
        }

        AppUpdateCheckResult check;
        try
        {
            check = await Task.Run(
                () => AppUpdateService.CheckForUpdateAsync(cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Startup update check stays silent when offline or when GitHub cannot be reached.
            return;
        }

        if (!check.UpdateAvailable || check.LatestRelease is null)
        {
            return;
        }

        AppReleaseInfo release = check.LatestRelease;
        Window? resolvedOwner = owner ?? Application.Current?.MainWindow;
        if (resolvedOwner is not null && !resolvedOwner.IsLoaded)
        {
            return;
        }

        string notesSection = BuildReleaseNotesSection(release.ReleaseNotes);
        string msg = Loc.F(
            "App.Update.StartupPromptMsg",
            check.CurrentVersion,
            release.TagName,
            release.ReleaseName,
            notesSection);

        MessageBoxResult answer = MessageBox.Show(
            resolvedOwner,
            msg,
            Loc.T("App.Update.StartupTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes)
        {
            state.Settings.CheckUpdatesOnStartup = false;
            state.Store.SaveSettings(state.Settings);
            state.Log(Loc.T("App.Update.StartupDeclinedLog"));
            state.NotifyStateChanged();
            return;
        }

        await DownloadAndApplyUpdateAsync(state, resolvedOwner, release);
    }

    public static async Task CheckAndUpdateManuallyAsync(AppState state, Window? owner)
    {
        Window? resolvedOwner = owner ?? Application.Current?.MainWindow;
        AppUpdateCheckResult check;
        try
        {
            state.SetBusy(true, Loc.T("App.Update.CheckingBusy"));
            check = await Task.Run(() => AppUpdateService.CheckForUpdateAsync());
            state.SetBusy(false);
        }
        catch (Exception ex)
        {
            state.SetBusy(false);
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.Update.CheckErrorMsg", ex.Message),
                    Loc.T("App.Update.CheckErrorTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                OpenReleasesPage();
            }

            return;
        }
        finally
        {
            state.SetBusy(false);
        }

        if (check.LatestRelease is null)
        {
            MessageBox.Show(
                resolvedOwner,
                Loc.F("App.Update.NoReleasesMsg", check.CurrentVersion),
                Loc.T("App.Update.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!check.UpdateAvailable)
        {
            MessageBox.Show(
                resolvedOwner,
                Loc.F("App.Update.UpToDateMsg", check.CurrentVersion),
                Loc.T("App.Update.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AppReleaseInfo release = check.LatestRelease;
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.Update.NoBinaryAssetMsg", release.TagName, release.ReleaseName),
                    Loc.T("App.Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                OpenReleasesPage(release.HtmlUrl);
            }

            return;
        }

        string packageName = string.IsNullOrWhiteSpace(release.AssetName)
            ? AppUpdateService.ExecutableFileName
            : release.AssetName;
        string sizeText = release.AssetSizeBytes > 0
            ? FormatHelper.FormatBytes(release.AssetSizeBytes)
            : "?";
        string notesSection = BuildReleaseNotesSection(release.ReleaseNotes);

        string confirmMsg = Loc.F(
            "App.Update.ManualConfirmMsg",
            check.CurrentVersion,
            release.TagName,
            release.ReleaseName,
            packageName,
            sizeText,
            notesSection);

        if (MessageBox.Show(
                resolvedOwner,
                confirmMsg,
                Loc.T("App.Update.ManualConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            return;
        }

        await DownloadAndApplyUpdateAsync(state, resolvedOwner, release);
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(
        AppState state,
        Window? owner,
        AppReleaseInfo release)
    {
        Window? resolvedOwner = owner ?? Application.Current?.MainWindow;

        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.Update.NoBinaryAssetMsg", release.TagName, release.ReleaseName),
                    Loc.T("App.Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                OpenReleasesPage(release.HtmlUrl);
            }

            return false;
        }

        try
        {
            state.SetBusy(true, Loc.T("App.Update.DownloadingBusy"));
            var progress = new Progress<string>(msg => state.SetBusy(true, msg));
            StagedAppUpdate staged = await Task.Run(() =>
                AppUpdateService.DownloadAndStageUpdateAsync(release, progress: progress));

            state.SaveAll();
            state.Log(Loc.F("App.Update.ApplyingLog", staged.ReleaseTag));
            AppUpdateService.LaunchSelfReplaceScript(staged);
            state.SetBusy(false);

            Application.Current?.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            state.SetBusy(false);
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.Update.InstallErrorMsg", ex.Message),
                    Loc.T("App.Update.InstallErrorTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error) == MessageBoxResult.Yes)
            {
                OpenReleasesPage(release.HtmlUrl);
            }

            return false;
        }
        finally
        {
            state.SetBusy(false);
        }
    }

    public static void OpenReleasesPage(string? url = null)
    {
        string target = string.IsNullOrWhiteSpace(url) ? AppUpdateService.ReleasesPageUrl : url;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static string BuildReleaseNotesSection(string? rawNotes)
    {
        if (string.IsNullOrWhiteSpace(rawNotes))
        {
            return string.Empty;
        }

        string[] lines = rawNotes
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string snippet = string.Join("\n", lines.Take(6));
        if (lines.Length > 6)
        {
            snippet += "\n...";
        }

        if (snippet.Length > 350)
        {
            snippet = snippet[..347].TrimEnd() + "...";
        }

        return Loc.F("App.Update.NotesHeader", snippet);
    }
}
