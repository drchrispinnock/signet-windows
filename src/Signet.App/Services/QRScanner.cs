using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Signet.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Signet.App.Services;

/// <summary>
/// The Windows half of upstream's <c>QRCamera</c> (AVFoundation there): the cameras, and pictures
/// from the clipboard or a file turned into <c>QRCode.Bitmap</c>s for Core to read.
/// </summary>
public static class QRCameraDevices
{
    /// <summary>Every enabled video capture device: built-in, USB and display cameras.</summary>
    public static async Task<IReadOnlyList<CameraDevice>> Devices()
    {
        var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return found.Where(d => d.IsEnabled).Select(d => new CameraDevice(d.Id, d.Name)).ToList();
    }

    /// <summary>
    /// What the clipboard holds for Scan QR code: the codes in an image on it (a screenshot),
    /// else its text (upstream <c>textsFromPasteboard</c>). Decided by <c>QRCamera.TextsFromPasteboard</c>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TextsFromClipboard()
    {
        var content = Clipboard.GetContent();
        QRCode.Bitmap? image = null;
        string? text = null;
        try
        {
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                image = await Decode(stream);
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { }
        try
        {
            if (content.Contains(StandardDataFormats.Text)) text = await content.GetTextAsync();
        }
        catch (COMException) { }
        // QR decoding is CPU work; keep it off the UI thread.
        return await Task.Run(() => QRCamera.TextsFromPasteboard(image, text));
    }

    /// <summary>Every QR code in an image file, or none when it cannot be read as an image.</summary>
    public static async Task<IReadOnlyList<string>> CodesInFile(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            if (await Decode(stream) is not { } image) return [];
            return await Task.Run(() => QRCode.DecodeAll(image));
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>An encoded image (PNG, JPEG, BMP, …) decoded by Windows Imaging Component.</summary>
    private static async Task<QRCode.Bitmap?> Decode(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        return Pixels(bitmap);
    }

    /// <summary>A Bgra8 software bitmap's pixels, copied out.</summary>
    internal static QRCode.Bitmap? Pixels(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new QRCode.Bitmap(pixels, bitmap.PixelWidth, bitmap.PixelHeight, QRCode.Bitmap.Layout.Bgra32);
    }
}

/// <summary>
/// Tells the model whether a camera is attached, now and as cameras come and go (a webcam plugged
/// in, a laptop docked). The app makes one and keeps it for as long as it runs.
/// </summary>
public sealed class CameraWatcher
{
    private readonly DeviceWatcher watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
    private readonly HashSet<string> enabled = [];
    private readonly object gate = new();
    private readonly DispatcherQueue queue;
    private readonly Action<bool> onChange;
    private bool ready;

    public CameraWatcher(DispatcherQueue queue, Action<bool> onChange)
    {
        this.queue = queue;
        this.onChange = onChange;
        watcher.Added += (_, info) => Update(() => { if (info.IsEnabled) enabled.Add(info.Id); });
        watcher.Updated += (_, update) => Update(() =>
        {
            if (update.Properties.TryGetValue("System.Devices.InterfaceEnabled", out var value) && value is bool on)
            {
                if (on) enabled.Add(update.Id);
                else enabled.Remove(update.Id);
            }
        });
        watcher.Removed += (_, update) => Update(() => enabled.Remove(update.Id));
        watcher.EnumerationCompleted += (_, _) => Update(() => ready = true);
        watcher.Start();
    }

    private void Update(Action change)
    {
        bool any;
        lock (gate)
        {
            change();
            if (!ready) return;
            any = enabled.Count > 0;
        }
        queue.TryEnqueue(() => onChange(any));
    }
}

/// <summary>
/// Reads QR codes from a camera: frames arrive on a worker thread, a few a second go to ZXing,
/// and each distinct code is reported on <c>CodeFound</c>. Every frame is also offered to
/// <c>FrameArrived</c> for the preview. Nothing is recorded, kept or logged; a code may be a secret key.
/// </summary>
public sealed class QRCaptureSession : IAsyncDisposable
{
    /// <summary>A code seen; raised on a worker thread.</summary>
    public event Action<string>? CodeFound;
    /// <summary>A frame for the preview, Bgra8 premultiplied; raised on a worker thread. The handler owns it.</summary>
    public event Action<SoftwareBitmap>? FrameArrived;

    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DateTime> seen = [];
    private DateTime lastDecode = DateTime.MinValue;
    private int decoding;

    /// <summary>Why the camera could not start.</summary>
    public enum Failure { Denied, Unavailable }

    /// <summary>Starts (or switches to) the camera with this id; <c>null</c> when it runs.</summary>
    public async Task<Failure?> Start(string deviceID)
    {
        await gate.WaitAsync();
        try
        {
            await StopLocked();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceID,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            };
            var media = new MediaCapture();
            try
            {
                try { await media.InitializeAsync(settings); }
                catch (Exception e) when (e is not UnauthorizedAccessException)
                {
                    // Another app has the camera; watching alongside it is enough to read a code.
                    media.Dispose();
                    media = new MediaCapture();
                    settings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
                    await media.InitializeAsync(settings);
                }
                var source = media.FrameSources.Values
                    .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                    .OrderBy(s => s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ? 0 : 1)
                    .FirstOrDefault();
                if (source is null)
                {
                    media.Dispose();
                    return Failure.Unavailable;
                }
                if (settings.SharingMode == MediaCaptureSharingMode.ExclusiveControl) await PreferModestFormat(source);
                var frames = await media.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
                frames.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                frames.FrameArrived += Arrived;
                if (await frames.StartAsync() != MediaFrameReaderStartStatus.Success)
                {
                    frames.Dispose();
                    media.Dispose();
                    return Failure.Unavailable;
                }
                lock (seen) seen.Clear();
                capture = media;
                reader = frames;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                media.Dispose();
                return Failure.Denied;
            }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                media.Dispose();
                return Failure.Unavailable;
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// A frame size around 1280 × 720 when the camera offers one: enough for a printed code, and
    /// quicker to search than a 4K frame.
    /// </summary>
    private static async Task PreferModestFormat(MediaFrameSource source)
    {
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width is >= 640 and <= 1920)
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - 1280))
            .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
            .FirstOrDefault();
        if (format is null) return;
        try { await source.SetFormatAsync(format); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
    }

    /// <summary>Stops the camera; its light goes out.</summary>
    public async Task Stop()
    {
        await gate.WaitAsync();
        try { await StopLocked(); }
        finally { gate.Release(); }
    }

    private async Task StopLocked()
    {
        if (reader is { } frames)
        {
            frames.FrameArrived -= Arrived;
            try { await frames.StopAsync(); } catch (COMException) { }
            frames.Dispose();
            reader = null;
        }
        capture?.Dispose();
        capture = null;
        lock (seen) seen.Clear();
    }

    public async ValueTask DisposeAsync() => await Stop();

    private void Arrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap) return;

        if (FrameArrived is { } preview)
            preview(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));

        // A few searches a second are plenty; one at a time, never queued behind each other.
        var now = DateTime.UtcNow;
        if ((now - lastDecode).TotalMilliseconds < 200 || Interlocked.Exchange(ref decoding, 1) == 1) return;
        lastDecode = now;
        try
        {
            using var copy = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            if (QRCameraDevices.Pixels(copy) is not { } image) return;
            // Every code in view is reported (a recovery sheet can show several parts of one key at
            // once). A code stays in view for many frames; say it once, and again after a pause so
            // a code the sheet could not use can be tried again.
            var fresh = new List<string>();
            lock (seen)
            {
                foreach (var stale in seen.Where(s => (now - s.Value).TotalSeconds > 3).Select(s => s.Key).ToList()) seen.Remove(stale);
                foreach (var code in QRCode.DecodeAll(image, thorough: false))
                {
                    if (seen.ContainsKey(code)) continue;
                    seen[code] = now;
                    fresh.Add(code);
                }
            }
            foreach (var code in fresh) CodeFound?.Invoke(code);
        }
        finally { Volatile.Write(ref decoding, 0); }
    }
}
