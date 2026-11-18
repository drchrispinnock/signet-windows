using System.Text;
using System.Text.RegularExpressions;

namespace Signet.Models;

/// <summary>
/// Where an account's key lives when a remote signer (octez-signer, Signatory, Russignol…) holds
/// it: the <c>secret_keys</c> locator octez-client writes for a remote signer,
/// <c>http://host:port/tz1…</c>, <c>https://host:port/tz1…</c>, <c>tcp://host:port/tz1…</c> or
/// <c>unix:/path?pkh=tz1…</c>. Signet never sees the secret; it sends the bytes to sign and checks
/// the signature that comes back against the account's public key.
/// </summary>
/// <param name="Path">The URL path in front of <c>/keys/…</c> for HTTP(S) (usually empty); the socket's path for Unix.</param>
public sealed record RemoteSignerLocator(RemoteSignerLocator.TransportKind Transport, string Host, int Port, string Path, Address Address)
{
    public enum TransportKind { Http, Https, Tcp, Unix }

    public RemoteSignerLocator(TransportKind transport, string host, int port, Address address) : this(transport, host, port, "", address) { }

    /// <summary>
    /// Reads octez-client's locator. <c>remote:tz1…</c> needs the client's configured signer and is
    /// not read.
    /// </summary>
    public static RemoteSignerLocator? FromLocator(string locator)
    {
        if (locator.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = locator["unix:".Length..];
            var question = rest.IndexOf('?');
            if (question < 0) return null;
            var path = rest[..question];
            var pkh = rest[(question + 1)..].Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "pkh" && p.Length == 2)?[1];
            if (pkh is null || path.Length == 0 || !new Address(pkh).IsImplicitAccount) return null;
            return new RemoteSignerLocator(TransportKind.Unix, "", 0, path, new Address(pkh));
        }
        var schemeEnd = locator.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || TransportFromRawValue(locator[..schemeEnd].ToLowerInvariant()) is not { } transport || transport == TransportKind.Unix) return null;
        if (!Uri.TryCreate(locator, UriKind.Absolute, out var url)) return null;
        var host = url.Host.Trim('[', ']');
        if (host.Length == 0 || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0) return null;
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segments.Count == 0 || !new Address(segments[^1]).IsImplicitAccount) return null;
        var last = segments[^1];
        segments.RemoveAt(segments.Count - 1);
        // .NET fills in a scheme's default port, so whether one was given is read from the text.
        var authority = locator[(schemeEnd + 3)..].Split('/')[0];
        var givenPort = authority.LastIndexOf(':') is var colon and >= 0 && !authority.EndsWith(']') && colon > authority.LastIndexOf(']') ? url.Port : (int?)null;
        int port;
        switch (transport)
        {
            case TransportKind.Tcp:
                if (givenPort is not { } given || segments.Count > 0) return null;
                port = given;
                break;
            case TransportKind.Http: port = givenPort ?? 80; break;
            default: port = givenPort ?? 443; break;
        }
        if (port is < 1 or > 65535) return null;
        return new RemoteSignerLocator(transport, host, port, segments.Count == 0 ? "" : "/" + string.Join('/', segments), new Address(last));
    }

    public static TransportKind? TransportFromRawValue(string raw) => raw switch
    {
        "http" => TransportKind.Http, "https" => TransportKind.Https, "tcp" => TransportKind.Tcp, "unix" => TransportKind.Unix, _ => null,
    };

    /// <summary>The host as it goes in a URL: an IPv6 literal in brackets.</summary>
    private string UrlHost => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    /// <summary>The signer itself, without the key: <c>tcp://localhost:7732</c>.</summary>
    public string Endpoint => Transport == TransportKind.Unix ? $"unix:{Path}" : $"{Transport.RawValue()}://{UrlHost}:{Port}{Path}";

    /// <summary>The <c>secret_keys</c> (and <c>public_keys</c>) locator octez-client reads.</summary>
    public string Locator => Transport == TransportKind.Unix ? $"unix:{Path}?pkh={Address.Value}" : $"{Endpoint}/{Address.Value}";

    /// <summary>The HTTP(S) signer's root, in front of <c>/keys/…</c>.</summary>
    public Uri? BaseURL => Transport is TransportKind.Http or TransportKind.Https && Uri.TryCreate(Endpoint, UriKind.Absolute, out var url) ? url : null;

    /// <summary>The same signer for another of its keys.</summary>
    public RemoteSignerLocator WithAddress(Address address) => this with { Address = address };
}

public static class RemoteSignerTransportExtensions
{
    public static string RawValue(this RemoteSignerLocator.TransportKind transport) => transport switch
    {
        RemoteSignerLocator.TransportKind.Http => "http",
        RemoteSignerLocator.TransportKind.Https => "https",
        RemoteSignerLocator.TransportKind.Tcp => "tcp",
        _ => "unix",
    };

    /// <summary>octez-signer's own defaults (<c>main_signer.ml</c>): HTTP 6732, HTTPS 443, TCP 7732.</summary>
    public static int? DefaultPort(this RemoteSignerLocator.TransportKind transport) => transport switch
    {
        RemoteSignerLocator.TransportKind.Http => 6732,
        RemoteSignerLocator.TransportKind.Https => 443,
        RemoteSignerLocator.TransportKind.Tcp => 7732,
        _ => null,
    };

    public static string Title(this RemoteSignerLocator.TransportKind transport) => transport switch
    {
        RemoteSignerLocator.TransportKind.Http => "HTTP",
        RemoteSignerLocator.TransportKind.Https => "HTTPS",
        RemoteSignerLocator.TransportKind.Tcp => "TCP",
        _ => "Unix socket",
    };
}

/// <summary>
/// octez-signer's binary protocol over TCP and Unix sockets (<c>signer_messages.ml</c>): every
/// message is a 2-byte big-endian length and a Data_encoding payload; a request is a one-byte kind
/// and its fields, a reply is <c>00</c> and the value or <c>01</c> and an error trace.
/// </summary>
public static class SignerWire
{
    /// <summary>Base58 prefixes of tz1…tz5 in the order of the signer's key hash tags 0…4.</summary>
    public static readonly byte[][] AddressPrefixes = [[6, 161, 159], [6, 161, 161], [6, 161, 164], [6, 161, 166], [6, 161, 169]];
    /// <summary>Public key prefixes and sizes by tag: edpk, sppk, p2pk, BLpk, mdpk.</summary>
    public static readonly (byte[] Prefix, int Size)[] PublicKeyFormats =
    [
        ([13, 15, 37, 217], 32), ([3, 254, 226, 86], 33), ([3, 178, 139, 127], 33), ([6, 149, 135, 204], 48), ([13, 7, 237, 67], 1312),
    ];
    /// <summary>The signature version tz4 requests name (<c>Signature.V_latest</c>, version 3).</summary>
    public const byte BlsSignatureVersion = 3;

    public abstract class Failure(string message) : Exception(message), IEquatable<Failure>
    {
        public sealed class Malformed() : Failure("The remote signer's reply could not be read.");
        public sealed class Refused(string? reason) : Failure("The remote signer refused" + (reason is null ? "." : $": {reason}"))
        {
            public string? Reason { get; } = reason;
        }

        public bool Equals(Failure? other) => other is not null && other.GetType() == GetType() && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as Failure);
        public override int GetHashCode() => HashCode.Combine(GetType(), Message);
    }

    /// <summary>The key hash as the signer encodes it: its tag and 20 bytes.</summary>
    public static byte[]? KeyHash(Address address)
    {
        if (Base58.CheckDecode(address.Value) is not { Length: 23 } bytes) return null;
        var tag = Array.FindIndex(AddressPrefixes, p => p.AsSpan().SequenceEqual(bytes.AsSpan(0, 3)));
        return tag < 0 ? null : [(byte)tag, .. bytes[3..]];
    }

    public static Address? AddressFromKeyHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 21 || bytes[0] >= AddressPrefixes.Length) return null;
        return new Address(Base58.CheckEncode(AddressPrefixes[bytes[0]], bytes[1..].ToArray()));
    }

    /// <summary>
    /// A sign request names a tz4 key with its signature version (<c>pkh_encoding</c>, tag 3). An
    /// older signer (before Octez 23) takes the bare hash, but it also signs BLS with the augmented
    /// scheme the chain no longer accepts, so it is not asked that way.
    /// </summary>
    private static byte[]? SigningKeyHash(Address address)
    {
        if (KeyHash(address) is not { } hash) return null;
        return hash[0] == 3 ? [3, .. hash, BlsSignatureVersion] : hash;
    }

    /// <summary>
    /// The authentication signature is the last field and of variable size, so when absent it is
    /// simply not there (Data_encoding writes no option tag for it).
    /// </summary>
    public static byte[]? SignRequest(Address address, byte[] data)
    {
        if (SigningKeyHash(address) is not { } pkh) return null;
        var length = (uint)data.Length;
        return [0, .. pkh, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. data];
    }

    public static byte[]? PublicKeyRequest(Address address) => KeyHash(address) is { } hash ? [1, .. hash] : null;
    public static readonly byte[] AuthorizedKeysRequest = [2];
    public static readonly byte[] KnownKeysRequest = [6];
    /// <summary>
    /// A BLS proof of possession for the key itself: no other public key (a fixed-size option,
    /// tag 00), no authentication (the variable last field, left out).
    /// </summary>
    public static byte[]? ProvePossessionRequest(Address address) => KeyHash(address) is { } hash ? [7, .. hash, 0] : null;

    public static byte[]? Frame(byte[] payload) =>
        payload.Length > 0xffff ? null : [(byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    /// <summary>The value in a reply, or the signer's error.</summary>
    public static byte[] Value(byte[] reply)
    {
        if (reply.Length == 0) throw new Failure.Malformed();
        return reply[0] switch
        {
            0 => reply[1..],
            1 => throw new Failure.Refused(ErrorReason(reply[1..])),
            _ => throw new Failure.Malformed(),
        };
    }

    /// <summary>
    /// The signer's reason in an error trace. octez writes each error as a BSON document (<c>kind</c>,
    /// <c>id</c>, often <c>msg</c>); the message is the readable part, the id the fallback.
    /// </summary>
    public static string? ErrorReason(byte[] bytes) => BsonString("msg", bytes) ?? BsonString("id", bytes) ?? JsonErrorID(bytes);

    /// <summary>
    /// The first BSON string element called <c>name</c>: <c>02</c>, the name, <c>00</c>, a little-endian
    /// length counting the closing <c>00</c>, then the text.
    /// </summary>
    public static string? BsonString(string name, byte[] bytes)
    {
        byte[] key = [2, .. Encoding.UTF8.GetBytes(name), 0];
        for (var index = 0; index + key.Length + 4 <= bytes.Length; index++)
        {
            if (!bytes.AsSpan(index, key.Length).SequenceEqual(key)) continue;
            var at = index + key.Length;
            var length = bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;
            var start = at + 4;
            if (length is >= 1 and <= 4096 && start + length <= bytes.Length && bytes[start + length - 1] == 0)
            {
                try
                {
                    var text = new UTF8Encoding(false, true).GetString(bytes, start, length - 1);
                    if (text.Length > 0) return text;
                }
                catch (DecoderFallbackException) { }
            }
        }
        return null;
    }

    /// <summary>An error id written as JSON, as a signer might over other transports.</summary>
    public static string? JsonErrorID(byte[] bytes)
    {
        var match = Regex.Match(Encoding.UTF8.GetString(bytes), "\"id\"\\s*:\\s*\"([^\"]{1,200})\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string PublicKeyFromValue(byte[] value)
    {
        if (value.Length == 0 || value[0] >= PublicKeyFormats.Length) throw new Failure.Malformed();
        var format = PublicKeyFormats[value[0]];
        if (value.Length != 1 + format.Size) throw new Failure.Malformed();
        return Base58.CheckEncode(format.Prefix, value[1..]);
    }

    /// <summary>
    /// A list of key hashes, 21 bytes each; inside a union octez writes it behind a 4-byte length
    /// (<c>00000000</c> for none), which cannot be mistaken for whole items.
    /// </summary>
    public static IReadOnlyList<Address> AddressesFromValue(byte[] whole)
    {
        var value = whole;
        if (value.Length % 21 == 4)
        {
            var length = value.Take(4).Aggregate(0, (a, b) => a << 8 | b);
            if (length != value.Length - 4) throw new Failure.Malformed();
            value = value[4..];
        }
        if (value.Length % 21 != 0) throw new Failure.Malformed();
        var addresses = new List<Address>();
        for (var start = 0; start < value.Length; start += 21)
            addresses.Add(AddressFromKeyHash(value.AsSpan(start, 21)) ?? throw new Failure.Malformed());
        return addresses;
    }

    /// <summary><c>null</c> when the signer takes unauthenticated requests, else the keys it accepts.</summary>
    public static IReadOnlyList<Address>? AuthorizedKeysFromValue(byte[] value) => value switch
    {
        [0] => null,
        [1, ..] => AddressesFromValue(value[1..]),
        _ => throw new Failure.Malformed(),
    };
}
