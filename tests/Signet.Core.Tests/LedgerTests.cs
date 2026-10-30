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

    [Fact(DisplayName = "connecting a Ledger writes an octez ledger:// alias and selects it")]
    public async Task ConnectWritesLocator()
    {
        var dir = TestSupport.TempDir("ledger");
        var store = new TezosClientStore(dir);
        var model = new Signet.ViewModels.WalletViewModel(chain: new MockChainService(), walletStore: store,
            ledger: new MockLedgerService([MockLedgerService.SampleDevice]));

        await model.ConnectLedger("Nano", MockLedgerService.SampleDevice.Id, LedgerCurve.P256, 1);
        var wallet = model.SelectedWallet;
        Assert.NotNull(wallet);
        Assert.Equal("Nano", wallet.Alias);
        Assert.Equal(KeyKind.Ledger, wallet.KeyKind);
        Assert.Equal(AddressScheme.Tz3, wallet.Scheme);
        Assert.Equal(new LedgerKey(MockLedgerService.RootAddressValue.Value, LedgerCurve.P256, 1), wallet.LedgerKey);
        Assert.Equal("p2pkMockLedger1", wallet.PublicKey);

        var locator = $"ledger://{MockLedgerService.RootAddressValue.Value}/P-256/1h/0h";
        using (var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "secret_keys"))))
            Assert.Equal(locator, secrets.RootElement[0].GetProperty("value").GetString());
        using (var publics = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "public_keys"))))
            Assert.Equal(locator, publics.RootElement[0].GetProperty("value").GetProperty("locator").GetString());

        Assert.Equal(new SigningKey.Ledger(wallet.LedgerKey!, wallet.Address), model.SigningKeyFor(wallet, null));

        await Assert.ThrowsAnyAsync<Signet.ViewModels.WalletViewModel.WalletError>(() =>
            model.ConnectLedger("Again", MockLedgerService.SampleDevice.Id, LedgerCurve.P256, 1));
    }
}

/// <summary>Ledger HID framing</summary>
public class LedgerFramingTests
{
    [Fact(DisplayName = "short APDUs fit one 64-byte report")]
    public void SinglePacket()
    {
        byte[] apdu = [0x80, 0x02, 0x00, 0x00, 0x00];
        var packets = LedgerFraming.Packets(apdu);
        Assert.Single(packets);
        Assert.Equal(64, packets[0].Length);
        Assert.Equal([0x01, 0x01, 0x05, 0x00, 0x00, 0x00, 0x05], packets[0][..7]);
        Assert.Equal(apdu, packets[0][7..12]);
        Assert.All(packets[0][12..], b => Assert.Equal(0, b));
    }

    [Fact(DisplayName = "long APDUs span reports with sequence numbers and reassemble")]
    public void MultiPacket()
    {
        var apdu = Enumerable.Range(0, 150).Select(i => (byte)(i & 0xff)).ToArray();
        var packets = LedgerFraming.Packets(apdu);
        Assert.Equal(3, packets.Count);   // 57 + 59 + 34
        Assert.Equal([0x01, 0x01, 0x05, 0x00, 0x01], packets[1][..5]);
        Assert.Equal([0x01, 0x01, 0x05, 0x00, 0x02], packets[2][..5]);

        var assembler = new LedgerFraming.Reassembler();
        Assert.Null(assembler.Add(packets[0]));
        Assert.Null(assembler.Add(packets[1]));
        Assert.Equal(apdu, assembler.Add(packets[2]));
    }

    [Fact(DisplayName = "reassembler rejects bad headers and out-of-order reports")]
    public void Rejects()
    {
        var assembler = new LedgerFraming.Reassembler();
        Assert.ThrowsAny<LedgerFraming.Reassembler.FramingError>(() => assembler.Add([0x00, 0x00, 0x05, 0, 0, 0, 1, 0x90]));
        var packets = LedgerFraming.Packets(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        Assert.ThrowsAny<LedgerFraming.Reassembler.FramingError>(() => assembler.Add(packets[1]));
    }

    /// <summary>
    /// S22: a response that arrives after its exchange timed out must not be taken as the
    /// answer to the next exchange. Once an exchange is abandoned the channel is dead until the
    /// device is reopened (a fresh tracker), and stale reports in between are dropped.
    /// </summary>
    [Fact(DisplayName = "a late response never satisfies a later exchange")]
    public void StaleResponsesAreDropped()
    {
        var first = LedgerFraming.Packets([0xde, 0xad, 0x90, 0x00]);
        var second = LedgerFraming.Packets([0xbe, 0xef, 0x90, 0x00]);
        var tracker = new LedgerFraming.ExchangeTracker();

        var one = tracker.Start();
        Assert.True(tracker.Belongs(one));
        tracker.Abandon(one);                       // timed out
        Assert.True(tracker.IsDesynchronised);
        Assert.Null(tracker.Receive(first[0]));     // the device answers late: dropped
        Assert.True(Assert.Throws<LedgerFraming.ExchangeTracker.TrackerError>(() => tracker.Start()).IsDesynchronised);

        // The HID layer reopens the device, which means a new tracker.
        var reopened = new LedgerFraming.ExchangeTracker();
        var two = reopened.Start();
        Assert.False(reopened.Belongs(one));
        var done = reopened.Receive(second[0]);
        Assert.NotNull(done);
        Assert.Equal(two, done.Value.Id);
        Assert.Equal([0xbe, 0xef, 0x90, 0x00], done.Value.Response);
        Assert.Null(reopened.Receive(first[0]));    // nothing pending now: dropped
    }

    [Fact(DisplayName = "a long stale response mid-flight breaks the exchange rather than completing it")]
    public void PartialStaleResponseFailsTheExchange()
    {
        var longReply = LedgerFraming.Packets(Enumerable.Range(0, 150).Select(i => (byte)(i & 0xff)).ToArray());
        var tracker = new LedgerFraming.ExchangeTracker();
        var id = tracker.Start();
        // The second report of an older multi-packet answer arrives first: out of order, so the
        // exchange fails and the channel is marked desynchronised instead of mixing bytes.
        Assert.ThrowsAny<LedgerFraming.Reassembler.FramingError>(() => tracker.Receive(longReply[1]));
        Assert.True(!tracker.Belongs(id) && tracker.IsDesynchronised);
        Assert.True(Assert.Throws<LedgerFraming.ExchangeTracker.TrackerError>(() => tracker.Start()).IsDesynchronised);
    }

    [Fact(DisplayName = "one exchange at a time per channel")]
    public void OneAtATime()
    {
        var tracker = new LedgerFraming.ExchangeTracker();
        var id = tracker.Start();
        Assert.False(Assert.Throws<LedgerFraming.ExchangeTracker.TrackerError>(() => tracker.Start()).IsDesynchronised);   // busy
        var reply = LedgerFraming.Packets([0x90, 0x00]);
        Assert.Equal(id, tracker.Receive(reply[0])?.Id);
        Assert.True(tracker.Start() > id);
    }
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

    [Fact(DisplayName = "signing with no Ledger connected fails cleanly")]
    public async Task NoDevice()
    {
        var devices = await new BridgeLedgerService().Devices();
        if (devices.Count > 0) return;   // a real Ledger is plugged in; the HID path is exercised by hand
        await Assert.ThrowsAsync<ChainError.LedgerNotConnected>(() => new BridgeLedgerService().AppInfo(""));
    }

    /// <summary>Part of upstream's AuditRepairTests: one device lease across bridge contexts.</summary>
    [Fact]
    public async Task LedgerDeviceLeaseSerializesIndependentClients()
    {
        var sessions = new LedgerSessions();
        var first = await sessions.Acquire("synthetic-device", TestContext.Current.CancellationToken);
        var entered = false;
        var other = Task.Run(async () =>
        {
            var lease = await sessions.Acquire("synthetic-device", TestContext.Current.CancellationToken);
            entered = true;
            return lease;
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(entered);
        sessions.Release(first, "synthetic-device");
        var second = await other;
        Assert.True(entered);
        sessions.Release(second, "synthetic-device");
    }
}
