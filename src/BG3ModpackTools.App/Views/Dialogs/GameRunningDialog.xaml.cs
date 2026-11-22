using System.Windows;
using BG3ModpackTools.App.Services;
using BG3ModpackTools.Core.Services;

namespace BG3ModpackTools.App.Views.Dialogs;

public partial class GameRunningDialog : Window
{
    public GameRunningDialog()
    {
        InitializeComponent();
        RefreshProcesses();
    }

    private bool RefreshProcesses()
    {
        IReadOnlyList<RunningGameProcess> running = GameProcessGuard.GetRunningGameProcesses();
        ProcessList.ItemsSource = running
            .Select(p => $"• {p.VariantLabel} (PID {p.Pid})")
            .ToList();

        if (running.Count == 0)
        {
            DialogResult = true;
            return true;
        }

        return false;
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        if (!RefreshProcesses())
        {
            HintText.Text = Loc.T("Dialog.GameRunning.HintStillRunning");
        }
    }

    private async void OnGracefulCloseClick(object sender, RoutedEventArgs e)
    {
        GracefulCloseButton.IsEnabled = false;
        RecheckButton.IsEnabled = false;
        HintText.Text = Loc.T("Dialog.GameRunning.HintClosing");

        try
        {
            bool closed = await GameProcessGuard.TryRequestGracefulCloseAsync(TimeSpan.FromSeconds(5));
            if (closed)
            {
                DialogResult = true;
                return;
            }

            RefreshProcesses();
            HintText.Text = Loc.T("Dialog.GameRunning.HintStillOpen");
        }
        finally
        {
            GracefulCloseButton.IsEnabled = true;
            RecheckButton.IsEnabled = true;
        }
    }
}
