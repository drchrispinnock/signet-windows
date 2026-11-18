using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;
using Windows.Graphics.Imaging;
using Windows.System;

namespace Signet.App.Views;

/// <summary>
/// Scan QR code: reads a code with the camera, from an image on the clipboard or from an image
/// file, and hands it to the sheet that deals with it. A dApp pairing code goes to dApps, a
/// secret key or recovery phrase to Import account, and an address or public key is shown here
/// with the choice of adding it to the address book or sending to it. Nothing is acted on
/// without that next sheet, and what was scanned is never displayed unless it is an address.
/// </summary>
public sealed partial class ScanQRSheet : Sheet
{
    private enum CameraState { Starting, Running, Denied, Missing }

    /// <summary>An address the scan found, waiting for the user to say what to do with it.</summary>
    private sealed record Found(Address Address, bool FromPublicKey);

    private readonly WalletViewModel model;
    private readonly QRCaptureSession capture = new();
    private readonly Image preview = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly SoftwareBitmapSource previewSource = new();
    private readonly Grid viewfinder = new() { Height = 330, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(217, 0, 0, 0)) };
    private readonly ComboBox cameraPicker = new() { Header = "Camera", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 16 };
    private readonly StackPanel noteRow = new();
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private CameraState cameraState = CameraState.Starting;
    private IReadOnlyList<CameraDevice> devices = [];
    private string? deviceID;
    private Found? found;
    private string? note;
    /// <summary>The parts read so far of a key that a recovery sheet printed across several codes.</summary>
    private KeyPartCollector keyParts = new();
    /// <summary>The frames read so far of an animated code (AirGap Vault's, when it does not fit one).</summary>
    private UniformResource.Decoder frames = new();
    private bool closed;
    private bool framePending;

    /// <summary>With no account yet the sheet is a way in: keys and phrases, or an address to watch.</summary>
    private bool IsFirstRun => model.Wallets.Count == 0;
    private Wallet? Existing => found is { } f ? model.Wallets.FirstOrDefault(w => w.Address == f.Address) : null;
    private bool CanSend => found is { } f && model.CanSendFromSelected && f.Address != model.SelectedWallet?.Address;

    public ScanQRSheet(WalletViewModel model) : base(model.Wallets.Count == 0 ? "Import from QR code" : "Scan QR code", 480)
    {
        this.model = model;
        preview.Source = previewSource;
        Form.Children.Add(body);
        Form.Children.Add(noteRow);
        Form.Children.Add(buttons);
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.None;

        cameraPicker.SelectionChanged += async (_, _) =>
        {
            if (cameraPicker.SelectedItem is ComboBoxItem { Tag: string id } && id != deviceID)
            {
                deviceID = id;
                if (found is null && cameraState == CameraState.Running) await StartCapture(id);
            }
        };
        capture.CodeFound += code => DispatcherQueue.TryEnqueue(() => Handle(code));
        capture.FrameArrived += ShowFrame;
        model.PropertyChanged += ModelChanged;

        // Ctrl+V reads the clipboard, as Cmd-V does on the Mac.
        var paste = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += async (_, args) =>
        {
            if (found is not null) return;
            args.Handled = true;
            await Paste();
        };
        KeyboardAccelerators.Add(paste);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        Opened += async (_, _) => await StartCamera();
        Closed += async (_, _) =>
        {
            closed = true;
            model.PropertyChanged -= ModelChanged;
            await capture.Stop();
        };
        Render();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.HasCamera)) _ = StartCamera();
    }

    // ---- Rendering --------------------------------------------------------------------------------

    private void Render()
    {
        body.Children.Clear();
        if (found is { } f) body.Children.Add(FoundPanel(f));
        else Scanner();

        if (found is null && !keyParts.IsEmpty)
        {
            var progress = new Grid { ColumnSpacing = 8 };
            progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progress.Children.Add(new ProgressRing { IsIndeterminate = false, Maximum = keyParts.Count, Value = keyParts.Received, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top });
            var text = new TextBlock
            {
                Text = $"A secret key in {keyParts.Count} codes: {keyParts.Received} read. Still needed: {string.Join(", ", keyParts.Missing)}. Scan them in any order.",
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(text, 1);
            progress.Children.Add(text);
            body.Children.Add(progress);
        }

        if (found is null && !frames.IsEmpty)
            body.Children.Add(AnimatedCodeProgress(frames, "An animated code: {0} of {1} parts read. Keep the code in view."));

        noteRow.Children.Clear();
        if (note is not null) noteRow.Children.Add(Label(note, "", "SystemFillColorCautionBrush"));

        buttons.Children.Clear();
        if (found is null)
        {
            var paste = MakeButton("Paste from Clipboard", () => _ = Paste());
            ToolTipService.SetToolTip(paste, "Reads a QR code from an image on the clipboard, such as a screenshot, or a copied code (Ctrl+V)");
            buttons.Children.Add(paste);
            buttons.Children.Add(MakeButton("Choose Image…", () => _ = ChooseImage()));
        }
        else
        {
            buttons.Children.Add(MakeButton("Scan Again", ScanAgain));
        }
    }

    private void Scanner()
    {
        viewfinder.Children.Clear();
        switch (cameraState)
        {
            case CameraState.Running:
                viewfinder.Children.Add(preview);
                break;
            case CameraState.Starting:
                viewfinder.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
                break;
            case CameraState.Denied:
                var settings = new HyperlinkButton { Content = "Open Privacy Settings…", HorizontalAlignment = HorizontalAlignment.Center };
                settings.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
                viewfinder.Children.Add(Placeholder("Signet may not use the camera.", settings));
                break;
            case CameraState.Missing:
                viewfinder.Children.Add(Placeholder("No camera is connected.", null));
                break;
        }
        body.Children.Add(viewfinder);

        if (devices.Count > 1 && cameraState == CameraState.Running)
        {
            if (cameraPicker.Items.Count != devices.Count)
            {
                cameraPicker.Items.Clear();
                foreach (var device in devices) cameraPicker.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            }
            cameraPicker.SelectedItem = cameraPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == deviceID);
            body.Children.Add(cameraPicker);
        }

        body.Children.Add(Note(IsFirstRun
            ? "Hold up a QR code of a secret key or a recovery phrase to import it, AirGap Vault's Pair Account code, or an address to watch it. A code shown on this PC's own screen can be read from a screenshot on the clipboard."
            : "Hold up a QR code: a dApp's pairing code, an address or public key, a secret key or recovery phrase, or AirGap Vault's Pair Account code. A code shown on this PC's own screen can be read from a screenshot on the clipboard."));
    }

    /// <summary>A small ring and "{0} of {1} parts read" for an animated code being gathered.</summary>
    internal static Grid AnimatedCodeProgress(UniformResource.Decoder decoder, string format)
    {
        var progress = new Grid { ColumnSpacing = 8 };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progress.Children.Add(new ProgressRing { IsIndeterminate = false, Maximum = 1, Value = decoder.Progress, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top });
        var text = new TextBlock { Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, decoder.ReceivedFragments, decoder.ExpectedParts), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        progress.Children.Add(text);
        return progress;
    }

    private static StackPanel Placeholder(string text, UIElement? action)
    {
        var white = new SolidColorBrush(Windows.UI.Color.FromArgb(204, 255, 255, 255));
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = "", FontSize = 32, Foreground = white });
        panel.Children.Add(new TextBlock { Text = text, Foreground = white, HorizontalAlignment = HorizontalAlignment.Center });
        if (action is not null) panel.Children.Add(action);
        return panel;
    }

    /// <summary>Puts a camera frame on screen, dropping frames while the last is still being shown.</summary>
    private void ShowFrame(SoftwareBitmap frame)
    {
        if (closed || framePending)
        {
            frame.Dispose();
            return;
        }
        framePending = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { if (!closed) await previewSource.SetBitmapAsync(frame); }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException) { }
                finally
                {
                    frame.Dispose();
                    framePending = false;
                }
            }))
        {
            frame.Dispose();
            framePending = false;
        }
    }

    // ---- Camera -----------------------------------------------------------------------------------

    private async Task StartCamera()
    {
        if (found is not null || closed) return;
        devices = await QRCameraDevices.Devices();
        if (deviceID is null || devices.All(d => d.Id != deviceID)) deviceID = QRCamera.Preferred(devices)?.Id;
        if (deviceID is not { } id)
        {
            await capture.Stop();
            cameraState = CameraState.Missing;
            Render();
            return;
        }
        await StartCapture(id);
    }

    private async Task StartCapture(string id)
    {
        cameraState = CameraState.Starting;
        Render();
        var failure = await capture.Start(id);
        if (closed || found is not null)
        {
            await capture.Stop();
            return;
        }
        cameraState = failure switch
        {
            null => CameraState.Running,
            QRCaptureSession.Failure.Denied => CameraState.Denied,
            _ => CameraState.Missing,
        };
        Render();
    }

    private void ScanAgain()
    {
        found = null;
        note = null;
        keyParts = new KeyPartCollector();
        frames = new UniformResource.Decoder();
        Render();
        _ = StartCamera();
    }

    private async Task Paste()
    {
        var texts = await QRCameraDevices.TextsFromClipboard();
        if (texts.Count == 0)
        {
            note = "The clipboard holds no QR code image and no text.";
            Render();
            return;
        }
        foreach (var text in texts) Handle(text);
    }

    private async Task ChooseImage()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp" }) picker.FileTypeFilter.Add(type);
        if (await picker.PickSingleFileAsync() is not { } result) return;
        var codes = await QRCameraDevices.CodesInFile(result.Path);
        if (codes.Count == 0)
        {
            note = "No QR code was found in that image.";
            Render();
            return;
        }
        foreach (var code in codes) Handle(code);
    }

    // ---- What was scanned -------------------------------------------------------------------------

    private void Handle(string text)
    {
        if (found is not null || closed) return;
        note = null;
        switch (QRPayload.Classify(text))
        {
            case QRPayload.Pairing pairing:
                if (!model.Wallets.Any(w => w.KeyKind.CanSign()))
                {
                    note = "That is a dApp's pairing code. Create or import an account first, then scan it again.";
                    break;
                }
                Finish(new WalletViewModel.ScanHandoff.Pair(pairing.Code));
                return;
            case QRPayload.SecretKey key:
                Finish(new WalletViewModel.ScanHandoff.ImportKey(new ImportPrefill.SecretKey(key.Key)));
                return;
            case QRPayload.RecoveryPhrase phrase:
                Finish(new WalletViewModel.ScanHandoff.ImportKey(new ImportPrefill.RecoveryPhrase(phrase.Phrase)));
                return;
            case QRPayload.KeyPartContent part:
                switch (keyParts.Add(part.Part))
                {
                    case KeyPartCollector.Outcome.Complete complete:
                        Finish(new WalletViewModel.ScanHandoff.ImportKey(new ImportPrefill.SecretKey(complete.Key)));
                        return;
                    case KeyPartCollector.Outcome.Invalid:
                        note = "Those codes do not fit together into a key. Scan every part of the same recovery sheet again.";
                        break;
                }
                break;
            case QRPayload.AddressContent address:
                Show(new Found(address.Address, false));
                return;
            case QRPayload.PublicKey key:
                _ = ShowPublicKey(key.Key);
                return;
            case QRPayload.UniformResourceContent ur:
                try
                {
                    if (frames.Receive(ur.Part) is UniformResource.Decoder.Outcome.Complete(var payload) && ReadAirGap(payload)) return;
                }
                catch (Exception e) when (e is UniformResource.DecodeError or CBOR.DecodeError)
                {
                    frames = new UniformResource.Decoder();
                    note = "That animated code could not be read. Hold it steady and scan it again.";
                }
                break;
            case QRPayload.AirGapLegacy:
                note = "That is an AirGap code in its older format. In AirGap Vault, open QR Details under the code and choose QR Code V3.";
                break;
            default:
                note = "That code is not a pairing code, an address or a key that Signet can use.";
                break;
        }
        Render();
    }

    /// <summary>An AirGap message: a Vault's account share is the only one that makes sense here. True when it was handed on.</summary>
    private bool ReadAirGap(byte[] payload)
    {
        try
        {
            var accounts = AirGap.Accounts(AirGap.Messages(payload));
            if (accounts.Count == 0)
            {
                note = "That AirGap code holds no Tezos account. In AirGap Vault, open the Tezos account and choose Pair Account.";
                return false;
            }
            Finish(new WalletViewModel.ScanHandoff.AirGap(accounts));
            return true;
        }
        catch (AirGap.Error e) when (e.Kind == AirGap.Error.Reason.UnsupportedVersion)
        {
            note = "That AirGap code is in a format Signet does not read. In AirGap Vault, choose QR Code V3 under QR Details.";
        }
        catch (Exception e) when (e is AirGap.Error or CBOR.DecodeError)
        {
            note = "That code is not one Signet can use.";
        }
        return false;
    }

    private async Task ShowPublicKey(string key)
    {
        try
        {
            Show(new Found(await model.AddressForScannedPublicKey(key), true));
        }
        catch (Exception)
        {
            note = "That looks like a public key, but Signet could not read it.";
            Render();
        }
    }

    private void Show(Found result)
    {
        if (found is not null || closed) return;
        _ = capture.Stop();
        found = result;
        Render();
    }

    /// <summary>Stops the camera and closes; the home view opens the next sheet once this one has gone.</summary>
    private void Finish(WalletViewModel.ScanHandoff handoff)
    {
        if (model.PendingScan is not null || closed) return;
        _ = capture.Stop();
        model.PendingScan = handoff;
        Hide();
    }

    // ---- An address -------------------------------------------------------------------------------

    private StackPanel FoundPanel(Found found)
    {
        var panel = new StackPanel { Spacing = 14 };
        var card = new Grid { ColumnSpacing = 12, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
                              Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"] };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(new AccountAvatarView(found.Address, 44));
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = found.Address.Value, Style = (Style)Application.Current.Resources["MonoText"], IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
        if (Existing is { } existing)
            text.Children.Add(Label($"In your list as “{existing.Alias}”", ""));
        else if (found.Address.IsContract)
            text.Children.Add(new TextBlock { Text = "Smart contract", FontSize = 12, Foreground = Secondary });
        else if (found.Address.Scheme is { } scheme)
            text.Children.Add(new TextBlock { Text = found.FromPublicKey ? $"{scheme.DisplayName()}, from the scanned public key" : scheme.DisplayName(), FontSize = 12, Foreground = Secondary });
        Grid.SetColumn(text, 1);
        card.Children.Add(text);
        panel.Children.Add(card);

        panel.Children.Add(Note("Check the address against where the code came from before you use it."));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (Existing is null)
            actions.Children.Add(MakeButton(IsFirstRun ? "Watch This Address…" : "Add to Address Book…", () => Finish(new WalletViewModel.ScanHandoff.AddAddress(found.Address))));
        if (CanSend)
        {
            var send = MakeButton("Send…", () => Finish(new WalletViewModel.ScanHandoff.Send(found.Address)));
            send.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            actions.Children.Add(send);
        }
        panel.Children.Add(actions);
        return panel;
    }
}
