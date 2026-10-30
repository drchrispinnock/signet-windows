using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>Octez-client's generic multisig, through the bridge (<c>TaquitoBridge/src/multisig.js</c>).</summary>
public interface IMultisigService
{
    IMultisigService ForNetwork(Network network) => this;
    /// <summary>Counter, threshold, keys, balance and whether the code is the generic multisig script.</summary>
    Task<MultisigInfo> Info(Uri rpcURL, Address address);
    /// <summary>The public key an account has revealed on chain (needed to make it a signer), or <c>null</c>.</summary>
    Task<string?> RevealedPublicKey(Uri rpcURL, Address address);
    /// <summary>The address a base58 public key hashes to, computed locally (no network).</summary>
    Task<Address> AddressForPublicKey(string publicKey);
    /// <summary>
    /// Signs the proposal's action for its chain, contract and counter. The bytes are rebuilt from
    /// those fields and only they are signed; they are returned so the caller can compare them
    /// with what it showed the user. <c>proposal.Bytes</c> is never used.
    /// </summary>
    Task<(string Bytes, string Signature)> Sign(SigningKey signer, MultisigProposal proposal);
    /// <summary>Fee and burn for deploying the contract with <c>amount</c> tez moved into it on creation.</summary>
    Task<TransferEstimate> EstimateOriginate(Uri rpcURL, Wallet wallet, int threshold, IReadOnlyList<string> keys, decimal amount);
    /// <summary>
    /// Deploys a multisig with the approved limits (S14). Returns the operation hash and the new
    /// address as soon as it is injected; the caller waits for the block (S15).
    /// </summary>
    Task<(string Hash, Address Address)> Originate(Uri rpcURL, SigningKey signer, int threshold, IReadOnlyList<string> keys, decimal amount, OperationLimits limits);
    /// <summary>The contract's state plus the bytes every signer signs for this action at its current counter.</summary>
    Task<(MultisigInfo Info, string ChainID, string Bytes)> Prepare(Uri rpcURL, Address contract, MultisigAction action);
    Task<TransferEstimate> EstimateSubmit(Uri rpcURL, Wallet wallet, MultisigProposal proposal, IReadOnlyList<string> signatures);
    /// <summary>
    /// Calls the contract's <c>main</c> entrypoint with the signatures, with the approved limits (S14).
    /// Returns the operation hash once injected; the caller waits for the block (S15).
    /// </summary>
    Task<string> Submit(Uri rpcURL, SigningKey signer, MultisigProposal proposal, IReadOnlyList<string> signatures, OperationLimits limits);
}

public sealed class BridgeMultisigService(TaquitoBridge? bridge = null, Network? network = null) : IMultisigService
{
    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;

    public IMultisigService ForNetwork(Network network) => new BridgeMultisigService(bridge, network);

    private async Task Verify(Uri rpcURL)
    {
        if (network is null || network.RpcURL != rpcURL)
            throw new TaquitoBridge.BridgeError.JavaScript("The multisig service needs the selected network and endpoint before contacting a node.");
        await bridge.Call("assertChain", rpcURL.AbsoluteString(), network.ExpectedChainID ?? "").WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static MultisigInfo InfoFrom(JSONValue r)
    {
        if (r["address"]?.StringValue is not { } address || r["isGenericMultisig"]?.BoolValue is not { } generic || r["scriptHash"]?.StringValue is not { } hash)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected multisig payload: {r}");
        // A generic multisig always has a counter and a threshold; a malformed one is an error,
        // not a contract at counter 0 (S20).
        int counter = 0, threshold = 0;
        if (generic)
        {
            if (r["counter"]?.IntValue is not (>= 0 and var c) || r["threshold"]?.IntValue is not (>= 0 and var t))
                throw new TaquitoBridge.BridgeError.JavaScript($"The multisig's counter or threshold is unreadable: {r}");
            (counter, threshold) = (c, t);
        }
        return new MultisigInfo(
            new Address(address), generic, hash, counter, threshold,
            (r["keys"]?.ArrayValue ?? []).Select(k => k.StringValue).OfType<string>().ToList(),
            Mutez.ToTez(r["balanceMutez"]?.StringValue) ?? 0);
    }

    /// <summary>The action as <c>multisig.js</c> reads it.</summary>
    public static string Json(MultisigAction action) => action switch
    {
        MultisigAction.Transfer t => new JsonObject { ["kind"] = "transfer", ["amountMutez"] = t.AmountMutez, ["destination"] = t.Destination }.ToJsonString(),
        MultisigAction.SetDelegate d => new JsonObject { ["kind"] = "delegate", ["delegate"] = d.Delegate }.ToJsonString(),
        _ => throw new ArgumentException("unknown multisig action"),
    };

    private static string Json(IReadOnlyList<string> list) => JsonSerializer.Serialize(list);

    public async Task<MultisigInfo> Info(Uri rpcURL, Address address)
    {
        await Verify(rpcURL);
        return InfoFrom(await bridge.Call("multisigInfo", rpcURL.AbsoluteString(), address.Value));
    }

    public async Task<string?> RevealedPublicKey(Uri rpcURL, Address address)
    {
        await Verify(rpcURL);
        return (await bridge.Call("multisigRevealedKey", rpcURL.AbsoluteString(), address.Value))["publicKey"]?.StringValue;
    }

    public async Task<Address> AddressForPublicKey(string publicKey) =>
        (await bridge.Call("addressFromPublicKey", publicKey)).StringValue is { } address
            ? new Address(address)
            : throw new TaquitoBridge.BridgeError.JavaScript($"“{publicKey[..Math.Min(12, publicKey.Length)]}…” is not a public key Signet can read.");

    public async Task<(string Bytes, string Signature)> Sign(SigningKey signer, MultisigProposal proposal)
    {
        var r = await bridge.Call("multisigSign", signer.BridgeSpec, proposal.ChainID, proposal.ContractAddress, proposal.Counter, Json(proposal.Action));
        if (r["bytes"]?.StringValue is not { } bytes || r["signature"]?.StringValue is not { } signature)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected signing payload: {r}");
        return (bytes, signature);
    }

    public async Task<TransferEstimate> EstimateOriginate(Uri rpcURL, Wallet wallet, int threshold, IReadOnlyList<string> keys, decimal amount)
    {
        await Verify(rpcURL);
        var publicKey = wallet.PublicKey ?? throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        return TaquitoChainService.EstimateFrom(await bridge.Call("multisigEstimateOriginate", rpcURL.AbsoluteString(), wallet.Address.Value, publicKey, threshold, Json(keys), Mutez.FromTez(amount)));
    }

    public async Task<(string Hash, Address Address)> Originate(Uri rpcURL, SigningKey signer, int threshold, IReadOnlyList<string> keys, decimal amount, OperationLimits limits)
    {
        await Verify(rpcURL);
        var r = await bridge.Call("multisigOriginate", rpcURL.AbsoluteString(), signer.BridgeSpec, threshold, Json(keys), limits.Json, Mutez.FromTez(amount));
        if (r["hash"]?.StringValue is not { } hash || r["address"]?.StringValue is not { } address)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected origination payload: {r}");
        return (hash, new Address(address));
    }

    public async Task<(MultisigInfo Info, string ChainID, string Bytes)> Prepare(Uri rpcURL, Address contract, MultisigAction action)
    {
        await Verify(rpcURL);
        var r = await bridge.Call("multisigPrepare", rpcURL.AbsoluteString(), contract.Value, Json(action));
        if (r["chainId"]?.StringValue is not { } chainID || r["bytes"]?.StringValue is not { } bytes)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected prepare payload: {r}");
        return (InfoFrom(r), chainID, bytes);
    }

    public async Task<TransferEstimate> EstimateSubmit(Uri rpcURL, Wallet wallet, MultisigProposal proposal, IReadOnlyList<string> signatures)
    {
        await Verify(rpcURL);
        var publicKey = wallet.PublicKey ?? throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        return TaquitoChainService.EstimateFrom(await bridge.Call("multisigEstimateSubmit",
            rpcURL.AbsoluteString(), wallet.Address.Value, publicKey, proposal.ContractAddress, Json(proposal.Action), proposal.Counter, Json(signatures)));
    }

    public async Task<string> Submit(Uri rpcURL, SigningKey signer, MultisigProposal proposal, IReadOnlyList<string> signatures, OperationLimits limits)
    {
        await Verify(rpcURL);
        var r = await bridge.Call("multisigSubmit",
            rpcURL.AbsoluteString(), signer.BridgeSpec, proposal.ContractAddress, Json(proposal.Action), proposal.Counter, Json(signatures), limits.Json);
        return r["hash"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected submit payload: {r}");
    }
}

/// <summary>For previews and tests: one generic multisig whose keys are whatever the test says.</summary>
public sealed class MockMultisigService(IReadOnlyList<string>? keys = null, int threshold = 2) : IMultisigService
{
    public static readonly Address SampleAddress = new("KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi");
    public IReadOnlyList<string> Keys { get; set; } = keys ?? ["edpkMOCK1", "edpkMOCK2", "edpkMOCK3"];
    public int Threshold { get; set; } = threshold;
    public int Counter { get; set; } = 3;
    public bool IsGeneric { get; set; } = true;
    public decimal Balance { get; set; } = 12;
    /// <summary>Public keys "revealed on chain" by address, for the tz-address path of Create.</summary>
    public Dictionary<string, string> Revealed { get; set; } = [];
    /// <summary>What each public key hashes to; keys not listed are "unreadable".</summary>
    public Dictionary<string, string> KeyAddresses { get; set; } = [];
    public List<(MultisigProposal Proposal, IReadOnlyList<string> Signatures)> Submitted { get; } = [];
    /// <summary>What <c>Sign</c> was asked for, to check it never gets stored bytes.</summary>
    public List<(string Bytes, MultisigAction Action)> Signed { get; } = [];
    public List<OperationLimits> Originations { get; } = [];
    /// <summary>The initial balance each origination asked for, in tez.</summary>
    public List<decimal> FundedWith { get; } = [];

    private MultisigInfo InfoFor(Address address) =>
        new(address, IsGeneric, IsGeneric ? "exprub9UzpxmhedNQnsv1J1DazWGJnj1dLhtG1fxkUoWSdFLBGLqJ4" : "exprOTHER", Counter, Threshold, Keys, Balance);

    public Task<MultisigInfo> Info(Uri rpcURL, Address address) => Task.FromResult(InfoFor(address));
    public Task<string?> RevealedPublicKey(Uri rpcURL, Address address) => Task.FromResult(Revealed.GetValueOrDefault(address.Value));

    public Task<Address> AddressForPublicKey(string publicKey) =>
        KeyAddresses.TryGetValue(publicKey, out var address)
            ? Task.FromResult(new Address(address))
            : Task.FromException<Address>(new TaquitoBridge.BridgeError.JavaScript($"unreadable key {publicKey}"));

    /// <summary>Deterministic stand-in for the packed payload: chain, contract, counter and action, hex-encoded.</summary>
    public static string MockBytes(string chainID, string contract, int counter, MultisigAction action)
    {
        var tag = action switch
        {
            MultisigAction.Transfer t => "01" + t.AmountMutez + t.Destination,
            MultisigAction.SetDelegate { Delegate: { } d } => "02" + d,
            _ => "03",
        };
        return "05" + Convert.ToHexString(Encoding.UTF8.GetBytes($"{chainID}|{contract}|{counter}|{tag}")).ToLowerInvariant();
    }

    public Task<(string Bytes, string Signature)> Sign(SigningKey signer, MultisigProposal proposal)
    {
        var bytes = MockBytes(proposal.ChainID, proposal.ContractAddress, proposal.Counter, proposal.Action);
        Signed.Add((bytes, proposal.Action));
        return Task.FromResult((bytes, "edsigMock" + bytes[^12..] + (uint)signer.BridgeSpec.GetHashCode() % 1000));
    }

    public Task<TransferEstimate> EstimateOriginate(Uri rpcURL, Wallet wallet, int threshold, IReadOnlyList<string> keys, decimal amount) =>
        Task.FromResult(new TransferEstimate(0.002m, 0.5m, 0.502m, 1000, 500, new OperationLimits("2000", 1000, 500)));

    public Task<(string Hash, Address Address)> Originate(Uri rpcURL, SigningKey signer, int threshold, IReadOnlyList<string> keys, decimal amount, OperationLimits limits)
    {
        (Keys, Threshold, Counter) = (keys, threshold, 0);
        Originations.Add(limits);
        FundedWith.Add(amount);
        return Task.FromResult(("ooMockOrigination", SampleAddress));
    }

    public Task<(MultisigInfo Info, string ChainID, string Bytes)> Prepare(Uri rpcURL, Address contract, MultisigAction action) =>
        Task.FromResult((InfoFor(contract), "NetXMock", MockBytes("NetXMock", contract.Value, Counter, action)));

    public Task<TransferEstimate> EstimateSubmit(Uri rpcURL, Wallet wallet, MultisigProposal proposal, IReadOnlyList<string> signatures) =>
        Task.FromResult(new TransferEstimate(0.001m, 0, 0.001m, 1000, 0, new OperationLimits("1000", 1000, 0)));

    public Task<string> Submit(Uri rpcURL, SigningKey signer, MultisigProposal proposal, IReadOnlyList<string> signatures, OperationLimits limits)
    {
        Submitted.Add((proposal, signatures));
        Counter++;
        return Task.FromResult("ooMockSubmit");
    }
}

// ---- Proposals on disk -------------------------------------------------------------------------

/// <summary>
/// Proposals awaiting signatures, kept beside the wallet files. Changes go through <c>Update</c>,
/// which reads the stored list, applies the change and writes it back as one step, so two
/// Signets (or a sheet holding a stale copy) cannot drop each other's proposals or signatures (S09).
/// </summary>
public interface IMultisigProposalStore
{
    IReadOnlyList<MultisigProposal> Load();
    /// <summary>Applies <c>change</c> to what is on disk right now and returns the stored list afterwards.</summary>
    IReadOnlyList<MultisigProposal> Update(Action<List<MultisigProposal>> change);
}

/// <summary>
/// <c>&lt;wallet dir&gt;\multisig-proposals.json</c>, read and written under the wallet lock, in the
/// same JSON the Mac writes so a wallet folder moves between the two.
/// </summary>
public sealed class FileMultisigProposalStore(string directory) : IMultisigProposalStore
{
    public const string FileName = "multisig-proposals.json";
    /// <summary>Budgets for a file another program could have edited (S19).</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxBytesHex = 128 * 1024;
    public const int MaxKeys = 256;
    public string Path { get; } = System.IO.Path.Combine(directory, FileName);

    public IReadOnlyList<MultisigProposal> Load()
    {
        try { return TezosClientStore.WithWalletLock(directory, Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TezosClientStore.StoreError) { return []; }
    }

    private List<MultisigProposal> Read()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length > MaxFileBytes) return [];
            if (JsonNode.Parse(File.ReadAllBytes(Path)) is not JsonArray array) return [];
            var proposals = new List<MultisigProposal>();
            foreach (var item in array)
            {
                try { if (item is not null) proposals.Add(MultisigProposal.FromJson(item)); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException) { }
            }
            return proposals.Where(IsReasonable).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    /// <summary>A proposal within the budgets; anything else is left out rather than loaded into memory or shown.</summary>
    public static bool IsReasonable(MultisigProposal p) =>
        p.Bytes.Length <= MaxBytesHex && p.Keys.Count <= MaxKeys && p.Signatures.Count <= MaxKeys && p.Threshold >= 0 && p.Counter >= 0 &&
        p.Keys.All(k => k.Length <= 256) && p.Signatures.All(s => s.Signature.Length <= 512 && (s.PublicKey?.Length ?? 0) <= 256);

    public IReadOnlyList<MultisigProposal> Update(Action<List<MultisigProposal>> change) =>
        TezosClientStore.WithWalletLock(directory, () =>
        {
            var proposals = Read();
            change(proposals);
            var json = new JsonArray(proposals.Select(p => (JsonNode?)p.ToJson()).ToArray());
            AtomicFile.WriteAllText(Path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n"));
            return (IReadOnlyList<MultisigProposal>)proposals;
        });
}

public sealed class InMemoryMultisigProposalStore : IMultisigProposalStore
{
    private readonly Lock gate = new();
    private List<MultisigProposal> proposals = [];

    public IReadOnlyList<MultisigProposal> Load()
    {
        lock (gate) return proposals.ToList();
    }

    public IReadOnlyList<MultisigProposal> Update(Action<List<MultisigProposal>> change)
    {
        lock (gate)
        {
            var copy = proposals.ToList();
            change(copy);
            proposals = copy;
            return copy.ToList();
        }
    }
}
