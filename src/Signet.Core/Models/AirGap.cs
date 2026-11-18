using System.IO.Compression;
using System.Security.Cryptography;

namespace Signet.Models;

/// <summary>
/// AirGap's inter-app messages (IAC, serializer version 3), the QR language of AirGap Vault and
/// AirGap Wallet: <c>gzip(CBOR([3, [message, …]]))</c> carried as a <c>ur:bytes</c> Uniform
/// Resource. A message is <c>[version, type, protocol, id, payload]</c>; the payload is the
/// message type's JSON schema flattened to an array of its properties in alphabetical order, with
/// a string that looks like hex sent as bytes behind a one-byte tag saying how to write it back.
///
/// Signet reads an account share (Vault's "Pair Account" code: the public key, derivation path and
/// the Vault's master fingerprint) and writes and reads Tezos transaction sign requests and
/// responses. The format is AirGap's (<c>@airgap/serializer</c>); nothing here talks to anything.
/// </summary>
public static class AirGap
{
    public const ulong SerializerVersion = 3;
    public const string TezosProtocol = "xtz";
    /// <summary>What AirGap Wallet puts in a sign request; Vault requires the field.</summary>
    public const string CallbackURL = "airgap-wallet://?d=";
    /// <summary>A message far larger than any Signet reads or writes.</summary>
    public const int MaximumInflatedLength = 512 * 1024;

    public enum MessageType : ulong
    {
        AccountShareRequest = 3,
        AccountShareResponse = 4,
        TransactionSignRequest = 5,
        TransactionSignResponse = 6,
        MessageSignRequest = 7,
        MessageSignResponse = 8,
    }

    public sealed class Error(Error.Reason reason) : Exception($"AirGap: {reason}")
    {
        public enum Reason
        {
            NotAirGap,
            UnsupportedVersion,
            Malformed,
            NoTezosAccount,
            WrongResponse,
            /// <summary>The reply answers this request but signs other bytes than were sent.</summary>
            DifferentOperation,
        }
        public Reason Kind { get; } = reason;
    }

    public sealed record Message
    {
        public ulong Version { get; init; } = 1;
        public ulong Type { get; init; }
        public string ProtocolIdentifier { get; init; }
        public ulong Id { get; init; }
        public IReadOnlyList<CBOR> Payload { get; init; }

        public CBOR CBORValue => new CBOR.Array([
            new CBOR.Unsigned(Version), new CBOR.Unsigned(Type), new CBOR.Text(ProtocolIdentifier), new CBOR.Unsigned(Id), new CBOR.Array(Payload)]);

        public Message(ulong type, string protocolIdentifier, ulong id, IReadOnlyList<CBOR> payload)
        {
            Type = type;
            ProtocolIdentifier = protocolIdentifier;
            Id = id;
            Payload = payload;
        }

        public static Message FromCBOR(CBOR cbor)
        {
            if (cbor is not CBOR.Array { Items: [CBOR.Unsigned(var version), CBOR.Unsigned(var type), CBOR.Text(var protocolIdentifier), CBOR.Unsigned(var id), CBOR.Array(var payload)] }
                || version > 1 || id > 99_999_999)
                throw new Error(Error.Reason.Malformed);
            return new Message(type, protocolIdentifier, id, payload) { Version = version };
        }

        public bool Equals(Message? other) =>
            other is not null && Version == other.Version && Type == other.Type && ProtocolIdentifier == other.ProtocolIdentifier
            && Id == other.Id && Payload.SequenceEqual(other.Payload);
        public override int GetHashCode() => HashCode.Combine(Type, Id);
    }

    // MARK: The wrapper

    /// <summary>The messages in an IAC payload: the bytes a <c>ur:bytes</c> UR carries.</summary>
    public static IReadOnlyList<Message> Messages(byte[] payload)
    {
        if (Gzip.Decompress(payload, MaximumInflatedLength) is not { } inflated) throw new Error(Error.Reason.NotAirGap);
        CBOR top;
        try { top = CBOR.Decode(inflated); }
        catch (CBOR.DecodeError) { throw new Error(Error.Reason.NotAirGap); }
        if (top is not CBOR.Array { Items: [CBOR.Unsigned(var version), CBOR.Array(var items)] }) throw new Error(Error.Reason.NotAirGap);
        if (version != SerializerVersion) throw new Error(Error.Reason.UnsupportedVersion);
        return items.Select(Message.FromCBOR).ToList();
    }

    public static byte[] PayloadFor(IReadOnlyList<Message> messages) =>
        Gzip.Compress(new CBOR.Array([new CBOR.Unsigned(SerializerVersion), new CBOR.Array(messages.Select(m => m.CBORValue).ToList())]).Encoded);

    // MARK: Strings

    /// <summary>A schema string as AirGap writes it: hex as tagged bytes, anything else as text.</summary>
    public static CBOR EncodeString(string value)
    {
        static bool IsHex(string s) => s.Length > 0 && s.All(char.IsAsciiHexDigit);
        byte tag;
        string hex;
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            hex = value[2..];
            if (!IsHex(hex)) return new CBOR.Bytes([0, .. System.Text.Encoding.UTF8.GetBytes(hex)]);
            tag = hex.Length % 2 == 0 ? (byte)1 : (byte)3;
        }
        else
        {
            if (!IsHex(value)) return new CBOR.Text(value);
            hex = value;
            tag = hex.Length % 2 == 0 ? (byte)2 : (byte)4;
        }
        var even = hex.Length % 2 == 0 ? hex : "0" + hex;
        return new CBOR.Bytes([tag, .. Hex.Bytes(even) ?? []]);
    }

    public static string DecodeString(CBOR value)
    {
        switch (value)
        {
            case CBOR.Text(var text): return text;
            case CBOR.Bytes(var bytes):
                if (bytes.Length == 0) throw new Error(Error.Reason.Malformed);
                var rest = bytes[1..];
                return bytes[0] switch
                {
                    0 => "0x" + System.Text.Encoding.UTF8.GetString(rest),
                    1 => "0x" + Convert.ToHexStringLower(rest),
                    2 => Convert.ToHexStringLower(rest),
                    3 => "0x" + Convert.ToHexStringLower(rest)[1..],
                    4 => Convert.ToHexStringLower(rest)[1..],
                    _ => throw new Error(Error.Reason.Malformed),
                };
            default: throw new Error(Error.Reason.Malformed);
        }
    }

    // MARK: Account share

    /// <summary>A Tezos account a Vault shared: its ed25519 public key and where the Vault keeps it.</summary>
    /// <param name="PublicKey"><c>edpk…</c>.</param>
    /// <param name="GroupLabel">The Vault's name for the secret the account comes from.</param>
    public sealed record Account(string PublicKey, string DerivationPath, string MasterFingerprint, string GroupLabel)
    {
        /// <summary>
        /// The <c>secret_keys</c> locator, in octez-client's shape (octez lists it as "airgap sk
        /// known" and leaves it alone): <c>airgap://&lt;fingerprint&gt;/&lt;path without m/&gt;</c>.
        /// </summary>
        public string Locator
        {
            get
            {
                var path = DerivationPath.StartsWith("m/", StringComparison.Ordinal) ? DerivationPath[2..] : DerivationPath;
                return $"airgap://{(MasterFingerprint.Length == 0 ? "vault" : MasterFingerprint)}/{path}";
            }
        }
    }

    /// <summary>The Tezos accounts in an account share response; other coins are skipped.</summary>
    public static IReadOnlyList<Account> Accounts(IReadOnlyList<Message> messages)
    {
        var accounts = new List<Account>();
        foreach (var message in messages)
        {
            if (message.Type != (ulong)MessageType.AccountShareResponse || message.ProtocolIdentifier != TezosProtocol) continue;
            // derivationPath, groupId, groupLabel, isActive, isExtendedPublicKey, masterFingerprint, publicKey
            if (message.Payload.Count != 7 || message.Payload[4] is not CBOR.Bool(false)) throw new Error(Error.Reason.Malformed);
            var key = DecodeString(message.Payload[6]);
            if (Edpk(key) is not { } publicKey) throw new Error(Error.Reason.Malformed);
            accounts.Add(new Account(publicKey, DecodeString(message.Payload[0]), DecodeString(message.Payload[5]), DecodeString(message.Payload[2])));
        }
        return accounts;
    }

    /// <summary>The base58 public key for AirGap's hex form (32 bytes of ed25519) or an <c>edpk</c> as is.</summary>
    public static string? Edpk(string key)
    {
        if (key.StartsWith("edpk", StringComparison.Ordinal))
            return Base58.CheckDecode(key) is { Length: 36 } ? key : null;
        if (key.Length != 64 || Hex.Bytes(key) is not { } bytes) return null;
        return Base58.CheckEncode(TezosPrefix.Edpk, bytes);
    }

    /// <summary>The 32 raw bytes of an <c>edpk</c>, as hex, which is how AirGap names the key in a request.</summary>
    public static string? HexPublicKey(string edpk)
    {
        if (!edpk.StartsWith("edpk", StringComparison.Ordinal) || Base58.CheckDecode(edpk) is not { Length: 36 } body) return null;
        return Convert.ToHexStringLower(body[^32..]);
    }

    // MARK: Signing

    /// <summary>A request for the Vault to sign <c>forged</c> (the operation's bytes, without the watermark).</summary>
    public static Message? SignRequest(string edpk, byte[] forged, ulong id)
    {
        if (HexPublicKey(edpk) is not { } key) return null;
        // callbackURL, publicKey, transaction { binaryTransaction }
        return new Message((ulong)MessageType.TransactionSignRequest, TezosProtocol, id,
            [EncodeString(CallbackURL), EncodeString(key), new CBOR.Array([EncodeString(Convert.ToHexStringLower(forged))])]);
    }

    /// <summary>
    /// The signed operation in a Vault's response to request <c>id</c>: forged bytes followed by
    /// the 64-byte signature.
    /// </summary>
    public static byte[] SignedOperation(IReadOnlyList<Message> messages, ulong id)
    {
        var message = messages.FirstOrDefault(m => m.Type == (ulong)MessageType.TransactionSignResponse) ?? throw new Error(Error.Reason.WrongResponse);
        if (message.ProtocolIdentifier != TezosProtocol || message.Id != id) throw new Error(Error.Reason.WrongResponse);
        // accountIdentifier, transaction
        if (message.Payload.Count != 2 || Hex.Bytes(DecodeString(message.Payload[1])) is not { } bytes) throw new Error(Error.Reason.Malformed);
        return bytes;
    }

    /// <summary>
    /// The 64-byte signature in a Vault's reply to request <c>id</c> for <c>forged</c>: the reply
    /// must carry exactly those bytes followed by the signature. The signature itself is checked
    /// against the account's key in the bridge before it is used.
    /// </summary>
    public static byte[] Signature(IReadOnlyList<Message> messages, ulong id, byte[] forged)
    {
        var signed = SignedOperation(messages, id);
        if (signed.Length != forged.Length + 64 || !signed.AsSpan(0, forged.Length).SequenceEqual(forged)) throw new Error(Error.Reason.DifferentOperation);
        return signed[^64..];
    }

    /// <summary>A fresh 8-digit message id, as AirGap makes them.</summary>
    public static ulong NewID() => (ulong)RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
}

/// <summary>gzip (RFC 1952) around raw DEFLATE.</summary>
public static class Gzip
{
    public static byte[]? Decompress(byte[] data, int limit)
    {
        if (data.Length < 18 || data[0] != 0x1f || data[1] != 0x8b || data[2] != 8) return null;
        var flags = data[3];
        var offset = 10;
        if ((flags & 0x04) != 0)
        {
            if (offset + 2 > data.Length) return null;
            offset += 2 + (data[offset] | data[offset + 1] << 8);
        }
        foreach (var flag in new byte[] { 0x08, 0x10 })
        {
            if ((flags & flag) == 0) continue;
            while (offset < data.Length && data[offset] != 0) offset++;
            offset++;
        }
        if ((flags & 0x02) != 0) offset += 2;
        if (offset >= data.Length - 8) return null;
        var trailer = data[^8..];
        var size = (long)BitConverter.ToUInt32(trailer, 4);
        if (size > limit) return null;
        var crc = BitConverter.ToUInt32(trailer, 0);
        var output = new byte[size + 1];
        int written;
        try
        {
            using var input = new DeflateStream(new MemoryStream(data, offset, data.Length - 8 - offset), CompressionMode.Decompress);
            written = 0;
            int n;
            while (written < output.Length && (n = input.Read(output, written, output.Length - written)) > 0) written += n;
        }
        catch (InvalidDataException) { return null; }
        if (written != size) return null;
        var result = output[..(int)size];
        return CRC32.Checksum(result) == crc ? result : null;
    }

    public static byte[] Compress(byte[] data)
    {
        var deflated = new MemoryStream();
        using (var stream = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) stream.Write(data);
        return [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3, .. deflated.ToArray(),
            .. BitConverter.GetBytes(CRC32.Checksum(data)), .. BitConverter.GetBytes((uint)data.Length)];
    }
}
