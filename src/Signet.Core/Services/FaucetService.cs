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
///
/// The faucet is an untrusted service (S21): every challenge is checked against fixed bounds
/// before any work is done, the solver checks for cancellation as it goes and gives up at a
/// deadline, and the whole exchange is capped in rounds and elapsed time.
/// </summary>
public sealed class FaucetService(Uri baseURL, Network.FaucetKindValue kind = Network.FaucetKindValue.Teztnets, HttpClient? http = null)
{
    public sealed record Info(string FaucetAddress, bool ChallengesEnabled, double MinTez, double MaxTez);

    public sealed record Challenge(string Value, int Difficulty, int ChallengeCounter, int ChallengesNeeded);

    public abstract class FaucetError(string message) : Exception(message)
    {
        public sealed class Server(string detail) : FaucetError($"Faucet: {detail}");
        public sealed class UnexpectedReply() : FaucetError("Faucet returned an unexpected reply.");
        /// <summary>The faucet asked for something outside the bounds Signet will do.</summary>
        public sealed class UnreasonableChallenge(string why) : FaucetError($"Faucet sent a challenge Signet will not attempt ({why}).");
        public sealed class TooManyRounds() : FaucetError("Faucet kept asking for more challenges than it announced; gave up.");
        public sealed class TimedOut() : FaucetError("Faucet challenges took too long; gave up.");
        public sealed class Cancelled() : FaucetError("Faucet request cancelled.");
        public sealed class InvalidAmount() : FaucetError("Faucet amount is not a valid number of tez.");
    }

    /// <summary>
    /// Bounds on what a faucet may ask for. Difficulty is in leading hex zeros: 8 is already
    /// 2^32 hashes on average, far beyond what any public faucet sets.
    /// </summary>
    public static class Limits
    {
        public const int MinDifficulty = 0, MaxDifficulty = 8;
        public const int MinChallengesNeeded = 1, MaxChallengesNeeded = 1000;
        /// <summary>The teztnets faucet sends 4096-character challenges; allow double that, no more.</summary>
        public const int ChallengeLength = 8192;
        /// <summary>Extra <c>verify</c> rounds tolerated beyond <c>challengesNeeded</c> before giving up.</summary>
        public const int RoundSlack = 5;
        /// <summary>Wall-clock budget for one <c>RequestTez</c>, solving included.</summary>
        public static readonly TimeSpan TotalDuration = TimeSpan.FromMinutes(10);
        /// <summary>Wall-clock budget for one challenge.</summary>
        public static readonly TimeSpan SolveDuration = TimeSpan.FromMinutes(2);
        /// <summary>How often the solver looks at the clock and at cancellation.</summary>
        public const int CheckEvery = 4096;
        public const double MaxTez = 1_000_000;
    }

    /// <summary>Progress while solving: challenges done so far out of the total.</summary>
    public delegate void Progress(int done, int total);

    public Uri BaseURL { get; } = baseURL;
    public Network.FaucetKindValue Kind { get; } = kind;
    private readonly HttpClient http = http ?? Http.Shared;

    /// <summary>Convenience for a network's own faucet, or <c>null</c> where there is none.</summary>
    public static FaucetService? For(Network network, HttpClient? http = null) =>
        network.FaucetURL is { } url ? new FaucetService(url, network.FaucetKind, http) : null;

    public async Task<Info> GetInfo(CancellationToken cancellation = default)
    {
        var data = await Request("info", null, cancellation);
        JsonObject obj;
        try { obj = JsonNode.Parse(data) as JsonObject ?? throw new FaucetError.UnexpectedReply(); }
        catch (JsonException) { throw new FaucetError.UnexpectedReply(); }
        Info info;
        if (Kind == Network.FaucetKindValue.Pqpark)
        {
            // {"address": "tz1…", "balance": 96587895.65, "defaultAmount": 100}; no stated limits.
            var defaultAmount = Num(obj["defaultAmount"]) ?? 100;
            info = new Info(Str(obj["address"]) ?? "", false, 1, Math.Max(defaultAmount * 10, 1000));
        }
        else
        {
            info = new Info(
                Str(obj["faucetAddress"]) ?? throw new FaucetError.UnexpectedReply(),
                obj["challengesEnabled"] is JsonValue ce && ce.TryGetValue<bool>(out var enabled) ? enabled : throw new FaucetError.UnexpectedReply(),
                Num(obj["minTez"]) ?? throw new FaucetError.UnexpectedReply(),
                Num(obj["maxTez"]) ?? throw new FaucetError.UnexpectedReply());
        }
        return Checked(info);
    }

    /// <summary>Limits must be finite, positive and ordered, or the faucet is not worth talking to.</summary>
    public static Info Checked(Info info)
    {
        if (!IsReasonableAmount(info.MinTez) || !IsReasonableAmount(info.MaxTez) || info.MinTez > info.MaxTez)
            throw new FaucetError.UnreasonableChallenge(string.Create(CultureInfo.InvariantCulture, $"limits {info.MinTez}…{info.MaxTez} tez"));
        return info;
    }

    public static bool IsReasonableAmount(double amount) => double.IsFinite(amount) && amount > 0 && amount <= Limits.MaxTez;

    /// <summary>
    /// Asks the faucet to send <c>amount</c> tez to <c>address</c>. Returns the operation hash.
    /// Cancelling <c>cancellation</c> stops both the requests and the proof of work
    /// (<see cref="FaucetError.Cancelled"/> or <see cref="OperationCanceledException"/>).
    /// </summary>
    public async Task<string> RequestTez(Address address, double amount, Progress? progress = null, CancellationToken cancellation = default)
    {
        if (!IsReasonableAmount(amount)) throw new FaucetError.InvalidAmount();
        var deadline = DateTime.UtcNow + Limits.TotalDuration;
        if (Kind == Network.FaucetKindValue.Pqpark)
        {
            var reply = await Request("send", new JsonObject { ["to"] = address.Value, ["amount"] = amount }, cancellation);
            JsonObject? sent = null;
            try { sent = JsonNode.Parse(reply) as JsonObject; } catch (JsonException) { }
            return Str(sent?["hash"]) ?? throw new FaucetError.UnexpectedReply();
        }
        var info = await GetInfo(cancellation);
        amount = Math.Min(Math.Max(amount, info.MinTez), info.MaxTez);
        var payload = new JsonObject { ["address"] = address.Value, ["amount"] = amount };

        if (!info.ChallengesEnabled)
        {
            payload["nonce"] = 0;
            payload["solution"] = "";
            return TxHash(await Request("verify", payload, cancellation));
        }

        var current = ChallengeFrom(await Request("challenge", payload, cancellation));
        var total = current.ChallengesNeeded;
        var rounds = 0;
        while (true)
        {
            if (cancellation.IsCancellationRequested) throw new FaucetError.Cancelled();
            if (DateTime.UtcNow >= deadline) throw new FaucetError.TimedOut();
            rounds += 1;
            if (rounds > total + Limits.RoundSlack) throw new FaucetError.TooManyRounds();
            progress?.Invoke(Math.Min(current.ChallengeCounter - 1, total), total);
            var solveDeadline = DateTime.UtcNow + Limits.SolveDuration;
            var (nonce, solution) = Solve(current, solveDeadline < deadline ? solveDeadline : deadline, cancellation);
            payload["nonce"] = nonce;
            payload["solution"] = solution;
            var reply = await Request("verify", payload, cancellation);
            try
            {
                var hash = TxHash(reply);
                progress?.Invoke(total, total);
                return hash;
            }
            catch (FaucetError) { }
            var next = ChallengeFrom(reply, current);
            current = next with { ChallengesNeeded = total };
        }
    }

    /// <summary>
    /// Finds <c>nonce</c> such that hex(SHA-256("challenge:nonce")) starts with <c>difficulty</c> zeros.
    /// Checks the clock and cancellation every <see cref="Limits.CheckEvery"/> hashes and throws past
    /// <c>deadline</c>.
    /// </summary>
    public static (int Nonce, string Solution) Solve(Challenge challenge, DateTime? deadline = null, CancellationToken cancellation = default)
    {
        Validate(challenge);
        var prefix = new string('0', challenge.Difficulty);
        for (var nonce = 0; ; nonce++)
        {
            if (nonce % Limits.CheckEvery == 0)
            {
                if (cancellation.IsCancellationRequested) throw new FaucetError.Cancelled();
                if (deadline is { } d && DateTime.UtcNow >= d) throw new FaucetError.TimedOut();
            }
            var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge.Value}:{nonce.ToString(CultureInfo.InvariantCulture)}")));
            if (hex.StartsWith(prefix, StringComparison.Ordinal)) return (nonce, hex);
        }
    }

    /// <summary>Refuses anything outside <see cref="Limits"/> before any work is done.</summary>
    public static void Validate(Challenge challenge)
    {
        if (challenge.Difficulty is < Limits.MinDifficulty or > Limits.MaxDifficulty)
            throw new FaucetError.UnreasonableChallenge($"difficulty {challenge.Difficulty}");
        if (challenge.ChallengeCounter < 1)
            throw new FaucetError.UnreasonableChallenge($"counter {challenge.ChallengeCounter}");
        if (challenge.ChallengesNeeded is < Limits.MinChallengesNeeded or > Limits.MaxChallengesNeeded)
            throw new FaucetError.UnreasonableChallenge($"{challenge.ChallengesNeeded} challenges");
        if (challenge.ChallengeCounter > challenge.ChallengesNeeded + Limits.RoundSlack)
            throw new FaucetError.UnreasonableChallenge($"counter {challenge.ChallengeCounter} of {challenge.ChallengesNeeded}");
        var length = Encoding.UTF8.GetByteCount(challenge.Value);
        if (length == 0 || length > Limits.ChallengeLength)
            throw new FaucetError.UnreasonableChallenge($"challenge length {length}");
    }

    // ---- Wire ------------------------------------------------------------------------------------

    private async Task<byte[]> Request(string path, JsonObject? body, CancellationToken cancellation)
    {
        var url = BaseURL.AppendingPathComponent(path);
        var reply = body is null
            ? await Http.Get(http, url, TimeSpan.FromSeconds(20), 64 << 10, BoundedHTTP.JSONLimits.Record, cancellation)
            : await Http.PostJSON(http, url, body.ToJsonString(), TimeSpan.FromSeconds(20), 64 << 10, BoundedHTTP.JSONLimits.Record, cancellation);
        if (!reply.IsSuccess)
        {
            JsonObject? obj = null;
            try { obj = JsonNode.Parse(reply.Body) as JsonObject; } catch (JsonException) { }
            throw new FaucetError.Server(Str(obj?["message"]) ?? Str(obj?["error"]) ?? $"HTTP {reply.Status}");
        }
        return reply.Body;
    }

    /// <summary>
    /// Parses a challenge reply and checks it against <see cref="Limits"/>; with <c>previous</c>, the
    /// counter must have moved forward and the number of challenges must not have grown.
    /// </summary>
    public static Challenge ChallengeFrom(byte[] data, Challenge? previous = null)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        // challengesNeeded is only present on the first reply; later ones carry the rest.
        if (Str(obj?["challenge"]) is not { } challenge || ExactInt(obj?["difficulty"]) is not { } difficulty || ExactInt(obj?["challengeCounter"]) is not { } counter)
            throw new FaucetError.Server(Str(obj?["message"]) ?? "no challenge in reply");
        var needed = ExactInt(obj?["challengesNeeded"]) ?? previous?.ChallengesNeeded ?? counter;
        var parsed = new Challenge(challenge, difficulty, counter, needed);
        Validate(parsed);
        if (previous is not null)
        {
            if (parsed.ChallengeCounter <= previous.ChallengeCounter) throw new FaucetError.UnreasonableChallenge("counter did not advance");
            if (parsed.ChallengesNeeded > previous.ChallengesNeeded) throw new FaucetError.UnreasonableChallenge("more challenges than announced");
        }
        return parsed;
    }

    public static string TxHash(byte[] data)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        return Str(obj?["txHash"]) ?? throw new FaucetError.UnexpectedReply();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var n) ? n : null;

    /// <summary>JSON numbers that are whole and in range; booleans and fractions are not counters.</summary>
    private static int? ExactInt(JsonNode? node)
    {
        if (Num(node) is not { } d || !double.IsFinite(d) || d != Math.Round(d) || Math.Abs(d) >= 1e15) return null;
        // Within ±1e15 but outside int: keep the sign so validation refuses it rather than wrapping.
        return d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;
    }
}
