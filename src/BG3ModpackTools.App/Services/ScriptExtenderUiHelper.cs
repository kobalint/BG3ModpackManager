using System.Diagnostics;
using System.Windows;
using BG3ModpackTools.App.Helpers;
using BG3ModpackTools.Core.Models;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.App.Services;

/// <summary>
/// Shared UI workflow for checking, downloading, installing, and removing Norbyte's
/// Baldur's Gate 3 Script Extender from both MainWindow and SettingsView.
/// </summary>
public static class ScriptExtenderUiHelper
{
    public static async Task<bool> DownloadAndInstallInteractiveAsync(
        AppState state,
        Window? owner,
        string? gameFolderOverride = null,
        string? dataFolderOverride = null)
    {
        Window? resolvedOwner = owner ?? Application.Current?.MainWindow;
        AppSettings effective = BuildEffectiveSettings(state, gameFolderOverride, dataFolderOverride);
        ScriptExtenderStatus status = ScriptExtenderService.GetStatus(effective);

        if (!status.GameBinFolderAvailable)
        {
            MessageBox.Show(
                resolvedOwner,
                Loc.T("App.ScriptExtender.MissingGameFolderMsg"),
                Loc.T("App.ScriptExtender.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (!state.EnsureGameClosedBeforeFileOps(resolvedOwner))
        {
            state.Log(Loc.T("App.ScriptExtender.CancelledBg3Running"));
            return false;
        }

        ScriptExtenderReleaseInfo release;
        try
        {
            state.SetBusy(true, Loc.T("App.ScriptExtender.CheckingBusy"));
            release = await Task.Run(() => ScriptExtenderService.FetchLatestReleaseAsync());
            state.SetBusy(false);
        }
        catch (Exception ex)
        {
            state.SetBusy(false);
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.ScriptExtender.FetchErrorMsg", ex.Message),
                    Loc.T("App.ScriptExtender.FetchErrorTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                OpenReleasesPage();
            }

            return false;
        }
        finally
        {
            state.SetBusy(false);
        }

        string sizeText = release.AssetSizeBytes > 0
            ? FormatHelper.FormatBytes(release.AssetSizeBytes)
            : "?";
        string confirmMsg = status.IsForeignDWriteDll
            ? Loc.F("App.ScriptExtender.ConfirmForeignMsg", status.BinFolderPath, release.TagName, release.AssetName, sizeText)
            : status.IsInstalled
                ? Loc.F("App.ScriptExtender.ConfirmReinstallMsg", release.ReleaseName, release.TagName, release.AssetName, sizeText, status.BinFolderPath)
                : Loc.F("App.ScriptExtender.ConfirmInstallMsg", release.ReleaseName, release.TagName, release.AssetName, sizeText, status.BinFolderPath);

        if (MessageBox.Show(
                resolvedOwner,
                confirmMsg,
                Loc.T("App.ScriptExtender.ConfirmTitle"),
                MessageBoxButton.YesNo,
                status.IsForeignDWriteDll ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            state.SetBusy(true, Loc.T("App.ScriptExtender.DownloadingBusy"));
            var progress = new Progress<string>(msg => state.SetBusy(true, msg));
            ScriptExtenderInstallResult installResult = await Task.Run(() =>
                ScriptExtenderService.DownloadAndInstallAsync(effective, release, progress));
            state.SetBusy(false);

            if (!string.IsNullOrWhiteSpace(installResult.BackedUpForeignDllPath))
            {
                state.Log(Loc.F("App.ScriptExtender.BackupForeignLog", installResult.BackedUpForeignDllPath));
            }

            state.Log(Loc.F("App.ScriptExtender.InstalledLog", installResult.ReleaseTag, installResult.InstalledDllPath));
            state.NotifyStateChanged();

            string doneMsg = !string.IsNullOrWhiteSpace(installResult.BackedUpForeignDllPath)
                ? Loc.F("App.ScriptExtender.SuccessWithBackupMsg", installResult.ReleaseTag, installResult.InstalledDllPath, installResult.BackedUpForeignDllPath)
                : Loc.F("App.ScriptExtender.SuccessMsg", installResult.ReleaseTag, installResult.InstalledDllPath);

            MessageBox.Show(
                resolvedOwner,
                doneMsg,
                Loc.T("App.ScriptExtender.SuccessTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            state.SetBusy(false);
            if (MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.ScriptExtender.InstallErrorMsg", ex.Message),
                    Loc.T("App.ScriptExtender.InstallErrorTitle"),
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

    public static bool UninstallInteractive(
        AppState state,
        Window? owner,
        string? gameFolderOverride = null,
        string? dataFolderOverride = null)
    {
        Window? resolvedOwner = owner ?? Application.Current?.MainWindow;
        AppSettings effective = BuildEffectiveSettings(state, gameFolderOverride, dataFolderOverride);
        ScriptExtenderStatus status = ScriptExtenderService.GetStatus(effective);

        if (!status.IsInstalled)
        {
            return false;
        }

        if (!state.EnsureGameClosedBeforeFileOps(resolvedOwner))
        {
            state.Log(Loc.T("App.ScriptExtender.CancelledBg3Running"));
            return false;
        }

        if (MessageBox.Show(
                resolvedOwner,
                Loc.F("App.ScriptExtender.ConfirmRemoveMsg", status.DWriteDllPath),
                Loc.T("App.ScriptExtender.ConfirmRemoveTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            ScriptExtenderUninstallResult res = ScriptExtenderService.UninstallScriptExtender(effective);
            if (!res.Removed)
            {
                return false;
            }

            state.Log(Loc.F("App.ScriptExtender.RemovedLog", status.DWriteDllPath));
            state.NotifyStateChanged();

            if (!string.IsNullOrWhiteSpace(res.RestoredBackupPath))
            {
                MessageBox.Show(
                    resolvedOwner,
                    Loc.F("App.ScriptExtender.RemovedRestoredMsg", res.RestoredBackupPath),
                    Loc.T("App.ScriptExtender.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                resolvedOwner,
                ex.Message,
                Loc.T("App.ScriptExtender.RemoveErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    public static void OpenReleasesPage(string? url = null)
    {
        string target = string.IsNullOrWhiteSpace(url) ? ScriptExtenderService.ReleasesPageUrl : url;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static AppSettings BuildEffectiveSettings(
        AppState state,
        string? gameFolderOverride,
        string? dataFolderOverride)
    {
        return new AppSettings
        {
            GameFolder = gameFolderOverride ?? state.Settings.GameFolder,
            DataFolder = dataFolderOverride ?? state.Settings.DataFolder
        };
    }
}
