using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class Base58Tests
{
    [Theory]
    [InlineData(new byte[0], "")]
    [InlineData(new byte[] { 0x61 }, "2g")]
    [InlineData(new byte[] { 0x62, 0x62, 0x62 }, "a3gV")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x28, 0x7f, 0xb4, 0xcd }, "111233QC4")]
    public void EncodesKnownVectors(byte[] bytes, string expected)
    {
        Assert.Equal(expected, Base58.Encode(bytes));
    }
}

public class KeyGeneratorTests
{
    /// <summary>
    /// Every supported scheme must produce a key that Taquito's own signer decodes to the same
    /// public key and address we recorded. For tz1/tz3 this proves the C# base58 encoding.
    /// </summary>
    [Theory]
    [InlineData(AddressScheme.Tz1)]
    [InlineData(AddressScheme.Tz2)]
    [InlineData(AddressScheme.Tz3)]
    [InlineData(AddressScheme.Tz4)]
    [InlineData(AddressScheme.Tz5)]
    public async Task GeneratesKeysTaquitoAgreesWith(AddressScheme scheme)
    {
        var material = await new KeyGenerator().Generate(scheme);

        Assert.Equal(scheme, material.Scheme);
        Assert.StartsWith(scheme.RawValue(), material.Address);
        Assert.Equal(new JSONValue.Bool(true), await TaquitoBridge.Shared.Call("isValidAddress", material.Address));

        var info = await TaquitoBridge.Shared.Call("keyInfoFromSecretKey", material.SecretKey);
        Assert.Equal(new JSONValue.String(material.PublicKey), info["publicKey"]);
        Assert.Equal(new JSONValue.String(material.Address), info["address"]);
    }

    [Fact]
    public async Task SecretKeysUseTheExpectedPrefixes()
    {
        var expected = new Dictionary<AddressScheme, (string Secret, string Public)>
        {
            [AddressScheme.Tz1] = ("edsk", "edpk"), [AddressScheme.Tz2] = ("spsk", "sppk"), [AddressScheme.Tz3] = ("p2sk", "p2pk"),
            [AddressScheme.Tz4] = ("BLsk", "BLpk"), [AddressScheme.Tz5] = ("mdsk", "mdpk"),
        };
        foreach (var (scheme, prefixes) in expected)
        {
            var material = await new KeyGenerator().Generate(scheme);
            Assert.True(material.SecretKey.StartsWith(prefixes.Secret), scheme.ToString());
            Assert.True(material.PublicKey.StartsWith(prefixes.Public), scheme.ToString());
        }
    }

    [Fact]
    public async Task TwoKeysAreNeverTheSame()
    {
        var a = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var b = await new KeyGenerator().Generate(AddressScheme.Tz1);
        Assert.NotEqual(a.Address, b.Address);
    }

    [Theory]
    [InlineData(AddressScheme.Tz6)]
    public async Task RefusesPostQuantumSchemes(AddressScheme scheme)
    {
        await Assert.ThrowsAnyAsync<KeyGenerator.KeyError>(() => new KeyGenerator().Generate(scheme));
    }

    // Not upstream: tz3 secrets are 32 bytes even when the scalar has leading zero bytes, and
    // public keys are always the 33-byte compressed point. A few hundred keys make a short D likely.
    [Fact]
    public async Task P256KeysAlwaysEncodeFullLength()
    {
        for (var i = 0; i < 300; i++)
        {
            var material = await new KeyGenerator().Generate(AddressScheme.Tz3);
            Assert.Equal(4 + 32, Base58.CheckDecode(material.SecretKey)?.Length);
            Assert.Equal(4 + 33, Base58.CheckDecode(material.PublicKey)?.Length);
        }
    }
}

public class WalletCreationTests
{
    [Fact]
    public async Task CreateWalletStoresKeyAndSelectsIt()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        Assert.Empty(model.Wallets);
        Assert.Null(model.SelectedWallet);

        await model.CreateWallet("  Savings ", AddressScheme.Tz3);

        var wallet = model.SelectedWallet;
        Assert.NotNull(wallet);
        Assert.Equal("Savings", wallet.Alias);
        Assert.Equal(AddressScheme.Tz3, wallet.Scheme);
        Assert.Equal(KeyKind.Unencrypted, wallet.KeyKind);
        Assert.StartsWith("tz3", wallet.Address.Value);
        Assert.StartsWith("p2pk", wallet.PublicKey);
        Assert.StartsWith("p2sk", store.SecretKey(wallet));
        Assert.Equal([wallet], store.Load());
    }

    [Fact]
    public async Task RejectsEmptyAliasDuplicatesAndUnsupportedSchemes()
    {
        var model = new WalletViewModel(chain: new MockChainService());
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.CreateWallet("   ", AddressScheme.Tz1));
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.CreateWallet("Quantum", AddressScheme.Tz6));
        await model.CreateWallet("Main", AddressScheme.Tz1);
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.CreateWallet("Main", AddressScheme.Tz1));
        Assert.Single(model.Wallets);
    }
}

public class WalletRenameTests
{
    [Fact]
    public async Task RenameUpdatesStoreAndKeepsSelection()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.CreateWallet("Main", AddressScheme.Tz1);
        await model.CreateWallet("Savings", AddressScheme.Tz1);
        var savings = model.SelectedWallet;
        Assert.Equal("Savings", savings?.Alias);

        model.RenameSelectedWallet(" Rainy Day ");

        Assert.Equal("Rainy Day", model.SelectedWallet?.Alias);
        Assert.Equal(savings!.Address, model.SelectedWallet?.Address);
        Assert.Equal(["Main", "Rainy Day"], store.Load().Select(w => w.Alias));
        Assert.NotNull(store.SecretKey(model.SelectedWallet!));

        Assert.ThrowsAny<WalletViewModel.WalletError>(() => model.RenameSelectedWallet("Main"));
        Assert.ThrowsAny<WalletViewModel.WalletError>(() => model.RenameSelectedWallet("  "));
    }
}

public class SelectionPersistenceTests
{
    [Fact]
    public async Task RestoresLastSelectedWalletAcrossLaunches()
    {
        var defaults = new InMemoryAppStateStore();
        var store = new InMemoryWalletStore();
        var first = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: defaults);
        await first.CreateWallet("Main", AddressScheme.Tz1);
        await first.CreateWallet("Savings", AddressScheme.Tz1);
        var main = first.Wallets.First(w => w.Alias == "Main");
        first.Select(main);
        Assert.Equal("Main", first.SelectedWallet?.Alias);

        // "Relaunch": a new model over the same store and defaults.
        var second = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: defaults);
        Assert.Equal("Main", second.SelectedWallet?.Alias);
    }

    [Fact]
    public async Task FollowsRenamesAndFallsBackWhenAliasIsGone()
    {
        var defaults = new InMemoryAppStateStore();
        var store = new InMemoryWalletStore();
        var first = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: defaults);
        await first.CreateWallet("Main", AddressScheme.Tz1);
        await first.CreateWallet("Savings", AddressScheme.Tz1);
        first.RenameSelectedWallet("Rainy Day");

        var second = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: defaults);
        Assert.Equal("Rainy Day", second.SelectedWallet?.Alias);

        // A remembered alias that no longer exists falls back to the first wallet.
        defaults.Save(new AppState { SelectedWalletAlias = "Vanished" });
        var third = new WalletViewModel(chain: new MockChainService(), walletStore: store, stateStore: defaults);
        Assert.Equal("Main", third.SelectedWallet?.Alias);
    }
}

public class NetworkSettingsTests
{
    /// <summary>A chain service that reports which network it was built for.</summary>
    private sealed class Probe(Network network) : TestChainService
    {
        public override Task<TezBalance> TezBalanceFor(Address address) =>
            Task.FromResult(new TezBalance(Spendable: network == Network.Mainnet ? 1 : 2));
    }

    [Fact]
    public async Task DefaultsToMainnetAndSwitchingRebuildsTheChainService()
    {
        var defaults = new InMemoryAppStateStore();
        var model = new WalletViewModel(WalletViewModel.SampleWallets, chainFactory: n => new Probe(n), stateStore: defaults);
        Assert.Equal(Network.Mainnet, model.Network);
        await model.Refresh();
        Assert.Equal(1, model.Assets.FirstOrDefault()?.Amount);

        model.Network = Network.Shadownet;
        await model.Refresh();
        Assert.Equal(2, model.Assets.FirstOrDefault()?.Amount);
        Assert.Equal("Shadownet", defaults.Load().NetworkName);
    }

    [Fact]
    public void RestoresTheChosenNetworkOnLaunch()
    {
        var defaults = new InMemoryAppStateStore(new AppState { NetworkName = "Shadownet" });
        var model = new WalletViewModel(chain: new MockChainService(), stateStore: defaults);
        Assert.Equal(Network.Shadownet, model.Network);

        defaults.Save(new AppState { NetworkName = "Nonsense" });
        Assert.Equal(Network.Mainnet, new WalletViewModel(chain: new MockChainService(), stateStore: defaults).Network);
    }

    [Fact]
    public void CustomNodeIsRememberedPerNetworkAndRestored()
    {
        var defaults = new InMemoryAppStateStore();
        var model = new WalletViewModel(chain: new MockChainService(), stateStore: defaults);
        Assert.True(model.Network.IsUsingDefaultNode);

        Assert.False(model.SetNode("not a url"));
        Assert.Equal(Network.Mainnet, model.Network);
        Assert.True(model.SetNode(" https://rpc.example.org/ "));
        Assert.Equal("https://rpc.example.org", model.Network.RpcURL.AbsoluteString());
        Assert.Equal("Mainnet", model.Network.Name);
        Assert.False(model.Network.IsUsingDefaultNode);
        Assert.Equal(new Dictionary<string, string> { ["Mainnet"] = "https://rpc.example.org" }, defaults.Load().NodeURLs);

        // Other networks keep their defaults; coming back to mainnet finds the custom node again.
        model.SwitchNetwork(Network.Shadownet);
        Assert.Equal(Network.Shadownet, model.Network);
        model.SwitchNetwork(Network.Mainnet);
        Assert.Equal("https://rpc.example.org", model.Network.RpcURL.AbsoluteString());

        var relaunched = new WalletViewModel(chain: new MockChainService(), stateStore: defaults);
        Assert.Equal("https://rpc.example.org", relaunched.Network.RpcURL.AbsoluteString());
        relaunched.UseDefaultNode();
        Assert.Equal(Network.Mainnet, relaunched.Network);
        Assert.Null(defaults.Load().NodeURLs);
    }
}

public class NetworkTests
{
    [Fact]
    public void ParsesNodeURLs()
    {
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL("https://rpc.tzbeta.net/")?.AbsoluteString());
        Assert.Equal("http://localhost:8732", Network.NodeURL("http://localhost:8732")?.AbsoluteString());
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL("rpc.tzbeta.net")?.AbsoluteString());
        Assert.Null(Network.NodeURL("ftp://rpc.tzbeta.net"));
        Assert.Null(Network.NodeURL(""));
    }

    [Fact]
    public void WeeklynetFollowsTheMostRecentWednesday()
    {
        static DateTimeOffset Date(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("https://rpc.weeklynet-2026-10-07.teztnets.com", Network.WeeklynetOn(Date("2026-10-07T00:00:00Z")).RpcURL.AbsoluteString());
        Assert.Equal("https://rpc.weeklynet-2026-10-07.teztnets.com", Network.WeeklynetOn(Date("2026-10-08T12:00:00Z")).RpcURL.AbsoluteString());
        Assert.Equal("https://rpc.weeklynet-2026-10-07.teztnets.com", Network.WeeklynetOn(Date("2026-10-13T23:59:59Z")).RpcURL.AbsoluteString());
        Assert.Equal("https://faucet.weeklynet-2026-10-14.teztnets.com", Network.WeeklynetOn(Date("2026-10-14T00:00:00Z")).FaucetURL?.AbsoluteString());
        Assert.Equal("weeklynet", Network.Named("Weeklynet")?.Chain);
    }
}

public class SuggestedAliasTests
{
    [Fact]
    public async Task NumbersTheDefaultAliasWhenTaken()
    {
        var model = new WalletViewModel(chain: new MockChainService());
        Assert.Equal("My Account", model.SuggestedAlias());
        await model.CreateWallet("My Account", AddressScheme.Tz1);
        Assert.Equal("My Account 2", model.SuggestedAlias());
        await model.CreateWallet("my account 2", AddressScheme.Tz1);  // case-insensitive
        Assert.Equal("My Account 3", model.SuggestedAlias());
        await model.CreateWallet("My Account 4", AddressScheme.Tz1);
        Assert.Equal("My Account 3", model.SuggestedAlias());  // first free number, not max + 1
    }
}
