using Signet.Models;

namespace Signet.Services;

/// <summary>What the Tezos app on a Ledger told us about itself.</summary>
/// <param name="IsWallet">False for the Tezos Baking app, which cannot sign wallet operations.</param>
public sealed record LedgerAppInfo(string Version, bool IsWallet);

/// <summary>
/// Discovery and key derivation on Ledger devices. Signing itself goes through <c>IChainService</c>
/// with a <c>SigningKey.Ledger</c>.
/// </summary>
public interface ILedgerService
{
    Task<IReadOnlyList<LedgerDevice>> Devices();
    /// <summary>Fails when the device shows the dashboard or another app.</summary>
    Task<LedgerAppInfo> AppInfo(string deviceID);
    /// <summary>Public key and address for <c>key</c>; with <c>prompt</c> the device shows the address for the user to approve.</summary>
    Task<(string PublicKey, Address Address)> AddressFor(string deviceID, LedgerKey key, bool prompt);
    /// <summary>octez-client's "ledger id": the ed25519 address at <c>44'/1729'</c>.</summary>
    Task<Address> RootAddress(string deviceID);
}

/// <summary>The real thing: Taquito's <c>LedgerSigner</c> over the native HID transport, via the bridge.</summary>
public sealed class BridgeLedgerService(TaquitoBridge? bridge = null) : ILedgerService
{
    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;

    public async Task<IReadOnlyList<LedgerDevice>> Devices()
    {
        IReadOnlyList<JSONValue>? list;
        try { list = (await bridge.Call("ledgerDevices")).ArrayValue; }
        catch (Exception) { return []; }
        return (list ?? [])
            .Select(item => item["id"]?.StringValue is { } id
                ? new LedgerDevice(id, item["name"]?.StringValue ?? "", (int)(item["productID"]?.DoubleValue ?? 0))
                : null)
            .OfType<LedgerDevice>()
            .ToList();
    }

    public async Task<LedgerAppInfo> AppInfo(string deviceID)
    {
        var r = await TaquitoBridge.BridgeError.Typed(() => bridge.Call("ledgerAppVersion", deviceID));
        return new LedgerAppInfo(r["version"]?.StringValue ?? "?", r["isWallet"]?.BoolValue ?? true);
    }

    public async Task<(string PublicKey, Address Address)> AddressFor(string deviceID, LedgerKey key, bool prompt)
    {
        var r = await TaquitoBridge.BridgeError.Typed(() => bridge.Call("ledgerGetAddress", deviceID, key.FullPath, key.Curve.DerivationType(), prompt));
        if (r["publicKey"]?.StringValue is not { } publicKey || r["address"]?.StringValue is not { } address)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected Ledger payload: {r}");
        return (publicKey, new Address(address));
    }

    public async Task<Address> RootAddress(string deviceID)
    {
        var r = await TaquitoBridge.BridgeError.Typed(() =>
            bridge.Call("ledgerGetAddress", deviceID, string.Join('/', LedgerKey.RootPath), LedgerCurve.Ed25519.DerivationType(), false));
        return r["address"]?.StringValue is { } address
            ? new Address(address)
            : throw new TaquitoBridge.BridgeError.JavaScript("no root address from Ledger");
    }
}

/// <summary>For previews and tests: a pretend Nano with fixed keys.</summary>
public sealed class MockLedgerService(IReadOnlyList<LedgerDevice>? connected = null, bool appOpen = true) : ILedgerService
{
    public static readonly LedgerDevice SampleDevice = new("4242", "Nano S Plus", 0x5011);
    public static readonly Address RootAddressValue = new("tz1LedgerRootMockMockMockMockMockMockMo");
    public IReadOnlyList<LedgerDevice> Connected { get; set; } = connected ?? [];
    public bool AppOpen { get; set; } = appOpen;

    public Task<IReadOnlyList<LedgerDevice>> Devices() => Task.FromResult(Connected);

    public Task<LedgerAppInfo> AppInfo(string deviceID) =>
        AppOpen ? Task.FromResult(new LedgerAppInfo("3.0.5", true)) : Task.FromException<LedgerAppInfo>(new ChainError.LedgerAppNotOpen());

    public Task<(string PublicKey, Address Address)> AddressFor(string deviceID, LedgerKey key, bool prompt)
    {
        if (!AppOpen) return Task.FromException<(string, Address)>(new ChainError.LedgerAppNotOpen());
        var n = key.Account ?? 0;
        return Task.FromResult(key.Curve switch
        {
            LedgerCurve.Ed25519 or LedgerCurve.Bip25519 => ($"edpkMockLedger{n}", new Address($"tz1Ledger{key.Curve.RawValue()}{n}")),
            LedgerCurve.Secp256k1 => ($"sppkMockLedger{n}", new Address($"tz2Ledger{n}")),
            _ => ($"p2pkMockLedger{n}", new Address($"tz3Ledger{n}")),
        });
    }

    public Task<Address> RootAddress(string deviceID) => Task.FromResult(RootAddressValue);
}
