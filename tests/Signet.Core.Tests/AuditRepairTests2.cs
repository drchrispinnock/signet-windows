using System.Net;
using System.Text;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

/// <summary>
/// A server for <c>BoundedHTTPTests</c>, by path (upstream's <c>BoundedHTTPProtocol</c>). "Chunked"
/// replies carry no declared length; <c>/pending-*</c> replies never finish, and record when the
/// reader stops consuming them.
/// </summary>
internal sealed class BoundedHTTPHandler : HttpMessageHandler
{
    private static readonly HashSet<string> Stops = [];

    public static bool WasStopped(Uri url) { lock (Stops) return Stops.Contains(url.AbsoluteUri); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        HttpResponseMessage Respond(byte[] data, bool declared, HttpStatusCode status = HttpStatusCode.OK)
        {
            HttpContent content = declared ? new ByteArrayContent(data) : new StreamContent(new UndeclaredStream(new MemoryStream(data)));
            return new HttpResponseMessage(status) { Content = content };
        }
        var path = url.AbsolutePath;
        // A service under test appends its own path (TzKT's /v1/…), so the first segment picks the reply.
        var first = "/" + path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var response = first switch
        {
            "/pending-large" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream(url, 64 << 10, declared: 1 << 20)) },
            "/pending-chunked" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream(url, 64 << 10, declared: null)) },
            "/declared-large" => Respond(new byte[1 << 20].Select(_ => (byte)0x20).ToArray(), declared: true),
            "/chunked-large" => Respond(new byte[1 << 20].Select(_ => (byte)0x20).ToArray(), declared: false),
            "/deep" => Respond(Encoding.UTF8.GetBytes(new string('[', 500) + new string(']', 500)), declared: true),
            "/small" => Respond("\"NetXdQprcVkpaWU\""u8.ToArray(), declared: true),
            "/rows" => Respond(Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat(
                """{"type":"transaction","hash":"oo1","sender":{"address":"tz1a"},"target":{"address":"tz1b"},"amount":1,"timestamp":"2026-01-01T00:00:00Z","status":"applied"}""", 40)) + "]"), declared: true),
            _ => Respond("{}"u8.ToArray(), declared: true, HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    /// <summary>A body whose length is not known up front.</summary>
    private sealed class UndeclaredStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends <c>first</c> bytes, then waits forever; disposing it is the reader stopping.</summary>
    private sealed class PendingStream(Uri url, int first, long? declared) : Stream
    {
        private int sent;
        public override bool CanRead => true;
        public override bool CanSeek => declared is not null;
        public override bool CanWrite => false;
        public override long Length => declared ?? throw new NotSupportedException();
        public override long Position { get => sent; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (sent >= first) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var n = Math.Min(count, first - sent);
            Array.Fill(buffer, (byte)0x20, offset, n);
            sent += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent >= first) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var n = Math.Min(buffer.Length, first - sent);
            buffer.Span[..n].Fill(0x20);
            sent += n;
            return n;
        }
        protected override void Dispose(bool disposing)
        {
            lock (Stops) Stops.Add(url.AbsoluteUri);
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>S19: native readers take replies as a stream, capped, and JSON within a budget.</summary>
public class BoundedHTTPTests
{
    private static readonly HttpClient Session = new(new BoundedHTTPHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private static Uri Request(string path) => new("https://bounded.invalid" + path);

    [Fact]
    public async Task DeclaredAndUndeclaredOversizeRepliesAreRefused()
    {
        Assert.Equal(4096, (await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, Request("/declared-large"), limit: 4096, cancellation: TestContext.Current.CancellationToken))).Limit);
        Assert.Equal(4096, (await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, Request("/chunked-large"), limit: 4096, cancellation: TestContext.Current.CancellationToken))).Limit);
    }

    [Fact]
    public async Task ExcessiveNestingIsRefusedBeforeParsing()
    {
        await Assert.ThrowsAsync<BoundedHTTP.Failure.OverBudget>(() => Http.Get(Session, Request("/deep"), limit: 4096, json: BoundedHTTP.JSONLimits.Small, cancellation: TestContext.Current.CancellationToken));
        var raw = await Http.Get(Session, Request("/deep"), limit: 4096, json: BoundedHTTP.JSONLimits.Unchecked, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(1000, raw.Body.Length);
    }

    [Fact]
    public async Task FittingRepliesPassWithTheirStatus()
    {
        var reply = await Http.Get(Session, Request("/small"), limit: 4096, cancellation: TestContext.Current.CancellationToken);
        Assert.True(reply.IsSuccess && reply.Text == "\"NetXdQprcVkpaWU\"");
        var missing = await Http.Get(Session, Request("/nothing"), limit: 4096, cancellation: TestContext.Current.CancellationToken);
        Assert.True(missing.Status == 404 && !missing.IsSuccess);
    }

    [Fact]
    public async Task RefusalCancelsTheUnderlyingUnfinishedResponse()
    {
        foreach (var path in new[] { "/pending-large", "/pending-chunked" })
        {
            var url = Request(path + "?id=" + Guid.NewGuid());
            await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() => Http.Get(Session, url, TimeSpan.FromSeconds(3), limit: 4096, cancellation: TestContext.Current.CancellationToken));
            for (var i = 0; i < 50 && !BoundedHTTPHandler.WasStopped(url); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.True(BoundedHTTPHandler.WasStopped(url), "An over-budget response must stop even before the server finishes");
        }
    }

    [Fact]
    public async Task NativeReadersUseTheBoundedPath()
    {
        // TzKT: an account record over its cap fails before anything is parsed.
        var error = await Assert.ThrowsAsync<BoundedHTTP.Failure.TooLarge>(() =>
            new TzKTService(new Uri("https://bounded.invalid/declared-large"), Session).AccountProfileFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
        Assert.Equal(TzKTService.RecordBytes, error.Limit);
        // The node monitor's probe refuses a block header that is anything but small (the probe
        // is built on this test's session, so the canned reply is what it reads).
        await Assert.ThrowsAnyAsync<BoundedHTTP.Failure>(() => NodeMonitor.ProbeOver(Session)(Request("/declared-large")));
        // Rows beyond the requested page size are dropped even when the server sends more.
        var rows = await new TzKTService(new Uri("https://bounded.invalid/rows"), Session).RecentOperationsFor(new Address("tz1a"), 10);
        Assert.Equal(10, rows.Count);
    }
}

/// <summary>S11: every line the bridge records is redacted and capped, wherever it goes.</summary>
public class LoggingPolicyTests
{
    private const string Key = "edskRuR3azSfg5xTz6dLovR7yr7hgP9Qt6Ub8rgqJG7VTH6RhJ1vXXhfSwtxw1E2Lc3b3TvsgrnWXKVyLRQgm3Rx8cFXqQhd3";

    [Fact]
    public async Task ConsoleLinesFromJavaScriptAreRedactedAndCapped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Own the bridge and log: parallel echo tests must not replace this test's message.
            var bridge = new TaquitoBridge(logPath: Path.Combine(directory, "bridge.log"), systemLog: _ => { });
            var hex = string.Concat(Enumerable.Repeat("ab", 64));
            var filler = new string('x', TaquitoBridge.MaxLineLength + 200);
            _ = await bridge.Call("bridgeEcho", $"{Key} {hex} {filler}");
            var echoed = Assert.Single(bridge.ConsoleLines, l => l.StartsWith("[log] echo: ", StringComparison.Ordinal));
            Assert.True(!echoed.Contains(Key) && !echoed.Contains(hex));
            Assert.True(echoed.Contains("edsk<redacted>") && echoed.Contains("<128 hex chars>"));
            Assert.True(echoed.Length <= TaquitoBridge.MaxLineLength + 1);
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void BridgeNotesGoThroughTheSamePolicy()
    {
        var bridge = new TaquitoBridge(systemLog: _ => { });
        bridge.Note($"uncaught: {Key}");
        Assert.Equal("[app] uncaught: edsk<redacted>", bridge.ConsoleLines.LastOrDefault());
    }

    [Fact]
    public async Task SystemConsoleAndFileReceiveTheSameRedactedText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(directory, "bridge.log");
            var sink = new List<string>();
            var bridge = new TaquitoBridge(logPath: file, systemLog: line => { lock (sink) sink.Add(line); });
            var input = "edsk" + new string('R', 50) + " " + string.Concat(Enumerable.Repeat("ab", 64)) + " " + new string('x', 1200);
            _ = await bridge.Call("bridgeEcho", input);
            var console = bridge.ConsoleLines;
            lock (sink) Assert.Equal(console, sink);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = new StreamReader(stream).ReadToEnd();
            foreach (var line in console)
            {
                Assert.DoesNotContain(new string('R', 50), line);
                Assert.DoesNotContain(string.Concat(Enumerable.Repeat("ab", 64)), line);
                Assert.True(line.Length <= TaquitoBridge.MaxLineLength + 1);
                Assert.Contains(line, text);
            }
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// Audit K01: the two forms the Octez Connect SDK logs communication keys in. A replacement
    /// key's seed is a UUID (E01), and a transport object carries its key pair as typed arrays,
    /// which <c>JSON.stringify</c> writes as decimal bytes (E02). Neither reaches any sink.
    /// </summary>
    [Fact]
    public async Task SDKSeedsAndKeyBytesAreKeptOutOfEverySink()
    {
        const string seed = "3f2b8c1e-9d4a-4e7b-b6c5-0a1d2e3f4a5b";
        Assert.Equal("seed <uuid 3f2b8c1e…> stored", TaquitoBridge.RedactedLine($"seed {seed} stored"));
        var bytes = string.Join(",", Enumerable.Range(200, 32));
        var asObject = "{" + string.Join(",", Enumerable.Range(0, 32).Select(i => $"\"{i}\":{200 + i % 50}")) + "}";
        Assert.Equal("key <32 bytes>", TaquitoBridge.RedactedLine($"key [{bytes}]"));
        Assert.Equal("{\"secretKey\":<32 bytes>}", TaquitoBridge.RedactedLine("{\"secretKey\":" + asObject + "}"));
        // Short arrays (amounts, counters) are left alone.
        Assert.Equal("[1,2,3]", TaquitoBridge.RedactedLine("[1,2,3]"));

        var sink = new List<string>();
        var bridge = new TaquitoBridge(systemLog: line => { lock (sink) sink.Add(line); });
        await bridge.ExecuteForTesting($$"""
            var secret = new Uint8Array(64); for (var i = 0; i < 64; i++) secret[i] = 100 + i;
            console.warn("new seed", "{{seed}}");
            console.log("transport", { client: { keyPair: { publicKey: "edpkPublic", secretKey: secret } }, listeners: 2 });
            console.log("bytes", { data: secret, privateKey: "plain-private-text" });
            """);
        var lines = bridge.ConsoleLines;
        lock (sink) Assert.Equal(lines, sink);
        var joined = string.Join("\n", lines);
        Assert.DoesNotContain("3f2b8c1e-9d4a", joined);
        Assert.DoesNotContain("\"0\":100", joined);
        Assert.DoesNotContain("plain-private-text", joined);
        Assert.Contains(lines, l => l.Contains("transport", StringComparison.Ordinal) && l.Contains("\"listeners\":2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"data\":\"<64 bytes>\"", StringComparison.Ordinal));
    }

    /// <summary>Audit K02: the crash log goes through the same redaction, nested exceptions included, and rotates.</summary>
    [Fact]
    public void CrashLogIsRedactedAndBounded()
    {
        var inner = new InvalidOperationException($"bad key {Key}");
        var text = CrashLog.Format(new Exception("outer 3f2b8c1e-9d4a-4e7b-b6c5-0a1d2e3f4a5b", inner), DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("3f2b8c1e-9d4a", text);
        Assert.Contains("edsk<redacted>", text);
        Assert.Contains("InvalidOperationException", text);

        var directory = Path.Combine(Path.GetTempPath(), "signet-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(directory, "crash.log");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(file, new byte[CrashLog.MaxBytes + 1]);
            CrashLog.Append(inner, file);
            Assert.Equal(CrashLog.MaxBytes + 1, new FileInfo(file + ".1").Length);
            Assert.Contains("edsk<redacted>", File.ReadAllText(file));
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    /// <summary>Windows: the rotation itself (upstream's is checked by hand), with a full log planted first.</summary>
    [Fact]
    public void AFullLogIsRotatedToDotOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), "signet-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "bridge.log");
            File.WriteAllBytes(file, new byte[TaquitoBridge.MaxLogBytes + 1]);
            var bridge = new TaquitoBridge(logPath: file, systemLog: _ => { });
            bridge.Note("after rotation");
            Assert.Equal(TaquitoBridge.MaxLogBytes + 1, new FileInfo(file + ".1").Length);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.EndsWith("[app] after rotation\n", new StreamReader(stream).ReadToEnd());
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }
}

/// <summary>S30: every export that signs is declared as such (the JavaScript inventory is upstream's npm test).</summary>
public class SigningExportsTests
{
    [Fact]
    public async Task TheSigningEntryPointRefusesReads()
    {
        await Assert.ThrowsAsync<TaquitoBridge.BridgeError.NotASigningCall>(() => TaquitoBridge.Shared.Sign("version"));
        Assert.NotNull((await TaquitoBridge.Shared.Call("version")).StringValue);
    }
}

/// <summary>F02: every bridge failure goes through one conversion to <c>ChainError</c>.</summary>
public class BridgeErrorConversionTests
{
    [Fact]
    public async Task KnownMessagesBecomeChainErrorsAndEverythingElsePassesThrough()
    {
        (string Message, Type Expected)[] cases =
        [
            ("Failed to decrypt the key", typeof(ChainError.WrongPassphrase)), ("Invalid passphrase", typeof(ChainError.WrongPassphrase)),
            ("Ledger device: Condition of use not satisfied (denied by the user?) (0x6985)", typeof(ChainError.LedgerDeclined)),
            ("Ledger Device is busy (0x5515 LOCKED_DEVICE)", typeof(ChainError.LedgerLocked)),
            ("CLA_NOT_SUPPORTED (0x6e00)", typeof(ChainError.LedgerAppNotOpen)), ("INS_NOT_SUPPORTED", typeof(ChainError.LedgerAppNotOpen)),
            ("No Ledger is connected", typeof(ChainError.LedgerNotConnected)), ("the device was unplugged", typeof(ChainError.LedgerNotConnected)),
            ("Ledger does not hold this key", typeof(ChainError.LedgerWrongDevice)),
            ("the key does not derive the account's address tz1x", typeof(ChainError.KeyAddressMismatch)),
        ];
        foreach (var (message, expected) in cases)
        {
            Assert.IsType(expected, new TaquitoBridge.BridgeError.JavaScript(message).AsChainError);
            var thrown = await Assert.ThrowsAnyAsync<ChainError>(() => TaquitoBridge.BridgeError.Typed<int>(() => throw new TaquitoBridge.BridgeError.JavaScript(message)));
            Assert.IsType(expected, thrown);
        }
        Assert.Equal("Ledger does not hold this key", new TaquitoBridge.BridgeError.JavaScript("Ledger does not hold this key").AsChainError.Message);
        // Precedence is the message scan's: a decrypt failure wins over a Ledger code in the same text.
        Assert.IsType<ChainError.WrongPassphrase>(new TaquitoBridge.BridgeError.JavaScript("could not decrypt (0x6985)").AsChainError);
        // Unknown JavaScript text and every other bridge failure come back exactly as they were.
        TaquitoBridge.BridgeError[] others =
        [
            new TaquitoBridge.BridgeError.JavaScript("something else"), new TaquitoBridge.BridgeError.TimedOut("getHead", TimeSpan.FromSeconds(60)),
            new TaquitoBridge.BridgeError.NotAFunction("x"), new TaquitoBridge.BridgeError.BundleMissing(), new TaquitoBridge.BridgeError.LoadFailed("y"),
            new TaquitoBridge.BridgeError.NotASigningCall("z"),
        ];
        foreach (var error in others) Assert.Same(error, error.AsChainError);
        // Other errors never enter the conversion, and a success comes back untouched.
        await Assert.ThrowsAsync<ChainError.AccountNotOnChain>(() => TaquitoBridge.BridgeError.Typed<int>(() => throw new ChainError.AccountNotOnChain(new Address("tz1"))));
        Assert.Equal(7, await TaquitoBridge.BridgeError.Typed(() => Task.FromResult(7)));
    }
}

/// <summary>A key importer whose password check runs a caller's code first, so a test can change the model while Forget waits.</summary>
internal sealed class InterferingImporter(Func<Task> during) : IKeyImporter
{
    private readonly MockKeyImporter fallback = new();
    public Task<InspectedSecretKey> Inspect(string secretKey, string? passphrase) => fallback.Inspect(secretKey, passphrase);
    public Task<MnemonicCheck> Check(string mnemonic) => fallback.Check(mnemonic);
    public Task<KeyMaterial> KeyFromMnemonic(string mnemonic, string? passphrase, string derivationPath, MnemonicCurve curve) => fallback.KeyFromMnemonic(mnemonic, passphrase, derivationPath, curve);
    public Task<KeyMaterial> KeyFromFundraiser(string email, string password, string mnemonic) => fallback.KeyFromFundraiser(email, password, mnemonic);
    public async Task<string> Decrypt(string secretKey, string passphrase)
    {
        await during();
        return await fallback.Decrypt(secretKey, passphrase);
    }
}

/// <summary>S07: a session-only Forget removes only the entry it verified.</summary>
public class SessionKeyRemovalTests
{
    internal static readonly KeyMaterial Paper = new(AddressScheme.Tz1, "edpkuPAPER", "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", "edeskPAPER");
    private static readonly KeyMaterial Other = new(AddressScheme.Tz1, "edpkuOTHER", "tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N", "edeskOTHER");

    [Fact]
    public async Task ForgetRefusesAnAliasReusedDuringThePasswordCheck()
    {
        WalletViewModel? model = null;
        var importer = new InterferingImporter(async () =>
        {
            if (model is null) return;
            // While the password is being checked: the account is renamed and another one takes its alias.
            model.RenameSelectedWallet("Saved");
            await model.ImportAccount("Paper", Other, alreadyEncrypted: true, keepForSession: true);
        });
        model = new WalletViewModel(chain: new MockChainService(), keyImporter: importer);
        await model.ImportAccount("Paper", Paper, alreadyEncrypted: true, keepForSession: true);
        var original = model.SelectedWallet!;

        await Assert.ThrowsAnyAsync<SessionKeyStore.StoreError>(() => model.ForgetSelectedWallet("correct horse"));

        // Nothing was removed: the original lives on as "Saved", the replacement "Paper" is intact.
        Assert.Equal(new HashSet<string> { "Saved", "Paper" }, model.Wallets.Select(w => w.Alias).ToHashSet());
        var saved = model.Wallets.First(w => w.Alias == "Saved");
        var replacement = model.Wallets.First(w => w.Alias == "Paper");
        Assert.True(saved.Address == original.Address && replacement.Address.Value == Other.Address);
        Assert.Equal(Other.SecretKey, Assert.IsType<SigningKey.Secret>(model.SigningKeyFor(replacement, "correct horse")).Key);
        Assert.Equal(Paper.SecretKey, Assert.IsType<SigningKey.Secret>(model.SigningKeyFor(saved, "correct horse")).Key);
    }

    [Fact]
    public async Task ForgetRefusesARenameDuringThePasswordCheck()
    {
        WalletViewModel? model = null;
        var importer = new InterferingImporter(() => { model?.RenameSelectedWallet("Moved"); return Task.CompletedTask; });
        model = new WalletViewModel(chain: new MockChainService(), keyImporter: importer);
        await model.ImportAccount("Paper", Paper, alreadyEncrypted: true, keepForSession: true);
        await Assert.ThrowsAnyAsync<SessionKeyStore.StoreError>(() => model.ForgetSelectedWallet("correct horse"));
        Assert.Equal(["Moved"], model.Wallets.Select(w => w.Alias));
        // Asked again for what is there now, Forget works (the importer renames to the same name).
        await model.ForgetSelectedWallet("correct horse");
        Assert.Empty(model.Wallets);
    }

    [Fact]
    public void SessionStoreNeverReplacesAnEntry()
    {
        var store = new SessionKeyStore();
        var a = new Wallet("a", new Address(Paper.Address), AddressScheme.Tz1, "edpkA", KeyKind.Unencrypted);
        store.Add(a, "edskA");
        Assert.ThrowsAny<SessionKeyStore.StoreError>(() => store.Add(a, "edskA2"));                       // same alias
        Assert.ThrowsAny<SessionKeyStore.StoreError>(() => store.Add(a with { Alias = "b" }, "edskA2"));  // same address
        var b = new Wallet("b", new Address(Other.Address), AddressScheme.Tz1, "edpkB", KeyKind.Unencrypted);
        store.Add(b, "edskB");
        Assert.ThrowsAny<SessionKeyStore.StoreError>(() => store.Rename("a", "b"));
        Assert.Equal("edskA", store.SecretKey(store.Wallets[0]));

        // A snapshot is only good for the entry as it was: rename or removal in between refuses it.
        var snapshot = store.RemovalSnapshotFor(store.Wallets[0])!;
        store.Rename("a", "c");
        Assert.ThrowsAny<SessionKeyStore.StoreError>(() => store.Remove(snapshot));
        Assert.Equal(["c", "b"], store.Wallets.Select(w => w.Alias));
        var fresh = store.RemovalSnapshotFor(store.Wallets[0])!;
        store.Remove(fresh);
        Assert.Equal(["b"], store.Wallets.Select(w => w.Alias));
        Assert.ThrowsAny<SessionKeyStore.StoreError>(() => store.Remove(fresh));
    }

    [Fact]
    public async Task ConcurrentImportsOfTheSameAliasAdmitOnlyOne()
    {
        var model = new WalletViewModel(chain: new MockChainService());
        var one = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var two = await new KeyGenerator().Generate(AddressScheme.Tz1);
        // Both pass the first check and encrypt (an await); the second must fail the recheck.
        async Task<bool> Import(KeyMaterial material)
        {
            try { await model.ImportAccount("Paper", material, storePassphrase: "correct horse battery", keepForSession: true); return true; }
            catch (Exception) { return false; }
        }
        var results = await Task.WhenAll(Import(one), Import(two));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(["Paper"], model.Wallets.Select(w => w.Alias));
    }
}

/// <summary>S09: import reads a recovered, locked source snapshot.</summary>
public class ImportSourceBoundaryTests
{
    [Fact]
    public void ImportRecoversTheSourceJournalBeforeReading()
    {
        var source = TestSupport.MakeDir("source");
        // A finished state: alice with her key. Then an unfinished change replaced the address
        // file (new address, no matching key yet) and left the journal holding the old address.
        var oldHashes = """[ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" } ]"""u8.ToArray();
        TestSupport.WriteText(source, "public_key_hashs", """[ { "name": "alice", "value": "tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq" } ]""");
        TestSupport.WriteText(source, "public_keys", """[ { "name": "alice", "value": { "locator": "unencrypted:edpkALICE", "key": "edpkALICE" } } ]""");
        TestSupport.WriteText(source, "secret_keys", """[ { "name": "alice", "value": "unencrypted:edskALICE" } ]""");
        TestSupport.WriteText(source, TezosClientStore.JournalFile, new System.Text.Json.Nodes.JsonObject { ["version"] = 1, ["files"] = new System.Text.Json.Nodes.JsonObject { ["public_key_hashs"] = Convert.ToBase64String(oldHashes) } }.ToJsonString());

        var store = new TezosClientStore(TestSupport.MakeDir("target"));
        Assert.Equal(1, store.ImportWallets(source));

        // The copy is of the recovered source: the old address, which the key belongs to.
        var imported = store.Load().First();
        Assert.True(imported.Address.Value == "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" && imported.PublicKey == "edpkALICE");
        Assert.Equal("edskALICE", store.SecretKey(imported));
        // The source itself was rolled back and its journal removed.
        Assert.False(File.Exists(Path.Combine(source, TezosClientStore.JournalFile)));
        Assert.Equal("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", new TezosClientStore(source).Load().First().Address.Value);
    }

    [Fact]
    public void ImportRecoversBeforeCheckingWhetherTheHashFileExists()
    {
        var source = TestSupport.MakeDir("missing-hashes");
        var hashes = """[{"name":"alice","value":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"}]"""u8.ToArray();
        TestSupport.WriteText(source, TezosClientStore.JournalFile, new System.Text.Json.Nodes.JsonObject { ["version"] = 1, ["files"] = new System.Text.Json.Nodes.JsonObject { [TezosClientStore.PublicKeyHashesFile] = Convert.ToBase64String(hashes) } }.ToJsonString());
        Assert.False(new TezosClientStore(source).HasWallets);
        var destination = new TezosClientStore(TestSupport.MakeDir("target"));
        Assert.Equal(1, destination.ImportWallets(source));
        Assert.Equal(["alice"], destination.Load().Select(w => w.Alias));
        Assert.False(File.Exists(Path.Combine(source, TezosClientStore.JournalFile)));
    }

    [Fact]
    public void ImportFiltersRejectedAliasesEvenWhenTargetKeyFilesAreMissing()
    {
        string source = TestSupport.MakeDir("source"), target = TestSupport.MakeDir("contracts-only");
        TezosClientStore from = new(source), to = new(target);
        from.AddWithSecretKey(new Wallet("reserved", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkRejected", keyKind: KeyKind.Unencrypted), "edskRejected");
        from.AddWithSecretKey(new Wallet("accepted", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), publicKey: "sppkAccepted", keyKind: KeyKind.Unencrypted), "spskAccepted");
        to.AddContract(new MultisigContract("reserved", new Address("KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton")));
        Assert.Equal(1, to.ImportWallets(source));
        Assert.True(to.Load().First(w => w.Alias == "reserved").Address.IsContract);
        Assert.Equal(["accepted"], to.Load().Where(w => !w.Address.IsContract).Select(w => w.Alias));
        foreach (var file in TezosClientStore.WalletFiles)
            Assert.DoesNotContain("reserved", File.ReadAllText(Path.Combine(target, file)));
    }

    [Fact]
    public void ImportRefusesTheDirectoryItself()
    {
        var dir = TestSupport.MakeDir("self");
        var store = new TezosClientStore(dir);
        store.AddWithSecretKey(new Wallet("me", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkME", keyKind: KeyKind.Unencrypted), "edskME");
        // Windows: a junction stands in for upstream's symbolic link.
        var viaLink = Path.Combine(TestSupport.MakeDir("links"), "wallet");
        TestSupport.CreateJunction(viaLink, dir);
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.ImportWallets(dir));
        Assert.ThrowsAny<TezosClientStore.StoreError>(() => store.ImportWallets(viaLink));
        Assert.Single(store.Load());
    }

    [Fact]
    public async Task ImportWaitsForAWriterHoldingTheSourceLock()
    {
        var source = TestSupport.MakeDir("source");
        TestSupport.WriteText(source, "public_key_hashs", """[ { "name": "alice", "value": "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" } ]""");
        var store = new TezosClientStore(TestSupport.MakeDir("target"));
        using ManualResetEventSlim holding = new(), release = new();
        var cancel = TestContext.Current.CancellationToken;
        var writer = Task.Run(() => TezosClientStore.WithWalletLock(source, () => { holding.Set(); release.Wait(TimeSpan.FromSeconds(3), cancel); return 0; }), cancel);
        Assert.True(holding.Wait(TimeSpan.FromSeconds(2), cancel));
        var started = DateTime.UtcNow;
        var reader = Task.Run(() => store.ImportWallets(source), cancel);
        await Task.Delay(300, cancel);
        Assert.False(reader.IsCompleted);   // blocked on the source lock
        release.Set();
        await reader.WaitAsync(TimeSpan.FromSeconds(3), cancel);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300));
        Assert.Equal(["alice"], store.Load().Select(w => w.Alias));
        await writer;
    }

    [Fact]
    public async Task OppositeImportsDoNotDeadlock()
    {
        string a = TestSupport.MakeDir("a"), b = TestSupport.MakeDir("b");
        new TezosClientStore(a).AddWithSecretKey(new Wallet("fromA", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkA", keyKind: KeyKind.Unencrypted), "edskA");
        new TezosClientStore(b).AddWithSecretKey(new Wallet("fromB", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), publicKey: "sppkB", keyKind: KeyKind.Unencrypted), "spskB");
        var cancel = TestContext.Current.CancellationToken;
        var jobs = new[] { (a, b), (b, a) }.Select(pair => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
                try { new TezosClientStore(pair.Item2).ImportWallets(pair.Item1); } catch (TezosClientStore.StoreError) { }
        }, cancel));
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(10), cancel);
        Assert.Equal(new HashSet<string> { "fromA", "fromB" }, new TezosClientStore(a).Load().Select(w => w.Alias).ToHashSet());
        Assert.Equal(new HashSet<string> { "fromA", "fromB" }, new TezosClientStore(b).Load().Select(w => w.Alias).ToHashSet());
    }
}

public class RememberKeyTests
{
    [Fact]
    public async Task RememberMovesASessionKeyIntoTheDirectory()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        Assert.False(model.CanRememberSelectedKey);
        await model.ImportAccount("Paper", SessionKeyRemovalTests.Paper, alreadyEncrypted: true, keepForSession: true);
        Assert.True(model.CanRememberSelectedKey && model.SelectedWallet?.IsSessionOnly == true);
        Assert.Empty(store.Load());

        await model.RememberSelectedWallet();

        var kept = model.SelectedWallet!;
        Assert.True(kept.Alias == "Paper" && !kept.IsSessionOnly && kept.KeyKind == KeyKind.Encrypted);
        Assert.True(!model.CanRememberSelectedKey && model.SessionWallets.Count == 0);
        Assert.Equal(["Paper"], store.Load().Select(w => w.Alias));
        Assert.Equal(SessionKeyRemovalTests.Paper.SecretKey, store.SecretKey(kept));
        // Asked again, there is nothing session-only to remember.
        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.RememberSelectedWallet());
    }

    [Fact]
    public async Task RememberCanEncryptAClearKeyOnTheWay()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        await model.ImportAccount("Clear", material, keepForSession: true);
        await Assert.ThrowsAsync<WalletViewModel.WalletError.PassphraseTooShort>(() => model.RememberSelectedWallet("short"));
        Assert.True(model.SelectedWallet?.IsSessionOnly);

        await model.RememberSelectedWallet("correct horse battery");
        var kept = model.SelectedWallet!;
        Assert.True(kept.KeyKind == KeyKind.Encrypted && !kept.IsSessionOnly);
        var stored = store.SecretKey(kept)!;
        Assert.StartsWith("edesk", stored);
        Assert.Equal(material.SecretKey, await new BridgeKeyImporter().Decrypt(stored, "correct horse battery"));
    }

    [Fact]
    public async Task RememberRefusesAnAliasOrAddressAlreadyStoredAndKeepsTheKey()
    {
        var store = new InMemoryWalletStore();
        var model = new WalletViewModel(chain: new MockChainService(), walletStore: store);
        await model.ImportAccount("Paper", SessionKeyRemovalTests.Paper, alreadyEncrypted: true, keepForSession: true);
        var session = model.SelectedWallet!;
        // The same address lands in the directory under another name behind the model's back.
        store.AddWithSecretKey(new Wallet("Disk", session.Address, AddressScheme.Tz1, "edpkuPAPER", KeyKind.Encrypted), "edeskPAPER");

        await Assert.ThrowsAnyAsync<WalletViewModel.WalletError>(() => model.RememberSelectedWallet());
        Assert.Equal(["Paper"], model.SessionWallets.Select(w => w.Alias));
        Assert.Equal(SessionKeyRemovalTests.Paper.SecretKey, Assert.IsType<SigningKey.Secret>(model.SigningKeyFor(session, "x")).Key);
    }
}
