using System.Text;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>Scan QR code: what a scanned text is taken for, and where the model sends it.</summary>
public class QRScanTests
{
    private const string Address = "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb";
    private static readonly string SecretKey = Base58.CheckEncode(TezosPrefix.Edsk, Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly string PublicKey = Base58.CheckEncode(TezosPrefix.Edpk, Enumerable.Repeat((byte)9, 32).ToArray());
    private static readonly string PairingCode = Base58.CheckEncode([], Encoding.UTF8.GetBytes("""{"type":"p2p-pairing-request","name":"dApp"}"""));
    private static readonly string Phrase = string.Join(' ', Enumerable.Repeat("abandon", 11)) + " about";

    private static QRPayload.AddressContent AddressOf(string value) => new(new Address(value));

    [Fact]
    public void ClassifiesAddresses()
    {
        Assert.Equal(AddressOf(Address), QRPayload.Classify(Address));
        Assert.Equal(AddressOf(Address), QRPayload.Classify($"  {Address}\n"));
        Assert.Equal(AddressOf(Address), QRPayload.Classify($"tezos:{Address}"));
        Assert.Equal(AddressOf(Address), QRPayload.Classify($"tezos://{Address}"));
        Assert.Equal(AddressOf("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb"), QRPayload.Classify("KT1AXRU3wLc87WNhLhVGrgqDGubLACUMUgPb"));
        // One wrong character breaks the checksum.
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjc"));
    }

    [Fact]
    public void ClassifiesKeys()
    {
        Assert.StartsWith("edsk", SecretKey);
        Assert.StartsWith("edpk", PublicKey);
        Assert.Equal(new QRPayload.SecretKey(SecretKey), QRPayload.Classify(SecretKey));
        Assert.Equal(new QRPayload.PublicKey(PublicKey), QRPayload.Classify(PublicKey));
        Assert.True(QRPayload.Classify(SecretKey).IsSecret);
        Assert.False(QRPayload.Classify(PublicKey).IsSecret);
        // The right letters without a valid checksum are nothing.
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("edsk" + new string('a', 50)));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("edpk" + new string('a', 50)));
    }

    [Fact]
    public void ClassifiesRecoveryPhrases()
    {
        Assert.Equal(new QRPayload.RecoveryPhrase(Phrase), QRPayload.Classify(Phrase));
        Assert.Equal(new QRPayload.RecoveryPhrase(Phrase), QRPayload.Classify(Phrase.Replace(" ", "\n  ")));
        Assert.True(QRPayload.Classify(Phrase).IsSecret);
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("abandon abandon abandon"));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify(Phrase.Replace("about", "About1")));
    }

    [Fact]
    public void ClassifiesPairingCodes()
    {
        Assert.Equal(new QRPayload.Pairing(PairingCode), QRPayload.Classify(PairingCode));
        Assert.Equal(new QRPayload.Pairing(PairingCode), QRPayload.Classify($"tezos://?type=tzip10&data={PairingCode}"));
        Assert.Equal(new QRPayload.Pairing("CODE"), QRPayload.Classify("signet://?type=tzip10&data=CODE"));
        Assert.Equal(new QRPayload.Pairing("CODE"), QRPayload.Classify("https://wallet.example/?type=tzip10&data=CODE"));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("https://example.com/?type=other&data=CODE"));
        // The signet:// handler itself still takes its own scheme only.
        Assert.Null(DAppPairingLink.Code(new Uri("tezos://?type=tzip10&data=CODE")));
    }

    [Fact]
    public void CollectsThePartsOfALongKey()
    {
        // A tz5 secret key: the mdsk prefix and 3872 bytes.
        var key = Base58.CheckEncode([9, 57, 116, 57], Enumerable.Range(0, 3872).Select(i => unchecked((byte)(i * 13))).ToArray());
        Assert.StartsWith("mdsk", key);
        var parts = KeyPart.Split(key, chunkLength: 1400);
        Assert.Equal(4, parts.Count);
        Assert.Single(parts.Select(p => p.Id).Distinct());
        Assert.All(parts, p =>
        {
            Assert.Equal(new QRPayload.KeyPartContent(p), QRPayload.Classify(p.Text));
            Assert.True(QRPayload.Classify(p.Text).IsSecret);
        });
        Assert.Empty(KeyPart.Split("short"));

        var collector = new KeyPartCollector();
        Assert.True(collector.IsEmpty);
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(parts[2]));
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(parts[2]));
        Assert.True(collector.Missing.SequenceEqual([1, 2, 4]) && collector.Received == 1 && collector.Count == 4);
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(parts[0]));
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(parts[3]));
        Assert.Equal(new KeyPartCollector.Outcome.Complete(key), collector.Add(parts[1]));
        Assert.True(collector.IsEmpty);

        // A part of another key starts over rather than mixing the two.
        var other = KeyPart.Split(Base58.CheckEncode([9, 57, 116, 57], Enumerable.Repeat((byte)9, 3872).ToArray()), chunkLength: 1400);
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(parts[0]));
        Assert.Equal(new KeyPartCollector.Outcome.NeedMore(), collector.Add(other[1]));
        Assert.Equal([1, 3, 4], collector.Missing);

        // Parts that claim one id but do not make that key are refused.
        var forged = new KeyPartCollector();
        KeyPart[] swapped = [parts[0], new KeyPart(2, 4, parts[0].Id, parts[2].Chunk), parts[2], parts[3]];
        Assert.Equal(new KeyPartCollector.Outcome.Invalid(), swapped.Select(forged.Add).ToList()[^1]);
        Assert.True(forged.IsEmpty);
    }

    [Theory]
    [InlineData("signet-key:")]
    [InlineData("signet-key:1/1:abcdef12:abc")]
    [InlineData("signet-key:0/3:abcdef12:abc")]
    [InlineData("signet-key:4/3:abcdef12:abc")]
    [InlineData("signet-key:1/99:abcdef12:abc")]
    [InlineData("signet-key:1/3:xyz:abc")]
    [InlineData("signet-key:1/3:abcdef12:")]
    [InlineData("signet-key:1/3:abcdef12:a b")]
    [InlineData("signet-key:1/3:abcdef12:abc:def")]
    [InlineData("signet-key:one/3:abcdef12:abc")]
    public void RefusesMalformedKeyParts(string text)
    {
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify(text));
        Assert.Equal(new QRPayload.KeyPartContent(new KeyPart(2, 3, "abcdef12", "abc")), QRPayload.Classify("signet-key:2/3:abcdef12:abc"));
    }

    [Fact]
    public void RefusesEverythingElse()
    {
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify(""));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("https://example.com"));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("hello world"));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify(new string('1', QRPayload.MaximumLength + 1)));
        // Valid base58check that is neither a key nor a pairing request.
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify(Base58.CheckEncode([], Encoding.UTF8.GetBytes("plain text"))));
    }

    [Fact]
    public void ReadsAScannedImageBackToItsPayload()
    {
        var image = QRCode.Image(SecretKey, 300);
        Assert.NotNull(image);
        Assert.Equal(new QRPayload.SecretKey(SecretKey), QRCode.Decode(image) is { } text ? QRPayload.Classify(text) : null);
    }

    /// <summary>
    /// Upstream fills a private NSPasteboard; on Windows the app reads the clipboard and Core
    /// decides from what it holds, so this checks that decision.
    /// </summary>
    [Fact]
    public void ReadsTheClipboardAsImageOrText()
    {
        Assert.Null(QRCamera.TextFromPasteboard(null, null));
        Assert.Equal(Address, QRCamera.TextFromPasteboard(null, Address));
        Assert.Equal(Address, QRCamera.TextFromPasteboard(QRCode.Image(Address, 300), null));
        // An image wins over text, and an image with no code falls back to the text.
        Assert.Equal(Address, QRCamera.TextFromPasteboard(QRCode.Image(Address, 300), "something else"));
        var blank = new QRCode.Bitmap(Enumerable.Repeat((byte)255, 100 * 100).ToArray(), 100, 100, QRCode.Bitmap.Layout.Gray8);
        Assert.Equal("copied", QRCamera.TextFromPasteboard(blank, "copied"));
    }

    [Fact]
    public void RoutesAScanToTheSheetThatHandlesIt()
    {
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService());
        Assert.False(model.HasCamera);
        model.RoutePendingScan();
        Assert.False(model.IsPresentingConnectDApp || model.IsPresentingImportAccount || model.IsPresentingAddAddress || model.IsPresentingSend);

        model.PendingScan = new WalletViewModel.ScanHandoff.Pair("CODE");
        model.RoutePendingScan();
        Assert.Null(model.PendingScan);
        Assert.True(model.IsPresentingConnectDApp && model.PendingPairingCode == "CODE");

        model.PendingScan = new WalletViewModel.ScanHandoff.ImportKey(new ImportPrefill.SecretKey(SecretKey));
        model.RoutePendingScan();
        Assert.True(model.IsPresentingImportAccount);
        Assert.Equal(new ImportPrefill.SecretKey(SecretKey), model.ImportPrefill);

        model.PendingScan = new WalletViewModel.ScanHandoff.ImportKey(new ImportPrefill.RecoveryPhrase(Phrase));
        model.RoutePendingScan();
        Assert.Equal(new ImportPrefill.RecoveryPhrase(Phrase), model.ImportPrefill);

        model.PendingScan = new WalletViewModel.ScanHandoff.AddAddress(new Address(Address));
        model.RoutePendingScan();
        Assert.True(model.IsPresentingAddAddress && model.AddAddressPrefill == new Address(Address));

        model.PendingScan = new WalletViewModel.ScanHandoff.Send(new Address(Address));
        model.RoutePendingScan();
        Assert.True(model.IsPresentingSend && model.SendPrefill == new Address(Address));
    }

    [Fact]
    public void DoesNotOpenSendWithoutAnAccount()
    {
        var model = new WalletViewModel([], new MockChainService());
        model.PendingScan = new WalletViewModel.ScanHandoff.Send(new Address(Address));
        model.RoutePendingScan();
        Assert.False(model.IsPresentingSend);
        Assert.Null(model.SendPrefill);
        Assert.False(model.CanSendFromSelected);
    }

    [Fact]
    public async Task DerivesTheAddressOfAScannedPublicKey()
    {
        var multisig = new MockMultisigService();
        multisig.KeyAddresses[PublicKey] = Address;
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), multisig: multisig);
        Assert.Equal(new Address(Address), await model.AddressForScannedPublicKey(PublicKey));
        await Assert.ThrowsAnyAsync<Exception>(() => model.AddressForScannedPublicKey("edpkUnknown"));
    }
}
