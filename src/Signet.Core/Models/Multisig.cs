using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Signet.Models;

/// <summary>A named multisig contract: an octez-client <c>contracts</c> alias pointing at a KT1 address.</summary>
public sealed record MultisigContract(string Alias, Address Address)
{
    public string Id => Alias;
}

/// <summary>What the chain says about a multisig contract right now.</summary>
/// <param name="IsGenericMultisig">The contract's code is octez-client's generic multisig; nothing below is meaningful otherwise.</param>
/// <param name="Keys">Base58 public keys in storage order; signatures are placed by that order.</param>
public sealed record MultisigInfo(Address Address, bool IsGenericMultisig, string ScriptHash, int Counter, int Threshold, IReadOnlyList<string> Keys, decimal Balance)
{
    /// <summary>Our wallets that can sign for this multisig.</summary>
    public IReadOnlyList<Wallet> Signers(IEnumerable<Wallet> wallets) =>
        wallets.Where(w => w.KeyKind.CanSign() && w.PublicKey is { } key && Keys.Contains(key)).ToList();

    /// <summary>Any of our wallets whose key is listed, signing or not (watch-only Ledger entries etc.).</summary>
    public IReadOnlyList<Wallet> Members(IEnumerable<Wallet> wallets) =>
        wallets.Where(w => w.PublicKey is { } key && Keys.Contains(key)).ToList();

    public bool Equals(MultisigInfo? other) =>
        other is not null && Address == other.Address && IsGenericMultisig == other.IsGenericMultisig && ScriptHash == other.ScriptHash &&
        Counter == other.Counter && Threshold == other.Threshold && Keys.SequenceEqual(other.Keys) && Balance == other.Balance;
    public override int GetHashCode() => HashCode.Combine(Address, ScriptHash, Counter, Threshold);
}

/// <summary>One signer's signature over a proposal's bytes.</summary>
public sealed record MultisigSignature(string? PublicKey, string Signature);

/// <summary>
/// What a multisig is asked to do: the lambda the signers sign. Both are what octez-client's
/// <c>prepare multisig transaction … transferring</c> / <c>setting delegate to</c> / <c>withdrawing delegate</c> make.
/// </summary>
public abstract record MultisigAction
{
    /// <param name="AmountMutez">Amount in mutez, as a string so nothing rounds.</param>
    public sealed record Transfer(string AmountMutez, string Destination) : MultisigAction;
    /// <param name="Delegate"><c>null</c> withdraws the delegate.</param>
    public sealed record SetDelegate(string? Delegate) : MultisigAction;

    public decimal Amount => this is Transfer t ? Mutez.ToTez(t.AmountMutez) ?? 0 : 0;

    /// <summary>One line for pickers and confirmations.</summary>
    public string Summary => this switch
    {
        Transfer t => $"{AssetBalance.Format(Amount, "tz")} to {new Address(t.Destination).Shortened()}",
        SetDelegate { Delegate: { } d } => $"Delegate to {new Address(d).Shortened()}",
        _ => "Remove delegate",
    };

    /// <summary>Swift's synthesized <c>Codable</c> form, so the proposals file reads the same on both platforms.</summary>
    internal JsonObject ToJson() => this switch
    {
        Transfer t => new JsonObject { ["transfer"] = new JsonObject { ["amountMutez"] = t.AmountMutez, ["destination"] = t.Destination } },
        SetDelegate { Delegate: { } d } => new JsonObject { ["setDelegate"] = new JsonObject { ["delegate"] = d } },
        _ => new JsonObject { ["setDelegate"] = new JsonObject() },
    };

    internal static MultisigAction FromJson(JsonNode? node)
    {
        if (node?["transfer"] is JsonObject t)
            return new Transfer(t["amountMutez"]!.GetValue<string>(), t["destination"]!.GetValue<string>());
        if (node?["setDelegate"] is JsonObject d)
            return new SetDelegate(d["delegate"] is JsonValue v ? v.GetValue<string>() : null);
        throw new JsonException("unknown multisig action");
    }
}

/// <summary>
/// An action proposed from a multisig, with the signatures gathered so far. Kept on disk so the
/// user can pick it up again when the other signers send theirs.
/// </summary>
/// <param name="Bytes">The packed payload every signer signs (hex, <c>05…</c>).</param>
public sealed record MultisigProposal(
    string ContractAlias, string ContractAddress, string NetworkName, string ChainID,
    int Counter, int Threshold, IReadOnlyList<string> Keys, MultisigAction Action, string Bytes)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public IReadOnlyList<MultisigSignature> Signatures { get; init; } = [];
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;

    public decimal Amount => Action.Amount;
    public string Summary => Action.Summary;
    public MultisigContract Contract => new(ContractAlias, new Address(ContractAddress));

    /// <summary>Signatures still needed before the proposal can be submitted.</summary>
    public int Missing => Math.Max(0, Threshold - Signatures.Count);
    public bool IsReady => Signatures.Count >= Threshold;

    public bool HasSignature(string publicKey) => Signatures.Any(s => s.PublicKey == publicKey);

    public bool Equals(MultisigProposal? other) =>
        other is not null && Id == other.Id && ContractAlias == other.ContractAlias && ContractAddress == other.ContractAddress &&
        NetworkName == other.NetworkName && ChainID == other.ChainID && Counter == other.Counter && Threshold == other.Threshold &&
        Keys.SequenceEqual(other.Keys) && Action == other.Action && Bytes == other.Bytes &&
        Signatures.SequenceEqual(other.Signatures) && Created == other.Created;
    public override int GetHashCode() => Id.GetHashCode();

    // ---- The proposals file: Swift's JSONEncoder form (sorted keys, ISO 8601 dates, uppercase UUIDs) ----

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["action"] = Action.ToJson(),
            ["bytes"] = Bytes,
            ["chainID"] = ChainID,
            ["contractAddress"] = ContractAddress,
            ["contractAlias"] = ContractAlias,
            ["counter"] = Counter,
            ["created"] = Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["id"] = Id.ToString().ToUpperInvariant(),
            ["keys"] = new JsonArray(Keys.Select(k => (JsonNode?)k).ToArray()),
            ["networkName"] = NetworkName,
            ["signatures"] = new JsonArray(Signatures.Select(s =>
            {
                var o = new JsonObject();
                if (s.PublicKey is not null) o["publicKey"] = s.PublicKey;
                o["signature"] = s.Signature;
                return (JsonNode?)o;
            }).ToArray()),
            ["threshold"] = Threshold,
        };
        return json;
    }

    internal static MultisigProposal FromJson(JsonNode node)
    {
        string S(string key) => node[key]!.GetValue<string>();
        return new MultisigProposal(
            S("contractAlias"), S("contractAddress"), S("networkName"), S("chainID"),
            node["counter"]!.GetValue<int>(), node["threshold"]!.GetValue<int>(),
            node["keys"]!.AsArray().Select(k => k!.GetValue<string>()).ToList(),
            MultisigAction.FromJson(node["action"]), S("bytes"))
        {
            Id = Guid.Parse(S("id")),
            Signatures = (node["signatures"] as JsonArray ?? []).Select(s => new MultisigSignature(
                s!["publicKey"] is JsonValue pk ? pk.GetValue<string>() : null, s["signature"]!.GetValue<string>())).ToList(),
            Created = DateTimeOffset.Parse(S("created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        };
    }
}
