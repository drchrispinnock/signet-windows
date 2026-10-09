namespace Signet.Services;

/// <summary>
/// USB HID access to Ledger devices. Placeholder until the HID layer is ported (HidSharp, vendor
/// 0x2c97, usage page 0xFFA0, <c>LedgerFraming</c>): it reports no devices and fails every exchange,
/// which is what the bridge sees on a machine with no Ledger plugged in.
/// </summary>
public sealed class LedgerHID
{
    public static LedgerHID Shared { get; } = new();

    public sealed record Device(string Id, string Name, string Model, int ProductID);

    public IReadOnlyList<Device> Devices() => [];

    /// <summary>Sends one APDU and reports the response (data ‖ status word) or an error message.</summary>
    public void Exchange(string deviceID, byte[] apdu, TimeSpan timeout, Action<byte[]?, string?> completion) =>
        completion(null, "No Ledger is connected. Plug it in, unlock it and open the Tezos app.");
}
