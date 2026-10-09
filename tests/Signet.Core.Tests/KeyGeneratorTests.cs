using Signet.Models;
using Signet.Services;

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
    [Fact(Skip = Pending.ViewModel)] public void CreateWalletStoresKeyAndSelectsIt() { }
    [Fact(Skip = Pending.ViewModel)] public void RejectsEmptyAliasDuplicatesAndUnsupportedSchemes() { }
}

public class WalletRenameTests
{
    [Fact(Skip = Pending.ViewModel)] public void RenameUpdatesStoreAndKeepsSelection() { }
}

public class SelectionPersistenceTests
{
    [Fact(Skip = Pending.ViewModel)] public void RestoresLastSelectedWalletAcrossLaunches() { }
    [Fact(Skip = Pending.ViewModel)] public void FollowsRenamesAndFallsBackWhenAliasIsGone() { }
}

public class NetworkSettingsTests
{
    [Fact(Skip = Pending.ViewModel)] public void DefaultsToMainnetAndSwitchingRebuildsTheChainService() { }
    [Fact(Skip = Pending.ViewModel)] public void RestoresTheChosenNetworkOnLaunch() { }
    [Fact(Skip = Pending.ViewModel)] public void CustomNodeIsRememberedPerNetworkAndRestored() { }
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
    [Fact(Skip = Pending.ViewModel)] public void NumbersTheDefaultAliasWhenTaken() { }
}
