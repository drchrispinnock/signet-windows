using System.Text.Json;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class Base58DecodeTests
{
    [Theory]
    [InlineData("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")]
    [InlineData("KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton")]
    [InlineData("edpkuBknW28nW72KG6RoHtYW7p12T6GKc7nAbwYX5m8Wd9sDVC9yav")]
    public void RoundTripsThroughEncode(string text)
    {
        var bytes = Base58.Decode(text);
        Assert.NotNull(bytes);
        Assert.Equal(text, Base58.Encode(bytes));
        Assert.NotNull(Base58.CheckDecode(text));
    }

    [Fact]
    public void RejectsBadCharactersAndChecksums()
    {
        Assert.Null(Base58.Decode("0OIl"));
        Assert.Null(Base58.CheckDecode("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73M"));  // last char changed
    }
}

public class AddressValidationTests
{
    [Theory]
    [InlineData("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")]
    [InlineData("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq")]
    [InlineData("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5")]
    [InlineData("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm")]
    [InlineData("tz5c9dMrWKhwBbSUPhxQRdwUnMMYHfqnpQRD")]   // ML-DSA, which Taquito rejects
    [InlineData("tz6JwJkwUGhWkPSJ2xfBaufqqP53FprApc9i")]   // XMSS
    [InlineData("KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton")]
    public void AcceptsEverySchemeAndContracts(string address)
    {
        Assert.True(new Address(address).IsValidAccount, address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tz1")]
    [InlineData("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73M")]
    [InlineData("edpkuBknW28nW72KG6RoHtYW7p12T6GKc7nAbwYX5m8Wd9sDVC9yav")]
    [InlineData("sr1Ghq66tYK9y3r8CC1Tf8i8m5nxh8nTvZEf")]
    [InlineData(" tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")]
    public void RejectsOtherThings(string address)
    {
        Assert.False(new Address(address).IsValidAccount, address);
    }
}

public class WatchOnlyStoreTests
{
    [Fact]
    public void AddWatchOnlyWritesOnlyThePublicKeyHashesFile()
    {
        var dir = TestSupport.TempDir("watch");
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(new Wallet("mine", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkM", keyKind: KeyKind.Unencrypted), "edskM");
        store.AddWatchOnly(new Wallet("Captain Stake", new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N")));

        var wallets = store.Load();
        Assert.Equal(["mine", "Captain Stake"], wallets.Select(w => w.Alias));
        Assert.Equal(KeyKind.None, wallets[1].KeyKind);
        Assert.Null(wallets[1].PublicKey);
        Assert.Null(store.SecretKey(wallets[1]));

        using var publicKeys = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "public_keys")));
        Assert.Equal(1, publicKeys.RootElement.GetArrayLength());
        Assert.ThrowsAny<TezosClientStore.StoreError>(() =>
            store.AddWatchOnly(new Wallet("mine", new Address("tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"))));
    }
}

public class AddressBookViewModelTests
{
    [Fact]
    public async Task AddsValidatesAndSelects()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.CreateWallet("Main", AddressScheme.Tz1);

        await model.AddWatchOnlyWallet(" Captain Stake ", " tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N ");
        var entry = model.SelectedWallet;
        Assert.NotNull(entry);
        Assert.Equal("Captain Stake", entry.Alias);
        Assert.Equal(KeyKind.None, entry.KeyKind);
        Assert.Equal(2, store.Load().Count);

        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddWatchOnlyWallet("Bad", "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73M"));
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddWatchOnlyWallet("Again", "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"));
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.AddWatchOnlyWallet("Main", "KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton"));
        Assert.Equal(2, model.Wallets.Count);
    }
}

public class ProfileNameTests
{
    [Fact]
    public async Task ProfileNameComesFromTheChainServiceOrIsNil()
    {
        var model = new WalletViewModel(chain: new MockChainService());
        Assert.Equal("Captain Stake", await model.ProfileName(MockChainService.CaptainStake));
        Assert.Null(await model.ProfileName(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
    }
}
