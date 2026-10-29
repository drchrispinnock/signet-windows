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

    /// <summary>Etherlink is not ported yet, so no network has a Layer 2 node to ask.</summary>
    public Task<decimal> EtherlinkBalanceFor(Address address) => throw new ChainError.NoEtherlinkNode();

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
