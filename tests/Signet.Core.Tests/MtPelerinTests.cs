using System.Globalization;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class MtPelerinTests
{
    [Fact]
    public void PacksTheMessageLikeTheDocs()
    {
        // Test vector from developers.mtpelerin.com (code 1234).
        Assert.Equal("05010000002454657a6f73205369676e6564204d6573736167653a204d7450656c6572696e2d31323334", MtPelerin.PackedMessage("1234"));
        Assert.Equal("Tezos Signed Message: MtPelerin-4321", MtPelerin.Message("4321"));
        var code = MtPelerin.RandomCode();
        Assert.True(code.Length == 4 && int.Parse(code, CultureInfo.InvariantCulture) >= 1000);
    }

    [Fact]
    public void BuildsTheArmoredBlockAndURL()
    {
        var validation = new MtPelerin.Validation("1234", "edpkus9ckWtwxNi7NqZqGT2bo1VGxyRkCvaV5zkms7rkeWUV532PhU",
            "edsigtzYwqm72h1cBbWLSMUVEu3t7bUT2RqBMppLdtVGH3VqKhppz96YE6yQSS4GKKkB6nHCRXmdiukHkw3Puy5Zd5b2rvaosRG");
        var block = MtPelerin.ArmoredBlock(validation);
        Assert.StartsWith("-----BEGIN TEZOS SIGNED MESSAGE-----\nTezos Signed Message: MtPelerin-1234\n-----BEGIN SIGNATURE-----\nedpkus9", block);
        Assert.EndsWith("-----END TEZOS SIGNED MESSAGE-----", block);
        Assert.Equal(6, block.Split('\n').Length);

        var url = MtPelerin.BuyURL(new Address("tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5"), "CHF", validation, culture: new CultureInfo("fr-CH"));
        var query = url.AbsoluteString();
        Assert.Equal("widget.mtpelerin.com", url.Host);
        Assert.True(query.Contains("bdc=XTZ") && query.Contains("net=tezos_mainnet") && query.Contains("dnet=tezos_mainnet"));
        Assert.Contains("addr=tz1dnCbNYHDoxmHss9kEQVjWQj6GvYX4gYp5", query);
        Assert.True(query.Contains("bsc=CHF") && query.Contains("lang=fr") && query.Contains("code=1234"));
        // The armored block is percent-encoded the way the docs show it.
        Assert.Contains("hash=-----BEGIN%20TEZOS%20SIGNED%20MESSAGE-----%0ATezos%20Signed%20Message%3A%20MtPelerin-1234%0A", query);
        Assert.Equal(block, MtPelerin.QueryValue(url, "hash"));

        var plain = MtPelerin.BuyURL(new Address("tz1abc"), "EUR", null, MtPelerin.Presentation.Browser, CultureInfo.InvariantCulture);
        Assert.True(!plain.AbsoluteString().Contains("hash=") && plain.AbsoluteString().Contains("lang=en") && plain.AbsoluteString().Contains("type=direct-link"));
        Assert.Contains("type=webview", query);
        Assert.True(BuyWebViewPolicy.IsTrusted(new Uri("https://widget.mtpelerin.com/x"), BuyProvider.MtPelerin)
            && BuyWebViewPolicy.IsTrusted(new Uri("https://kyc.mtpelerin.com/"), BuyProvider.MtPelerin)
            && !BuyWebViewPolicy.IsTrusted(new Uri("https://evil.example/mtpelerin.com"), BuyProvider.MtPelerin));
        Assert.Contains("type=direct-link", BuyProvider.MtPelerin.BrowserURL(url).AbsoluteString());
        // The remembered choice is whatever was last picked in the Buy sheet, so only the fallback
        // is pinned: unknown or unset means Mt Pelerin.
        Assert.Contains(BuyProviderExtensions.Current, Enum.GetValues<BuyProvider>());
        Assert.True(BuyProviderExtensions.FromRawValue("nowhere") is null && BuyProviderExtensions.Default == BuyProvider.MtPelerin);
    }

    [Fact]
    public void PicksFiatAndLanguageFromTheLocale()
    {
        Assert.Equal("GBP", MtPelerin.DefaultFiat(new CultureInfo("en-GB")));
        Assert.Equal("EUR", MtPelerin.DefaultFiat(new CultureInfo("ja-JP")));
        Assert.Equal("de", MtPelerin.Language(new CultureInfo("de-DE")));
        Assert.Equal("en", MtPelerin.Language(new CultureInfo("nl-NL")));
    }

    [Fact]
    public async Task ViewModelSignsWhenItCan()
    {
        var store = new InMemoryWalletStore();
        store.AddWithSecretKey(new Wallet("a", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkX", keyKind: KeyKind.Unencrypted), "edskX");
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        var url = await model.BuyURL(BuyProvider.MtPelerin, "EUR", null);
        Assert.True(url.AbsoluteString().Contains("hash=") && url.AbsoluteString().Contains("edsigMock"));

        var watch = new WalletViewModel([new Wallet("w", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), keyKind: KeyKind.None)], new MockChainService());
        var plain = await watch.BuyURL(BuyProvider.MtPelerin, "EUR", null);
        Assert.DoesNotContain("hash=", plain.AbsoluteString());
    }

    /// <summary>Live: a fresh tz1 key signs the packed message through the bridge and the signature verifies.</summary>
    [Fact]
    public async Task BridgeSignsThePackedMessage()
    {
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var chain = new TaquitoChainService(Network.Mainnet);
        var signed = await chain.SignPayload(new SigningKey.Secret(material.SecretKey, null, new Address(material.Address)), MtPelerin.PackedMessage("1234"));
        Assert.Equal(material.PublicKey, signed.PublicKey);
        Assert.StartsWith("edsig", signed.Signature);
        var verified = await TaquitoBridge.Shared.Call("verifySignature", MtPelerin.PackedMessage("1234"), signed.PublicKey, signed.Signature);
        Assert.Equal(new JSONValue.Bool(true), verified);
    }
}

public class CoinbaseProviderTests
{
    [Fact]
    public void CoinbaseIsABrowserHandOffNotAWidget()
    {
        var provider = BuyProvider.Coinbase;
        Assert.True(!provider.UsesWidget() && BuyProvider.MtPelerin.UsesWidget());
        Assert.True(provider.BrowserPage()?.Host == "www.coinbase.com" && BuyProvider.MtPelerin.BrowserPage() is null);
        Assert.True(provider.OwnershipPayload("1234") is null && provider.FiatCurrencies().Count == 0);
        // Nothing of Coinbase's is ever trusted inside the embedded web view.
        Assert.False(BuyWebViewPolicy.IsTrusted(new Uri("https://www.coinbase.com/"), BuyProvider.Coinbase));
        Assert.Equal([BuyProvider.MtPelerin, BuyProvider.Coinbase, BuyProvider.Kraken, BuyProvider.Transak], Enum.GetValues<BuyProvider>());
        var url = provider.BuyURL(new Address("tz1abc"), "", "0000", null, embedded: true, dark: false);
        Assert.True(url == provider.BrowserPage() && provider.BrowserURL(url) == url);
    }

    [Fact]
    public void KrakenIsABrowserHandOffLikeCoinbase()
    {
        var provider = BuyProvider.Kraken;
        Assert.True(!provider.UsesWidget() && provider.BrowserPage()?.Host == "www.kraken.com");
        Assert.True(provider.OwnershipPayload("1234") is null && provider.FiatCurrencies().Count == 0 && provider.DefaultFiat() == "");
        Assert.True(provider.TrustedHosts().Count == 0 && !BuyWebViewPolicy.IsTrusted(new Uri("https://www.kraken.com/"), BuyProvider.Kraken));
        var url = provider.BuyURL(new Address("tz1abc"), "", "0000", null, embedded: false, dark: true);
        Assert.True(url == provider.BrowserPage() && provider.BrowserURL(url) == url);
        Assert.Equal("Mt Pelerin, Coinbase, Kraken or Transak", BuyProviderExtensions.Choices);
        // Every hand-off opens over HTTPS, says where the address goes and trusts nothing embedded.
        foreach (var handOff in Enum.GetValues<BuyProvider>().Where(p => !p.UsesWidget()))
            Assert.True(handOff.BrowserPage()?.Scheme == "https" && handOff.DeliveryStep().Length > 0 && handOff.TrustedHosts().Count == 0);
    }

    [Fact]
    public void TransakIsItsOwnSiteInTheBrowser()
    {
        var provider = BuyProvider.Transak;
        Assert.True(!provider.UsesWidget() && provider.BrowserPage() == new Uri("https://transak.com/buy/xtz"));
        Assert.True(provider.OwnershipPayload("1234") is null && provider.FiatCurrencies().Count == 0);
        Assert.False(BuyWebViewPolicy.IsTrusted(new Uri("https://global.transak.com/"), BuyProvider.Transak));
        // The address is asked for during the purchase, not at a Withdraw screen afterwards.
        Assert.True(provider.DeliveryStep().Contains("when it asks") && BuyProvider.Kraken.DeliveryStep().Contains("Withdraw"));
    }
}
