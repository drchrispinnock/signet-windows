using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// The 5 November 2026 audit's fixes (upstream <c>audit/AUDIT_20261105.md</c>): the console boundary
/// (K01), the shielded wallet file's cross-process lock and size bounds (G01, G05), untrusted
/// rollup numbers (G08), records that survive an unknown operator outcome (T01), and the
/// plain-HTTP note (G04).
/// </summary>
[Collection(ShieldedTestSupport.CpuHeavy)]
public class AuditFixTests
{
    private static readonly Address Account = new("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");

    // ---- K01: the console boundary ---------------------------------------------------------------

    [Fact]
    public void RedactionCoversUUIDSeedsAndByteDumps()
    {
        const string seed = "1786ff8a-2dd8-4554-8f99-6735d76e2170";
        Assert.Equal("Generating new ID: <uuid 1786ff8a…>", TaquitoBridge.RedactedLine($"Generating new ID: {seed}"));
        var asObject = "{" + string.Join(",", Enumerable.Range(0, 32).Select(i => $"\"{i}\":{i * 7 % 256}")) + "}";
        Assert.Equal("key <32 bytes> end", TaquitoBridge.RedactedLine($"key {asObject} end"));
        var array = "[" + string.Join(", ", Enumerable.Range(0, 32).Select(i => (i * 5 % 256).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        Assert.Equal("sk <32 bytes>", TaquitoBridge.RedactedLine($"sk {array}"));
        // Short runs and ordinary numbers are left alone.
        Assert.Equal("[1, 2, 3] {\"0\":1,\"1\":2}", TaquitoBridge.RedactedLine("[1, 2, 3] {\"0\":1,\"1\":2}"));
        Assert.Equal("fetch #3 ← 200 1234 bytes", TaquitoBridge.RedactedLine("fetch #3 ← 200 1234 bytes"));
    }

    [Fact]
    public async Task ConsoleObjectsAreScrubbedBeforeSerialisation()
    {
        var bridge = new TaquitoBridge();
        var bytes = Enumerable.Range(0, 32).Select(i => i * 3 % 256).ToArray();
        var asObject = new JsonObject();
        for (var i = 0; i < bytes.Length; i++) asObject[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = bytes[i];
        var json = new JsonObject
        {
            ["ok"] = 1, ["name"] = "transport",
            ["client"] = new JsonObject
            {
                ["keyPair"] = new JsonObject { ["publicKey"] = JsonSerializer.SerializeToNode(bytes), ["secretKey"] = JsonSerializer.SerializeToNode(bytes) },
                ["seed"] = "1786ff8a-2dd8-4554-8f99-6735d76e2170",
            },
            ["privateKey"] = "plain", ["bytes"] = JsonSerializer.SerializeToNode(bytes), ["asObject"] = asObject,
        }.ToJsonString();
        await bridge.Call("bridgeEchoObject", json);
        var line = Assert.Single(bridge.ConsoleLines, l => l.Contains("echo-object:", StringComparison.Ordinal));
        Assert.True(line.Contains("\"ok\":1", StringComparison.Ordinal) && line.Contains("\"name\":\"transport\"", StringComparison.Ordinal), line);
        Assert.True(line.Contains("\"keyPair\":\"<redacted>\"", StringComparison.Ordinal) && line.Contains("\"seed\":\"<redacted>\"", StringComparison.Ordinal)
                    && line.Contains("\"privateKey\":\"<redacted>\"", StringComparison.Ordinal), line);
        Assert.True(line.Contains("\"bytes\":\"<32 bytes>\"", StringComparison.Ordinal) && line.Contains("\"typed\":\"<32 bytes>\"", StringComparison.Ordinal)
                    && line.Contains("\"asObject\":\"<32 bytes>\"", StringComparison.Ordinal), line);
        Assert.False(line.Contains("93", StringComparison.Ordinal) || line.Contains("1786ff8a-2dd8", StringComparison.Ordinal), line);
    }

    // ---- G01, G05: the shielded wallet file --------------------------------------------------------

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "signet-audit-" + Guid.NewGuid().ToString("N"));

    private static void Remove(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ShieldedWalletFileIsNeverWrittenLargerThanItIsRead()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new FileShieldedWalletStore(directory, "test") { MaxBytes = 4096 };
            var wallet = ShieldedWallet.Make("test");
            store.Save(wallet, Account);
            var path = Path.Combine(directory, FileShieldedWalletStore.WalletFile("test"));
            var before = PrivateFile.Read(path);
            Assert.NotNull(before);
            wallet.LeafCms = Enumerable.Range(0, 200).Select(_ => string.Concat(Enumerable.Repeat("ab", 32))).ToList();
            Assert.ThrowsAny<ShieldedError>(() => store.Save(wallet, Account));
            Assert.Equal(before, PrivateFile.Read(path));
            Assert.True(store.Load(Account)?.LeafCms.Count == 0);
            // A file beyond the bound on disk is refused on reading rather than parsed.
            PrivateFile.Write(path, Enumerable.Repeat((byte)0x20, 5000).ToArray());
            Assert.ThrowsAny<Exception>(() => store.Load(Account));
        }
        finally { Remove(directory); }
    }

    /// <summary>
    /// Upstream holds the <c>lockf</c> lock from a Python process. Windows' lock is an open with no
    /// sharing, which no other open gets past, this process's included, so the test holds it itself.
    /// </summary>
    [Fact]
    public void ShieldedWalletFileIsLockedAgainstOtherProcesses()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new FileShieldedWalletStore(directory, "test") { LockTimeout = TimeSpan.FromMilliseconds(300) };
            store.Save(ShieldedWallet.Make("test"), Account);
            var lockPath = Path.Combine(directory, FileShieldedWalletStore.LockFile("test"));
            Assert.True(File.Exists(lockPath));
            // Another holder takes the same lock for a while.
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<ShieldedError.WalletLocked>(() => store.Update(Account, null, w => w.DepositNonce += 1));
            var updated = store.Update(Account, null, w => w.DepositNonce += 1);
            Assert.Equal(1ul, updated?.DepositNonce);
        }
        finally { Remove(directory); }
    }

    [Fact]
    public async Task ShieldedWalletFileLockIsSharedAcrossPathAliases()
    {
        // A real parent folder and a link to it both lead to the same wallet file; two stores, one
        // through each, must take the same lock (the lock file alone would make one of them fail).
        var parent = TemporaryDirectory();
        try
        {
            var real = Path.Combine(parent, "real");
            Directory.CreateDirectory(Path.Combine(real, "wallet"));
            var alias = Path.Combine(parent, "alias");
            if (!MakeLink(alias, real)) Assert.Skip("This account can make neither a symbolic link nor a junction.");
            var a = new FileShieldedWalletStore(Path.Combine(real, "wallet"), "test");
            var b = new FileShieldedWalletStore(Path.Combine(alias, "wallet"), "test");
            a.Save(ShieldedWallet.Make("test"), Account);
            await Task.WhenAll(new[] { a, b }.Select(store => Task.Run(() =>
            {
                for (var i = 0; i < 40; i++) store.Update(Account, null, w => w.DepositNonce += 1);
            }, TestContext.Current.CancellationToken)));
            Assert.True(a.Load(Account)?.DepositNonce == 80 && b.Load(Account)?.DepositNonce == 80);
        }
        finally
        {
            try { Directory.Delete(Path.Combine(parent, "alias")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Remove(parent);
        }

        static bool MakeLink(string link, string target)
        {
            try { Directory.CreateSymbolicLink(link, target); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // A junction needs no privilege.
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            return mklink.ExitCode == 0 && Directory.Exists(link);
        }
    }

    [Fact]
    public void BackupsReadEveryWalletFileTheStoreAccepts()
    {
        var root = TemporaryDirectory();
        try
        {
            var wallet = Path.Combine(root, "wallet");
            var backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(wallet);
            PrivateFile.Write(Path.Combine(wallet, TezosClientStore.PublicKeyHashesFile), "[]"u8.ToArray());
            // A shielded wallet file larger than the ordinary 32 MB bound, as a long scan leaves it.
            var name = FileShieldedWalletStore.WalletFile(Network.Shadownet.Chain);
            var big = new MemoryStream();
            big.Write("{\"version\":2,\"chain\":\"x\",\"pad\":\""u8);
            big.Write(Enumerable.Repeat((byte)0x61, 40 << 20).ToArray());
            big.Write("\"}"u8);
            PrivateFile.Write(Path.Combine(wallet, name), big.ToArray());
            Assert.True(PrivateFile.ReadLimit(name) == PrivateFile.LargeStateBytes && PrivateFile.ReadLimit("secret_keys") == PrivateFile.MaxBytes);
            Assert.ThrowsAny<Exception>(() => PrivateFile.Read(Path.Combine(wallet, name)));
            var outcome = new WalletBackup(wallet, backups, 2).Run();
            var generation = Assert.IsType<WalletBackup.Outcome.Created>(outcome).Generation;
            Assert.Equal(big.ToArray(), PrivateFile.Read(Path.Combine(generation, name), PrivateFile.LargeStateBytes));
            Assert.Equal(WalletBackup.Fingerprint(wallet), WalletBackup.Fingerprint(generation));
        }
        finally { Remove(root); }
    }

    // ---- G08: untrusted rollup numbers -------------------------------------------------------------

    /// <summary>Upstream's levels are 64-bit; here a level is an <c>int</c>, so the edges are int's.</summary>
    [Fact]
    public async Task RollupLevelsOutOfRangeAreErrorsNotTraps()
    {
        Assert.True(RollupState.Level(-1) is null && RollupState.Level((long)int.MaxValue + 1) is null && RollupState.Level(5) == 5);
        var rollup = new MockShieldedRollup();
        foreach (var level in new[] { int.MaxValue, -1, int.MinValue })
            await Assert.ThrowsAnyAsync<ShieldedError>(() => rollup.RequiredTxFee(new RollupHead("x", level)));
        rollup.Level = 100;
        Assert.Equal(RollupState.RequiredTxFee(0), await rollup.RequiredTxFee(await rollup.Head()));
    }

    // ---- T01: shielded records before and after the operator ----------------------------------------

    [Fact]
    public void OlderRecordsReadAsAcknowledgedAndNewOnesKeepTheirPayload()
    {
        const string old = """
            {"submissionID":"sub-1","recipient":"tz1a","amountMutez":"10","rollupFeeMutez":"1","producerFeeMutez":"1","nullifiers":["n1"],"changeCm":null,"submittedLevel":7,"created":"2026-11-01T00:00:00Z","operationHash":null,"applied":false,"outboxLevel":null,"messageIndex":null,"executedHash":null}
            """;
        var withdrawal = JsonSerializer.Deserialize<ShieldedWithdrawal>(old, ShieldedJSON.Options)!;
        Assert.True(withdrawal.Submitted && withdrawal.PayloadHex is null && withdrawal.Id == "sub-1");
        const string oldTransfer = """
            {"submissionID":"sub-2","recipientFingerprint":"fp","recipientName":null,"amountMutez":"10","rollupFeeMutez":"1","producerFeeMutez":"1","memo":"","nullifiers":["n2"],"recipientCm":"c1","changeCm":"c2","created":"2026-11-01T00:00:00Z","operationHash":null,"applied":false}
            """;
        var transfer = JsonSerializer.Deserialize<ShieldedTransfer>(oldTransfer, ShieldedJSON.Options)!;
        Assert.True(transfer.Submitted && transfer.PayloadHex is null);
        var fresh = new ShieldedWithdrawal
        {
            SubmissionID = ShieldedWithdrawal.LocalID(), Recipient = "tz1a", AmountMutez = "10", RollupFeeMutez = "1", ProducerFeeMutez = "1",
            Nullifiers = ["n1"], SubmittedLevel = 7, Created = DateTimeOffset.UtcNow, PayloadHex = "0102", Submitted = false,
        };
        Assert.StartsWith("local:", fresh.Id, StringComparison.Ordinal);
        var round = ShieldedJSON.Clone(fresh);
        Assert.True(round.PayloadHex == "0102" && !round.Submitted && round.Id == fresh.Id);
        fresh.SubmissionID = "sub-3";
        fresh.Submitted = true;
        Assert.Equal("sub-3", fresh.Id);
        // An unacknowledged withdrawal reserves its notes and says so.
        var wallet = ShieldedWallet.Make("test");
        wallet.Notes = [new ShieldedNote { Index = 0, Cm = "c", Rseed = "r", Nullifier = "n1", ValueMutez = "10", AddressIndex = 0 }];
        wallet.Withdrawals = [round];
        Assert.True(wallet.AvailableNotes.Count == 0 && wallet.ReservedMutez == 10);
        Assert.IsType<ShieldedWithdrawalStatus.WithdrawalState.Unconfirmed>(new ShieldedWithdrawalStatus(round, null).State);
    }

    [Fact]
    public async Task SubmittingKeepsTheRecordUnlessTheOperatorRefusedIt()
    {
        var dropped = 0;
        var submission = new OperatorSubmission("s1", "queued");
        var ok = await WalletViewModel.SubmitShielded("local:abc", () => dropped++, () => Task.FromResult(submission));
        Assert.True(ok.Id == "s1" && dropped == 0);
        // Refusals the operator made itself: the record goes.
        foreach (ShieldedError refusal in new ShieldedError[] { new ShieldedError.OperatorRefused("token"), new ShieldedError.Rejected(400, "bad payload") })
            await Assert.ThrowsAnyAsync<ShieldedError>(() => WalletViewModel.SubmitShielded("local:abc", () => dropped++, () => Task.FromException<OperatorSubmission>(refusal)));
        Assert.Equal(2, dropped);
        // A lost reply, a timeout or a bad answer: the record stays and the error says so.
        foreach (var unknown in new Exception[] { new TimeoutException("timed out"), new ShieldedError.BadReply("HTTP 502"), new OperationCanceledException() })
        {
            var error = await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() =>
                WalletViewModel.SubmitShielded("local:abcdef0123", () => dropped++, () => Task.FromException<OperatorSubmission>(unknown)));
            Assert.True(error.Message.Contains("abcdef01…", StringComparison.Ordinal) && error.Message.Contains("reserved", StringComparison.Ordinal), error.Message);
        }
        Assert.Equal(2, dropped);
    }

    /// <summary>A wallet with two unacknowledged records, a withdrawal spending n1 and a transfer spending n2.</summary>
    private static (WalletViewModel Model, InMemoryShieldedWalletStore Store, MockShieldedRollup Rollup, string WithdrawalID, string TransferID) Unacknowledged()
    {
        var store = new InMemoryShieldedWalletStore();
        var rollup = new MockShieldedRollup();
        var model = ShieldFlowTests.MakeModel(store, rollup);
        model.SetShieldedOperatorToken("operator-secret", Network.Shadownet);
        var me = WalletViewModel.SampleWallets[0].Address;
        var wallet = ShieldedWallet.Make(Network.Shadownet.Chain);
        wallet.Notes =
        [
            new ShieldedNote { Index = 0, Cm = "c", Rseed = "r", Nullifier = "n1", ValueMutez = "10", AddressIndex = 0 },
            new ShieldedNote { Index = 1, Cm = "d", Rseed = "r", Nullifier = "n2", ValueMutez = "10", AddressIndex = 0 },
        ];
        var withdrawalID = ShieldedWithdrawal.LocalID();
        var transferID = ShieldedWithdrawal.LocalID();
        wallet.Withdrawals =
        [
            new ShieldedWithdrawal
            {
                SubmissionID = withdrawalID, Recipient = me.Value, AmountMutez = "5", RollupFeeMutez = "1", ProducerFeeMutez = "1", Nullifiers = ["n1"],
                SubmittedLevel = 7, Created = DateTimeOffset.UtcNow, PayloadHex = "aa01", Submitted = false,
            },
        ];
        wallet.Transfers =
        [
            new ShieldedTransfer
            {
                SubmissionID = transferID, RecipientFingerprint = "fp", AmountMutez = "5", RollupFeeMutez = "1", ProducerFeeMutez = "1", Memo = "",
                Nullifiers = ["n2"], RecipientCm = "c1", ChangeCm = "c2", Created = DateTimeOffset.UtcNow, PayloadHex = "bb02", Submitted = false,
            },
        ];
        store.Save(wallet, me);
        return (model, store, rollup, withdrawalID, transferID);
    }

    [Fact]
    public async Task UnacknowledgedRecordsCanBeHandedToTheOperatorAgain()
    {
        var (model, store, rollup, withdrawalID, transferID) = Unacknowledged();
        var me = WalletViewModel.SampleWallets[0].Address;
        Assert.Empty(store.Load(me)!.AvailableNotes);
        // The kept payloads go out as they were; the records take the operator's ids.
        await model.ResubmitWithdrawal(withdrawalID);
        await model.ResubmitTransfer(transferID);
        var after = store.Load(me)!;
        Assert.Equal([("unshield", "aa01"), ("transfer", "bb02")], rollup.Submitted.Select(s => (s.Kind, s.Payload)));
        Assert.True(after.Withdrawals.Select(w => w.Id).SequenceEqual(["sub-1"]) && after.Withdrawals[0].Submitted);
        Assert.True(after.Transfers.Select(t => t.Id).SequenceEqual(["sub-2"]) && after.Transfers[0].Submitted);
        Assert.Empty(after.AvailableNotes);   // still reserved until the rollup applies them
        // Acknowledged records are not sent twice.
        await Assert.ThrowsAnyAsync<ShieldedError>(() => model.ResubmitWithdrawal("sub-1"));
    }

    [Fact]
    public async Task RefusedRetriesPreserveEarlierUncertainSubmissions()
    {
        var (model, store, rollup, withdrawalID, transferID) = Unacknowledged();
        var me = WalletViewModel.SampleWallets[0].Address;

        // Both first attempts are accepted by the operator, but their replies are lost.
        foreach (var (kind, payload, id) in new[] { ("unshield", "aa01", withdrawalID), ("transfer", "bb02", transferID) })
        {
            await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() => WalletViewModel.SubmitShielded(id,
                () => store.Update(me, null, w => { w.Withdrawals.RemoveAll(x => x.Id == id); w.Transfers.RemoveAll(x => x.Id == id); }),
                async () =>
                {
                    await rollup.Submit(kind, payload, "synthetic-rollup", "operator-secret");
                    throw new TimeoutException("timed out");
                }));
        }
        Assert.Equal(2, rollup.Submitted.Count);

        // A new authentication failure cannot resolve either earlier attempt.
        model.SetShieldedOperatorToken("wrong", Network.Shadownet);
        foreach (var id in new[] { withdrawalID, transferID })
        {
            var error = await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() => id == withdrawalID ? model.ResubmitWithdrawal(id) : model.ResubmitTransfer(id));
            Assert.Contains("reserved", error.Message, StringComparison.Ordinal);
        }
        var pending = store.Load(me)!;
        Assert.True(pending.Withdrawals.Select(w => w.Id).SequenceEqual([withdrawalID]) && pending.Withdrawals[0].PayloadHex == "aa01" && !pending.Withdrawals[0].Submitted);
        Assert.True(pending.Transfers.Select(t => t.Id).SequenceEqual([transferID]) && pending.Transfers[0].PayloadHex == "bb02" && !pending.Transfers[0].Submitted);
        Assert.True(pending.ReservedNullifiers.SetEquals(["n1", "n2"]) && pending.AvailableNotes.Count == 0 && pending.ReservedMutez == 20);
        Assert.Equal(2, rollup.Submitted.Count);

        // Correcting the token still permits replay of the same payloads, keeping reservations.
        model.SetShieldedOperatorToken("operator-secret", Network.Shadownet);
        await model.ResubmitWithdrawal(withdrawalID);
        await model.ResubmitTransfer(transferID);
        var acknowledged = store.Load(me)!;
        Assert.True(acknowledged.Withdrawals.Select(w => w.Id).SequenceEqual(["sub-3"]) && acknowledged.Withdrawals[0].Submitted);
        Assert.True(acknowledged.Transfers.Select(t => t.Id).SequenceEqual(["sub-4"]) && acknowledged.Transfers[0].Submitted);
        Assert.Empty(acknowledged.AvailableNotes);
        Assert.Equal([("unshield", "aa01"), ("transfer", "bb02"), ("unshield", "aa01"), ("transfer", "bb02")], rollup.Submitted.Select(s => (s.Kind, s.Payload)));
    }

    [Fact]
    public async Task RetryRefusalsRemainUncertain()
    {
        foreach (ShieldedError refusal in new ShieldedError[]
                 {
                     new ShieldedError.OperatorRefused("expired token"), new ShieldedError.Rejected(400, "bad request"),
                     new ShieldedError.Rejected(403, "forbidden"), new ShieldedError.Rejected(422, "invalid payload"),
                 })
        {
            var error = await Assert.ThrowsAsync<ShieldedError.SubmissionPending>(() =>
                WalletViewModel.SubmitShielded("local:abcdef0123", () => Task.FromException<OperatorSubmission>(refusal)));
            Assert.True(error.Message.Contains("abcdef01…", StringComparison.Ordinal) && error.Message.Contains("reserved", StringComparison.Ordinal), error.Message);
        }
    }

    [Fact]
    public void OnlyDefiniteClientErrorsCountAsRejections()
    {
        foreach (var status in new[] { 400, 401, 403, 404, 413, 422 }) Assert.True(RollupNodeService.IsRejection(status), $"{status}");
        foreach (var status in new[] { 408, 409, 425, 429, 500, 502, 503, 504, 200 }) Assert.False(RollupNodeService.IsRejection(status), $"{status}");
    }

    // ---- G04 ----------------------------------------------------------------------------------------

    [Fact]
    public void PlainHTTPServicesAreNamed()
    {
        Assert.Null(ShieldSettingsNotes.PlainHTTP("https://operator.example", "https://rollup.example"));
        Assert.StartsWith("The operator is reached over plain HTTP", ShieldSettingsNotes.PlainHTTP("http://operator.example:8787", "https://rollup.example"), StringComparison.Ordinal);
        Assert.StartsWith("The operator and rollup node are reached", ShieldSettingsNotes.PlainHTTP("HTTP://operator.example", "http://rollup.example"), StringComparison.Ordinal);
        Assert.Null(ShieldSettingsNotes.PlainHTTP("", ""));
    }
}
