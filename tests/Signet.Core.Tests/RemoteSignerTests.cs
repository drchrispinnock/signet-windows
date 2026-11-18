using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// Remote signers (octez-signer). The wire vectors are what octez-client sent to and octez-signer
/// 21 answered over TCP, captured with a logging proxy, and requests octez-signer accepted;
/// <c>signer_messages.ml</c> in octez is the reference for the encodings.
/// </summary>
public class RemoteSignerTests
{
    private static readonly Address Alice = new("tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG");
    private const string AliceKey = "edpkuStAKZyEk99wkft2maSTVqAmj9HoCwf63y2HyMvhPP7MMH5crT";
    private static readonly Address Bob = new("tz4Euk9qYr7b43PC8iyufuK4ycrmhiQ7NYJY");
    private const string AliceHash = "00120136f4f9c3d29772aa2b3663e3c24acfc735e1";

    private static string Hx(byte[] bytes) => Convert.ToHexStringLower(bytes);

    // ---- Locators ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("tcp://localhost:7732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", RemoteSignerLocator.TransportKind.Tcp, "localhost", 7732, "")]
    [InlineData("http://192.168.1.20:6732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", RemoteSignerLocator.TransportKind.Http, "192.168.1.20", 6732, "")]
    [InlineData("https://signer.example.com/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", RemoteSignerLocator.TransportKind.Https, "signer.example.com", 443, "")]
    [InlineData("https://signer.example.com:8443/tezos/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", RemoteSignerLocator.TransportKind.Https, "signer.example.com", 8443, "/tezos")]
    [InlineData("http://signer.local/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", RemoteSignerLocator.TransportKind.Http, "signer.local", 80, "")]
    public void ReadsOctezClientLocators(string text, RemoteSignerLocator.TransportKind transport, string host, int port, string path)
    {
        var signer = RemoteSignerLocator.FromLocator(text);
        Assert.NotNull(signer);
        Assert.Equal(transport, signer.Transport);
        Assert.Equal(host, signer.Host);
        Assert.Equal(port, signer.Port);
        Assert.Equal(path, signer.Path);
        Assert.Equal(Alice, signer.Address);
        Assert.Equal(signer, RemoteSignerLocator.FromLocator(signer.Locator));
        Assert.Equal(KeyKind.Remote, KeyKindExtensions.FromLocator(text));
    }

    [Fact]
    public void WritesTheLocatorOctezClientReads()
    {
        var signer = new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Tcp, "localhost", 7732, Alice);
        Assert.Equal("tcp://localhost:7732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", signer.Locator);
        Assert.Equal("tcp://localhost:7732", signer.Endpoint);
        Assert.Equal("http://[::1]:6732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Http, "::1", 6732, Alice).Locator);
        var unix = new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Unix, "", 0, "/tmp/signer.sock", Alice);
        Assert.Equal("unix:/tmp/signer.sock?pkh=tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", unix.Locator);
        Assert.Equal(unix, RemoteSignerLocator.FromLocator(unix.Locator));
        Assert.Equal(6732, RemoteSignerLocator.TransportKind.Http.DefaultPort());
        Assert.Equal(443, RemoteSignerLocator.TransportKind.Https.DefaultPort());
        Assert.Equal(7732, RemoteSignerLocator.TransportKind.Tcp.DefaultPort());
    }

    [Theory]
    [InlineData("remote:tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG")]
    [InlineData("tcp://localhost/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG")]
    [InlineData("tcp://localhost:7732/KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi")]
    [InlineData("tcp://localhost:7732/nonsense")]
    [InlineData("https://user:pw@signer.example.com/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG")]
    [InlineData("http://localhost:6732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG?x=1")]
    [InlineData("unix:/tmp/signer.sock")]
    public void RefusesWhatItCannotUse(string text)
    {
        Assert.Null(RemoteSignerLocator.FromLocator(text));
    }

    [Fact]
    public void RemoteKeysSignButRemoteColonDoesNot()
    {
        Assert.True(KeyKind.Remote.CanSign() && KeyKind.Remote.SignsMessages());
        Assert.Equal(KeyKind.Unknown, KeyKindExtensions.FromLocator("remote:tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG"));
        Assert.Equal(KeyKind.Remote, KeyKindExtensions.FromLocator("unix:/tmp/s?pkh=tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG"));
    }

    // ---- Wire --------------------------------------------------------------------------------------

    [Fact]
    public void WritesTheRequestsOctezSends()
    {
        Assert.Equal("01" + AliceHash, Hx(SignerWire.PublicKeyRequest(Alice)!));
        Assert.Equal("0016" + "01" + AliceHash, Hx(SignerWire.Frame([1, .. Hex.Bytes(AliceHash)!])!));
        byte[] data = [3, .. Enumerable.Repeat((byte)0xab, 40)];
        // Accepted by octez-signer: kind 00, key hash, 4-byte length, data, and no byte for the absent authentication.
        Assert.Equal("00" + AliceHash + "00000029" + Hx(data), Hx(SignerWire.SignRequest(Alice, data)!));
        Assert.Equal([2], SignerWire.AuthorizedKeysRequest);
        Assert.Equal([6], SignerWire.KnownKeysRequest);
        Assert.Equal(1 + 21 + 1, SignerWire.ProvePossessionRequest(Bob)!.Length);
    }

    [Fact]
    public void NamesATz4KeyWithItsVersion()
    {
        var hash = SignerWire.KeyHash(Bob)!;
        Assert.True(hash.Length == 21 && hash[0] == 3);
        var current = SignerWire.SignRequest(Bob, [3, 1])!;
        Assert.Equal([0, 3, .. hash, SignerWire.BlsSignatureVersion], current[..24]);
        Assert.Equal(Bob, SignerWire.AddressFromKeyHash(hash));
    }

    [Fact]
    public void ReadsTheSignersReplies()
    {
        var publicKeyReply = Hex.Bytes("0000" + "69f1f01c04a09e8dec3f53e95f2ec95d2d65202b7af989f63a1d29aa32aead85")!;
        Assert.Equal(AliceKey, SignerWire.PublicKeyFromValue(SignerWire.Value(publicKeyReply)));
        // `--require-authentication` with no keys, then no authentication.
        Assert.Equal([], SignerWire.AuthorizedKeysFromValue(SignerWire.Value(Hex.Bytes("000100000000")!))!);
        Assert.Null(SignerWire.AuthorizedKeysFromValue(SignerWire.Value([0, 0])));
        var one = Hex.Bytes(AliceHash)!;
        Assert.Equal([Alice, Alice], SignerWire.AddressesFromValue([.. one, .. one]));
        Assert.Equal([Alice], SignerWire.AddressesFromValue([0, 0, 0, 21, .. one]));
        Assert.Throws<SignerWire.Failure.Malformed>(() => SignerWire.AddressesFromValue([0, 0, 0, 22, .. one]));
        byte[] refusal = [1, .. """[{"kind":"temporary","id":"signer.unknown_key","msg":"x"}]"""u8.ToArray()];
        Assert.Equal("signer.unknown_key", Assert.Throws<SignerWire.Failure.Refused>(() => SignerWire.Value(refusal)).Reason);
        // What an octez-signer started without --allow-list-known-keys answers to known_keys: BSON.
        var listing = Hex.Bytes("010000005b0000005757000000026b696e64000a00000074656d706f726172790002696400080000006661696c75726500026d736700250000004c697374206b6e6f776e206b6579732072657175657374206e6f7420616c6c6f7765642e0000")!;
        Assert.Equal("List known keys request not allowed.", Assert.Throws<SignerWire.Failure.Refused>(() => SignerWire.Value(listing)).Reason);
        var declined = new RemoteSignerError.Declined(RemoteSignerError.RequestKind.ListKeys, "tcp://192.168.178.213:7732", Alice, "List known keys request not allowed.");
        Assert.Contains("The signer at tcp://192.168.178.213:7732 will not list its keys. The signer said: “List known keys request not allowed.”", declined.Message);
        Assert.Contains("--allow-list-known-keys", declined.Message);
        var sign = new RemoteSignerError.Declined(RemoteSignerError.RequestKind.Sign, "tcp://h:7732", Alice, null).Message;
        Assert.Contains($"refused to sign with {Alice.Value}. The signer gave no reason.", sign);
        Assert.Throws<SignerWire.Failure.Malformed>(() => SignerWire.Value([7]));
    }

    [Fact]
    public void HandsTheBridgeTheSignatureAsGiven()
    {
        Assert.Equal("""{"raw":"abcd"}""", new RemoteSignature.Raw([0xab, 0xcd]).BridgeJSON);
        Assert.Equal("""{"signature":"edsigX"}""", new RemoteSignature.Base58Signature("edsigX").BridgeJSON);
        var signer = new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Tcp, "localhost", 7732, Alice);
        var key = new SigningKey.Remote(signer, AliceKey);
        var spec = JsonSerializer.Deserialize<Dictionary<string, string>>(key.BridgeSpec);
        Assert.Equal(new Dictionary<string, string> { ["kind"] = "remote", ["locator"] = signer.Locator, ["publicKey"] = AliceKey, ["address"] = Alice.Value }, spec);
    }

    [Fact]
    public async Task AnswersTheBridgeOnlyForTheKeyItNames()
    {
        var mock = new MockRemoteSignerService(new Dictionary<Address, string> { [Alice] = AliceKey }, signature: (bytes, _) => new RemoteSignature.Raw(bytes.Reverse().ToArray()));
        var locator = $"tcp://localhost:7732/{Alice.Value}";
        string Request(string loc, Address address) => new JsonObject { ["op"] = "sign", ["locator"] = loc, ["address"] = address.Value, ["bytes"] = "0301" }.ToJsonString();
        Assert.Equal("""{"raw":"0103"}""", await RemoteSigners.Answer(Request(locator, Alice), mock));
        await Assert.ThrowsAsync<RemoteSignerError.MalformedReply>(() => RemoteSigners.Answer(Request(locator, Bob), mock));
        await Assert.ThrowsAsync<RemoteSignerError.MalformedReply>(() => RemoteSigners.Answer(Request($"remote:{Alice.Value}", Alice), mock));
    }

    // ---- Accounts ----------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectsAnAccountOctezClientCanUse()
    {
        var dir = TestSupport.MakeDir("signet-remote");
        try
        {
            var store = new TezosClientStore(dir);
            var multisig = new MockMultisigService { KeyAddresses = new() { [AliceKey] = Alice.Value, ["edpkOther"] = "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" } };
            var model = new WalletViewModel(chain: new MockChainService(), multisig: multisig, walletStore: store)
            {
                RemoteSigners = new MockRemoteSignerService(new Dictionary<Address, string> { [Alice] = AliceKey }),
            };
            var signer = new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Tcp, "localhost", 7732, Alice);

            var check = await model.CheckRemoteSigner(signer);
            Assert.True(check.PublicKey == AliceKey && check.AcceptsUnauthenticated);
            await model.ConnectRemoteSigner("Baker", signer);

            var wallet = model.SelectedWallet!;
            Assert.True(wallet.Alias == "Baker" && wallet.KeyKind == KeyKind.Remote && wallet.RemoteSigner == signer && wallet.PublicKey == AliceKey);
            var secrets = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "secret_keys"))) as JsonArray;
            Assert.Equal("tcp://localhost:7732/tz1MHEJyAu3Jw6uHKqdkvbkwLPEwxgfvgXcG", secrets?[0]?["value"]?.GetValue<string>());
            var publics = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "public_keys"))) as JsonArray;
            Assert.Equal(signer.Locator, publics?[0]?["value"]?["locator"]?.GetValue<string>());
            Assert.Equal(wallet, store.Load()[0]);
            Assert.Equal(new SigningKey.Remote(signer, AliceKey), model.SigningKeyFor(wallet, null));

            await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.ConnectRemoteSigner("Again", signer));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RefusesAKeyThatIsNotTheAddresss()
    {
        var multisig = new MockMultisigService { KeyAddresses = new() { ["edpkOther"] = "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" } };
        var model = new WalletViewModel(chain: new MockChainService(), multisig: multisig, walletStore: new InMemoryWalletStore())
        {
            RemoteSigners = new MockRemoteSignerService(new Dictionary<Address, string> { [Alice] = "edpkOther" }, requiresAuthentication: true),
        };
        var signer = new RemoteSignerLocator(RemoteSignerLocator.TransportKind.Http, "localhost", 6732, Alice);
        var error = await Assert.ThrowsAsync<RemoteSignerError.KeyMismatch>(() => model.ConnectRemoteSigner("Liar", signer));
        Assert.Equal(new RemoteSignerError.KeyMismatch(Alice, new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")), error);
        Assert.All(model.Wallets, w => Assert.NotEqual("Liar", w.Alias));
    }
}

/// <summary>
/// The whole path through the real bridge: Taquito's signer → <c>__signet.remoteSign</c> → this
/// client → an octez-signer → back into the bridge, which checks the signature. Opt-in: set
/// <c>SIGNET_REMOTE_SIGNERS</c> to comma-separated locators of running signers, e.g.
/// <c>tcp://localhost:7732/tz1…,http://localhost:6732/tz4…</c> (<c>octez-signer launch socket signer</c>).
/// A tz4 key needs octez-signer 23 or later: older ones make BLS signatures the chain refuses.
/// </summary>
public class RemoteSignerLiveTests
{
    private static readonly string[] Locators = (Environment.GetEnvironmentVariable("SIGNET_REMOTE_SIGNERS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    [Trait(Category.Name, Category.Network)]
    public async Task SignsThroughTheBridgeWithARealSigner()
    {
        Assert.SkipWhen(Locators.Length == 0, "Set SIGNET_REMOTE_SIGNERS to locators of running octez-signers");
        var chain = new TaquitoChainService(Network.Shadownet);
        foreach (var text in Locators)
        {
            var signer = RemoteSignerLocator.FromLocator(text);
            Assert.NotNull(signer);
            var publicKey = await new NetworkRemoteSignerService().PublicKey(signer);
            // A Micheline payload: signed with no watermark, as dApps and multisig do.
            var signed = await chain.SignPayload(new SigningKey.Remote(signer, publicKey), "05" + "0100000006" + Convert.ToHexStringLower("Signet"u8.ToArray()));
            Assert.Equal(publicKey, signed.PublicKey);
            Assert.True(signed.Signature.StartsWith(signer.Address.Scheme == AddressScheme.Tz4 ? "BLsig" : "edsig", StringComparison.Ordinal)
                        || signed.Signature.StartsWith("sig", StringComparison.Ordinal), text);
            // A signer holding another key under this address is caught by the bridge.
            var other = signer.Address.Scheme == AddressScheme.Tz4 ? "BLpk1tmtxM1XWK3V3DCb2KS4ykCiQV5XhrrsGgTx54HkP4g6wSqP7edV8dj84S9R2jKnmQKaTgkQ" : "edpkuStAKZyEk99wkft2maSTVqAmj9HoCwf63y2HyMvhPP7MMH5crT";
            if (other != publicKey)
                await Assert.ThrowsAnyAsync<Exception>(() => chain.SignPayload(new SigningKey.Remote(signer, other), "0500"));
        }
    }
}
