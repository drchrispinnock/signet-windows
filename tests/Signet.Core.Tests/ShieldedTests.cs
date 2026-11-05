using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

// ---- Vectors ---------------------------------------------------------------------------------------

/// <summary>
/// The tzel cross-implementation vectors, read from a checkout (<c>TZEL_VECTORS</c> or <c>..\tzel</c> beside
/// the upstream checkout), never copied here: the tzel licence forbids it. null when there is none.
/// </summary>
internal static class TzELVectors
{
    public static JsonObject? Load()
    {
        var path = Environment.GetEnvironmentVariable("TZEL_VECTORS");
        if (path is null)
        {
            var directory = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && directory is not null; i++, directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, "..", "tzel", "specs", "ocaml_vectors", "protocol_v1.json");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        JsonObject? json = null;
        try { if (path is not null && File.Exists(path)) json = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject; }
        catch (System.Text.Json.JsonException) { }
        // Optional by default; a release gate sets SIGNET_TZEL_VECTORS_REQUIRED=1 so a missing or
        // unreadable checkout fails the run instead of passing quietly.
        if (json is null && Environment.GetEnvironmentVariable("SIGNET_TZEL_VECTORS_REQUIRED") is not null)
            Assert.Fail($"tzel vectors required but not readable at {path ?? "..\\tzel"}");
        return json;
    }
}

/// <summary>Tests that build XMSS signing trees use every core; they run alone so timing-sensitive tests elsewhere are not starved.</summary>
[CollectionDefinition(ShieldedTestSupport.CpuHeavy, DisableParallelization = true)]
public sealed class CpuHeavyCollection;

internal static class ShieldedTestSupport
{
    public const string CpuHeavy = "CPU heavy";
    public static string Hex(byte[] bytes) => HexBytes.Encode(bytes);
    /// <summary>Two records are the same when their files would be.</summary>
    public static bool Same<T>(T a, T b) => ShieldedJSON.Text(a) == ShieldedJSON.Text(b);
}

// ---- BLAKE2s ----------------------------------------------------------------------------------------

public class Blake2sTests
{
    private static string Hash(byte[] bytes, string? personal = null) =>
        HexBytes.Encode(Blake2s.Hash(bytes, personal is null ? null : Blake2s.Personalization.From(personal)));

    [Fact]
    public void MatchesTheRFCVectorAndNoble()
    {
        // RFC 7693 appendix: BLAKE2s-256("abc"); the rest were computed with @noble/hashes, the bridge's BLAKE2s.
        Assert.Equal("508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982", Hash("abc"u8.ToArray()));
        Assert.Equal("69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9", Hash([]));
        Assert.Equal("c25fa43362917ce953ec2b680c0b1475c534e19d84ac8dc660907bd3633e3586", Hash(new byte[64], "mrklSP__"));
        Assert.Equal("95d4d347cd6df1d063a95d17e8364acb1754b2ec3b224a11f392cfc5f917acc3", Hash(Enumerable.Repeat((byte)0xff, 96).ToArray(), "sighSP__"));
        Assert.Equal("1fa877de67259d19863a2a34bcc6962a2b25fcbf5cbecd7ede8f1fa36688a796", Hash(Enumerable.Range(0, 128).Select(i => (byte)i).ToArray()));
        Assert.Equal("4954f84ca29ed06714c1732e2a8a199a9e784009e9495d04dc7736bdca41233d", Hash(Enumerable.Range(0, 200).Select(i => (byte)((i * 7) & 255)).ToArray()));
        Assert.True(Blake2s.Personalization.From("short") is null && Blake2s.Personalization.From("mrklSP__") is not null);
    }

    [Fact]
    public void MatchesEveryBlake2sVector()
    {
        if (TzELVectors.Load()?["blake2s"] is not JsonArray cases) return;
        foreach (var c in cases)
        {
            var input = HexBytes.Decode(c?["input"]?.GetValue<string>() ?? "")!;
            var personal = c?["personal"]?.GetValue<string>() ?? "";
            Assert.Equal(c?["output"]?.GetValue<string>(), Hash(input, personal.Length == 0 ? null : personal));
        }
    }
}

// ---- Auth tree --------------------------------------------------------------------------------------

[Collection(ShieldedTestSupport.CpuHeavy)]
public class XMSSAuthTreeTests
{
    /// <summary>The native leaves are the bridge's leaves, so a tree built here signs with keys the bridge's <c>wotsSign</c> holds.</summary>
    [Fact]
    public async Task LeavesAndSeedsAgreeWithTheBridge()
    {
        var bridge = TaquitoBridge.Shared;
        var masterSk = HexBytes.Encode(XMSSAuthTree.FeltOfInt(777));
        var account = await bridge.Call("tzelAccount", masterSk);
        var keys = await bridge.Call("tzelAddressKeys", account.JsonText, 0);
        var askJHex = keys["askJ"]!.StringValue!;
        var pubSeedHex = keys["pubSeed"]!.StringValue!;
        var askJ = HexBytes.Decode(askJHex)!;
        var pubSeed = HexBytes.Decode(pubSeedHex)!;
        Assert.Equal(pubSeedHex, HexBytes.Encode(XMSSAuthTree.PubSeed(askJ)));
        var askBase = HexBytes.Decode(account["askBase"]!.StringValue!)!;
        Assert.Equal(askJHex, HexBytes.Encode(XMSSAuthTree.DeriveAsk(askBase, 0)));
        foreach (var index in new[] { 0, 1, 2, 4097, 65535 })
        {
            var expected = (await bridge.Call("tzelAuthLeaf", askJHex, pubSeedHex, index)).StringValue;
            Assert.Equal(expected, HexBytes.Encode(XMSSAuthTree.Leaf(askJ, pubSeed, index)));
        }
    }

    /// <summary>The whole tree against the shared vectors: the first address's root and a WOTS+ leaf.</summary>
    [Fact]
    public void FullTreeMatchesTheVectors()
    {
        if (TzELVectors.Load() is not { } vectors) return;
        var hierarchy = vectors["key_hierarchy"]!;
        var first = (vectors["addresses"] as JsonArray)!.First(a => a?["j"]?.GetValue<int>() == 0)!;
        var askBase = HexBytes.Decode(hierarchy["ask_base"]!.GetValue<string>())!;
        var askJ = XMSSAuthTree.DeriveAsk(askBase, 0);
        var pubSeed = XMSSAuthTree.PubSeed(askJ);
        Assert.Equal(first["auth_pub_seed"]!.GetValue<string>(), HexBytes.Encode(pubSeed));
        var leaves = XMSSAuthTree.BuildLeaves(askJ, pubSeed, cancel: TestContext.Current.CancellationToken);
        var levels = XMSSAuthTree.Levels(pubSeed, leaves);
        Assert.Equal(first["auth_root"]!.GetValue<string>(), HexBytes.Encode(XMSSAuthTree.Root(levels)));
        Assert.Equal(16, XMSSAuthTree.Path(levels, 3).Count);
        Assert.Equal(leaves[(2 * 32)..(3 * 32)], XMSSAuthTree.Path(levels, 3)[0]);   // the first sibling of leaf 3 is leaf 2
        foreach (var w in (vectors["wots"] as JsonArray)!)
        {
            var ask = HexBytes.Decode(w!["ask_j"]!.GetValue<string>())!;
            var seed = HexBytes.Decode(w["auth_pub_seed"]!.GetValue<string>())!;
            Assert.Equal(w["leaf"]!.GetValue<string>(), HexBytes.Encode(XMSSAuthTree.Leaf(ask, seed, w["key_idx"]!.GetValue<int>())));
        }
    }

    /// <summary>The native tree builds in seconds across the cores, and its root agrees with the bridge's leaves.</summary>
    [Fact]
    public void BuildsAWholeTreeQuickly()
    {
        var askJ = XMSSAuthTree.FeltOfInt(12345);
        var pubSeed = XMSSAuthTree.PubSeed(askJ);
        var started = DateTimeOffset.UtcNow;
        var reported = 0;
        var leaves = XMSSAuthTree.BuildLeaves(askJ, pubSeed, done => Interlocked.Exchange(ref reported, Math.Max(reported, done)), TestContext.Current.CancellationToken);
        var elapsed = DateTimeOffset.UtcNow - started;
        Assert.Equal(XMSSAuthTree.LeavesBytes, leaves.Length);
        Assert.Equal(XMSSAuthTree.Size, reported);
        Assert.Equal(XMSSAuthTree.Leaf(askJ, pubSeed, 4097), leaves[(4097 * 32)..(4098 * 32)]);
        Assert.True(elapsed < TimeSpan.FromSeconds(60), $"took {elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>A real proof is a few hundred KB of hex; the encoder must come back through the bridge as one string.</summary>
    [Fact]
    public async Task ALargeProofEncodesThroughTheBridge()
    {
        var zeroNote = string.Concat(Enumerable.Repeat("00", 1088)) + "0000" + string.Concat(Enumerable.Repeat("00", 1088 + 12 + 1080 + 185));
        var message = new JsonObject
        {
            ["pubkeyHash"] = string.Concat(Enumerable.Repeat("ab", 32)), ["fee"] = "100000", ["v"] = "1", ["producerFee"] = "100000",
            ["proofBytes"] = string.Concat(Enumerable.Repeat("cd", 300_000)),
            ["outputPreimage"] = new JsonArray(Enumerable.Repeat(string.Concat(Enumerable.Repeat("ef", 32)), 9).Select(s => (JsonNode?)s).ToArray()),
            ["clientCm"] = string.Concat(Enumerable.Repeat("11", 32)), ["clientEnc"] = zeroNote, ["producerCm"] = string.Concat(Enumerable.Repeat("22", 32)), ["producerEnc"] = zeroNote,
        };
        var result = await TaquitoBridge.Shared.Call("tzelEncodeShieldMessage", message.ToJsonString());
        var payload = result.StringValue!;
        var header = 3 + 32 + 24 + 4;
        var proof = 4 + 300_000 + 4 + 9 * 32;
        var notes = 32 + 3455 + 32 + 3455;
        Assert.Equal(2 * (header + proof + notes), payload.Length);
        Assert.StartsWith("110002" + string.Concat(Enumerable.Repeat("ab", 32)), payload);
    }

    [Fact]
    public void FeltHelpersMatchTheSpec()
    {
        Assert.Equal("72742d73736d7800000000000000000000000000000000000000000000000000", HexBytes.Encode(XMSSAuthTree.FeltTag("xmss-tr")));
        Assert.Equal("0201000000000000000000000000000000000000000000000000000000000000", HexBytes.Encode(XMSSAuthTree.FeltOfInt(0x0102)));
        Assert.True(HexBytes.Decode("zz") is null && HexBytes.Decode("abc") is null);
        Assert.Equal([0xab, 0xcd], HexBytes.Decode("ABcd"));
        Assert.Equal("abcd", HexBytes.Encode([0xab, 0xcd]));
    }
}

// ---- Store ------------------------------------------------------------------------------------------

public class ShieldedWalletStoreTests
{
    private static readonly Address Me = new("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");

    private static ShieldedAddress Addr(int index, string tag, int next) =>
        new() { Index = index, DJ = "d" + tag, AuthRoot = "r" + tag, PubSeed = "p" + tag, NkTag = "n" + tag, EkV = "v" + tag, EkD = "e" + tag, NextKeyIndex = next };

    [Fact]
    public void FileStoreRoundTripsPrivatelyAndIsBackedUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"signet-shielded-{Guid.NewGuid()}");
        try
        {
            var store = new FileShieldedWalletStore(directory, "shadownet");
            var other = new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq");
            Assert.True(store.Load(Me) is null && store.LoadLeaves(Me, 0) is null && store.OperatorToken() is null);
            var wallet = ShieldedWallet.Make("shadownet");
            Assert.True(HexBytes.Decode(wallet.MasterSk)?.Length == 32 && (HexBytes.Decode(wallet.MasterSk)![31] & 0xf8) == 0, "a 251-bit master key");
            Assert.NotEqual(wallet.MasterSk, ShieldedWallet.Make("shadownet").MasterSk);
            wallet.Addresses.Add(new ShieldedAddress { Index = 0, DJ = "aa", AuthRoot = "bb", PubSeed = "cc", NkTag = "dd", EkV = "ee", EkD = "ff", NextKeyIndex = 3 });
            wallet.Pools.Add(new DepositPool { PubkeyHash = "11", Blind = "22", AddressIndex = 0, AuthDomain = "33", DepositedMutez = "1000000", OperationHash = "ooX", Created = DateTimeOffset.FromUnixTimeSeconds(1_000_000) });
            wallet.Notes.Add(new ShieldedNote { Index = 7, Cm = "44", Rseed = "55", Nullifier = "66", ValueMutez = "1800000" });
            wallet.Notes.Add(new ShieldedNote { Index = 8, Cm = "47", Rseed = "58", Nullifier = "69", ValueMutez = "100", Spent = true });
            wallet.Scanned = 9;
            store.Save(wallet, Me);
            store.SetOperatorToken("secret");
            Assert.True(Equals(store.Load(Me), wallet) && store.Load(other) is null && store.OperatorToken() == "secret");
            // Another account's wallet in the same file, and the token survives both.
            var second = ShieldedWallet.Make("shadownet");
            store.Save(second, other);
            Assert.True(Equals(store.Load(Me), wallet) && Equals(store.Load(other), second) && store.OperatorToken() == "secret");
            store.SetOperatorToken(null);
            Assert.True(store.OperatorToken() is null && Equals(store.Load(Me), wallet));
            Assert.True(wallet.BalanceMutez == 1_800_000 && wallet.Balance == 1.8m);
            // The file is the Mac's form: sorted keys, ISO 8601 dates, LF.
            var text = File.ReadAllText(Path.Combine(directory, "tzel-shadownet.json"));
            Assert.Contains("\"created\": \"1970-01-12T13:46:40Z\"", text);
            Assert.Contains("\"dJ\": \"aa\"", text);
            Assert.DoesNotContain("\r\n", text);
            var leaves = Enumerable.Range(0, XMSSAuthTree.LeavesBytes).Select(i => (byte)(i & 0xff)).ToArray();
            store.SaveLeaves(leaves, Me, 0);
            Assert.True(store.LoadLeaves(Me, 0)!.SequenceEqual(leaves) && store.LoadLeaves(other, 0) is null);
            Assert.Contains("tzel-shadownet.json", WalletBackup.Files);
            Assert.DoesNotContain("tzel-shadownet-auth0.bin", WalletBackup.Files);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A scan that started before a spend must not undo it when it writes: the key index spent
    /// meanwhile, the change address and the record added stay, while the scan's notes, cursors
    /// and record progress land.
    /// </summary>
    [Fact]
    public void AScanMergesOntoTheWalletAsItIsOnFile()
    {
        var @base = new ShieldedWallet("shadownet", "ab")
        {
            Addresses = [Addr(0, "0", 1)],
            DepositNonce = 1,
            Pools = [new DepositPool { PubkeyHash = "p-drained", Blind = "b", AuthDomain = "ad", DepositedMutez = "1000000", OperationHash = "ooA", ShieldedCm = "cmA", Created = DateTimeOffset.UtcNow }],
            Notes = [new ShieldedNote { Index = 3, Cm = "cmA", Rseed = "rs", Nullifier = "nfA", ValueMutez = "800000" }],
            Scanned = 4, NullifiersSeen = 2, LeafCms = ["c0", "c1", "c2", "cmA"],
        };
        var old = new ShieldedWithdrawal
        {
            SubmissionID = "w-old", Recipient = Me.Value, AmountMutez = "1", RollupFeeMutez = "1", ProducerFeeMutez = "1", Nullifiers = ["x"], SubmittedLevel = 1,
            Created = DateTimeOffset.FromUnixTimeSeconds(0), OperationHash = "ooO", Applied = true, OutboxLevel = 1, MessageIndex = 0, ExecutedHash = "ooE",
        };
        @base.Withdrawals = [old];

        // The scan: a new note, the pool retired, the week-old executed withdrawal pruned, cursors on.
        var scanned = @base.Copy();
        scanned.Notes.Add(new ShieldedNote { Index = 5, Cm = "cmB", Rseed = "rs", Nullifier = "nfB", ValueMutez = "100", Memo = "hi" });
        scanned.Pools = [];
        scanned.Withdrawals = [];
        scanned.Scanned = 6; scanned.NullifiersSeen = 3; scanned.LeafCms.AddRange(["cmB", "c5"]);

        // Meanwhile on file: the unshield spent key 1, made address 1, reserved a new pool and recorded itself.
        var latest = @base.Copy();
        latest.Addresses[0].NextKeyIndex = 2;
        latest.Addresses.Add(Addr(1, "1", 0));
        latest.DepositNonce = 2;
        latest.Pools.Add(new DepositPool { PubkeyHash = "p-new", Blind = "b", AuthDomain = "ad", DepositedMutez = "0", Created = DateTimeOffset.UtcNow });
        var fresh = new ShieldedWithdrawal
        {
            SubmissionID = "w-new", Recipient = Me.Value, AmountMutez = "300000", RollupFeeMutez = "100000", ProducerFeeMutez = "100000", Nullifiers = ["nfA"],
            ChangeCm = "cmC", SubmittedLevel = 9, Created = DateTimeOffset.UtcNow, OperationHash = "ooN",
        };
        latest.Withdrawals.Add(ShieldedJSON.Clone(fresh));

        var merged = latest.Merging(scanned, @base);
        Assert.Equal([2, 0], merged.Addresses.Select(a => a.NextKeyIndex));   // a key index is never rolled back and an address never lost
        Assert.Equal(2UL, merged.DepositNonce);
        Assert.Equal(["p-new"], merged.Pools.Select(p => p.PubkeyHash));   // the retired pool goes, the new one stays
        Assert.True(merged.Notes.Select(n => n.Cm).SequenceEqual(["cmA", "cmB"]) && merged.Notes[1].Memo == "hi");
        Assert.True(merged.Scanned == 6 && merged.NullifiersSeen == 3 && merged.LeafCms.Count == 6);
        Assert.Equal(["w-new"], merged.Withdrawals.Select(w => w.Id));   // the pruned record goes, the new one stays

        // The scan's progress on a record lands without undoing what the account did to it since.
        var applying = latest.Copy();
        applying.Withdrawals = [ShieldedJSON.Clone(old), ShieldedJSON.Clone(fresh)];
        var seen = scanned.Copy();
        var progressed = ShieldedJSON.Clone(fresh);
        progressed.Applied = true; progressed.OutboxLevel = 12; progressed.MessageIndex = 1;
        seen.Withdrawals = [ShieldedJSON.Clone(old), progressed];
        var executed = applying.Copy();
        executed.Withdrawals[1].ExecutedHash = "ooX";
        var later = executed.Merging(seen, applying);
        Assert.True(later.Withdrawals[1].Applied && later.Withdrawals[1].OutboxLevel == 12 && later.Withdrawals[1].MessageIndex == 1 && later.Withdrawals[1].ExecutedHash == "ooX");
        var spentMeanwhile = seen.Copy();
        spentMeanwhile.Notes[0].Spent = true;
        Assert.True(latest.Merging(spentMeanwhile, @base).Notes[0].Spent, "a spent note stays spent");

        // The store's update is the one step that writes it, from what is on file at that moment.
        var store = new InMemoryShieldedWalletStore();
        Assert.Null(store.Update(Me, null, w => w.Scanned = 1));   // nothing to update without a wallet
        Assert.True(store.Update(Me, @base, _ => { })?.Scanned == 4 && store.History.Count == 1, "made from the initial wallet");
        Assert.True(store.Update(Me, null, _ => { })?.Scanned == 4 && store.History.Count == 1, "not written when unchanged");
        store.Update(Me, null, w => w.Addresses[0].NextKeyIndex++);
        Assert.True(store.Load(Me)?.Addresses[0].NextKeyIndex == 2 && store.History.Count == 2);
        Assert.Throws<ShieldedError.TreeStale>(() => store.Update(Me, null, _ => throw new ShieldedError.TreeStale()));
        Assert.Equal(2, store.Load(Me)?.Addresses[0].NextKeyIndex);   // a change that throws writes nothing
    }

    /// <summary>
    /// A version 1 file (one wallet for the network, token inside) is read and handed to the
    /// account that adopts it, leaves file included; nothing shielded is lost.
    /// </summary>
    [Fact]
    public void AVersionOneFileIsAdoptedByAnAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"signet-shielded-v1-{Guid.NewGuid()}");
        try
        {
            var v1 = $$"""
            {"version":1,"chain":"shadownet","masterSk":"{{string.Concat(Enumerable.Repeat("07", 32))}}","depositNonce":1,"scanned":28,"nullifiersSeen":9,"operatorToken":"tok",
             "addresses":[{"index":0,"dJ":"aa","authRoot":"bb","pubSeed":"cc","nkTag":"dd","ekV":"ee","ekD":"ff","nextKeyIndex":1}],
             "pools":[{"pubkeyHash":"11","blind":"22","addressIndex":0,"authDomain":"33","depositedMutez":"10000000","operationHash":"ooDeposit","shieldedCm":"44","created":"2026-10-19T20:00:00Z"}],
             "notes":[{"index":26,"cm":"44","rseed":"55","nullifier":"66","valueMutez":"9800000","addressIndex":0,"spent":false}]}
            """;
            PrivateFile.Write(Path.Combine(directory, "tzel-shadownet.json"), Encoding.UTF8.GetBytes(v1));
            var leaves = Enumerable.Repeat((byte)9, XMSSAuthTree.LeavesBytes).ToArray();
            PrivateFile.Write(Path.Combine(directory, "tzel-shadownet-auth0.bin"), leaves);
            var store = new FileShieldedWalletStore(directory, "shadownet");
            Assert.True(store.Load(Me) is null && store.OperatorToken() == "tok");
            var legacy = store.LegacyWallet()!;
            Assert.True(legacy.BalanceMutez == 9_800_000 && legacy.Addresses[0].NextKeyIndex == 1 && legacy.Pools[0].OperationHash == "ooDeposit" && legacy.Scanned == 28);
            store.AdoptLegacyWallet(Me);
            Assert.True(store.LegacyWallet() is null && Equals(store.Load(Me), legacy) && store.OperatorToken() == "tok");
            Assert.True(store.LoadLeaves(Me, 0)!.SequenceEqual(leaves));
            store.AdoptLegacyWallet(Me);   // a second time changes nothing
            Assert.True(Equals(store.Load(Me), legacy));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheFeeRuleAndStateKeysFollowTheKernel()
    {
        Assert.True(RollupState.RequiredTxFee(0) == 100_000 && RollupState.RequiredTxFee(1) == 100_000);
        Assert.True(RollupState.RequiredTxFee(2) == 200_000 && RollupState.RequiredTxFee(7) == 6_400_000 && RollupState.RequiredTxFee(70) == 6_400_000);
        Assert.Equal("/tzel/v1/state/notes/000000000000001a", RollupState.Indexed(RollupState.NotePrefix, 26));
        Assert.Equal("/tzel/v1/state/deposits/balance/ab", RollupState.DepositBalance("ab"));
        Assert.True(Network.Shadownet.Shielded?.Ticketer.Value == "KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4" && Network.Quantumnet.Shielded is null && Network.Mainnet.Shielded is null);
        Assert.True(!Network.Shadownet.CanShield && Network.Shadownet.UsingShielded(true).CanShield && !Network.Quantumnet.UsingShielded(true).CanShield);
        Assert.Equal(2368, ShieldedConfig.Shadownet.ProducerAddressJSON()["ek_v"].Length);
    }

    /// <summary>The settings of a deployment: each item is checked, kept only where it differs from what ships, and a network shields once all are there.</summary>
    [Fact]
    public void SettingsAreCheckedAndKeptPerNetwork()
    {
        var store = new InMemoryAppStateStore();
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), stateStore: store);
        Assert.False(model.IsShieldedEnabled(Network.Shadownet));
        model.SetShieldedEnabled(true, Network.Shadownet);
        Assert.True(model.IsShieldedEnabled(Network.Shadownet) && store.Load().ShieldedEnabled?["Shadownet"] == true);
        Assert.Equal("http://rollup.tzel.tezos.com:8932", model.ShieldedSetting(ShieldedConfig.Setting.RollupNode, Network.Shadownet));
        Assert.False(model.SetShieldedSetting("sr1nope", ShieldedConfig.Setting.RollupAddress, Network.Shadownet));
        Assert.True(model.SetShieldedSetting("rollup.example.org/", ShieldedConfig.Setting.RollupNode, Network.Shadownet));
        Assert.Equal("https://rollup.example.org", model.ShieldedSetting(ShieldedConfig.Setting.RollupNode, Network.Shadownet));
        Assert.Equal(new Uri("https://rollup.example.org"), model.Resolved(Network.Shadownet).Shielded?.RollupNode);
        Assert.Equal("tzel-shadownet-producer", model.Resolved(Network.Shadownet).Shielded?.ProducerResource);   // the shipped producer address stays the bundled one
        model.UseDefaultShieldedSetting(ShieldedConfig.Setting.RollupNode, Network.Shadownet);
        Assert.Null(store.Load().ShieldedSettings);
        // A network Signet ships nothing for shields once every item is filled in.
        Assert.Null(model.Resolved(Network.Quantumnet).Shielded);
        foreach (var setting in ShieldedConfig.AllSettings)
            Assert.True(model.SetShieldedSetting(ShieldedConfig.Shadownet.Text(setting), setting, Network.Quantumnet), setting.Title());
        Assert.Equal("sr1Psv7PdY533c4njAW4qg2acbMhi8goqxcK", model.Resolved(Network.Quantumnet).Shielded?.RollupAddress);
        Assert.Equal(7, store.Load().ShieldedSettings?["Quantumnet"].Count);
    }
}

// ---- The flow, against a rollup in memory -------------------------------------------------------------

/// <summary>The rollup node, proving service and operator as one in-memory state.</summary>
public sealed class MockShieldedRollup : IShieldedRollup
{
    private readonly Lock gate = new();
    public string Address = "sr1Psv7PdY533c4njAW4qg2acbMhi8goqxcK";
    public string AuthDomainHex = string.Concat(Enumerable.Repeat("07", 32));
    public string Ticketer = "KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4";
    public bool VerifierInstalled = true;
    public int Level = 100;
    public ulong PrivateTxsInLevel;
    public Dictionary<string, ulong> Pools = [];
    public List<byte[]> Notes = [];
    public List<string> Nullifiers = [];
    /// <summary>What <c>/tzel/v1/state/tree/root</c> serves; tests set it from the bridge's <c>tzelMerkleRoot</c>.</summary>
    public string? TreeRootHex;
    public int Cemented;
    public List<OutboxEntry> Outbox = [];
    public List<(string Kind, string Payload, string Token)> Submitted = [];
    public List<IReadOnlyList<string>> ProofRequests = [];
    public string? FailProving;
    public bool FailDelivery;
    /// <summary>Audit T01: what Submit does instead of answering: throw before taking the request, or take it and lose the reply.</summary>
    public Exception? SubmitFailure;
    public bool LoseSubmitReply;
    /// <summary>Thrown by every status poll (a network failure while waiting for delivery).</summary>
    public Exception? PollFailure;

    public Task<RollupHead> Head() { lock (gate) return Task.FromResult(new RollupHead($"BLmock{Level}", Level)); }
    public Task<string> SmartRollupAddress() { lock (gate) return Task.FromResult(Address); }

    private static byte[] LE(ulong n, int count) => Enumerable.Range(0, count).Select(i => (byte)(n >> (8 * i))).ToArray();

    private byte[]? Value(string key)
    {
        switch (key)
        {
            case RollupState.AuthDomain: return HexBytes.Decode(AuthDomainHex);
            case RollupState.VerifierConfig: return VerifierInstalled ? [1, 2, 3] : null;
            case RollupState.BridgeTicketer: return Encoding.UTF8.GetBytes(Ticketer);
            case RollupState.TreeSize: return LE((ulong)Notes.Count, 8);
            case RollupState.TreeRoot: return TreeRootHex is null ? null : HexBytes.Decode(TreeRootHex);
            case RollupState.NullifierCount: return LE((ulong)Nullifiers.Count, 8);
            case RollupState.LastInputLevel or RollupState.PrivateTxFeeLevel: return PrivateTxsInLevel > 0 ? LE((ulong)(Level + 1), 4) : null;
            case RollupState.PrivateTxCountInLevel: return LE(PrivateTxsInLevel, 8);
        }
        if (key.StartsWith(RollupState.DepositBalancePrefix, StringComparison.Ordinal))
            return Pools.TryGetValue(key[RollupState.DepositBalancePrefix.Length..], out var balance) ? LE(balance, 8) : null;
        if (key.StartsWith(RollupState.NullifierIndexPrefix, StringComparison.Ordinal) &&
            int.TryParse(key[RollupState.NullifierIndexPrefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && n < Nullifiers.Count)
            return HexBytes.Decode(Nullifiers[n]);
        if (key.StartsWith(RollupState.NotePrefix, StringComparison.Ordinal))
        {
            var parts = key[RollupState.NotePrefix.Length..].Split('/');
            if (!int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var i) || i >= Notes.Count) return null;
            if (parts.Length == 1) return null;   // served chunked, as Shadownet does
            if (parts[1] == "len") return LE((ulong)Notes[i].Length, 8);
            if (parts[1] == "chunk" && int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c))
            {
                var start = c * RollupState.NoteChunkBytes;
                if (start >= Notes[i].Length) return null;
                return Notes[i][start..Math.Min(start + RollupState.NoteChunkBytes, Notes[i].Length)];
            }
        }
        return null;
    }

    public Task<int?> DurableLength(string block, string key) { lock (gate) return Task.FromResult(Value(key)?.Length); }
    public Task<byte[]?> DurableValue(string block, string key) { lock (gate) return Task.FromResult(Value(key)); }
    public Task<string> ProgramHash(string program) => Task.FromResult(string.Concat(Enumerable.Repeat("ab", 32)));

    public Task<string> StartProof(string programHash, IReadOnlyList<string> arguments)
    {
        lock (gate)
        {
            ProofRequests.Add(arguments);
            return Task.FromResult($"job-{ProofRequests.Count}");
        }
    }

    /// <summary>A witness word (<c>0x…</c>, big-endian) as a felt, hex.</summary>
    public static string Felt(string arg)
    {
        var hex = arg[2..].PadLeft(64, '0');
        return HexBytes.Encode(HexBytes.Decode(hex)!.Reverse().ToArray());
    }

    /// <summary>
    /// "Proves" by echoing the witness's public outputs behind the bootloader header, as the
    /// service does: the shield's nine, or the unshield's <c>2 + n + 7</c> (the two commitments the
    /// circuit would compute are computed with the bridge).
    /// </summary>
    public async Task<ProofJob> ProofJobFor(string id, string? lastStatus)
    {
        IReadOnlyList<string>? request;
        string? failure;
        lock (gate) { request = ProofRequests.LastOrDefault(); failure = FailProving; }
        if (failure is not null) return new ProofJob.Failed(failure);
        if (request is null) return new ProofJob.Failed("no witness");
        List<string> outputs;
        var w = request;
        if (w.Count == 171)
        {
            outputs = w.Skip(1).Take(9).Select(Felt).ToList();
        }
        else if (int.TryParse(w[1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && w.Count == 1 + 4 + n * 206 + 24)
        {
            // A transfer: the three output commitments and memo hashes are in the witness.
            var tail = 5 + 206 * n;
            outputs = [.. new[] { w[2], w[3] }.Select(Felt), .. Enumerable.Range(0, n).Select(i => Felt(w[5 + 9 * i])), Felt(w[4]),
                       .. new[] { w[tail], w[tail + 8], w[tail + 16] }.Select(Felt), .. new[] { w[tail + 7], w[tail + 15], w[tail + 23] }.Select(Felt)];
        }
        else
        {
            if (!int.TryParse(w[1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) || w.Count != 1 + 6 + n * 206 + 15) return new ProofJob.Failed("bad witness");
            async Task<string> Commitment(int at)
            {
                var v = ulong.Parse(w[at + 1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                return (await TaquitoBridge.Shared.Call("tzelNoteCommitment", Felt(w[at]), v, Felt(w[at + 2]), Felt(w[at + 3]), Felt(w[at + 4]), Felt(w[at + 5]))).StringValue ?? "";
            }
            var flag = 7 + 206 * n;
            var hasChange = w[flag] != "0x0";
            var changeCm = hasChange ? await Commitment(flag + 1) : HexBytes.Encode(XMSSAuthTree.FeltOfInt(0));
            var feeCm = await Commitment(flag + 8);
            outputs = [.. new[] { w[2], w[3] }.Select(Felt), .. Enumerable.Range(0, n).Select(i => Felt(w[7 + 9 * i])), .. new[] { w[4], w[5], w[6] }.Select(Felt),
                       changeCm, Felt(w[flag + 7]), feeCm, Felt(w[flag + 14])];
        }
        List<string> header = [HexBytes.Encode(XMSSAuthTree.FeltOfInt(1)), HexBytes.Encode(XMSSAuthTree.FeltOfInt((ulong)outputs.Count + 3)),
                               string.Concat(Enumerable.Repeat("ab", 32)), HexBytes.Encode(XMSSAuthTree.FeltOfInt((ulong)outputs.Count))];
        return lastStatus is null ? new ProofJob.Running() : new ProofJob.Done(new ProofBundle("aabbcc", [.. header, .. outputs]));
    }

    public Task<OperatorSubmission> Submit(string kind, string payloadHex, string rollupAddress, string token)
    {
        if (token != "operator-secret") throw new ShieldedError.OperatorRefused("missing Authorization header");
        lock (gate)
        {
            if (SubmitFailure is { } failure) throw failure;
            Submitted.Add((kind, payloadHex, token));
            if (LoseSubmitReply) throw new HttpRequestException("connection reset");
            return Task.FromResult(new OperatorSubmission($"sub-{Submitted.Count}", "commitment_included", "dal"));
        }
    }

    public Task<OperatorSubmission> SubmissionFor(string id, string token)
    {
        lock (gate)
            return PollFailure is { } failure ? Task.FromException<OperatorSubmission>(failure) : Task.FromResult(FailDelivery
                ? new OperatorSubmission(id, "failed", "dal", null, "slot not attested")
                : new OperatorSubmission(id, "submitted_to_l1", "dal", "ooShieldPointer"));
    }

    public Task<int> CementedLevel() { lock (gate) return Task.FromResult(Cemented); }
    public Task<IReadOnlyList<OutboxEntry>> PendingOutbox() { lock (gate) return Task.FromResult<IReadOnlyList<OutboxEntry>>(Outbox.ToList()); }

    public Task<OutboxProof> OutboxProofFor(int outboxLevel, int messageIndex)
    {
        lock (gate)
        {
            if (Cemented < outboxLevel || !Outbox.Any(e => e.OutboxLevel == outboxLevel && e.MessageIndex == messageIndex))
                throw new ShieldedError.WithdrawalNotReady($"outbox level {outboxLevel} is not cemented");
            return Task.FromResult(new OutboxProof("src13Ycd5ZPbQGiLiVuWqAMUhNuaWafb5T7KoqHsMGEAStvScGDFgx", "aabb"));
        }
    }

    private sealed class Reader(byte[] bytes, int offset)
    {
        public int O = offset;
        public int U32() { var v = bytes[O] << 24 | bytes[O + 1] << 16 | bytes[O + 2] << 8 | bytes[O + 3]; O += 4; return v; }
        public byte[] Take(int n) { var s = bytes[O..(O + n)]; O += n; return s; }
    }

    /// <summary>Applies the last submitted transfer as the kernel would. Returns the three commitments.</summary>
    public IReadOnlyList<string> ApplyLastTransfer()
    {
        lock (gate)
        {
            var r = new Reader(HexBytes.Decode(Submitted[^1].Payload)!, 7);
            r.Take(32);
            var end = r.O + r.U32();
            var spent = new List<string>();
            while (r.O < end) spent.Add(HexBytes.Encode(r.Take(r.U32())));
            r.Take(8);
            var cms = Enumerable.Range(0, 3).Select(_ => r.Take(32)).ToList();
            r.Take(r.U32());
            foreach (var cm in cms) Notes.Add([.. cm, .. r.Take(r.U32())]);
            Nullifiers.AddRange(spent);
            return cms.Select(c => HexBytes.Encode(c)).ToList();
        }
    }

    /// <summary>
    /// Applies the last submitted unshield as the kernel would: the nullifiers are spent, the
    /// change note (if any) and the producer note join the tree, and the ticketer's <c>burn</c> for
    /// the recipient is queued in the outbox at the current level.
    /// </summary>
    public (string? ChangeCm, IReadOnlyList<string> Nullifiers) ApplyLastUnshield()
    {
        lock (gate)
        {
            var r = new Reader(HexBytes.Decode(Submitted[^1].Payload)!, 7);
            r.Take(32);
            var end = r.O + r.U32();
            var spent = new List<string>();
            while (r.O < end) spent.Add(HexBytes.Encode(r.Take(r.U32())));
            var vPub = BitConverter.ToUInt64(r.Take(8));
            r.Take(8);
            var recipient = Encoding.UTF8.GetString(r.Take(r.U32()));
            var cmChange = r.Take(32);
            r.Take(r.U32());
            string? changeCm = null;
            if (r.Take(1)[0] == 0xff) { Notes.Add([.. cmChange, .. r.Take(r.U32())]); changeCm = HexBytes.Encode(cmChange); }
            var cmFee = r.Take(32);
            Notes.Add([.. cmFee, .. r.Take(r.U32())]);
            Nullifiers.AddRange(spent);
            Outbox.Add(new OutboxEntry(Level, Outbox.Count, Ticketer, "burn", new Address(recipient).ForgedContract() ?? "", vPub));
            return (changeCm, spent);
        }
    }

    /// <summary>Puts the client note of the last submitted shield into the tree, as the kernel would.</summary>
    public string ApplyLastShield()
    {
        lock (gate)
        {
            var bytes = HexBytes.Decode(Submitted[^1].Payload)!;
            var proofLength = bytes[59] << 24 | bytes[60] << 16 | bytes[61] << 8 | bytes[62];
            var start = 63 + proofLength;
            var note = bytes[start..(start + RollupState.PublishedNoteBytes)];
            Notes.Add(note);
            Pools.Remove(HexBytes.Encode(bytes[3..35]));
            return HexBytes.Encode(note[..32]);
        }
    }
}

[Collection(ShieldedTestSupport.CpuHeavy)]
public class ShieldFlowTests
{
    static ShieldFlowTests() => ShieldedRollupExtensions.DeliveryPollInterval = TimeSpan.FromMilliseconds(10);

    internal static WalletViewModel MakeModel(IShieldedWalletStore store, IShieldedRollup rollup, IChainService? chain = null, IReadOnlyList<Wallet>? wallets = null)
    {
        ShieldedRollupExtensions.DeliveryPollInterval = TimeSpan.FromMilliseconds(10);
        wallets ??= WalletViewModel.SampleWallets;
        var keys = new InMemoryWalletStore();
        keys.AddWithSecretKey(wallets[0], "edskSAMPLE");
        if (wallets.Count > 1) keys.AddWithSecretKey(wallets[1], "edeskSAMPLE");
        var model = new WalletViewModel(wallets, chain ?? new MockChainService(), walletStore: keys, stateStore: new InMemoryAppStateStore());
        model.ConfigureShielded((_, _) => store, _ => rollup);
        model.SetShieldedEnabled(true, Network.Shadownet);
        model.SwitchNetwork(Network.Shadownet);
        model.ShieldedCreditPolls = 2;
        model.ShieldedPollInterval = TimeSpan.FromMilliseconds(10);
        return model;
    }

    private static Address Me => WalletViewModel.SampleWallets[0].Address;

    [Fact]
    public async Task ShieldsThroughDepositProofAndOperatorWithEveryStatePersistedFirst()
    {
        var store = new InMemoryShieldedWalletStore();
        var rollup = new MockShieldedRollup();
        var model = MakeModel(store, rollup);
        Assert.True(model.CanShield && model.Network.CanShield);
        // Nothing is on file yet and the token is not set: the Shield sheet can prepare, but not shield.
        var pool = await model.PrepareShield();
        var wallet = store.Load(Me)!;
        Assert.True(wallet.Addresses.Count == 1 && wallet.Addresses[0].NextKeyIndex == 0 && ShieldedTestSupport.Same(wallet.Pools, new List<DepositPool> { pool }) && wallet.DepositNonce == 1);
        Assert.Equal(XMSSAuthTree.LeavesBytes, store.LoadLeaves(Me, 0)?.Length);
        Assert.True(pool.OperationHash is null && pool.AuthDomain == rollup.AuthDomainHex && pool.AddressIndex == 0);
        // Preparing again reuses the unfunded pool rather than minting ids.
        Assert.True(ShieldedTestSupport.Same(await model.PrepareShield(), pool));
        var estimate = await model.EstimateShield(2, pool);
        Assert.True(estimate.RollupFeeMutez == 100_000 && estimate.ProducerFeeMutez == 100_000 && estimate.ShieldedAmount == 1.8m && estimate.Estimate.Limits is not null);
        await Assert.ThrowsAsync<ShieldedError.OperatorTokenMissing>(() => model.ShieldTez(estimate, null));
        Assert.Null(store.Load(Me)?.Pools.First().OperationHash);   // nothing was sent without the token

        model.SetShieldedOperatorToken("operator-secret", Network.Shadownet);
        Assert.True(model.ShieldedOperatorTokenIsSet(Network.Shadownet) && !model.ShieldedOperatorTokenIsSet(Network.Mainnet));
        // The rollup credits the pool when the (mock) deposit lands.
        rollup.Pools[pool.PubkeyHash] = 2_000_000;
        var receipt = await model.ShieldTez(estimate, null);
        Assert.True(receipt.DepositHash == "ooMockShieldDeposit" && receipt.ShieldedMutez == 1_800_000 && receipt.RollupFeeMutez == 100_000 && receipt.OperationHash == "ooShieldPointer");
        wallet = store.Load(Me)!;
        Assert.True(wallet.Pools[0].OperationHash == "ooMockShieldDeposit" && wallet.Pools[0].DepositedMutez == "2000000" && wallet.Pools[0].ShieldedCm == receipt.Cm);
        Assert.Equal(1, wallet.Addresses[0].NextKeyIndex);
        // The key index was on file before the witness existed, and the deposit before the block was waited for.
        var saves = store.History;
        var indexSpent = saves.FindIndex(s => s.Addresses.FirstOrDefault()?.NextKeyIndex == 1);
        var shieldRecorded = saves.FindIndex(s => s.Pools.FirstOrDefault()?.ShieldedCm is not null);
        var depositRecorded = saves.FindIndex(s => s.Pools.FirstOrDefault()?.OperationHash is not null);
        Assert.True(depositRecorded >= 0 && depositRecorded < indexSpent && indexSpent < shieldRecorded);
        // What went to the operator: a version 17 shield message for this pool, built from the proof.
        var sent = rollup.Submitted[^1];
        Assert.True(sent.Kind == "shield" && sent.Payload.StartsWith("110002" + pool.PubkeyHash, StringComparison.Ordinal) && !sent.Payload.Contains("operator-secret"));
        Assert.True(rollup.ProofRequests[^1].Count == 171 && rollup.ProofRequests[^1][0] == "0xaa");

        // The kernel applies it: the note is ours, the pool is gone, the row shows 1.8 tz.
        var cm = rollup.ApplyLastShield();
        Assert.Equal(receipt.Cm, cm);
        await model.Refresh();
        var shielded = model.ShieldedWallet!;
        Assert.True(shielded.Notes.Count == 1 && shielded.Notes[0].Cm == cm && shielded.Notes[0].ValueMutez == "1800000" && shielded.Notes[0].Index == 0 && shielded.Scanned == 1);
        Assert.True(shielded.Pools.Count == 0 && model.ShieldedPools.Count == 0);
        var row = model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded);
        Assert.True(row.Amount == 1.8m && row.Unavailable is null && row.Details.Count == 0);
        // Spent on the rollup: the nullifier appears and the balance drops.
        rollup.Nullifiers = [shielded.Notes[0].Nullifier];
        await model.Refresh();
        Assert.True(model.ShieldedWallet?.Notes[0].Spent == true && model.ShieldedWallet?.BalanceMutez == 0 && model.ShieldedWallet?.NullifiersSeen == 1);
        Assert.Equal(0, model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded).Amount);
        // Another account in the same directory has a shielded wallet of its own: nothing to show yet.
        rollup.Nullifiers = [];
        model.Select(WalletViewModel.SampleWallets[1]);
        await model.Refresh();
        Assert.True(model.ShieldedWallet is null && model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded).Amount == 0 && model.ShieldedPools.Count == 0);
        model.Select(WalletViewModel.SampleWallets[0]);
        await model.Refresh();
        Assert.Equal(1, model.ShieldedWallet?.Notes.Count);
    }

    [Fact]
    public async Task RefusesAStaleContextARollupForAnotherDeploymentAndAnUncreditedPool()
    {
        var store = new InMemoryShieldedWalletStore();
        var rollup = new MockShieldedRollup();
        var model = MakeModel(store, rollup);
        model.SetShieldedOperatorToken("operator-secret", Network.Shadownet);
        rollup.Address = "sr1Ghq66tYK9y3r8CC1Tf8i8m5nxh8nTvZEf";
        Assert.Equal(new ShieldedError.RollupMismatch("sr1Ghq66tYK9y3r8CC1Tf8i8m5nxh8nTvZEf", "sr1Psv7PdY533c4njAW4qg2acbMhi8goqxcK"),
                     await Assert.ThrowsAsync<ShieldedError.RollupMismatch>(() => model.PrepareShield()));
        rollup.Address = "sr1Psv7PdY533c4njAW4qg2acbMhi8goqxcK";
        rollup.Ticketer = "KT1Mkqf6TnMVmhAAuBJCizkzAu4Nhg6DTPmM";
        Assert.Equal(new ShieldedError.TicketerMismatch("KT1Mkqf6TnMVmhAAuBJCizkzAu4Nhg6DTPmM", "KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4"),
                     await Assert.ThrowsAsync<ShieldedError.TicketerMismatch>(() => model.PrepareShield()));
        rollup.Ticketer = "KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4";
        Assert.Null(store.Load(Me));   // no wallet before the rollup passed its checks
        var pool = await model.PrepareShield();
        var estimate = await model.EstimateShield(1, pool);
        // Another account was selected after the estimate: nothing is sent.
        model.Select(WalletViewModel.SampleWallets[1]);
        await Assert.ThrowsAsync<WalletViewModel.WalletError.ContextChanged>(() => model.ShieldTez(estimate, null));
        model.Select(WalletViewModel.SampleWallets[0]);
        var again = await model.EstimateShield(1, pool);   // the context moved, so a fresh estimate
        // A pool the rollup never credited: the deposit is on file, the shield gives up and the pool stays to retry.
        rollup.Pools.Clear();
        Assert.Equal(new ShieldedError.PoolNotCredited(pool.PubkeyHash), await Assert.ThrowsAsync<ShieldedError.PoolNotCredited>(() => model.ShieldTez(again, null)));
        var wallet = store.Load(Me)!;
        Assert.True(wallet.Pools[0].OperationHash == "ooMockShieldDeposit" && wallet.Pools[0].ShieldedCm is null && wallet.Addresses[0].NextKeyIndex == 0);
        await model.Refresh();
        Assert.True(model.ShieldedPools.Count == 1 && model.ShieldedPools[0].BalanceMutez is null);
        // Credited later and too small for the fees, then enough: ShieldPool picks it up.
        rollup.Pools[pool.PubkeyHash] = 150_000;
        Assert.Equal(new ShieldedError.PoolTooSmall(150_000, 200_000), await Assert.ThrowsAsync<ShieldedError.PoolTooSmall>(() => model.ShieldPool(pool.PubkeyHash)));
        rollup.Pools[pool.PubkeyHash] = 1_000_000;
        rollup.PrivateTxsInLevel = 3;   // the dynamic fee: 0.1 tez doubled twice
        var receipt = await model.ShieldPool(pool.PubkeyHash);
        Assert.True(receipt.RollupFeeMutez == 400_000 && receipt.ShieldedMutez == 500_000 && receipt.DepositHash is null);
        await Assert.ThrowsAsync<ShieldedError.PoolUnknown>(() => model.ShieldPool("ff"));
    }

    private sealed class Funder : TestChainService
    {
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(1));
    }

    [Fact]
    public async Task ALegacyWalletGoesToTheAccountThatFundedIt()
    {
        var legacy = ShieldedWallet.Make("shadownet");
        legacy.Notes.Add(new ShieldedNote { Index = 26, Cm = "11", Rseed = "22", Nullifier = "33", ValueMutez = "9800000" });
        legacy.Pools.Add(new DepositPool { PubkeyHash = "44", Blind = "55", AuthDomain = "66", DepositedMutez = "10000000", ShieldedCm = "11", Created = DateTimeOffset.UtcNow });
        var store = new InMemoryShieldedWalletStore(legacy: legacy);
        var model = MakeModel(store, new MockShieldedRollup(), new Funder());
        await model.Refresh();
        // No operation hash to ask TzKT about: the selected account adopts it.
        Assert.True(store.LegacyWallet() is null && store.Load(Me)?.Notes.Count == 1);
        Assert.True(model.ShieldedWallet?.BalanceMutez == 9_800_000 && model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded).Amount == 9.8m);
        model.Select(WalletViewModel.SampleWallets[1]);
        await model.Refresh();
        Assert.True(model.ShieldedWallet is null && model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded).Amount == 0);
    }

    private sealed class Down : IShieldedRollup
    {
        private static HttpRequestException Fail() => new("cannot connect to host");
        public Task<RollupHead> Head() => throw Fail();
        public Task<string> SmartRollupAddress() => throw Fail();
        public Task<int?> DurableLength(string block, string key) => Task.FromResult<int?>(null);
        public Task<byte[]?> DurableValue(string block, string key) => Task.FromResult<byte[]?>(null);
        public Task<string> ProgramHash(string program) => throw Fail();
        public Task<string> StartProof(string programHash, IReadOnlyList<string> arguments) => throw Fail();
        public Task<ProofJob> ProofJobFor(string id, string? lastStatus) => throw Fail();
        public Task<OperatorSubmission> Submit(string kind, string payloadHex, string rollupAddress, string token) => throw Fail();
        public Task<OperatorSubmission> SubmissionFor(string id, string token) => throw Fail();
        public Task<int> CementedLevel() => throw Fail();
        public Task<IReadOnlyList<OutboxEntry>> PendingOutbox() => throw Fail();
        public Task<OutboxProof> OutboxProofFor(int outboxLevel, int messageIndex) => throw Fail();
    }

    [Fact]
    public async Task TheDashboardRowReportsAnUnreachableRollupWithoutLosingTheWallet()
    {
        var wallet = ShieldedWallet.Make("shadownet");
        wallet.Notes.Add(new ShieldedNote { Index = 0, Cm = "11", Rseed = "22", Nullifier = "33", ValueMutez = "2500000" });
        wallet.Pools.Add(new DepositPool { PubkeyHash = "44", Blind = "55", AuthDomain = "66", DepositedMutez = "1", OperationHash = "ooX", Created = DateTimeOffset.UtcNow });
        var store = new InMemoryShieldedWalletStore(wallets: new() { [Me.Value] = wallet });
        var keys = new InMemoryWalletStore();
        keys.AddWithSecretKey(WalletViewModel.SampleWallets[0], "edskSAMPLE");
        var model = new WalletViewModel(WalletViewModel.SampleWallets, new MockChainService(), walletStore: keys, stateStore: new InMemoryAppStateStore());
        model.ConfigureShielded((_, _) => store, _ => new Down());
        await model.Refresh();
        Assert.Equal(["tez"], model.Assets.Select(a => a.Id));   // off by default
        model.SetShieldedEnabled(true, Network.Shadownet);
        model.SwitchNetwork(Network.Shadownet);
        await model.Refresh();
        var row = model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded);
        Assert.True(row.Amount == 2.5m && row.Unavailable?.Contains("Shielded balance unavailable") == true);
        Assert.True(Equals(model.ShieldedWallet, wallet) && model.ShieldedPools.Count == 1 && model.ShieldedPools[0].BalanceMutez is null);
        Assert.Empty(store.History);   // nothing is written when the rollup cannot be read
        // Quantumnet has shielded transactions but no services Signet knows: the placeholder row.
        model.SetShieldedEnabled(true, Network.Quantumnet);
        model.SwitchNetwork(Network.Quantumnet);
        await model.Refresh();
        Assert.Equal(AssetBalance.ShieldedPlaceholder, model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded));
        Assert.False(model.CanShield);
    }
}

[Collection(ShieldedTestSupport.CpuHeavy)]
public class UnshieldFlowTests
{
    private static Address Me => WalletViewModel.SampleWallets[0].Address;

    private static async Task<(WalletViewModel Model, InMemoryShieldedWalletStore Store, MockShieldedRollup Rollup, ShieldedNote Note)> ShieldedModel()
    {
        var store = new InMemoryShieldedWalletStore();
        var rollup = new MockShieldedRollup();
        var model = ShieldFlowTests.MakeModel(store, rollup, wallets: [WalletViewModel.SampleWallets[0], .. WalletViewModel.SampleWallets.Skip(1)]);
        model.SetShieldedOperatorToken("operator-secret", Network.Shadownet);
        // A 1.8 tz note at tree position 0, from a 2 tz deposit.
        var pool = await model.PrepareShield();
        rollup.Pools[pool.PubkeyHash] = 2_000_000;
        await model.ShieldTez(await model.EstimateShield(2, pool), null);
        var cm = rollup.ApplyLastShield();
        rollup.TreeRootHex = (await TaquitoBridge.Shared.Call("tzelMerkleRoot", JsonSerializer.Serialize(new[] { cm }))).StringValue;
        await model.Refresh();
        var note = model.ShieldedWallet!.Notes[0];
        Assert.True(model.ShieldedWallet.LeafCms.SequenceEqual([cm]) && note.ValueMutez == "1800000" && !note.Spent);
        return (model, store, rollup, note);
    }

    [Fact]
    public async Task UnshieldsANoteWithChangeAndFollowsTheWithdrawalToTezos()
    {
        var (model, store, rollup, note) = await ShieldedModel();
        // A bad recipient, or more than the notes hold, is refused at the estimate.
        Assert.Equal(new ShieldedError.BadRecipient("bob"), await Assert.ThrowsAsync<ShieldedError.BadRecipient>(() => model.EstimateUnshield(1, "bob")));
        Assert.Equal(new ShieldedError.InsufficientShielded(1_800_000, 2_200_000), await Assert.ThrowsAsync<ShieldedError.InsufficientShielded>(() => model.EstimateUnshield(2, Me.Value)));
        var estimate = await model.EstimateUnshield(1, $" {Me.Value} ");
        Assert.True(ShieldedTestSupport.Same(estimate.Inputs, new[] { note }) && estimate.RollupFeeMutez == 100_000 && estimate.ProducerFeeMutez == 100_000 &&
                    estimate.ChangeMutez == 600_000 && estimate.Recipient == Me.Value);
        // The rollup's fee moved since the estimate: refused before anything is proved or any key spent.
        rollup.PrivateTxsInLevel = 3;
        Assert.Equal(new ShieldedError.FeeChanged(100_000, 400_000), await Assert.ThrowsAsync<ShieldedError.FeeChanged>(() => model.UnshieldTez(estimate)));
        rollup.PrivateTxsInLevel = 0;
        Assert.True(store.Load(Me)?.Addresses[0].NextKeyIndex == 1 && rollup.ProofRequests.Count == 1);

        var receipt = await model.UnshieldTez(estimate);
        Assert.True(receipt.Amount == 1 && receipt.ChangeMutez == 600_000 && receipt.Withdrawal.Recipient == Me.Value && receipt.Withdrawal.Nullifiers.SequenceEqual([note.Nullifier]));
        Assert.True(receipt.Withdrawal.OperationHash == "ooShieldPointer" && receipt.Withdrawal.SubmittedLevel == rollup.Level && receipt.Withdrawal.ChangeCm is not null);
        var wallet = store.Load(Me)!;
        Assert.True(wallet.Addresses.Count == 2 && wallet.Addresses[0].NextKeyIndex == 2 && wallet.Addresses[1].NextKeyIndex == 0, "one key spent, a change address made");
        Assert.Equal(XMSSAuthTree.LeavesBytes, store.LoadLeaves(Me, 1)?.Length);   // the change address's signing tree is kept
        Assert.True(wallet.Withdrawals.Count == 1 && !wallet.Withdrawals[0].Applied && wallet.AvailableMutez == 0 && wallet.BalanceMutez == 1_800_000 && wallet.ReservedMutez == 1_800_000);
        Assert.Equal(["unshielding"], model.Assets.First(a => a.Kind is AssetBalance.AssetKind.Shielded).Details.Select(d => d.Id));
        // The witness: one input, 227 felts after the length word; the message: tag 4, then the root.
        var witness = rollup.ProofRequests[^1];
        Assert.True(witness.Count == 1 + 6 + 206 + 15 && witness[0] == "0xe3" && witness[1] == "0x1");
        var sent = rollup.Submitted[^1];
        Assert.True(sent.Kind == "unshield" && sent.Payload.StartsWith("110004", StringComparison.Ordinal) && sent.Payload.Substring(14, 64) == rollup.TreeRootHex);
        // The key index was on file before the proof was asked for, the withdrawal before the operator was polled.
        var indexSpent = store.History.FindIndex(s => s.Addresses.FirstOrDefault()?.NextKeyIndex == 2);
        var recorded = store.History.FindIndex(s => s.Withdrawals.Count > 0);
        Assert.True(indexSpent >= 0 && indexSpent < recorded);
        // A reserved note is not offered to another unshield.
        Assert.Equal(new ShieldedError.InsufficientShielded(0, 300_000), await Assert.ThrowsAsync<ShieldedError.InsufficientShielded>(() => model.EstimateUnshield(0.1m, Me.Value)));

        // The kernel applies it: the note is spent, the change note is ours, the withdrawal is in the outbox and waiting.
        var applied = rollup.ApplyLastUnshield();
        Assert.True(applied.ChangeCm == receipt.Withdrawal.ChangeCm && applied.Nullifiers.SequenceEqual([note.Nullifier]));
        await model.Refresh();
        wallet = model.ShieldedWallet!;
        Assert.True(wallet.Notes.Count == 2 && wallet.Notes[0].Spent && wallet.Notes[1].ValueMutez == "600000" && wallet.Notes[1].AddressIndex == 1 && wallet.Notes[1].Index == 1);
        Assert.True(wallet.LeafCms.Count == 3 && wallet.BalanceMutez == 600_000 && wallet.AvailableMutez == 600_000);
        var status = model.ShieldedWithdrawals[0];
        Assert.True(status.Withdrawal.Applied && status.Withdrawal.OutboxLevel == rollup.Level && status.Withdrawal.MessageIndex == 0);
        Assert.Equal(new ShieldedWithdrawalStatus.WithdrawalState.Waiting(rollup.Level), status.State);
        await Assert.ThrowsAsync<ShieldedError.WithdrawalNotReady>(() => model.EstimateWithdrawalExecution(status.Id));
        // Cemented: the account executes it through the approved-limits path.
        rollup.Cemented = rollup.Level;
        await model.Refresh();
        Assert.IsType<ShieldedWithdrawalStatus.WithdrawalState.Ready>(model.ShieldedWithdrawals[0].State);
        var execution = await model.EstimateWithdrawalExecution(status.Id);
        Assert.True(execution.Execution.CementedCommitment.StartsWith("src1", StringComparison.Ordinal) && execution.Execution.Rollup == ShieldedConfig.Shadownet.RollupAddress && execution.Estimate.Limits is not null);
        var hash = await model.ExecuteWithdrawal(execution, null);
        Assert.True(hash == "ooMockOutbox" && model.ShieldedWallet?.Withdrawals[0].ExecutedHash == "ooMockOutbox");
        Assert.Equal(new ShieldedWithdrawalStatus.WithdrawalState.Executed("ooMockOutbox"), model.ShieldedWithdrawals[0].State);
        Assert.Equal(new ShieldedError.WithdrawalNotReady("it has been executed already"),
                     await Assert.ThrowsAsync<ShieldedError.WithdrawalNotReady>(() => model.EstimateWithdrawalExecution(status.Id)));
    }

    [Fact]
    public async Task AnUndeliveredUnshieldFreesItsNotesAndAnExactOneMakesNoChange()
    {
        var (model, store, rollup, note) = await ShieldedModel();
        // The operator gives up on it: no withdrawal stays, the notes are available again, the key index stays spent.
        rollup.FailDelivery = true;
        var estimate = await model.EstimateUnshield(1, Me.Value);
        Assert.Equal(new ShieldedError.SubmissionFailed("slot not attested"), await Assert.ThrowsAsync<ShieldedError.SubmissionFailed>(() => model.UnshieldTez(estimate)));
        var wallet = store.Load(Me)!;
        Assert.True(wallet.Withdrawals.Count == 0 && wallet.AvailableMutez == 1_800_000 && wallet.Addresses[0].NextKeyIndex == 2 && wallet.Addresses.Count == 2);
        rollup.FailDelivery = false;
        // Unshielding exactly what is left after the fees: no change note, the flag and its fields are zero.
        var exact = await model.EstimateUnshield(1.6m, "KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4");
        Assert.True(exact.ChangeMutez == 0 && ShieldedTestSupport.Same(exact.Inputs, new[] { note }));
        var receipt = await model.UnshieldTez(exact);
        Assert.True(receipt.ChangeMutez == 0 && receipt.Withdrawal.ChangeCm is null);
        wallet = store.Load(Me)!;
        Assert.True(wallet.Addresses.Count == 2 && wallet.Addresses[0].NextKeyIndex == 3, "no further change address");
        var witness = rollup.ProofRequests[^1];
        Assert.Equal(Enumerable.Repeat("0x0", 8), witness.Skip(7 + 206).Take(8));
        // Forgetting one the operator has not delivered frees the notes; when it lands after all, the scan still spends them.
        model.ForgetWithdrawal(receipt.Withdrawal.Id);
        Assert.True(store.Load(Me)?.AvailableMutez == 1_800_000 && model.ShieldedWithdrawals.Count == 0);
        var applied = rollup.ApplyLastUnshield();
        Assert.Null(applied.ChangeCm);
        await model.Refresh();
        Assert.True(model.ShieldedWallet?.BalanceMutez == 0 && model.ShieldedWallet?.Notes.Count == 1 && model.ShieldedWallet?.LeafCms.Count == 2);
    }

    /// <summary>
    /// Audit T01: an unshield whose outcome is uncertain keeps its record and its reserved notes,
    /// since the operator may still deliver it; only the operator's own refusal frees them.
    /// </summary>
    [Fact]
    public async Task AnUncertainSubmissionKeepsItsRecordAndOnlyARefusalFreesTheNotes()
    {
        var (model, store, rollup, _) = await ShieldedModel();
        // The operator takes the request and the reply is lost.
        rollup.LoseSubmitReply = true;
        var estimate = await model.EstimateUnshield(1, Me.Value);
        var before = rollup.Submitted.Count;
        await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() => model.UnshieldTez(estimate));
        var wallet = store.Load(Me)!;
        Assert.Equal(("unshield", before + 1), (rollup.Submitted[^1].Kind, rollup.Submitted.Count));
        Assert.True(wallet.Withdrawals.Count == 1 && wallet.Withdrawals[0].SubmissionID.StartsWith("local:", StringComparison.Ordinal));
        Assert.True(!wallet.Withdrawals[0].Submitted && wallet.Withdrawals[0].PayloadHex == rollup.Submitted[^1].Payload);
        Assert.Equal(0ul, wallet.AvailableMutez);
        rollup.LoseSubmitReply = false;
        model.ForgetWithdrawal(wallet.Withdrawals[0].Id);

        // Accepted, then every status poll fails: kept and reserved under the operator's id, acknowledged.
        rollup.PollFailure = new HttpRequestException("no route to host");
        estimate = await model.EstimateUnshield(1, Me.Value);
        await model.UnshieldTez(estimate);
        wallet = store.Load(Me)!;
        Assert.True(wallet.Withdrawals.Count == 1 && wallet.Withdrawals[0].SubmissionID == $"sub-{rollup.Submitted.Count}" && wallet.Withdrawals[0].Submitted && wallet.AvailableMutez == 0);
        rollup.PollFailure = null;
        model.ForgetWithdrawal(wallet.Withdrawals[0].Id);

        // A server error is not a refusal either; a 4xx is.
        rollup.SubmitFailure = new ShieldedError.BadReply("HTTP 502 from operator");
        estimate = await model.EstimateUnshield(1, Me.Value);
        await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() => model.UnshieldTez(estimate));
        Assert.Single(store.Load(Me)!.Withdrawals);
        model.ForgetWithdrawal(store.Load(Me)!.Withdrawals[0].Id);
        rollup.SubmitFailure = new ShieldedError.Rejected(422, "operator: bad proof");
        estimate = await model.EstimateUnshield(1, Me.Value);
        await Assert.ThrowsAsync<ShieldedError.Rejected>(() => model.UnshieldTez(estimate));
        wallet = store.Load(Me)!;
        Assert.True(wallet.Withdrawals.Count == 0 && wallet.AvailableMutez == 1_800_000);
    }

    [Fact]
    public void SelectsTheLargestNotesFirstAndRefusesMoreThanSevenInputs()
    {
        static ShieldedNote Note(int i, ulong v) => new() { Index = i, Cm = $"cm{i}", Nullifier = $"nf{i}", ValueMutez = v.ToString(CultureInfo.InvariantCulture) };
        var notes = Enumerable.Range(0, 10).Select(i => Note(i, (ulong)(i + 1) * 100_000)).ToList();
        Assert.Equal([9], WalletViewModel.SelectNotes(notes, 1_000_000).Select(n => n.Index));
        Assert.Equal([9, 8], WalletViewModel.SelectNotes(notes, 1_000_001).Select(n => n.Index));
        Assert.Equal([9, 8, 7, 6, 5, 4, 3], WalletViewModel.SelectNotes(notes, 4_900_000).Select(n => n.Index));
        Assert.Equal(new ShieldedError.TooManyInputs(8), Assert.Throws<ShieldedError.TooManyInputs>(() => WalletViewModel.SelectNotes(notes, 4_900_001)));
        Assert.Equal(new ShieldedError.InsufficientShielded(5_500_000, 5_500_001), Assert.Throws<ShieldedError.InsufficientShielded>(() => WalletViewModel.SelectNotes(notes, 5_500_001)));
        Assert.Empty(WalletViewModel.SelectNotes([], 0));
    }

    /// <summary>
    /// Alice (sample account 0) sends to Bob (sample account 1), both in the same directory:
    /// Bob's shielded address comes from his Shielded address sheet, goes into Alice's address
    /// book, and the note reaches him with the memo while Alice gets her change.
    /// </summary>
    [Fact]
    public async Task SendsAShieldedTransferToAnotherAccountsAddress()
    {
        var (model, store, rollup, note) = await ShieldedModel();
        var alice = WalletViewModel.SampleWallets[0];
        var bob = WalletViewModel.SampleWallets[1];
        // Bob hands out his address: the JSON other wallets take, with its fingerprint; a new one is a fresh address of his.
        model.Select(bob);
        var handed = await model.ShieldedReceiveAddressForSelected();
        Assert.True(handed.Index == 0 && handed.Json.Contains("\"ek_v\"") && handed.Fingerprint.Length == 64);
        var fresh = await model.MakeShieldedAddress();
        Assert.True(fresh.Index == 1 && fresh.Fingerprint != handed.Fingerprint && store.Load(bob.Address)?.Addresses.Count == 2);
        Assert.Equal(fresh, await model.ShieldedReceiveAddressForSelected());   // the newest address is the one handed out
        // Alice pastes it, saves it, and sends 1 tz with a memo.
        model.Select(alice);
        await model.Refresh();
        var pasted = await model.ShieldedRecipient(fresh.Json);
        Assert.True(pasted.Fingerprint == fresh.Fingerprint && pasted.Name.Length == 0);
        await Assert.ThrowsAnyAsync<ShieldedError>(() => model.ShieldedRecipient("{\"d_j\": \"zz\"}"));
        model.AddShieldedContact(pasted, "Bob");
        Assert.Equal(new ShieldedError.ContactExists("Bob"), Assert.Throws<ShieldedError.ContactExists>(() => model.AddShieldedContact(pasted, "Robert")));
        var contact = model.ShieldedContacts()[0];
        Assert.True(contact.Name == "Bob" && contact.Fingerprint == fresh.Fingerprint);
        Assert.Equal("Bob", (await model.ShieldedRecipient(fresh.Json)).Name);   // a known address comes back with its name
        var estimate = await model.EstimateSendShielded(1, contact, "for lunch");
        Assert.True(ShieldedTestSupport.Same(estimate.Inputs, new[] { note }) && estimate.ChangeMutez == 600_000 && estimate.Fees == 0.2m);
        await Assert.ThrowsAsync<ShieldedError.MemoTooLong>(() => model.EstimateSendShielded(1, contact, new string('x', 1025)));
        var receipt = await model.SendShielded(estimate);
        Assert.True(receipt.Amount == 1 && receipt.Transfer.RecipientName == "Bob" && receipt.Transfer.Memo == "for lunch" &&
                    receipt.Transfer.Nullifiers.SequenceEqual([note.Nullifier]) && !receipt.Transfer.Applied);
        var witness = rollup.ProofRequests[^1];
        Assert.True(witness.Count == 1 + 4 + 206 + 24 && witness[0] == "0xea" && witness[1] == "0x1");
        var sent = rollup.Submitted[^1];
        Assert.True(sent.Kind == "transfer" && sent.Payload.StartsWith("110003", StringComparison.Ordinal));
        var wallet = store.Load(alice.Address)!;
        Assert.True(wallet.Transfers.Count == 1 && wallet.AvailableMutez == 0 && wallet.Addresses.Count == 2 && wallet.Addresses[0].NextKeyIndex == 2);
        // The kernel applies it: Alice's note is spent and her change is back; Bob has 1 tz with the memo.
        var cms = rollup.ApplyLastTransfer();
        Assert.True(cms[0] == receipt.Transfer.RecipientCm && cms[1] == receipt.Transfer.ChangeCm);
        await model.Refresh();
        wallet = model.ShieldedWallet!;
        Assert.True(wallet.Notes.Count == 2 && wallet.Notes[0].Spent && wallet.Notes[1].ValueMutez == "600000" && wallet.Notes[1].AddressIndex == 1 && wallet.Transfers[0].Applied);
        Assert.True(model.ShieldedTransfers[0].Applied && wallet.AvailableMutez == 600_000);
        model.Select(bob);
        await model.Refresh();
        var received = model.ShieldedWallet!.Notes[0];
        Assert.True(received.Cm == cms[0] && received.ValueMutez == "1000000" && received.AddressIndex == 1 && received.Memo == "for lunch" && model.ShieldedWallet.BalanceMutez == 1_000_000);
        Assert.Single(model.ShieldedWallet.Notes);   // the change and the producer note are not Bob's
        model.RemoveShieldedContact(contact.Fingerprint);
        Assert.Empty(model.ShieldedContacts());
    }

    [Fact]
    public void ForgesRecipientsAsOutboxMessagesNameThem()
    {
        Assert.Equal("00006b82198cb179e8306c1bedd08f12dc863f328886", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb").ForgedContract());
        var kt1 = new Address("KT1RCZzNe8G4VQ94YWqb6Xz4q9WGWVdMWSB4").ForgedContract();
        Assert.True(kt1?.StartsWith("01", StringComparison.Ordinal) == true && kt1.Length == 44);
        Assert.StartsWith("0002", new Address("tz3Rb5RVucLc6ani23jVdZM4Qm6c4oQ74zhh").ForgedContract());
        Assert.Null(new Address("bob").ForgedContract());
    }
}

/// <summary>The live Shadownet rollup and proving service answer as the client expects.</summary>
public class ShadownetShieldTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ReadsTheRollupAndTheProvingServiceRegistry()
    {
        var rollup = new RollupNodeService(ShieldedConfig.Shadownet);
        Assert.Equal(ShieldedConfig.Shadownet.RollupAddress, await rollup.SmartRollupAddress());
        var head = await rollup.Head();
        Assert.True(head.Level > 0);
        Assert.True(await rollup.VerifierConfigured(head.Hash));
        Assert.Equal(ShieldedConfig.Shadownet.Ticketer.Value, await rollup.BridgeTicketer(head.Hash));
        Assert.Equal(64, (await rollup.AuthDomain(head.Hash)).Length);
        Assert.True(await rollup.RequiredTxFee(head) >= RollupState.MinTxFee);
        Assert.Equal(64, (await rollup.ProgramHash("run_shield")).Length);
    }
}
