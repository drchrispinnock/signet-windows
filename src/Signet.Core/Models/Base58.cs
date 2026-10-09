using System.Security.Cryptography;
using System.Text;

namespace Signet.Models;

/// <summary>Base58Check as used by Tezos: <c>base58(prefix + payload + sha256(sha256(prefix + payload))[0..4])</c>.</summary>
public static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0) zeros++;

        var digits = new List<byte>();  // little-endian base-58 digits
        foreach (var b in bytes[zeros..])
        {
            int carry = b;
            for (var i = 0; i < digits.Count; i++)
            {
                carry += digits[i] << 8;
                digits[i] = (byte)(carry % 58);
                carry /= 58;
            }
            while (carry > 0)
            {
                digits.Add((byte)(carry % 58));
                carry /= 58;
            }
        }

        var result = new StringBuilder(zeros + digits.Count);
        result.Append('1', zeros);
        for (var i = digits.Count - 1; i >= 0; i--) result.Append(Alphabet[digits[i]]);
        return result.ToString();
    }

    public static string CheckEncode(byte[] prefix, byte[] payload)
    {
        byte[] body = [.. prefix, .. payload];
        return Encode([.. body, .. Checksum(body)]);
    }

    /// <summary>Decodes base58 text to bytes, or <c>null</c> on a bad character.</summary>
    public static byte[]? Decode(string text)
    {
        var zeros = 0;
        while (zeros < text.Length && text[zeros] == '1') zeros++;

        var bytes = new List<byte>();  // little-endian base-256 digits
        foreach (var c in text.AsSpan(zeros))
        {
            var carry = c < 128 ? Alphabet.IndexOf(c) : -1;
            if (carry < 0) return null;
            for (var i = 0; i < bytes.Count; i++)
            {
                carry += bytes[i] * 58;
                bytes[i] = (byte)(carry & 0xff);
                carry >>= 8;
            }
            while (carry > 0)
            {
                bytes.Add((byte)(carry & 0xff));
                carry >>= 8;
            }
        }
        bytes.Reverse();
        return [.. new byte[zeros], .. bytes];
    }

    /// <summary>Decodes and verifies the 4-byte double-SHA256 checksum; returns the payload including prefix.</summary>
    public static byte[]? CheckDecode(string text)
    {
        if (Decode(text) is not { Length: > 4 } bytes) return null;
        var body = bytes[..^4];
        return bytes.AsSpan(bytes.Length - 4).SequenceEqual(Checksum(body)) ? body : null;
    }

    private static byte[] Checksum(byte[] body) => SHA256.HashData(SHA256.HashData(body))[..4];
}

/// <summary>Byte prefixes that make Tezos base58 strings start with their familiar letters.</summary>
public static class TezosPrefix
{
    public static readonly byte[] Edsk = [13, 15, 58, 7];        // Ed25519 32-byte seed
    public static readonly byte[] Edpk = [13, 15, 37, 217];
    public static readonly byte[] Spsk = [17, 162, 224, 201];
    public static readonly byte[] Sppk = [3, 254, 226, 86];
    public static readonly byte[] P2sk = [16, 81, 238, 189];
    public static readonly byte[] P2pk = [3, 178, 139, 127];
    public static readonly byte[] Blsk = [3, 150, 192, 40];
    public static readonly byte[] Blpk = [6, 149, 135, 204];
}
