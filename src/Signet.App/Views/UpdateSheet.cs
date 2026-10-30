using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.App.Services;

namespace Signet.App.Views;

/// <summary>
/// Windows-only: "Check for Updates…" and the offer of an update the daily check found, where
/// Sparkle shows its own update window on the Mac. Checks when opened (unless an update is already
/// found or downloading), then offers Download and Install, shows progress, and Restart to Update.
/// </summary>
public sealed partial class UpdateSheet : Sheet
{
    private readonly UpdaterService updater = UpdaterService.Shared;

    public UpdateSheet() : base("Software Update", 460)
    {
        CloseButtonText = "Later";
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            switch (updater.State)
            {
                case UpdaterService.UpdateState.Available:
                    var deferral = args.GetDeferral();
                    try { await updater.Download(); }
                    finally { deferral.Complete(); }
                    break;
                case UpdaterService.UpdateState.Ready:
                    updater.RestartToUpdate();
                    break;
            }
        };
        // A download under way must not be dropped with Esc; it carries on if the sheet closes otherwise.
        updater.PropertyChanged += Changed;
        Closed += (_, _) => updater.PropertyChanged -= Changed;
        Opened += async (_, _) =>
        {
            if (updater.State is UpdaterService.UpdateState.Idle or UpdaterService.UpdateState.UpToDate or UpdaterService.UpdateState.Failed)
                await updater.CheckForUpdates();
        };
        Render();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        Form.Children.Clear();
        if (!updater.IsAvailable)
        {
            Form.Children.Add(Note($"This is Signet {updater.CurrentVersion}. Updates work in the installed copy of Signet (the Setup or Portable download from GitHub); this copy was not installed that way."));
            PrimaryButtonText = "";
            CloseButtonText = "Done";
            return;
        }
        (PrimaryButtonText, CloseButtonText) = ("", "Done");
        switch (updater.State)
        {
            case UpdaterService.UpdateState.Idle or UpdaterService.UpdateState.Checking:
                ShowProgress("Checking for updates…");
                break;
            case UpdaterService.UpdateState.UpToDate:
                ShowProgress(null);
                Form.Children.Add(Success($"Signet {updater.CurrentVersion} is the latest version."));
                break;
            case UpdaterService.UpdateState.Available available:
                ShowProgress(null);
                Form.Children.Add(new TextBlock { Text = $"Signet {available.Version} is available. You have {updater.CurrentVersion}.", TextWrapping = TextWrapping.Wrap });
                Form.Children.Add(Note("It is downloaded now and installed when Signet restarts. Your accounts stay in the wallet folder."));
                (PrimaryButtonText, CloseButtonText) = ("Download and Install", "Later");
                break;
            case UpdaterService.UpdateState.Downloading downloading:
                ShowProgress($"Downloading Signet {downloading.Version}… {downloading.Percent}%");
                Form.Children.Add(new ProgressBar { Value = downloading.Percent, Maximum = 100 });
                CloseButtonText = "Hide";
                break;
            case UpdaterService.UpdateState.Ready ready:
                ShowProgress(null);
                Form.Children.Add(Success($"Signet {ready.Version} is ready to install."));
                Form.Children.Add(Note("Signet closes, installs it and opens again. Anything you were doing in a sheet is closed first, so finish any operation before restarting."));
                (PrimaryButtonText, CloseButtonText) = ("Restart to Update", "Later");
                break;
            case UpdaterService.UpdateState.Failed failed:
                ShowProgress(null);
                ShowError(failed.Message);
                break;
        }
        DefaultButton = PrimaryButtonText.Length > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close;
        PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        IsPrimaryButtonEnabled = true;
    }
}
