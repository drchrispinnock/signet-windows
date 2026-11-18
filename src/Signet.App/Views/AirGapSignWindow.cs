using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;
using Windows.Graphics.Imaging;
using Windows.System;

namespace Signet.App.Views;

/// <summary>
/// Upstream's <c>AirGapSignView</c>: the request code, then the camera for the reply.
/// Windows difference: upstream puts it as a sheet on the deepest sheet of the front window; WinUI
/// allows one ContentDialog at a time and the sheet that asked (Send, Staking, …) is one, so here
/// it is a small window of its own, kept above Signet's, whose close button cancels.
/// </summary>
public sealed partial class AirGapSignWindow : Window
{
    private enum CameraState { Starting, Running, Denied, Missing }

    private readonly AirGapSignSession session;
    private readonly QRCaptureSession capture = new();
    private readonly Image preview = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly SoftwareBitmapSource previewSource = new();
    private readonly Grid viewfinder = new() { Height = 300, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(217, 0, 0, 0)) };
    private readonly ComboBox cameraPicker = new() { Header = "Camera", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly StackPanel noteRow = new();
    private readonly Grid buttons = new() { ColumnSpacing = 8 };
    private readonly Image code = new() { Width = 300, Height = 300, Stretch = Stretch.Uniform };
    private readonly Dictionary<int, BitmapImage> frameImages = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer frameTimer;
    private CameraState cameraState = CameraState.Starting;
    private IReadOnlyList<CameraDevice> devices = [];
    private string? deviceID;
    private int frameIndex;
    private bool closed;
    private bool framePending;

    private AirGapSignWindow(AirGapSignSession session)
    {
        this.session = session;
        Title = "Sign with AirGap Vault";
        preview.Source = previewSource;

        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "Sign with AirGap Vault", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        heading.Children.Add(new TextBlock { Text = $"Account {new Address(session.Address).Shortened()}", Style = (Style)Application.Current.Resources["MonoText"], Foreground = Secondary });

        var root = new StackPanel { Spacing = 14, Padding = new Thickness(20) };
        root.Children.Add(heading);
        root.Children.Add(body);
        root.Children.Add(noteRow);
        root.Children.Add(buttons);
        var host = new Grid { Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"], RequestedTheme = AppearanceApplier.Current };
        host.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = host;

        frameTimer = DispatcherQueue.CreateTimer();
        frameTimer.Interval = TimeSpan.FromMilliseconds(250);
        frameTimer.Tick += (_, _) =>
        {
            frameIndex += 1;
            if (session.Phase == AirGapSignSession.PhaseKind.Showing) code.Source = FrameImage(frameIndex % session.Frames.Count);
        };

        cameraPicker.SelectionChanged += async (_, _) =>
        {
            if (cameraPicker.SelectedItem is ComboBoxItem { Tag: string id } && id != deviceID)
            {
                deviceID = id;
                if (session.Phase == AirGapSignSession.PhaseKind.Scanning && cameraState == CameraState.Running) await StartCapture(id);
            }
        };
        capture.CodeFound += text => DispatcherQueue.TryEnqueue(() => session.Receive(text));
        capture.FrameArrived += ShowFrame;
        session.PropertyChanged += SessionChanged;

        // Ctrl+V reads the clipboard, as Cmd-V does on the Mac.
        var paste = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += async (_, args) =>
        {
            if (session.Phase != AirGapSignSession.PhaseKind.Scanning) return;
            args.Handled = true;
            await Paste();
        };
        root.KeyboardAccelerators.Add(paste);
        root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        Closed += async (_, _) =>
        {
            closed = true;
            frameTimer.Stop();
            session.PropertyChanged -= SessionChanged;
            // Closing the window is Cancel; after a reply it does nothing.
            session.Cancel();
            await capture.Stop();
        };
        Render();
        if (session.Frames.Count > 1) frameTimer.Start();
    }

    /// <summary>Opens the panel for <c>session</c> over <c>owner</c> and returns what closes it.</summary>
    public static Action Show(AirGapSignSession session, Window? owner)
    {
        var window = new AirGapSignWindow(session);
        var scale = (owner?.Content?.XamlRoot?.RasterizationScale) ?? 1;
        window.AppWindow.SetIcon("Assets/Signet.ico");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(520 * scale), (int)(720 * scale)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        if (owner is not null)
        {
            var area = owner.AppWindow.Position;
            var size = owner.AppWindow.Size;
            window.AppWindow.Move(new Windows.Graphics.PointInt32(
                area.X + Math.Max(0, (size.Width - window.AppWindow.Size.Width) / 2),
                area.Y + Math.Max(0, (size.Height - window.AppWindow.Size.Height) / 3)));
        }
        window.Activate();
        return () => { if (!window.closed) window.Close(); };
    }

    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private void SessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Render);

    private BitmapImage FrameImage(int index)
    {
        if (frameImages.TryGetValue(index, out var image)) return image;
        image = new BitmapImage();
        using (var stream = new MemoryStream(QRCode.Png(session.Frames[index], 300, QRCoder.QRCodeGenerator.ECCLevel.L)))
            image.SetSource(stream.AsRandomAccessStream());
        frameImages[index] = image;
        return image;
    }

    // ---- Rendering --------------------------------------------------------------------------------

    private void Render()
    {
        if (closed) return;
        body.Children.Clear();
        if (session.Phase == AirGapSignSession.PhaseKind.Showing) RequestCode();
        else ReplyScanner();

        noteRow.Children.Clear();
        if (session.Note is { } note)
            noteRow.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });

        buttons.Children.Clear();
        buttons.ColumnDefinitions.Clear();
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (session.Phase == AirGapSignSession.PhaseKind.Scanning)
        {
            left.Children.Add(MakeButton("Show Request Again", ShowRequest));
            var paste = MakeButton("Paste from Clipboard", () => _ = Paste());
            ToolTipService.SetToolTip(paste, "Reads the Vault's reply from a screenshot or image on the clipboard (Ctrl+V)");
            left.Children.Add(paste);
        }
        right.Children.Add(MakeButton("Cancel", Close));
        if (session.Phase == AirGapSignSession.PhaseKind.Showing)
        {
            var scan = MakeButton("Scan Vault's Reply", Scan);
            scan.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            right.Children.Add(scan);
        }
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        buttons.Children.Add(left);
        buttons.Children.Add(right);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private void RequestCode()
    {
        code.Source = FrameImage(frameIndex % session.Frames.Count);
        var frame = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = code };
        body.Children.Add(frame);
        var steps = new StackPanel { Spacing = 4 };
        steps.Children.Add(Step($"1. In AirGap Vault, tap Scan and point the phone at this code{(session.Frames.Count > 1 ? " until it has read every part" : "")}."));
        steps.Children.Add(Step("2. Check the operation on the phone and approve it."));
        steps.Children.Add(Step("3. Click Scan Vault's Reply and hold the code the Vault shows up to this PC's camera."));
        body.Children.Add(steps);
    }

    private static TextBlock Step(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private void ReplyScanner()
    {
        viewfinder.Children.Clear();
        var white = new SolidColorBrush(Windows.UI.Color.FromArgb(204, 255, 255, 255));
        switch (cameraState)
        {
            case CameraState.Running:
                viewfinder.Children.Add(preview);
                break;
            case CameraState.Starting:
                viewfinder.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
                break;
            case CameraState.Denied:
            {
                var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new FontIcon { Glyph = "", FontSize = 32, Foreground = white });
                panel.Children.Add(new TextBlock { Text = "Signet may not use the camera.", Foreground = white, HorizontalAlignment = HorizontalAlignment.Center });
                var settings = new HyperlinkButton { Content = "Open Privacy Settings…", HorizontalAlignment = HorizontalAlignment.Center };
                settings.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
                panel.Children.Add(settings);
                viewfinder.Children.Add(panel);
                break;
            }
            case CameraState.Missing:
            {
                var panel = new StackPanel { Spacing = 10, Padding = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new FontIcon { Glyph = "", FontSize = 32, Foreground = white });
                panel.Children.Add(new TextBlock { Text = "No camera is connected. Paste a screenshot of the Vault's code instead.", Foreground = white, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                viewfinder.Children.Add(panel);
                break;
            }
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

        if (session.IsCollecting)
            body.Children.Add(ScanQRSheet.AnimatedCodeProgress(session.Decoder, "An animated code: {0} of {1} parts read. Keep it in view."));
        else
            body.Children.Add(new TextBlock
            {
                Text = "Hold up the signed code AirGap Vault shows after you approve. Signet checks it signs exactly this operation before sending.",
                TextWrapping = TextWrapping.Wrap, Foreground = Secondary,
            });
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

    // ---- Actions ----------------------------------------------------------------------------------

    private void ShowRequest()
    {
        _ = capture.Stop();
        session.Note = null;
        session.Phase = AirGapSignSession.PhaseKind.Showing;
    }

    private void Scan()
    {
        session.Note = null;
        cameraState = CameraState.Starting;
        session.Phase = AirGapSignSession.PhaseKind.Scanning;
        _ = StartCamera();
    }

    private async Task Paste()
    {
        var texts = await QRCameraDevices.TextsFromClipboard();
        if (texts.Count == 0)
        {
            session.Note = "The clipboard holds no QR code image and no text.";
            return;
        }
        foreach (var text in texts) session.Receive(text);
    }

    private async Task StartCamera()
    {
        devices = await QRCameraDevices.Devices();
        if (deviceID is null || devices.All(d => d.Id != deviceID)) deviceID = QRCamera.Preferred(devices)?.Id;
        if (deviceID is not { } id)
        {
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
        if (closed || session.Phase != AirGapSignSession.PhaseKind.Scanning)
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
}
