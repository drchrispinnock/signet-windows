using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;

namespace Signet.App.Views;

/// <summary>
/// Saves the artwork of one or many NFTs into the wallet directory's <c>nfts</c> folder, a row per
/// token with its progress, and shows the folder in File Explorer when asked. Closing the sheet
/// stops whatever is still downloading; what was saved stays.
/// </summary>
public sealed partial class DownloadNFTsSheet : Sheet
{
    private abstract record Outcome
    {
        public sealed record Saved(string File) : Outcome;
        public sealed record Failed(string Reason) : Outcome;
    }

    private readonly NFTDownloader downloader;
    private readonly NFTDownload download;
    private readonly Dictionary<string, Outcome> outcomes = [];
    private readonly StackPanel rows = new() { Spacing = 8 };
    private readonly StackPanel summary = new();
    private readonly CancellationTokenSource cancel = new();

    public DownloadNFTsSheet(NFTDownloader downloader, NFTDownload download)
        : base(download.Nfts.Count == 1 ? "Download NFT" : $"Download {download.Nfts.Count} NFTs", 480)
    {
        this.downloader = downloader;
        this.download = download;
        Form.Children.Add(Note($"The full artwork of each token is saved into {downloader.Directory}, named after the token, its contract and its id."));
        Form.Children.Add(new ScrollViewer { Content = rows, MinHeight = 60, MaxHeight = 280 });
        Form.Children.Add(summary);
        // Show in File Explorer is the secondary button; the dialog stays open after it.
        SecondaryButtonText = "Show in File Explorer";
        SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await Reveal();
        };
        Primary("Done", () => Task.CompletedTask);
        CloseButtonText = "Cancel";
        Closed += (_, _) => cancel.Cancel();
        Opened += async (_, _) => await Run();
        Render();
    }

    private bool Finished => outcomes.Count == download.Nfts.Count;
    private IReadOnlyList<string> Saved => download.Nfts.Select(n => outcomes.GetValueOrDefault(n.Id)).OfType<Outcome.Saved>().Select(s => s.File).ToList();

    private void Render()
    {
        rows.Children.Clear();
        foreach (var nft in download.Nfts) rows.Children.Add(Row(nft));
        summary.Children.Clear();
        if (Finished)
        {
            var failed = download.Nfts.Count - Saved.Count;
            summary.Children.Add(failed == 0
                ? Success(Saved.Count == 1 ? "Saved" : $"Saved {Saved.Count} files")
                : Label($"Saved {Saved.Count} of {download.Nfts.Count}", "", "SystemFillColorCautionBrush"));
        }
        IsSecondaryButtonEnabled = Saved.Count > 0;
        IsPrimaryButtonEnabled = Finished;
        CloseButtonText = Finished ? "" : "Cancel";
    }

    private Grid Row(NFT nft)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var outcome = outcomes.GetValueOrDefault(nft.Id);
        row.Children.Add(outcome switch
        {
            Outcome.Saved => new FontIcon { Glyph = "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] },
            Outcome.Failed => new FontIcon { Glyph = "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] },
            _ => new ProgressRing { Width = 16, Height = 16, IsActive = true },
        });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = nft.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(outcome switch
        {
            Outcome.Saved saved => new TextBlock { Text = Path.GetFileName(saved.File), FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis },
            Outcome.Failed failed => new TextBlock { Text = failed.Reason, FontSize = 12, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap },
            _ => new TextBlock { Text = nft.IsMoving ? "Downloading the video or sound…" : "Downloading…", FontSize = 12, Foreground = Secondary },
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Three at a time: the public client has six slots and the tiles may be loading too.</summary>
    private async Task Run()
    {
        using var slots = new SemaphoreSlim(3);
        await Task.WhenAll(download.Nfts.Select(async nft =>
        {
            Outcome outcome;
            try
            {
                await slots.WaitAsync(cancel.Token);
                try { outcome = new Outcome.Saved(await downloader.Download(nft, cancel.Token)); }
                finally { slots.Release(); }
            }
            catch (OperationCanceledException) { outcome = new Outcome.Failed("Cancelled"); }
            catch (Exception e) { outcome = new Outcome.Failed(e.Message); }
            if (cancel.IsCancellationRequested) return;
            outcomes[nft.Id] = outcome;
            Render();
        }));
    }

    /// <summary>Opens the folder in File Explorer with the first saved file selected.</summary>
    private async Task Reveal()
    {
        if (Saved.FirstOrDefault() is not { } file) return;
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(downloader.Directory);
            var options = new Windows.System.FolderLauncherOptions();
            foreach (var path in Saved)
                options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            await Windows.System.Launcher.LaunchFolderAsync(folder, options);
        }
        catch (Exception)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
    }
}
