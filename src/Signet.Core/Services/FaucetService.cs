using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Client for the teztnets faucets (github.com/tacoinfra/tezos-faucet), the same HTTP API the
/// <c>get-tez</c> command line uses: request a proof-of-work challenge, solve it, verify, repeat until
/// the faucet returns the transaction hash. No captcha is needed on this path.
/// </summary>
public sealed class FaucetService(Uri baseURL, Network.FaucetKindValue kind = Network.FaucetKindValue.Teztnets, HttpClient? http = null)
{
    public sealed record Info(string FaucetAddress, bool ChallengesEnabled, double MinTez, double MaxTez);

    public sealed record Challenge(string Value, int Difficulty, int ChallengeCounter, int ChallengesNeeded);

    public abstract class FaucetError(string message) : Exception(message)
    {
        public sealed class Server(string detail) : FaucetError($"Faucet: {detail}");
        public sealed class UnexpectedReply() : FaucetError("Faucet returned an unexpected reply.");
    }

    /// <summary>Progress while solving: challenges done so far out of the total.</summary>
    public delegate void Progress(int done, int total);

    public Uri BaseURL { get; } = baseURL;
    public Network.FaucetKindValue Kind { get; } = kind;
    private readonly HttpClient http = http ?? Http.Shared;

    /// <summary>Convenience for a network's own faucet, or <c>null</c> where there is none.</summary>
    public static FaucetService? For(Network network, HttpClient? http = null) =>
        network.FaucetURL is { } url ? new FaucetService(url, network.FaucetKind, http) : null;

    public async Task<Info> GetInfo()
    {
        var data = await Request("info", null);
        var obj = JsonNode.Parse(data) as JsonObject ?? throw new FaucetError.UnexpectedReply();
        if (Kind == Network.FaucetKindValue.Pqpark)
        {
            // {"address": "tz1…", "balance": 96587895.65, "defaultAmount": 100}; no stated limits.
            var defaultAmount = Num(obj["defaultAmount"]) ?? 100;
            return new Info(Str(obj["address"]) ?? "", false, 1, Math.Max(defaultAmount * 10, 1000));
        }
        return new Info(
            Str(obj["faucetAddress"]) ?? throw new FaucetError.UnexpectedReply(),
            obj["challengesEnabled"] is JsonValue ce && ce.TryGetValue<bool>(out var enabled) ? enabled : throw new FaucetError.UnexpectedReply(),
            Num(obj["minTez"]) ?? throw new FaucetError.UnexpectedReply(),
            Num(obj["maxTez"]) ?? throw new FaucetError.UnexpectedReply());
    }

    /// <summary>Asks the faucet to send <c>amount</c> tez to <c>address</c>. Returns the operation hash.</summary>
    public async Task<string> RequestTez(Address address, double amount, Progress? progress = null)
    {
        if (Kind == Network.FaucetKindValue.Pqpark)
        {
            var reply = await Request("send", new JsonObject { ["to"] = address.Value, ["amount"] = amount });
            return Str((JsonNode.Parse(reply) as JsonObject)?["hash"]) ?? throw new FaucetError.UnexpectedReply();
        }
        var info = await GetInfo();
        amount = Math.Min(Math.Max(amount, info.MinTez), info.MaxTez);
        var payload = new JsonObject { ["address"] = address.Value, ["amount"] = amount };

        if (!info.ChallengesEnabled)
        {
            payload["nonce"] = 0;
            payload["solution"] = "";
            return TxHash(await Request("verify", payload));
        }

        var current = ChallengeFrom(await Request("challenge", payload));
        while (true)
        {
            progress?.Invoke(current.ChallengeCounter - 1, current.ChallengesNeeded);
            var (nonce, solution) = Solve(current);
            payload["nonce"] = nonce;
            payload["solution"] = solution;
            var reply = await Request("verify", payload);
            try
            {
                var hash = TxHash(reply);
                progress?.Invoke(current.ChallengesNeeded, current.ChallengesNeeded);
                return hash;
            }
            catch (FaucetError) { }
            var next = ChallengeFrom(reply);
            current = next with { ChallengesNeeded = current.ChallengesNeeded };
        }
    }

    /// <summary>Finds <c>nonce</c> such that hex(SHA-256("challenge:nonce")) starts with <c>difficulty</c> zeros.</summary>
    public static (int Nonce, string Solution) Solve(Challenge challenge)
    {
        var prefix = new string('0', challenge.Difficulty);
        for (var nonce = 0; ; nonce++)
        {
            var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge.Value}:{nonce.ToString(CultureInfo.InvariantCulture)}"))).ToLowerInvariant();
            if (hex.StartsWith(prefix, StringComparison.Ordinal)) return (nonce, hex);
        }
    }

    // ---- Wire ------------------------------------------------------------------------------------

    private async Task<byte[]> Request(string path, JsonObject? body)
    {
        var url = BaseURL.AppendingPathComponent(path);
        var reply = body is null
            ? await Http.Get(http, url, TimeSpan.FromSeconds(20))
            : await Http.PostJSON(http, url, body.ToJsonString(), TimeSpan.FromSeconds(20));
        if (!reply.IsSuccess)
        {
            JsonObject? obj = null;
            try { obj = JsonNode.Parse(reply.Body) as JsonObject; } catch (JsonException) { }
            throw new FaucetError.Server(Str(obj?["message"]) ?? Str(obj?["error"]) ?? $"HTTP {reply.Status}");
        }
        return reply.Body;
    }

    public static Challenge ChallengeFrom(byte[] data)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        // challengesNeeded is only present on the first reply; later ones carry the rest.
        if (Str(obj?["challenge"]) is not { } challenge || Int(obj?["difficulty"]) is not { } difficulty || Int(obj?["challengeCounter"]) is not { } counter)
            throw new FaucetError.Server(Str(obj?["message"]) ?? "no challenge in reply");
        return new Challenge(challenge, difficulty, counter, Int(obj?["challengesNeeded"]) ?? counter);
    }

    public static string TxHash(byte[] data)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        return Str(obj?["txHash"]) ?? throw new FaucetError.UnexpectedReply();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var n) ? n : null;
}
