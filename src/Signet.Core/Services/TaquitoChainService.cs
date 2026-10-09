using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Live chain access through the Taquito bridge. Anything the bridge does not provide yet is
/// delegated to <c>Fallback</c> so the UI stays complete while features land one at a time.
/// </summary>
public sealed class TaquitoChainService(Network? network = null, TaquitoBridge? bridge = null, IChainService? fallback = null, HttpClient? http = null)
    : IChainService
{
    public Network Network { get; } = network ?? Network.Mainnet;
    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;
    public IChainService Fallback { get; } = fallback ?? new MockChainService();
    private readonly HttpClient http = http ?? Http.Shared;

    private string Rpc => Network.RpcURL.AbsoluteString();

    public async Task<TezBalance> TezBalanceFor(Address address)
    {
        // Taquito rejects tz5/tz6 addresses, so ask the node directly for those.
        if (address.Scheme is { } scheme && !scheme.IsSupported()) return await DirectTezBalanceFor(address);
        var result = await bridge.Call("getTezBalances", Rpc, address.Value);
        if (result["exists"] == new JSONValue.Bool(false)) throw new ChainError.AccountNotOnChain(address);
        return TezBalance.FromMutez(result["spendable"]?.StringValue, result["staked"]?.StringValue,
                                    result["unstakedFrozen"]?.StringValue, result["unstakedFinalizable"]?.StringValue)
            ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected balance payload: {result}");
    }

    /// <summary>The same breakdown straight from the node: <c>/context/contracts/&lt;pkh&gt;/{spendable,staked_balance,...}</c>.</summary>
    public async Task<TezBalance> DirectTezBalanceFor(Address address)
    {
        // full_balance is refused with a "missing_key" storage error for accounts the chain has never seen.
        try
        {
            await RpcMutez("full_balance", address);
        }
        catch (RPCError e) when (e.Body.Contains("missing_key") || e.Body.Contains("storage_error"))
        {
            throw new ChainError.AccountNotOnChain(address);
        }
        var fields = await Task.WhenAll(
            RpcMutez("spendable", address), RpcMutez("staked_balance", address),
            RpcMutez("unstaked_frozen_balance", address), RpcMutez("unstaked_finalizable_balance", address));
        return TezBalance.FromMutez(fields[0], fields[1], fields[2], fields[3])
            ?? throw new FormatException("The node's balance reply could not be parsed.");
    }

    /// <summary><c>GET /chains/main/blocks/head/context/contracts/&lt;pkh&gt;/&lt;field&gt;</c> → <c>"&lt;mutez&gt;"</c>.</summary>
    private async Task<string> RpcMutez(string field, Address address)
    {
        var url = Network.RpcURL.AppendingPathComponent($"chains/main/blocks/head/context/contracts/{address.Value}/{field}");
        var reply = await Http.Get(http, url);
        if (!reply.IsSuccess) throw new RPCError(reply.Status, field, address, reply.Text);
        try
        {
            return JsonNode.Parse(reply.Body) is JsonValue v && v.TryGetValue<string>(out var text)
                ? text
                : throw new FormatException("The node's balance reply could not be parsed.");
        }
        catch (JsonException) { throw new FormatException("The node's balance reply could not be parsed."); }
    }

    public Task<decimal> EtherlinkBalanceFor(Address address) => Fallback.EtherlinkBalanceFor(address);

    public async Task<IReadOnlyList<TezosTransaction>> RecentTransactionsFor(Address address, int limit)
    {
        if (Network.TzktURL is not { } tzkt) return [];
        try { return await new TzKTService(tzkt, http).RecentOperationsFor(address, limit); }
        catch (Exception e) when (IsLookupFailure(e))
        {
            Trace.WriteLine($"TzKT history lookup failed for {address}: {e.Message}");
            return [];
        }
    }

    public async Task<Address?> ResolveDomain(string name) =>
        Network.TezosDomainsURL is { } endpoint ? await new TezosDomainsService(endpoint, http).Resolve(name) : null;

    /// <summary>Always mainnet: TzProfiles are mainnet identities regardless of the network in use.</summary>
    public Task<AccountProfile?> AccountProfileFor(Address address) =>
        new TzKTService(Profiles.ProfileIndexer, http).AccountProfileFor(address);

    public async Task<TransferEstimate> EstimateTransfer(Wallet wallet, Address destination, decimal amount)
    {
        if (wallet.PublicKey is not { } publicKey)
            throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        var result = await bridge.Call("estimateTransfer", Rpc, wallet.Address.Value, publicKey, destination.Value, Mutez.FromTez(amount));
        if (Mutez.ToTez(result["feeMutez"]?.StringValue) is not { } fee ||
            Mutez.ToTez(result["burnMutez"]?.StringValue) is not { } burn ||
            Mutez.ToTez(result["totalCostMutez"]?.StringValue) is null)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected estimate payload: {result}");
        return new TransferEstimate(fee, burn, amount + fee + burn,
                                    (int)(result["gasLimit"]?.DoubleValue ?? 0), (int)(result["storageLimit"]?.DoubleValue ?? 0));
    }

    public async Task<string> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount)
    {
        var result = await Signing(() => bridge.Call("sendTransfer", Rpc, signer.BridgeSpec, destination.Value, Mutez.FromTez(amount)));
        return result["hash"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected send payload: {result}");
    }

    /// <summary>
    /// Runs a bridge call that signs, turning the bridge's error text for wrong passwords and
    /// Ledger trouble into <see cref="ChainError"/>s the UI can word properly.
    /// </summary>
    private static async Task<JSONValue> Signing(Func<Task<JSONValue>> body)
    {
        try { return await body(); }
        catch (TaquitoBridge.BridgeError.JavaScript e) when (ChainError.FromBridgeMessage(e.Message) is { } known)
        {
            throw known;
        }
    }

    public async Task<int> WaitForConfirmation(string operationHash)
    {
        try
        {
            var result = await bridge.Call("waitForConfirmation", operationHash, 1);
            return (int)(result.DoubleValue ?? 0);
        }
        catch (TaquitoBridge.BridgeError.JavaScript e) when (e.Message.Contains("unknown operation"))
        {
            // Raw operations are not tracked by Taquito; watch the chain for them instead.
            var result = await bridge.Call("waitForRawOperation", Rpc, operationHash, 15);
            return (int)(result.DoubleValue ?? 0);
        }
    }

    public async Task<DelegateInfo> DelegateInfoFor(Address address)
    {
        var r = await bridge.Call("getDelegateInfo", Rpc, address.Value);
        static Address? Addr(JSONValue? v) => v?.StringValue is { } s ? new Address(s) : null;
        static int? Int(JSONValue? v) => v?.DoubleValue is { } d ? (int)d : null;
        DelegateInfo.BakerRecord? baker = null;
        if (r["baker"] is { ObjectValue: not null } b)
        {
            baker = new DelegateInfo.BakerRecord(
                Deactivated: b["deactivated"]?.BoolValue ?? false,
                GracePeriod: Int(b["gracePeriod"]),
                ConsensusKey: Addr(b["consensusKey"]),
                PendingConsensusKeys: (b["pendingConsensusKeys"]?.ArrayValue ?? []).Select(k => Addr(k["key"])).OfType<Address>().ToList(),
                CompanionKey: Addr(b["companionKey"]),
                PendingCompanionKeys: (b["pendingCompanionKeys"]?.ArrayValue ?? []).Select(k => Addr(k["key"])).OfType<Address>().ToList());
            if (b["stakingParameters"] is { } p && Int(p["limitMillionth"]) is { } limit && Int(p["edgeBillionth"]) is { } edge)
                baker = baker with { StakingParameters = new StakingParameters(limit, edge) };
            baker = baker with
            {
                PendingStakingParameters = (b["pendingStakingParameters"]?.ArrayValue ?? [])
                    .Select(q => Int(q["limitMillionth"]) is { } l && Int(q["edgeBillionth"]) is { } e ? new StakingParameters(l, e, Int(q["cycle"])) : null)
                    .OfType<StakingParameters>().ToList(),
            };
        }
        return new DelegateInfo(Addr(r["delegate"]), baker, r["acceptsStaking"]?.BoolValue);
    }

    public async Task<IReadOnlyList<BakerCandidate>> Bakers(int limit) =>
        Network.TzktURL is { } tzkt ? await new TzKTService(tzkt, http).Bakers(limit) : [];

    /// <summary>Operations Taquito cannot encode itself (keys of schemes it does not know) go through the node.</summary>
    private static List<Dictionary<string, object>>? RawContents(StakingOperation operation)
    {
        static List<Dictionary<string, object>> Content(string kind, string pk, string? proof)
        {
            var entry = new Dictionary<string, object> { ["kind"] = kind, ["pk"] = pk };
            if (proof is not null) entry["proof"] = proof;
            return [entry];
        }
        return operation switch
        {
            StakingOperation.UpdateConsensusKey k when !TaquitoEncodablePublicKey(k.PublicKey) => Content("update_consensus_key", k.PublicKey, k.Proof),
            StakingOperation.UpdateCompanionKey k when !TaquitoEncodablePublicKey(k.PublicKey) => Content("update_companion_key", k.PublicKey, k.Proof),
            _ => null,
        };
    }

    public static bool TaquitoEncodablePublicKey(string pk) =>
        new[] { "edpk", "sppk", "p2pk", "BLpk", "mdpk" }.Any(p => pk.StartsWith(p, StringComparison.Ordinal));

    public async Task<TransferEstimate> EstimateStaking(StakingOperation operation, Wallet wallet)
    {
        if (wallet.PublicKey is not { } publicKey)
            throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        if (RawContents(operation) is { } contents)
        {
            var raw = await bridge.Call("estimateRawOperation", Rpc, wallet.Address.Value, publicKey, JsonSerializer.Serialize(contents));
            var rawFee = Mutez.ToTez(raw["feeMutez"]?.StringValue) ?? 0;
            return new TransferEstimate(rawFee, 0, rawFee, (int)(raw["gasLimit"]?.DoubleValue ?? 0), 0);
        }
        var r = await bridge.Call("estimateStakingOperation", Rpc, wallet.Address.Value, publicKey, operation.BridgeKind,
                                  JsonSerializer.Serialize(operation.BridgeArgument));
        if (Mutez.ToTez(r["feeMutez"]?.StringValue) is not { } fee || Mutez.ToTez(r["burnMutez"]?.StringValue) is not { } burn)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected estimate payload: {r}");
        var amount = operation is StakingOperation.Stake s ? s.Amount : 0;
        return new TransferEstimate(fee, burn, amount + fee + burn, (int)(r["gasLimit"]?.DoubleValue ?? 0), (int)(r["storageLimit"]?.DoubleValue ?? 0));
    }

    public async Task<string> PerformStaking(StakingOperation operation, Wallet wallet, SigningKey signer)
    {
        var r = await Signing(() => RawContents(operation) is { } contents
            ? bridge.Call("sendRawOperation", Rpc, signer.BridgeSpec, JsonSerializer.Serialize(contents))
            : bridge.Call("sendStakingOperation", Rpc, signer.BridgeSpec, operation.BridgeKind, JsonSerializer.Serialize(operation.BridgeArgument)));
        return r["hash"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected payload: {r}");
    }

    public async Task<string> ProofOfPossession(SigningKey signer)
    {
        var r = await Signing(() => bridge.Call("provePossession", signer.BridgeSpec));
        return r["proof"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript("no proof returned");
    }

    public async Task<SignedPayload> SignPayload(SigningKey signer, string payloadHex)
    {
        var r = await Signing(() => bridge.Call("signPayload", signer.BridgeSpec, payloadHex));
        if (r["publicKey"]?.StringValue is not { } pk || r["signature"]?.StringValue is not { } signature)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected signature payload: {r}");
        return new SignedPayload(pk, signature);
    }

    public async Task<GovernanceInfo> GovernanceInfoFor(Address address) =>
        GovernanceInfoFrom(await bridge.Call("getGovernanceInfo", Rpc, address.Value));

    public static GovernanceInfo GovernanceInfoFrom(JSONValue r)
    {
        static decimal Dec(JSONValue? v) =>
            v?.StringValue is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
            : v?.DoubleValue is { } n ? (decimal)n : 0;
        static int? Int(JSONValue? v) => v?.DoubleValue is { } d ? (int)d : null;
        return new GovernanceInfo
        {
            Kind = VotingPeriodKindExtensions.FromRawValue(r["kind"]?.StringValue),
            Index = Int(r["index"]),
            Position = Int(r["position"]),
            Remaining = Int(r["remaining"]),
            Proposals = (r["proposals"]?.ArrayValue ?? [])
                .Select(p => p["hash"]?.StringValue is { } hash ? new GovernanceInfo.Proposal(hash, Dec(p["votingPower"])) : null)
                .OfType<GovernanceInfo.Proposal>()
                .OrderByDescending(p => p.VotingPower)
                .ToList(),
            CurrentProposal = r["currentProposal"]?.StringValue,
            VotingPower = r["votingPower"]?.StringValue is { } vp && decimal.TryParse(vp, NumberStyles.Number, CultureInfo.InvariantCulture, out var power) ? power : null,
            TotalVotingPower = Dec(r["totalVotingPower"]),
            QuorumPerTenThousand = Int(r["quorumPerTenThousand"]),
            BallotTally = new GovernanceInfo.Ballots(Dec(r["ballots"]?["yay"]), Dec(r["ballots"]?["nay"]), Dec(r["ballots"]?["pass"])),
            MyBallot = BallotVoteExtensions.FromRawValue(r["myBallot"]?.StringValue),
            ProposalCount = Int(r["proposalCount"]) ?? 0,
        };
    }

    public async Task<string> PerformGovernance(GovernanceOperation operation, Wallet wallet, SigningKey signer)
    {
        var r = await Signing(() => bridge.Call("sendGovernanceOperation", Rpc, signer.BridgeSpec, operation.BridgeKind,
                                                JsonSerializer.Serialize(operation.BridgeArgument)));
        return r["hash"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected payload: {r}");
    }

    /// <summary>Fungible tokens from TzKT. Indexer trouble is logged and shown as an empty list.</summary>
    public async Task<IReadOnlyList<AssetBalance>> TokenBalancesFor(Address address)
    {
        if (Network.TzktURL is not { } tzkt) return [];
        try { return await new TzKTService(tzkt, http).FungibleTokensFor(address); }
        catch (Exception e) when (IsLookupFailure(e))
        {
            Trace.WriteLine($"TzKT token lookup failed for {address}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// The address's published Tezos Domains name, if any. A lookup failure is logged and
    /// treated as "no name" so a flaky domains API never hides the balances.
    /// </summary>
    public async Task<IReadOnlyList<string>> DomainsFor(Address address)
    {
        if (Network.TezosDomainsURL is not { } endpoint) return [];
        try { return await new TezosDomainsService(endpoint, http).ReverseName(address) is { } name ? [name] : []; }
        catch (Exception e) when (IsLookupFailure(e))
        {
            Trace.WriteLine($"Tezos Domains lookup failed for {address}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// NFTs from the TzKT indexer. Indexer trouble is logged and shown as an empty grid rather
    /// than failing the whole refresh.
    /// </summary>
    public async Task<IReadOnlyList<NFT>> NftsFor(Address address)
    {
        if (Network.TzktURL is not { } tzkt) return [];
        try { return await new TzKTService(tzkt, http).NftsFor(address); }
        catch (Exception e) when (IsLookupFailure(e))
        {
            Trace.WriteLine($"TzKT NFT lookup failed for {address}: {e.Message}");
            return [];
        }
    }

    /// <summary>Network, timeout and parse failures: what upstream's bare <c>catch</c> absorbs for indexer lookups.</summary>
    private static bool IsLookupFailure(Exception e) =>
        e is HttpRequestException or TimeoutException or JsonException or TezosDomainsService.DomainsError or FormatException;
}

/// <summary>A non-2xx answer from the node's RPC, with the body kept for classification.</summary>
public sealed class RPCError(int status, string field, Address address, string body)
    : Exception($"RPC error {status} for {address.Shortened()} {field}: {(body.Length > 200 ? body[..200] : body)}")
{
    public int Status { get; } = status;
    public string Field { get; } = field;
    public Address Address { get; } = address;
    public string Body { get; } = body;
}
