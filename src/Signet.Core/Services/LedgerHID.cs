using HidSharp;
using HidSharp.Reports;

namespace Signet.Services;

/// <summary>
/// Ledger's USB HID framing: an APDU is cut into 64-byte reports, each headed by the channel
/// (0x0101), a tag (0x05) and a sequence number; the first report also carries the total length.
/// Responses come back the same way. Pure functions, so the tests can cover them.
/// </summary>
public static class LedgerFraming
{
    public const int PacketSize = 64;
    public static readonly byte[] Channel = [0x01, 0x01];
    public const byte Tag = 0x05;

    public static IReadOnlyList<byte[]> Packets(byte[] apdu)
    {
        var packets = new List<byte[]>();
        var offset = 0;
        var sequence = 0;
        do
        {
            var packet = new List<byte>(PacketSize) { Channel[0], Channel[1], Tag, (byte)(sequence >> 8), (byte)(sequence & 0xff) };
            if (sequence == 0) packet.AddRange([(byte)(apdu.Length >> 8), (byte)(apdu.Length & 0xff)]);
            var room = PacketSize - packet.Count;
            var chunk = apdu.Skip(offset).Take(room).ToArray();
            packet.AddRange(chunk);
            while (packet.Count < PacketSize) packet.Add(0);
            packets.Add([.. packet]);
            offset += chunk.Length;
            sequence++;
        } while (offset < apdu.Length);
        return packets;
    }

    /// <summary>Collects incoming reports until a whole response has arrived.</summary>
    public sealed class Reassembler
    {
        public class FramingError(string message) : Exception(message)
        {
            public sealed class BadHeader() : FramingError("The Ledger sent a malformed report.");
            public sealed class OutOfOrder(int expected, int got) : FramingError($"The Ledger's reports arrived out of order (expected {expected}, got {got}).");
        }

        private int? expectedLength;
        private readonly List<byte> data = [];
        private int nextSequence;

        /// <summary>Feeds one report. Returns the complete response once the last report is in.</summary>
        public byte[]? Add(byte[] packet)
        {
            if (packet.Length < 5 || packet[0] != Channel[0] || packet[1] != Channel[1] || packet[2] != Tag) throw new FramingError.BadHeader();
            var sequence = packet[3] << 8 | packet[4];
            if (sequence != nextSequence) throw new FramingError.OutOfOrder(nextSequence, sequence);
            IEnumerable<byte> body;
            if (sequence == 0)
            {
                if (packet.Length < 7) throw new FramingError.BadHeader();
                expectedLength = packet[5] << 8 | packet[6];
                body = packet.Skip(7);
            }
            else
            {
                body = packet.Skip(5);
            }
            if (expectedLength is not { } expected) throw new FramingError.BadHeader();
            data.AddRange(body.Take(expected - data.Count));
            nextSequence++;
            if (data.Count < expected) return null;
            var complete = data.ToArray();
            expectedLength = null;
            data.Clear();
            nextSequence = 0;
            return complete;
        }
    }

    /// <summary>
    /// Which exchange the reports arriving on a channel belong to (S22). An exchange that timed out
    /// or failed to write leaves the channel desynchronised: its late reports must never satisfy a
    /// later exchange, so the tracker refuses to start another until it is reset, which the HID layer
    /// does by closing and reopening the device. Pure, so the tests can cover it.
    /// </summary>
    public sealed class ExchangeTracker
    {
        public sealed class TrackerError(string message, bool desynchronised) : Exception(message)
        {
            public bool IsDesynchronised { get; } = desynchronised;
            public static TrackerError Desynchronised => new("The Ledger channel is out of step; it will be reopened.", true);
            public static TrackerError Busy => new("The Ledger is busy with another request.", false);
        }

        public int? Current { get; private set; }
        public bool IsDesynchronised { get; private set; }
        private Reassembler reassembler = new();

        /// <summary>
        /// Exchange ids are unique for the life of the process, so an id from a channel that was
        /// closed can never name an exchange on the one that replaced it.
        /// </summary>
        private static int nextID;

        /// <summary>Begins an exchange and returns its id; refuses while another is pending or after a fault.</summary>
        public int Start()
        {
            if (IsDesynchronised) throw TrackerError.Desynchronised;
            if (Current is not null) throw TrackerError.Busy;
            var id = Interlocked.Increment(ref nextID);
            Current = id;
            reassembler = new Reassembler();
            return id;
        }

        /// <summary>The exchange timed out or could not be written: whatever the device sends next is stale.</summary>
        public void Abandon(int id)
        {
            if (Current != id) return;
            Current = null;
            IsDesynchronised = true;
            reassembler = new Reassembler();
        }

        /// <summary>
        /// A report arrived. Returns the complete response when it finishes the current exchange,
        /// <c>null</c> when more is needed or when there is no current exchange (a stale report, dropped).
        /// A framing error marks the channel desynchronised.
        /// </summary>
        public (int Id, byte[] Response)? Receive(byte[] packet)
        {
            if (Current is not { } id || IsDesynchronised) return null;
            try
            {
                if (reassembler.Add(packet) is not { } response) return null;
                Current = null;
                return (id, response);
            }
            catch (Exception)
            {
                Abandon(id);
                throw;
            }
        }

        /// <summary>Whether a report arriving now would count towards exchange <c>id</c>.</summary>
        public bool Belongs(int id) => Current == id && !IsDesynchronised;
    }
}

/// <summary>A Ledger plugged in over USB, as the HID layer sees it.</summary>
/// <param name="Id">The HID device path: stable while the device stays plugged in (upstream: the IOKit registry entry id).</param>
public sealed record LedgerDevice(string Id, string Name, int ProductID)
{
    /// <summary>Model name from the product id when the product string is missing or generic.</summary>
    public string Model => (ProductID >> 8) switch
    {
        0x10 => "Ledger Nano S",
        0x40 => "Ledger Nano X",
        0x50 => "Ledger Nano S Plus",
        0x60 => "Ledger Stax",
        0x70 => "Ledger Flex",
        _ => Name.Length == 0 ? "Ledger" : Name,
    };
}

/// <summary>
/// Talks to Ledger devices over USB HID with HidSharp (upstream: IOKit). One exchange at a time per
/// device; each exchange writes its reports and reads the answer on a worker task, and callers get
/// their answer through the completion (the bridge posts it back to its own thread).
///
/// Windows: reports carry a leading report-id byte (0) both ways, added and stripped here. A
/// read that times out, fails or comes back malformed closes the device, so the next exchange
/// opens it afresh with a new tracker (S22), as upstream closes and reopens it.
/// </summary>
public sealed class LedgerHID
{
    public static class HIDError
    {
        public const string NoSuchDevice = "No Ledger is connected. Plug it in, unlock it and open the Tezos app.";
        public static string OpenFailed(string detail) => $"Could not open the Ledger ({detail}).";
        public static string WriteFailed(string detail) => $"Could not write to the Ledger ({detail}).";
        public const string Timeout = "The Ledger did not answer in time.";
        public const string Busy = "The Ledger is busy with another request.";
        public const string Unplugged = "The Ledger was unplugged.";
    }

    public static LedgerHID Shared { get; } = new();
    public const int VendorID = 0x2c97;
    /// <summary>Ledger's vendor-specific HID interface; the same devices also show keyboard/U2F interfaces.</summary>
    public const uint UsagePage = 0xffa0;

    private readonly Lock gate = new();
    private readonly Dictionary<string, OpenDevice> open = [];

    private sealed class OpenDevice(HidDevice device, HidStream stream)
    {
        public HidDevice Device { get; } = device;
        public HidStream Stream { get; } = stream;
        public LedgerFraming.ExchangeTracker Tracker { get; } = new();
        public bool Pending { get; set; }
    }

    /// <summary>Ledgers currently plugged in (vendor-specific HID interface only).</summary>
    public IReadOnlyList<LedgerDevice> Devices()
    {
        try
        {
            return DeviceList.Local.GetHidDevices(VendorID)
                .Where(IsLedgerInterface)
                .Select(Describe)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The interface whose report descriptor uses Ledger's vendor page (not the keyboard or FIDO ones).</summary>
    private static bool IsLedgerInterface(HidDevice device)
    {
        try
        {
            return device.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .Any(usage => usage >> 16 == UsagePage);
        }
        catch (Exception)
        {
            // No descriptor: Windows names the vendor interface as interface 0 in its path.
            return device.DevicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LedgerDevice Describe(HidDevice device)
    {
        string name;
        try { name = device.GetProductName() ?? ""; }
        catch (Exception) { name = ""; }
        return new LedgerDevice(device.DevicePath, name, device.ProductID);
    }

    /// <summary>Sends one APDU and reports the response (data ‖ status word) or an error message.</summary>
    public void Exchange(string deviceID, byte[] apdu, TimeSpan timeout, Action<byte[]?, string?> completion) =>
        _ = Task.Run(() =>
        {
            var (response, error) = Run(deviceID, apdu, timeout);
            completion(response, error);
        });

    private (byte[]? Response, string? Error) Run(string deviceID, byte[] apdu, TimeSpan timeout)
    {
        OpenDevice entry;
        int exchangeID;
        lock (gate)
        {
            try { entry = OpenDeviceFor(deviceID); }
            catch (LedgerException e) { return (null, e.Message); }
            if (entry.Pending) return (null, HIDError.Busy);
            try { exchangeID = entry.Tracker.Start(); }
            catch (LedgerFraming.ExchangeTracker.TrackerError e) when (!e.IsDesynchronised) { return (null, HIDError.Busy); }
            catch (LedgerFraming.ExchangeTracker.TrackerError e) { Desynchronise(entry); return (null, e.Message); }
            entry.Pending = true;
        }

        var stream = entry.Stream;
        var outLength = entry.Device.GetMaxOutputReportLength();
        var inLength = entry.Device.GetMaxInputReportLength();
        try
        {
            // Windows reports start with the report id (0), so a 64-byte report is written as 65 bytes.
            var withReportID = outLength > LedgerFraming.PacketSize;
            foreach (var packet in LedgerFraming.Packets(apdu))
            {
                var report = new byte[withReportID ? outLength : LedgerFraming.PacketSize];
                Array.Copy(packet, 0, report, withReportID ? 1 : 0, packet.Length);
                stream.Write(report, 0, report.Length);
            }
        }
        catch (Exception e)
        {
            // A half-written APDU leaves the device in an unknown state: drop the channel.
            Fail(entry, exchangeID);
            return (null, HIDError.WriteFailed(e.Message));
        }

        var deadline = DateTime.UtcNow + timeout;
        var buffer = new byte[Math.Max(inLength, LedgerFraming.PacketSize + 1)];
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Fail(entry, exchangeID);
                return (null, HIDError.Timeout);
            }
            int read;
            try
            {
                stream.ReadTimeout = (int)Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue);
                read = stream.Read(buffer, 0, inLength > 0 ? inLength : buffer.Length);
            }
            catch (TimeoutException)
            {
                // A timed-out exchange may still be answered later; that answer must not reach the
                // next exchange, so the channel is closed and the device reopened fresh next time (S22).
                Fail(entry, exchangeID);
                return (null, HIDError.Timeout);
            }
            catch (Exception)
            {
                Fail(entry, exchangeID);
                return (null, HIDError.Unplugged);
            }
            if (read <= 0) continue;
            // Strip the report id when the report is one longer than Ledger's 64 bytes.
            var packet = read > LedgerFraming.PacketSize ? buffer[1..read] : buffer[..read];
            lock (gate)
            {
                try
                {
                    if (entry.Tracker.Receive(packet) is not { } done || done.Id != exchangeID) continue;
                    entry.Pending = false;
                    return (done.Response, null);
                }
                catch (Exception e)
                {
                    entry.Pending = false;
                    Desynchronise(entry);
                    return (null, e.Message);
                }
            }
        }
    }

    private sealed class LedgerException(string message) : Exception(message);

    private OpenDevice OpenDeviceFor(string id)
    {
        if (open.TryGetValue(id, out var existing)) return existing;
        var device = DeviceList.Local.GetHidDevices(VendorID).FirstOrDefault(d => d.DevicePath == id)
            ?? throw new LedgerException(HIDError.NoSuchDevice);
        if (!device.TryOpen(out var stream)) throw new LedgerException(HIDError.OpenFailed("the device refused to open"));
        var entry = new OpenDevice(device, stream);
        open[id] = entry;
        return entry;
    }

    private void Fail(OpenDevice entry, int exchangeID)
    {
        lock (gate)
        {
            entry.Pending = false;
            entry.Tracker.Abandon(exchangeID);
            Desynchronise(entry);
        }
    }

    /// <summary>
    /// Closes the device and forgets it. Late reports from it are never delivered; the next
    /// exchange opens it again with a fresh tracker.
    /// </summary>
    private void Desynchronise(OpenDevice entry)
    {
        foreach (var key in open.Where(pair => pair.Value == entry).Select(pair => pair.Key).ToList()) open.Remove(key);
        try { entry.Stream.Dispose(); }
        catch (Exception) { }
    }
}
