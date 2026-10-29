using System.Text.Json;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>Ledger keys</summary>
public class LedgerKeyTests
{
    [Fact(DisplayName = "parses octez-client locators")]
    public void ParsesLocators()
    {
        var key = LedgerKey.FromLocator("ledger://masculine-pig-stupendous-coyote/ed25519/0h/0h");
        Assert.NotNull(key);
        Assert.Equal("masculine-pig-stupendous-coyote", key.RootID);
        Assert.Equal(LedgerCurve.Ed25519, key.Curve);
        Assert.Equal(["0'", "0'"], key.RelativePath);
        Assert.Equal("44'/1729'/0'/0'", key.FullPath);
        Assert.Equal(0, key.Account);
        Assert.Equal("ledger://masculine-pig-stupendous-coyote/ed25519/0h/0h", key.Locator);

        var explicitPath = LedgerKey.FromLocator("ledger://tz1abc/P-256/44'/1729'/3'/0'");
        Assert.NotNull(explicitPath);
        Assert.Equal(LedgerCurve.P256, explicitPath.Curve);
        Assert.Equal("44'/1729'/3'/0'", explicitPath.FullPath);
        Assert.Equal(3, explicitPath.Account);
        Assert.Equal("ledger://tz1abc/P-256/3h/0h", explicitPath.Locator);

        Assert.Equal(LedgerCurve.Bip25519, LedgerKey.FromLocator("ledger://x/bip32-ed25519/1h/0h")?.Curve);
        Assert.Equal(AddressScheme.Tz2, LedgerKey.FromLocator("ledger://x/secp256k1/0h/0h")?.Curve.Scheme());
        Assert.Null(LedgerKey.FromLocator("ledger://x/secp256k1/0h/1h")?.Account);
        Assert.Null(LedgerKey.FromLocator("unencrypted:edsk"));
        Assert.Null(LedgerKey.FromLocator("ledger://x/weird/0h"));
        Assert.Null(LedgerKey.FromLocator("ledger://x/ed25519/0h/zz"));
    }

    [Fact(DisplayName = "curves map to Taquito derivation types and schemes")]
    public void Curves()
    {
        Assert.Equal([0, 1, 2, 3], LedgerCurveExtensions.AllCases.Select(c => c.DerivationType()));
        Assert.Equal(LedgerCurve.P256, LedgerCurveExtensions.FromOctezName("secp256r1"));
        Assert.Equal(AddressScheme.Tz1, LedgerCurve.Bip25519.Scheme());
    }

    [Fact(DisplayName = "signing key specs for the bridge")]
    public void Specs()
    {
        var ledger = new SigningKey.Ledger(new LedgerKey("tz1root", LedgerCurve.Secp256k1, account: 2), new Address("tz2abc"));
        var spec = JsonDocument.Parse(ledger.BridgeSpec).RootElement;
        Assert.Equal("ledger", spec.GetProperty("kind").GetString());
        Assert.Equal("44'/1729'/2'/0'", spec.GetProperty("path").GetString());
        Assert.Equal(1, spec.GetProperty("derivationType").GetInt32());
        Assert.Equal("tz2abc", spec.GetProperty("address").GetString());
        Assert.True(ledger.IsLedger);

        var secret = new SigningKey.Secret("edskX", "pw", new Address("tz1abc"));
        var s2 = JsonDocument.Parse(secret.BridgeSpec).RootElement;
        Assert.Equal("secret", s2.GetProperty("kind").GetString());
        Assert.Equal("edskX", s2.GetProperty("secretKey").GetString());
        Assert.Equal("pw", s2.GetProperty("passphrase").GetString());
        Assert.False(secret.IsLedger);
    }

    [Fact(DisplayName = "ledger aliases load with their key and can sign")]
    public void LoadsLedgerAliases()
    {
        var dir = TestSupport.MakeDir("ledger");
        TestSupport.WriteText(dir, "public_key_hashs", """
        [{"name":"nano","value":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}]
        """);
        TestSupport.WriteText(dir, "public_keys", """
        [{"name":"nano","value":{"locator":"ledger://tz1root/ed25519/0h/0h","key":"edpkuBknW28nW72KG6RoHtYW7p12T6GKc7nAbwYX5m8Wd9sDVC9yav"}}]
        """);
        TestSupport.WriteText(dir, "secret_keys", """
        [{"name":"nano","value":"ledger://tz1root/ed25519/0h/0h"}]
        """);

        var store = new TezosClientStore(dir);
        var wallet = store.Load()[0];
        Assert.Equal(KeyKind.Ledger, wallet.KeyKind);
        Assert.True(wallet.KeyKind.CanSign());
        Assert.Equal("44'/1729'/0'/0'", wallet.LedgerKey?.FullPath);
        Assert.Null(store.SecretKey(wallet));
    }

    [Fact(DisplayName = "connecting a Ledger writes an octez ledger:// alias and selects it", Skip = Pending.ViewModel)]
    public void ConnectWritesLocator() { }
}

/// <summary>Ledger HID framing</summary>
public class LedgerFramingTests
{
    [Fact(DisplayName = "short APDUs fit one 64-byte report", Skip = Pending.Ledger)]
    public void SinglePacket() { }

    [Fact(DisplayName = "long APDUs span reports with sequence numbers and reassemble", Skip = Pending.Ledger)]
    public void MultiPacket() { }

    [Fact(DisplayName = "reassembler rejects bad headers and out-of-order reports", Skip = Pending.Ledger)]
    public void Rejects() { }
}

/// <summary>Ledger bridge</summary>
public class LedgerBridgeTests
{
    [Fact(DisplayName = "lists devices without crashing when none is plugged in")]
    public async Task Devices()
    {
        var list = await TaquitoBridge.Shared.Call("ledgerDevices");
        Assert.NotNull(list.ArrayValue);
    }

    [Fact(DisplayName = "signing with no Ledger connected fails cleanly", Skip = Pending.Ledger)]
    public void NoDevice() { }
}
