using System.Globalization;
using Signet.Services;

namespace Signet.Models;

/// <summary>
/// A chain the wallet can talk to and the node it uses for it. RPC URLs come from
/// https://teztnets.com/teztnets.json; Ghostnet was retired, so Shadownet is the long-running
/// public testnet. <c>RpcURL</c> is the node in use; <c>DefaultRPCURL</c> is the one we ship, which the
/// user can override per network in the Networks sheet.
///
/// Not ported yet: Etherlink (the <c>l2…</c> members, <c>Layer2</c>, <c>HasL2</c>) and shielded
/// transactions (<c>shielded…</c>, <c>CanShield</c>).
/// </summary>
public sealed record Network
{
    public enum FaucetKindValue
    {
        /// <summary>tacoinfra/tezos-faucet: <c>/info</c>, <c>/challenge</c> (proof of work), <c>/verify</c>.</summary>
        Teztnets,
        /// <summary>Nomadic Labs' PQPark faucet: <c>/info</c>, then <c>POST /send {to, amount}</c>.</summary>
        Pqpark,
    }

    public Network(string name, string chain, Uri rpcURL, Uri? tezosDomainsURL, Uri? tzktURL, Uri? faucetURL = null,
                   FaucetKindValue faucetKind = FaucetKindValue.Teztnets, Uri? explorerURL = null, string? expectedChainID = null)
    {
        Name = name;
        Chain = chain;
        RpcURL = rpcURL;
        DefaultRPCURL = rpcURL;
        TezosDomainsURL = tezosDomainsURL;
        TzktURL = tzktURL;
        DefaultTzKTURL = tzktURL;
        FaucetURL = chain == "mainnet" ? null : faucetURL;
        DefaultFaucetURL = FaucetURL;
        FaucetKind = faucetKind;
        ExplorerURL = explorerURL;
        DefaultExplorerURL = explorerURL;
        ExpectedChainID = expectedChainID;
    }

    public string Id => Name;

    public string Name { get; }
    /// <summary>Which chain this node serves, e.g. "mainnet" or "shadownet".</summary>
    public string Chain { get; }
    /// <summary>The node in use. Defaults to <c>DefaultRPCURL</c>.</summary>
    public Uri RpcURL { get; init; }
    /// <summary>The node we ship for this network.</summary>
    public Uri DefaultRPCURL { get; }
    /// <summary>Tezos Domains GraphQL endpoint, or <c>null</c> where the service does not run.</summary>
    public Uri? TezosDomainsURL { get; }
    /// <summary>
    /// TzKT indexer base URL (tokens, NFTs, history), or <c>null</c> where none is available.
    /// <c>TzktURL</c> is the one in use, <c>DefaultTzKTURL</c> the one we ship (the Networks sheet lets
    /// the user replace or clear each of the TzKT, explorer and faucet links per network).
    /// </summary>
    public Uri? TzktURL { get; init; }
    public Uri? DefaultTzKTURL { get; }
    /// <summary>Testnet faucet, or <c>null</c> on mainnet (from teztnets.json). Mainnet never has one.</summary>
    public Uri? FaucetURL { get; init; }
    public Uri? DefaultFaucetURL { get; }
    /// <summary>Which faucet protocol <c>FaucetURL</c> speaks.</summary>
    public FaucetKindValue FaucetKind { get; init; }
    /// <summary>Block explorer for this network, used to link operations; <c>null</c> when there is none.</summary>
    public Uri? ExplorerURL { get; init; }
    public Uri? DefaultExplorerURL { get; }
    /// <summary>
    /// The chain id a node for this network must report (<c>/chains/main/chain_id</c>), when the
    /// network is long-lived enough to pin one (S12). The node monitor shows a node serving
    /// another chain in red, and nothing is estimated or signed through it.
    /// </summary>
    public string? ExpectedChainID { get; init; }

    /// <summary>1 for a Tezos network, 2 for its Etherlink counterpart (not ported yet).</summary>
    public int LayerNumber { get; init; } = 1;

    /// <summary>
    /// The links a network uses besides its node: the TzKT API, the block explorer and the
    /// faucet. Each can be replaced or cleared per network in the Networks sheet.
    /// </summary>
    public enum Service { Tzkt, Explorer, Faucet }

    /// <summary>Whether <c>service</c> can be set on this network at all: mainnet has no faucet.</summary>
    public bool Offers(Service service) => service != Service.Faucet || !IsMainnet;

    public Uri? UrlFor(Service service) => service switch
    {
        Service.Tzkt => TzktURL,
        Service.Explorer => ExplorerURL,
        _ => FaucetURL,
    };

    public Uri? DefaultURLFor(Service service) => service switch
    {
        Service.Tzkt => DefaultTzKTURL,
        Service.Explorer => DefaultExplorerURL,
        _ => DefaultFaucetURL,
    };

    public bool IsUsingDefault(Service service) => UrlFor(service) == DefaultURLFor(service);

    /// <summary>
    /// The same network with <c>service</c> pointed at <c>url</c> (<c>null</c> turns it off). A faucet on
    /// mainnet is ignored.
    /// </summary>
    public Network Using(Uri? url, Service service) => service switch
    {
        Service.Tzkt => this with { TzktURL = url },
        Service.Explorer => this with { ExplorerURL = url },
        _ => this with { FaucetURL = Offers(Service.Faucet) ? url : null },
    };

    /// <summary>Where to look an operation up, if this network has an explorer.</summary>
    public Uri? ExplorerURLForOperation(string hash) => ExplorerLink(hash, "op");

    /// <summary>Where to look a contract up, if this network has an explorer.</summary>
    public Uri? ExplorerURLForContract(string address) => ExplorerLink(address, "contract");

    // tzkt.io front ends (mainnet and the testnets) take the hash as a path; self-hosted TzKT
    // front ends use the hash route.
    private Uri? ExplorerLink(string item, string route)
    {
        if (ExplorerURL is null) return null;
        return IsTzKTFrontEnd(ExplorerURL) ? ExplorerURL.AppendingPathComponent(item) : new Uri($"{ExplorerURL.AbsoluteString()}/#{route}/{item}");
    }

    private static bool IsTzKTFrontEnd(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return host == "tzkt.io" || host.EndsWith(".tzkt.io", StringComparison.Ordinal);
    }

    public bool IsMainnet => Chain == "mainnet";
    public bool IsUsingDefaultNode => RpcURL == DefaultRPCURL;

    /// <summary>The same network talking to <c>url</c> instead of its current node.</summary>
    public Network UsingNode(Uri url) => this with { RpcURL = url };

    /// <summary>
    /// Parses what the user typed for a node: an absolute URL with a host, trailing slashes dropped
    /// so paths append cleanly, or <c>null</c> when it is not one. A bare host such as <c>rpc.tzbeta.net</c>
    /// (optionally with a port or path) is taken as HTTPS. Plain <c>http://</c> is accepted only for
    /// this machine and the local network, where there is nothing to intercept (a node you run
    /// yourself); anything further away must be HTTPS. Credentials in the URL are refused (S12).
    /// </summary>
    public static Uri? NodeURL(string text)
    {
        var trimmed = text.Trim().TrimEnd('/');
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        var lower = trimmed.ToLowerInvariant();
        if (!lower.StartsWith("http://", StringComparison.Ordinal) && !lower.StartsWith("https://", StringComparison.Ordinal))
        {
            if (trimmed.Contains("://")) return null;   // some other scheme
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url)) return null;
        var host = url.IdnHost;
        if (host.Length == 0 || url.UserInfo.Length > 0) return null;
        var local = EndpointPolicy.IsLocalOrPrivateHost(host);
        if (!local && !host.Contains('.')) return null;
        if (url.Scheme == "http" && !local) return null;
        return url;
    }

    /// <summary>Why <c>NodeURL</c> refused <c>text</c>, for the Networks sheet.</summary>
    public static string NodeURLProblem(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("http://", StringComparison.Ordinal)) return "Plain http is only allowed for a node on this machine or your local network; use https for anything else.";
        if (trimmed.Contains('@')) return "Leave credentials out of the node URL.";
        return "Enter a host name such as rpc.tzbeta.net.";
    }

    /// <summary>The node as shown to the user: the full URL, e.g. <c>https://rpc.tzbeta.net</c>.</summary>
    public string NodeDisplayText => NodeDisplayTextStandalone(RpcURL);

    public static string NodeDisplayTextStandalone(Uri url) => url.AbsoluteString();

    public static readonly Network Mainnet = new(
        name: "Mainnet",
        chain: "mainnet",
        rpcURL: new Uri("https://rpc.tzbeta.net"),
        tezosDomainsURL: new Uri("https://api.tezos.domains/graphql"),
        tzktURL: new Uri("https://api.tzkt.io"),
        explorerURL: new Uri("https://tzkt.io"),
        expectedChainID: "NetXdQprcVkpaWU");

    public static readonly Network Shadownet = new(
        name: "Shadownet",
        chain: "shadownet",
        rpcURL: new Uri("https://rpc.shadownet.teztnets.com"),
        tezosDomainsURL: null,
        tzktURL: new Uri("https://api.shadownet.tzkt.io"),
        faucetURL: new Uri("https://faucet.shadownet.teztnets.com"),
        explorerURL: new Uri("https://shadownet.tzkt.io"),
        expectedChainID: "NetXsqzbfFenSTS");

    public static readonly Network Bakingnet = new(
        name: "Bakingnet",
        chain: "bakingnet",
        rpcURL: new Uri("https://rpc.bakingnet.teztnets.com"),
        tezosDomainsURL: null,
        tzktURL: new Uri("https://api.bakingnet.tzkt.io"),
        faucetURL: new Uri("https://faucet.bakingnet.teztnets.com"),
        explorerURL: new Uri("https://bakingnet.tzkt.io"),
        expectedChainID: "NetXvNVUNbWHxGt");

    /// <summary>
    /// The protocol-proposal testnet of the day (Ushuaia, October 2026). teztnets.com also
    /// aliases it as "currentnet", but TzKT names its instance after the protocol, so the entry
    /// is the protocol's own: when the next proposal's network appears, replace this one.
    /// </summary>
    public static readonly Network Ushuaianet = new(
        name: "Ushuaianet",
        chain: "ushuaianet",
        rpcURL: new Uri("https://rpc.ushuaianet.teztnets.com"),
        tezosDomainsURL: null,
        tzktURL: new Uri("https://api.ushuaianet.tzkt.io"),
        faucetURL: new Uri("https://faucet.ushuaianet.teztnets.com"),
        explorerURL: new Uri("https://ushuaianet.tzkt.io"),
        expectedChainID: "NetXpX8WSZkAZZA");

    /// <summary>
    /// Weeklynet restarts every Wednesday from tezos/tezos master and its hosts carry the launch
    /// date (<c>rpc.weeklynet-YYYY-MM-DD.teztnets.com</c>); there is no dated-free alias, so the URL
    /// is built from the most recent Wednesday (UTC). Set a node in Networks to pin another.
    /// </summary>
    public static Network Weeklynet => WeeklynetOn(DateTimeOffset.UtcNow);

    public static Network WeeklynetOn(DateTimeOffset date)
    {
        var day = date.UtcDateTime.Date;
        var daysSinceWednesday = ((int)day.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        var stamp = day.AddDays(-daysSinceWednesday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Network(
            name: "Weeklynet",
            chain: "weeklynet",
            rpcURL: new Uri($"https://rpc.weeklynet-{stamp}.teztnets.com"),
            tezosDomainsURL: null,
            tzktURL: null,
            faucetURL: new Uri($"https://faucet.weeklynet-{stamp}.teztnets.com"));
    }

    /// <summary>
    /// Nomadic Labs' post-quantum testnet (tz5 enabled, 6 s blocks). Not on teztnets.com: its node,
    /// TzKT and faucet live under pqpark.dal.nomadic-labs.com, and the faucet is PQPark's own.
    /// </summary>
    public static readonly Network Quantumnet = new(
        name: "Quantumnet",
        chain: "quantumnet",
        rpcURL: new Uri("https://quantumnet.pqpark.dal.nomadic-labs.com/rpc"),
        tezosDomainsURL: null,
        tzktURL: new Uri("https://quantumnet-tzkt.pqpark.dal.nomadic-labs.com"),
        faucetURL: new Uri("https://quantumnet-faucet.pqpark.dal.nomadic-labs.com/api"),
        faucetKind: FaucetKindValue.Pqpark,
        explorerURL: new Uri("https://quantumnet-tzkt.pqpark.dal.nomadic-labs.com"));

    /// <summary>A network of the user's own: any node they point it at. Defaults to a local node.</summary>
    public static readonly Network Custom = new(
        name: "Custom",
        chain: "custom",
        rpcURL: new Uri("http://localhost:8732"),
        tezosDomainsURL: null,
        tzktURL: null);

    /// <summary>Networks offered in the Networks sheet and the badge, in display order, each with its default node.</summary>
    public static IReadOnlyList<Network> All => [Mainnet, Shadownet, Bakingnet, Ushuaianet, Weeklynet, Quantumnet, Custom];

    public static Network? Named(string? name) => All.FirstOrDefault(n => n.Name == name);
}
