using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// What Signet asks a remote signer that speaks octez-signer's protocol (octez-signer itself,
/// Signatory, Russignol and others): its keys, a key's public key, whether it wants requests
/// authenticated, a signature, and a tz4 key's proof of possession. HTTP(S) speaks the signer's
/// JSON RPC (<c>signer_services.ml</c>); TCP and Unix sockets its binary messages (<c>SignerWire</c>).
/// Nothing secret is ever sent: what is signed is public once injected, and every signature is
/// checked against the account's key before it is used.
/// </summary>
public interface IRemoteSignerService
{
    /// <summary>The keys the signer holds (<c>known_keys</c>; older signers do not answer it).</summary>
    Task<IReadOnlyList<Address>> KnownKeys(RemoteSignerLocator signer);
    /// <summary>The base58 public key of the locator's key.</summary>
    Task<string> PublicKey(RemoteSignerLocator signer);
    /// <summary><c>null</c> when the signer accepts unauthenticated requests, else the keys it wants a request signed by.</summary>
    Task<IReadOnlyList<Address>?> AuthorizedKeys(RemoteSignerLocator signer);
    Task<RemoteSignature> Sign(byte[] bytes, RemoteSignerLocator signer);
    /// <summary>A BLS proof of possession (<c>BLsig…</c>) for the locator's tz4 key.</summary>
    Task<string> ProvePossession(RemoteSignerLocator signer);
}

/// <summary>
/// A signature as the signer gave it: base58 over HTTP (often the generic <c>sig…</c> form), raw
/// bytes over a socket. The bridge turns either into the right form for the key.
/// </summary>
public abstract record RemoteSignature
{
    public sealed record Base58Signature(string Text) : RemoteSignature;
    public sealed record Raw(byte[] Bytes) : RemoteSignature
    {
        public bool Equals(Raw? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);
        public override int GetHashCode() => Bytes.Length;
    }

    public string BridgeJSON => this switch
    {
        Base58Signature b => new JsonObject { ["signature"] = b.Text }.ToJsonString(),
        Raw r => new JsonObject { ["raw"] = Convert.ToHexStringLower(r.Bytes) }.ToJsonString(),
        _ => throw new InvalidOperationException(),
    };
}

public abstract class RemoteSignerError(string message) : Exception(message), IEquatable<RemoteSignerError>
{
    public sealed class Unreachable(string detail) : RemoteSignerError($"Signet could not reach the remote signer: {detail}");
    /// <summary>The signer hung up without answering: what an octez-signer does with a request it cannot decode.</summary>
    public sealed class Closed() : RemoteSignerError("The remote signer closed the connection without answering; it may not understand this request (an older signer?).");
    public sealed class TimedOut() : RemoteSignerError("The remote signer did not answer in time.");
    public sealed class AuthenticationRequired()
        : RemoteSignerError("The remote signer only accepts authenticated requests (octez-signer: --require-authentication). Signet cannot authenticate to a signer yet.");
    public sealed class Refused(string? reason) : RemoteSignerError("The remote signer refused the request" + (reason is null ? "." : $": {reason}"))
    {
        public string? Reason { get; } = reason;
    }
    /// <summary>A request the signer answered with an error: which, to which signer, for which key, and its reason.</summary>
    public sealed class Declined(RequestKind request, string signer, Address key, string? reason)
        : RemoteSignerError(Describe(request, signer, key, reason?.Trim('.', ' ')));
    public sealed class NotFound() : RemoteSignerError("The remote signer does not answer that request (it may be an older signer, or one that does not offer it).");
    public sealed class MalformedReply() : RemoteSignerError("The remote signer's reply could not be read.");
    public sealed class Unsupported(string what) : RemoteSignerError(what);
    /// <summary>The signer's public key for this address hashes to another address.</summary>
    public sealed class KeyMismatch(Address expected, Address found)
        : RemoteSignerError($"The signer's public key for {expected.Value} belongs to {found.Value}. Signet will not use it.");

    public enum RequestKind { ListKeys, PublicKey, Authorization, Sign, ProveIt }

    /// <summary>
    /// Octez 23 moved BLS signing to the proof-of-possession scheme the chain checks today, and names
    /// a tz4 key in a request with its signature version; older signers do neither.
    /// </summary>
    public const string Tz4NeedsRecentSigner = "This signer does not understand a signing request for a tz4 key. tz4 keys need a signer that makes the BLS signatures the chain accepts today (for octez-signer, version 23 or later).";

    /// <summary>What to tell the user when a signer says no: what was asked of whom, the signer's words, and the usual cause.</summary>
    public static string Describe(RequestKind request, string signer, Address key, string? reason)
    {
        var said = reason is null ? " The signer gave no reason." : $" The signer said: “{reason}.”";
        var notAllowed = reason?.Contains("not allowed", StringComparison.OrdinalIgnoreCase) == true;
        return request switch
        {
            RequestKind.ListKeys => $"The signer at {signer} will not list its keys.{said}"
                + (notAllowed ? " Many signers list keys only when configured to (octez-signer: --allow-list-known-keys)." : "")
                + " Type the key's address instead; the signer's own configuration or key list shows it.",
            RequestKind.PublicKey => $"The signer at {signer} did not give the public key for {key.Value}.{said} Check that the signer holds this key and is set up to use it.",
            RequestKind.Authorization => $"The signer at {signer} would not say whether it needs authentication.{said}",
            RequestKind.Sign => $"The signer at {signer} refused to sign with {key.Value}.{said} A signer refuses what its policy forbids, such as an operation kind or magic byte it does not allow, or a level below its high watermark. Nothing was sent.",
            _ => $"The signer at {signer} refused to make a proof of possession for {key.Value}.{said}",
        };
    }

    public bool Equals(RemoteSignerError? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
    public override bool Equals(object? obj) => Equals(obj as RemoteSignerError);
    public override int GetHashCode() => HashCode.Combine(GetType(), Message);
}

public sealed class NetworkRemoteSignerService : IRemoteSignerService
{
    /// <summary>Long enough for a signer that waits on a person (a Ledger behind octez-signer).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>The most a reply may carry: a public key list, a key, a signature.</summary>
    public const int ReplyLimit = 256 << 10;

    /// <summary>
    /// Windows: upstream makes an ephemeral URLSession per request. Here one client without cookies,
    /// proxy or redirects; local and private hosts are allowed (a signer usually is one).
    /// </summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private async Task<IReadOnlyList<Address>> ListKeys(RemoteSignerLocator signer)
    {
        if (IsHTTP(signer))
        {
            var obj = await GetJSON(signer, "/known_keys");
            if (obj["known_keys"] is not JsonArray list) throw new RemoteSignerError.MalformedReply();
            return list.Select(n => Str(n) is { } s ? new Address(s) : (Address?)null).OfType<Address>().Where(a => a.IsImplicitAccount).ToList();
        }
        return SignerWire.AddressesFromValue(SignerWire.Value((await Exchange(signer, [SignerWire.KnownKeysRequest]))[^1]));
    }

    private async Task<string> FetchPublicKey(RemoteSignerLocator signer)
    {
        if (IsHTTP(signer))
        {
            var obj = await GetJSON(signer, $"/keys/{signer.Address.Value}");
            return Str(obj["public_key"]) ?? throw new RemoteSignerError.MalformedReply();
        }
        var request = SignerWire.PublicKeyRequest(signer.Address) ?? throw new RemoteSignerError.MalformedReply();
        return SignerWire.PublicKeyFromValue(SignerWire.Value((await Exchange(signer, [request]))[^1]));
    }

    private async Task<IReadOnlyList<Address>?> FetchAuthorizedKeys(RemoteSignerLocator signer)
    {
        if (IsHTTP(signer))
        {
            var obj = await GetJSON(signer, "/authorized_keys");
            if (!obj.TryGetPropertyValue("authorized_keys", out var list) || list is null) return null;
            if (list is not JsonArray keys) throw new RemoteSignerError.MalformedReply();
            return keys.Select(n => new Address(Str(n) ?? throw new RemoteSignerError.MalformedReply())).ToList();
        }
        return SignerWire.AuthorizedKeysFromValue(SignerWire.Value((await Exchange(signer, [SignerWire.AuthorizedKeysRequest]))[^1]));
    }

    private async Task<RemoteSignature> SignBytes(byte[] bytes, RemoteSignerLocator signer)
    {
        if (IsHTTP(signer))
        {
            if (await FetchAuthorizedKeys(signer) is not null) throw new RemoteSignerError.AuthenticationRequired();
            var obj = await RequestJSON(signer, $"/keys/{signer.Address.Value}", HttpMethod.Post, JsonSerializer.Serialize(Convert.ToHexStringLower(bytes)));
            return new RemoteSignature.Base58Signature(Str(obj["signature"]) ?? throw new RemoteSignerError.MalformedReply());
        }
        // As octez-client does: ask whether authentication is needed, then sign, on one connection.
        var request = SignerWire.SignRequest(signer.Address, bytes) ?? throw new RemoteSignerError.MalformedReply();
        IReadOnlyList<byte[]> replies;
        try
        {
            replies = await Exchange(signer, [SignerWire.AuthorizedKeysRequest, request], (index, reply) =>
            {
                if (index == 0 && SignerWire.AuthorizedKeysFromValue(SignerWire.Value(reply)) is not null) throw new RemoteSignerError.AuthenticationRequired();
            });
        }
        catch (RemoteSignerError.Closed) when (signer.Address.Scheme == AddressScheme.Tz4)
        {
            throw new RemoteSignerError.Unsupported(RemoteSignerError.Tz4NeedsRecentSigner);
        }
        var signature = SignerWire.Value(replies[1]);
        if (signature.Length is not (64 or 96 or 2420)) throw new RemoteSignerError.MalformedReply();
        return new RemoteSignature.Raw(signature);
    }

    private async Task<string> Prove(RemoteSignerLocator signer)
    {
        if (signer.Address.Scheme != AddressScheme.Tz4) throw new RemoteSignerError.Unsupported("Only a tz4 key has a proof of possession.");
        if (IsHTTP(signer))
        {
            var obj = await GetJSON(signer, $"/bls_prove_possession/{signer.Address.Value}");
            return Str(obj["bls_prove_possession"]) is { } proof && proof.StartsWith("BLsig", StringComparison.Ordinal) ? proof : throw new RemoteSignerError.MalformedReply();
        }
        var request = SignerWire.ProvePossessionRequest(signer.Address) ?? throw new RemoteSignerError.MalformedReply();
        var value = SignerWire.Value((await Exchange(signer, [request]))[^1]);
        if (value.Length != 96) throw new RemoteSignerError.MalformedReply();
        return Base58.CheckEncode([40, 171, 64, 207], value);
    }

    // Every refusal names the signer, the request and the key, with the signer's own reason.
    public Task<IReadOnlyList<Address>> KnownKeys(RemoteSignerLocator signer) => Explaining(RemoteSignerError.RequestKind.ListKeys, signer, () => ListKeys(signer));
    public Task<string> PublicKey(RemoteSignerLocator signer) => Explaining(RemoteSignerError.RequestKind.PublicKey, signer, () => FetchPublicKey(signer));
    public Task<IReadOnlyList<Address>?> AuthorizedKeys(RemoteSignerLocator signer) => Explaining(RemoteSignerError.RequestKind.Authorization, signer, () => FetchAuthorizedKeys(signer));
    public Task<RemoteSignature> Sign(byte[] bytes, RemoteSignerLocator signer) => Explaining(RemoteSignerError.RequestKind.Sign, signer, () => SignBytes(bytes, signer));
    public Task<string> ProvePossession(RemoteSignerLocator signer) => Explaining(RemoteSignerError.RequestKind.ProveIt, signer, () => Prove(signer));

    private static async Task<T> Explaining<T>(RemoteSignerError.RequestKind request, RemoteSignerLocator signer, Func<Task<T>> work)
    {
        try { return await work(); }
        catch (SignerWire.Failure.Refused e) { throw new RemoteSignerError.Declined(request, signer.Endpoint, signer.Address, e.Reason); }
        catch (RemoteSignerError.Refused e) { throw new RemoteSignerError.Declined(request, signer.Endpoint, signer.Address, e.Reason); }
        catch (SignerWire.Failure.Malformed) { throw new RemoteSignerError.MalformedReply(); }
    }

    private static bool IsHTTP(RemoteSignerLocator signer) => signer.Transport is RemoteSignerLocator.TransportKind.Http or RemoteSignerLocator.TransportKind.Https;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- HTTP ------------------------------------------------------------------------------------

    private Task<JsonObject> GetJSON(RemoteSignerLocator signer, string path) => RequestJSON(signer, path, HttpMethod.Get, null);

    private async Task<JsonObject> RequestJSON(RemoteSignerLocator signer, string path, HttpMethod method, string? body)
    {
        if (signer.BaseURL is not { } baseURL || !Uri.TryCreate(baseURL.AbsoluteString() + path, UriKind.Absolute, out var url))
            throw new RemoteSignerError.Unreachable("the signer's address is not a valid URL");
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        Http.Reply reply;
        try
        {
            reply = await Http.Send(Client, request, Timeout, ReplyLimit, BoundedHTTP.JSONLimits.Record);
        }
        catch (TimeoutException) { throw new RemoteSignerError.TimedOut(); }
        catch (BoundedHTTP.Failure) { throw new RemoteSignerError.MalformedReply(); }
        catch (HttpRequestException e) { throw new RemoteSignerError.Unreachable(e.InnerException?.Message ?? e.Message); }
        if (!reply.IsSuccess)
        {
            if (reply.Status == 404) throw new RemoteSignerError.NotFound();
            JsonObject? first = null;
            try { first = (JsonNode.Parse(reply.Body) as JsonArray)?.FirstOrDefault() as JsonObject; }
            catch (JsonException) { }
            var id = Str(first?["id"]);
            if (id is not null && (id.Contains("authentication") || id.Contains("unauthorized"))) throw new RemoteSignerError.AuthenticationRequired();
            throw new RemoteSignerError.Refused(Str(first?["msg"]) ?? id ?? $"HTTP {reply.Status}");
        }
        try { return JsonNode.Parse(reply.Body) as JsonObject ?? throw new RemoteSignerError.MalformedReply(); }
        catch (JsonException) { throw new RemoteSignerError.MalformedReply(); }
    }

    // ---- Sockets ---------------------------------------------------------------------------------

    /// <summary>
    /// Sends <c>requests</c> one after another on one connection and returns each reply's payload.
    /// <c>check</c> sees every reply as it arrives and may stop the exchange by throwing.
    /// </summary>
    private async Task<IReadOnlyList<byte[]>> Exchange(RemoteSignerLocator signer, byte[][] requests, Action<int, byte[]>? check = null)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        Socket socket;
        EndPoint endpoint;
        switch (signer.Transport)
        {
            case RemoteSignerLocator.TransportKind.Tcp:
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                endpoint = new DnsEndPoint(signer.Host, signer.Port);
                break;
            case RemoteSignerLocator.TransportKind.Unix:
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                endpoint = new UnixDomainSocketEndPoint(signer.Path);
                break;
            default:
                throw new RemoteSignerError.Unreachable("not a socket signer");
        }
        using (socket)
        {
            try
            {
                await socket.ConnectAsync(endpoint, deadline.Token);
                var replies = new List<byte[]>();
                for (var index = 0; index < requests.Length; index++)
                {
                    var frame = SignerWire.Frame(requests[index]) ?? throw new RemoteSignerError.MalformedReply();
                    await socket.SendAsync(frame, SocketFlags.None, deadline.Token);
                    var header = await Receive(socket, 2, deadline.Token);
                    var reply = await Receive(socket, header[0] << 8 | header[1], deadline.Token);
                    check?.Invoke(index, reply);
                    replies.Add(reply);
                }
                return replies;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw new RemoteSignerError.TimedOut(); }
            catch (SocketException e) { throw new RemoteSignerError.Unreachable(Describe(e)); }
        }
    }

    private static async Task<byte[]> Receive(Socket socket, int count, CancellationToken cancel)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancel);
            if (n == 0) throw new RemoteSignerError.Closed();
            read += n;
        }
        return buffer;
    }

    private static string Describe(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "nothing is listening there (connection refused)",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "the host name could not be found",
        _ => e.Message,
    };
}

/// <summary>For tests and previews: a signer holding the given keys that signs with <c>Signature</c>.</summary>
public sealed class MockRemoteSignerService(IReadOnlyDictionary<Address, string>? keys = null, bool requiresAuthentication = false,
                                            Func<byte[], Address, RemoteSignature>? signature = null) : IRemoteSignerService
{
    public IReadOnlyDictionary<Address, string> Keys { get; } = keys ?? new Dictionary<Address, string>();
    public bool RequiresAuthentication { get; } = requiresAuthentication;
    private readonly Func<byte[], Address, RemoteSignature> signature = signature ?? ((_, _) => new RemoteSignature.Raw(new byte[64]));

    public Task<IReadOnlyList<Address>> KnownKeys(RemoteSignerLocator signer) =>
        Task.FromResult<IReadOnlyList<Address>>(Keys.Keys.OrderBy(a => a.Value, StringComparer.Ordinal).ToList());

    public Task<string> PublicKey(RemoteSignerLocator signer) =>
        Keys.TryGetValue(signer.Address, out var key) ? Task.FromResult(key) : Task.FromException<string>(new RemoteSignerError.Refused("unknown_key"));

    public Task<IReadOnlyList<Address>?> AuthorizedKeys(RemoteSignerLocator signer) =>
        Task.FromResult<IReadOnlyList<Address>?>(RequiresAuthentication ? [] : null);

    public Task<RemoteSignature> Sign(byte[] bytes, RemoteSignerLocator signer) =>
        RequiresAuthentication ? Task.FromException<RemoteSignature>(new RemoteSignerError.AuthenticationRequired()) : Task.FromResult(signature(bytes, signer.Address));

    public Task<string> ProvePossession(RemoteSignerLocator signer) => Task.FromResult("BLsigMockProof");
}

/// <summary>
/// The bridge's way to a remote signer: <c>{op: "sign", locator, address, bytes}</c> or
/// <c>{op: "prove", locator, address}</c>, answered with <c>RemoteSignature.BridgeJSON</c> or
/// <c>{"proof": "BLsig…"}</c>. The locator is the one the app put in the signing key.
/// </summary>
public static class RemoteSigners
{
    public static IRemoteSignerService Service { get; } = new NetworkRemoteSignerService();

    public static async Task<string> Answer(string requestJSON, IRemoteSignerService? service = null)
    {
        service ??= Service;
        string? op, locator, address, hex;
        try
        {
            if (JsonNode.Parse(requestJSON) is not JsonObject obj) throw new RemoteSignerError.MalformedReply();
            string? S(string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            (op, locator, address, hex) = (S("op"), S("locator"), S("address"), S("bytes"));
        }
        catch (JsonException) { throw new RemoteSignerError.MalformedReply(); }
        if (op is null || locator is null || RemoteSignerLocator.FromLocator(locator) is not { } signer || signer.Address.Value != address)
            throw new RemoteSignerError.MalformedReply();
        switch (op)
        {
            case "sign":
                if (hex is null || Hex.Bytes(hex) is not { Length: > 0 } bytes) throw new RemoteSignerError.MalformedReply();
                return (await service.Sign(bytes, signer)).BridgeJSON;
            case "prove":
                return new JsonObject { ["proof"] = await service.ProvePossession(signer) }.ToJsonString();
            default:
                throw new RemoteSignerError.MalformedReply();
        }
    }
}
