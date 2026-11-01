using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>Live chain access through the Taquito bridge.</summary>
public sealed class TaquitoChainService(Network? network = null, TaquitoBridge? bridge = null, HttpClient? http = null)
    : IChainService
{
    public Network Network { get; } = network ?? Network.Mainnet;
    private readonly TaquitoBridge bridge = bridge ?? TaquitoBridge.Shared;
    private readonly HttpClient http = http ?? Http.Shared;

    private string Rpc => Network.RpcURL.AbsoluteString();

    public async Task<TezBalance> TezBalanceFor(Address address)
    {
        // The Michelson interface has no staking and no full_balance: `balance` is the whole story,
        // and an account the rollup has never seen is simply 0.
        if (Network.LayerNumber == 2)
            return TezBalance.FromMutez(await RpcMutez("balance", address), "0", "0", "0")
                ?? throw new FormatException("The node's balance reply could not be parsed.");
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

    /// <summary>
    /// <c>GET /chains/main/blocks/head/context/contracts/&lt;pkh&gt;/&lt;field&gt;</c> → <c>"&lt;mutez&gt;"</c>, from the
    /// network's node or the node at <c>at</c> (the Etherlink one).
    /// </summary>
    private async Task<string> RpcMutez(string field, Address address, Uri? at = null)
    {
        var url = (at ?? Network.RpcURL).AppendingPathComponent($"chains/main/blocks/head/context/contracts/{address.Value}/{field}");
        var reply = await Http.Get(http, url);
        if (!reply.IsSuccess) throw new RPCError(reply.Status, field, address, reply.Text);
        return JSONString(reply.Body) ?? throw new FormatException("The node's balance reply could not be parsed.");
    }

    /// <summary><c>GET &lt;node&gt;/&lt;path&gt;</c> → a JSON string, such as <c>chains/main/chain_id</c>.</summary>
    private async Task<string> RpcString(string path, Uri node)
    {
        var reply = await Http.Get(http, node.AppendingPathComponent(path));
        if (!reply.IsSuccess) throw new HttpRequestException($"The node returned HTTP {reply.Status} for {path}.");
        return JSONString(reply.Body) ?? throw new FormatException("The node's reply could not be parsed.");
    }

    private static string? JSONString(byte[] body)
    {
        try { return JsonNode.Parse(body) is JsonValue v && v.TryGetValue<string>(out var text) ? text : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The account's tez on Etherlink, through the rollup's Tezos-style RPC (<c>Network.L2RPCURL</c>):
    /// the same <c>…/contracts/&lt;pkh&gt;/balance</c> call as a layer 1 node answers. An account the
    /// rollup has never seen is simply 0. The node must report the pinned Etherlink chain id.
    /// </summary>
    public async Task<decimal> EtherlinkBalanceFor(Address address)
    {
        if (!Network.HasL2 || Network.L2RPCURL is not { } l2) throw new ChainError.NoEtherlinkNode();
        if (Network.ExpectedL2ChainID is { } expected)
        {
            var found = await RpcString("chains/main/chain_id", l2);
            if (found != expected) throw new ChainError.WrongChain(expected, found);
        }
        var mutez = await RpcMutez("balance", address, l2);
        return Mutez.ToTez(mutez) ?? throw new FormatException("The node's balance reply could not be parsed.");
    }

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
        await VerifyChain();
        var result = await bridge.Call("estimateTransfer", Rpc, wallet.Address.Value, publicKey, destination.Value, Mutez.FromTez(amount));
        return EstimateFrom(result, amount);
    }

    /// <summary>An estimate as the bridge reports it: fee, burn and the limits to send with (S14, S20).</summary>
    public static TransferEstimate EstimateFrom(JSONValue result, decimal amount = 0)
    {
        if (Mutez.ToTez(result["feeMutez"]?.StringValue) is not { } fee ||
            Mutez.ToTez(result["burnMutez"]?.StringValue) is not { } burn ||
            result["gasLimit"]?.IntValue is not { } gasLimit || result["storageLimit"]?.IntValue is not { } storageLimit ||
            OperationLimits.From(result["limits"]) is not { } limits)
            throw new TaquitoBridge.BridgeError.JavaScript($"unexpected estimate payload: {result}");
        return new TransferEstimate(fee, burn, amount + fee + burn, gasLimit, storageLimit, limits);
    }

    /// <summary>Refuses the node before anything is estimated or signed when it serves another chain (S12).</summary>
    public async Task VerifyChain()
    {
        if (Network.ExpectedChainID is not { } expected) return;
        await bridge.Call("assertChain", Rpc, expected);
    }

    public async Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits)
    {
        await VerifyChain();
        var result = await Signing(() => bridge.Call("sendTransfer", Rpc, signer.BridgeSpec, destination.Value, Mutez.FromTez(amount), limits.Json));
        return SentOperation.From(result) ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected send payload: {result}");
    }

    public async Task<TransferEstimate> EstimateEtherlinkDeposit(Wallet wallet, decimal amount)
    {
        if (wallet.PublicKey is not { } publicKey)
            throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        if (!Network.CanDepositToL2 || Network.L2BridgeHelper is not { } helper || Network.L2Rollup is not { } rollup) throw new ChainError.NoEtherlinkBridge();
        await VerifyChain();
        var result = await bridge.Call("estimateEtherlinkDeposit", Rpc, wallet.Address.Value, publicKey, helper.Value, rollup, wallet.Address.Value, Mutez.FromTez(amount));
        return EstimateFrom(result, amount);
    }

    public async Task<SentOperation> SendEtherlinkDeposit(Wallet wallet, SigningKey signer, decimal amount, OperationLimits limits)
    {
        if (!Network.CanDepositToL2 || Network.L2BridgeHelper is not { } helper || Network.L2Rollup is not { } rollup) throw new ChainError.NoEtherlinkBridge();
        await VerifyChain();
        var result = await Signing(() => bridge.Call("sendEtherlinkDeposit", Rpc, signer.BridgeSpec, helper.Value, rollup, wallet.Address.Value, Mutez.FromTez(amount), limits.Json));
        return SentOperation.From(result) ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected deposit payload: {result}");
    }

    // Etherlink alias (0.9.3), on the Layer 2 service

    /// <summary>
    /// Asks the gateway's <c>resolveAddress</c> view (Tezos runtime 0 → Ethereum runtime 1) on the
    /// Layer 2 node: <c>Some (Pair 0 addr)</c> is recorded, <c>Some (Pair 1 addr)</c> only derived.
    /// </summary>
    public async Task<EtherlinkAliasStatus> EtherlinkAliasStatusFor(Address address)
    {
        if (Network.LayerNumber != 2) throw new ChainError.NotALayer2Service();
        await VerifyChain();
        var body = new JsonObject
        {
            ["contract"] = Network.EtherlinkGateway.Value,
            ["view"] = "resolveAddress",
            ["input"] = new JsonObject
            {
                ["prim"] = "Pair",
                ["args"] = new JsonArray(new JsonObject { ["string"] = address.Value }, new JsonObject { ["int"] = "0" }, new JsonObject { ["int"] = "1" }),
            },
            ["chain_id"] = Network.ExpectedChainID ?? "",
            ["unparsing_mode"] = "Readable",
        };
        var reply = await Http.PostJSON(http, Network.RpcURL.AppendingPathComponent("chains/main/blocks/head/helpers/scripts/run_script_view"),
                                        body.ToJsonString(), TimeSpan.FromSeconds(20));
        if (!reply.IsSuccess) throw new HttpRequestException($"The node returned HTTP {reply.Status} for the alias view.");
        return AliasStatus(reply.Body, address);
    }

    /// <summary>Pure parser for the view's reply; the address it names must be the one we derive.</summary>
    public static EtherlinkAliasStatus AliasStatus(byte[] data, Address address)
    {
        string? kind = null, named = null;
        try
        {
            if (JsonNode.Parse(data) is JsonObject root && root["data"] is JsonObject some && Str(some["prim"]) == "Some" &&
                some["args"] is JsonArray { Count: > 0 } outer && outer[0] is JsonObject pair && Str(pair["prim"]) == "Pair" &&
                pair["args"] is JsonArray { Count: 2 } args)
            {
                kind = Str(args[0]?["int"]);
                named = Str(args[1]?["string"]);
            }
        }
        catch (JsonException) { }
        if (kind is null || named is null) throw new FormatException("The alias view's reply could not be parsed.");
        var expected = EtherlinkAlias.AddressFor(address);
        if (named.ToLowerInvariant() != expected) throw new FormatException("The alias view named another address.");
        return kind switch
        {
            "0" => new EtherlinkAliasStatus.Linked(expected),
            "1" => new EtherlinkAliasStatus.Unlinked(expected),
            _ => throw new FormatException("The alias view's reply could not be parsed."),
        };

        static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public async Task<TransferEstimate> EstimateEtherlinkLink(Wallet wallet)
    {
        if (Network.LayerNumber != 2) throw new ChainError.NotALayer2Service();
        if (wallet.PublicKey is not { } publicKey)
            throw new TaquitoBridge.BridgeError.JavaScript($"Account “{wallet.Alias}” has no public key, so its fees cannot be estimated.");
        await VerifyChain();
        var result = await bridge.Call("estimateEtherlinkLink", Rpc, wallet.Address.Value, publicKey, Network.EtherlinkGateway.Value);
        return EstimateFrom(result);
    }

    public async Task<SentOperation> SendEtherlinkLink(Wallet wallet, SigningKey signer, OperationLimits limits)
    {
        if (Network.LayerNumber != 2) throw new ChainError.NotALayer2Service();
        await VerifyChain();
        var result = await Signing(() => bridge.Call("sendEtherlinkLink", Rpc, signer.BridgeSpec, Network.EtherlinkGateway.Value, limits.Json));
        return SentOperation.From(result) ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected link payload: {result}");
    }

    public async Task<SentOperation?> PendingOperationFor(Address address)
    {
        await VerifyChain();
        var value = await bridge.Call("pendingOperation", Rpc, address.Value);
        return value.IsNull ? null : SentOperation.From(value);
    }

    public async Task<SentOperation> Rebroadcast(SentOperation sent)
    {
        if (sent.SignedBytes is not { } bytes) throw new TaquitoBridge.BridgeError.JavaScript($"There are no signed bytes to send again for {sent.Hash}.");
        var result = await bridge.Call("injectSignedBytes", Rpc, bytes);
        return SentOperation.From(result) ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected injection payload: {result}");
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

    /// <summary>
    /// Every operation is forged and injected locally, so the block watcher is the one way to
    /// see it land: it scans up to 15 blocks from the recorded level and returns the block's.
    /// </summary>
    public async Task<int> WaitForConfirmation(string operationHash)
    {
        var result = await bridge.Call("waitForRawOperation", Rpc, operationHash, 15);
        return result.IntValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected confirmation payload: {result}");
    }

    public async Task<DelegateInfo> DelegateInfoFor(Address address)
    {
        var r = await bridge.Call("getDelegateInfo", Rpc, address.Value);
        static Address? Addr(JSONValue? v) => v?.StringValue is { } s ? new Address(s) : null;
        static int? Int(JSONValue? v) => v?.IntValue;
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
        await VerifyChain();
        if (RawContents(operation) is { } contents)
            return EstimateFrom(await bridge.Call("estimateRawOperation", Rpc, wallet.Address.Value, publicKey, JsonSerializer.Serialize(contents)));
        var r = await bridge.Call("estimateStakingOperation", Rpc, wallet.Address.Value, publicKey, operation.BridgeKind,
                                  JsonSerializer.Serialize(operation.BridgeArgument));
        var amount = operation is StakingOperation.Stake s ? s.Amount : 0;
        return EstimateFrom(r, amount);
    }

    public async Task<SentOperation> PerformStaking(StakingOperation operation, Wallet wallet, SigningKey signer, OperationLimits limits)
    {
        await VerifyChain();
        var r = await Signing(() => RawContents(operation) is { } contents
            ? bridge.Call("sendRawOperation", Rpc, signer.BridgeSpec, JsonSerializer.Serialize(contents), limits.Json)
            : bridge.Call("sendStakingOperation", Rpc, signer.BridgeSpec, operation.BridgeKind, JsonSerializer.Serialize(operation.BridgeArgument), limits.Json));
        return SentOperation.From(r) ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected payload: {r}");
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
        static int? Int(JSONValue? v) => v?.IntValue;
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
        await VerifyChain();
        var r = await Signing(() => bridge.Call("sendGovernanceOperation", Rpc, signer.BridgeSpec, operation.BridgeKind,
                                                JsonSerializer.Serialize(operation.BridgeArgument)));
        return r["hash"]?.StringValue ?? throw new TaquitoBridge.BridgeError.JavaScript($"unexpected payload: {r}");
    }

    public async Task<IReadOnlyList<AssetBalance>> TokenBalancesFor(Address address) =>
        Network.TzktURL is { } tzkt ? await new TzKTService(tzkt, http).FungibleTokensFor(address) : [];

    /// <summary>
    /// One paginated indexer snapshot for both tabs. Indexer trouble is reported as
    /// <c>Unavailable</c>, never as an account with nothing in it (S26).
    /// </summary>
    public async Task<Portfolio> PortfolioFor(Address address)
    {
        if (Network.TzktURL is not { } tzkt) return Portfolio.Empty;
        try { return await new TzKTService(tzkt, http).PortfolioFor(address); }
        catch (Exception e) when (IsLookupFailure(e))
        {
            Trace.WriteLine($"TzKT portfolio lookup failed for {address}: {e.Message}");
            return new Portfolio([], [], new Portfolio.PortfolioState.Unavailable(e.Message));
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

    /// <summary>NFTs from the TzKT indexer.</summary>
    public async Task<IReadOnlyList<NFT>> NftsFor(Address address) =>
        Network.TzktURL is { } tzkt ? await new TzKTService(tzkt, http).NftsFor(address) : [];

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
