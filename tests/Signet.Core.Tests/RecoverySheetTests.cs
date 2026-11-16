using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>The printed recovery sheet: what it carries and that Scan QR code can read it back.</summary>
public class RecoverySheetTests
{
    private static readonly string Clear = Base58.CheckEncode(TezosPrefix.Edsk, Enumerable.Repeat((byte)7, 32).ToArray());
    /// <summary>edesk: the octez prefix for an encrypted ed25519 seed (8-byte salt and 48 bytes of box).</summary>
    private static readonly string Encrypted = Base58.CheckEncode([7, 90, 60, 179, 41], Enumerable.Repeat((byte)3, 56).ToArray());
    private static readonly Wallet SavingsWallet = new("Savings", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.Encrypted);

    [Fact]
    public void CarriesTheEncryptedKeyWhenThereIsOne()
    {
        Assert.StartsWith("edesk", Encrypted);
        var sheet = new RecoverySheet(SavingsWallet, new WalletViewModel.ExportedKey(Clear, Encrypted), version: "1.0");
        Assert.Equal(Encrypted, sheet.SecretKey);
        Assert.True(sheet.IsEncrypted);
        Assert.Equal("Savings", sheet.Alias);
        var plain = new RecoverySheet(SavingsWallet, new WalletViewModel.ExportedKey(Clear, null), version: "1.0");
        Assert.Equal(Clear, plain.SecretKey);
        Assert.False(plain.IsEncrypted);
    }

    [Fact]
    public void TheQRCodeScansBackAsASecretKey()
    {
        foreach (var key in new[] { new WalletViewModel.ExportedKey(Clear, Encrypted), new WalletViewModel.ExportedKey(Clear, null) })
        {
            var sheet = new RecoverySheet(SavingsWallet, key, version: "1.0");
            Assert.True(sheet.HasQRCode);
            Assert.Equal(1, sheet.PageCount);
            var image = sheet.QRImage(600);
            Assert.NotNull(image);
            Assert.Equal(new QRPayload.SecretKey(sheet.SecretKey), QRCode.Decode(image) is { } text ? QRPayload.Classify(text) : null);
        }
    }

    /// <summary>A tz5 (ML-DSA-44) secret is 3872 bytes and its public key 1312, more than a QR code holds.</summary>
    private static readonly string LongSecret = Base58.CheckEncode([9, 57, 116, 57], Enumerable.Range(0, 3872).Select(i => unchecked((byte)(i * 31))).ToArray());
    private static readonly string LongPublic = Base58.CheckEncode(TezosPrefix.Edpk, Enumerable.Range(0, 1312).Select(i => unchecked((byte)(i * 17))).ToArray());

    [Fact]
    public void AKeyTooLongForAQRCodeIsPrintedAsText()
    {
        var wallet = new Wallet("Quantum", SavingsWallet.Address, publicKey: LongPublic, keyKind: KeyKind.Unencrypted);
        var sheet = new RecoverySheet(wallet, new WalletViewModel.ExportedKey(LongSecret, null), version: "1.0");
        Assert.StartsWith("mdsk", LongSecret);
        Assert.False(sheet.HasQRCode);
        var pages = sheet.SecretKeyPages;
        Assert.NotEmpty(pages);
        Assert.Equal(1 + sheet.CodePages.Count + pages.Count, sheet.PageCount);
        // Every character is there once, in order, in lines of the stated length.
        Assert.Equal(LongSecret, string.Concat(pages.SelectMany(p => p.Lines)));
        Assert.All(pages, p => Assert.True(p.Lines.Count <= RecoverySheet.LinesPerPage && p.Lines.All(l => l.Length <= RecoverySheet.LineLength)));
        Assert.Equal(Enumerable.Range(0, pages.Count).Select(i => i * RecoverySheet.LinesPerPage + 1), pages.Select(p => p.FirstLine));
        Assert.Empty(RecoverySheet.Lines(""));
    }

    [Fact]
    public void ALongKeyIsSplitAcrossCodesThatScanBackTogether()
    {
        var sheet = new RecoverySheet(SavingsWallet, new WalletViewModel.ExportedKey(LongSecret, null), version: "1.0");
        var parts = sheet.KeyParts;
        Assert.InRange(parts.Count, 2, 12);
        Assert.Equal(parts, sheet.CodePages.SelectMany(p => p));
        Assert.All(sheet.CodePages, p => Assert.True(p.Count <= RecoverySheet.CodesPerPage));
        Assert.Equal(LongSecret, string.Concat(parts.Select(p => p.Chunk)));
        // Each part is a real QR code that reads back, and the parts, scanned out of order and
        // with one repeated, make the key again.
        var collector = new KeyPartCollector();
        KeyPartCollector.Outcome result = new KeyPartCollector.Outcome.NeedMore();
        foreach (var part in new[] { parts[^1] }.Concat(parts.Reverse()))
        {
            var image = QRCode.Image(part.Text, 600, QRCoder.QRCodeGenerator.ECCLevel.L);
            Assert.NotNull(image);
            var scanned = Assert.IsType<QRPayload.KeyPartContent>(QRPayload.Classify(QRCode.Decode(image) ?? "")).Part;
            Assert.Equal(part, scanned);
            result = collector.Add(scanned);
        }
        Assert.Equal(new KeyPartCollector.Outcome.Complete(LongSecret), result);
        // A short key needs no parts.
        Assert.Empty(new RecoverySheet(SavingsWallet, new WalletViewModel.ExportedKey(Clear, null), version: "1.0").KeyParts);
    }

    [Fact(Skip = "Upstream renders the pages to PDF in the test; on Windows the pages are drawn by the app's RecoverySheetPrinter (GDI+, the print dialog), which Core's tests cannot reach")]
    public void RendersALongKeyOverSeveralPages() { }

    [Fact(Skip = "Upstream renders the pages to PDF in the test; on Windows the pages are drawn by the app's RecoverySheetPrinter (GDI+, the print dialog), which Core's tests cannot reach")]
    public void RendersOnePageWithTheAccountOnIt() { }
}
