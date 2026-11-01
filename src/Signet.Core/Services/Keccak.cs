using System.Text;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Keccak-256 as Ethereum uses it (the original Keccak padding, not FIPS SHA3-256), for the
/// account's alias on Etherlink's EVM side: the first 20 bytes of the hash of the base58 address
/// string (<c>EtherlinkAlias</c>). .NET's SHA3_256 is the FIPS variant, and the alias should be shown
/// without a round trip through the bridge.
/// </summary>
public static class Keccak
{
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808a, 0x8000000080008000,
        0x000000000000808b, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008a, 0x0000000000000088, 0x0000000080008009, 0x000000008000000a,
        0x000000008000808b, 0x800000000000008b, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800a, 0x800000008000000a,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];
    private static readonly int[] Rotations = [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
    private static readonly int[] PiLanes = [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

    /// <summary>Keccak-256 digest (32 bytes) of <c>message</c>.</summary>
    public static byte[] Hash256(ReadOnlySpan<byte> message)
    {
        const int rate = 136;
        var state = new ulong[25];
        var paddedLength = (message.Length / rate + 1) * rate;
        var padded = new byte[paddedLength];
        message.CopyTo(padded);
        padded[message.Length] = 0x01;
        padded[^1] |= 0x80;
        for (var block = 0; block < padded.Length; block += rate)
        {
            for (var lane = 0; lane < rate / 8; lane++)
            {
                ulong word = 0;
                for (var b = 0; b < 8; b++) word |= (ulong)padded[block + lane * 8 + b] << (8 * b);
                state[lane] ^= word;
            }
            Permute(state);
        }
        var output = new byte[32];
        for (var lane = 0; lane < 4; lane++)
            for (var b = 0; b < 8; b++) output[lane * 8 + b] = (byte)(state[lane] >> (8 * b));
        return output;
    }

    private static void Permute(ulong[] a)
    {
        var b = new ulong[5];
        for (var round = 0; round < 24; round++)
        {
            // θ
            for (var x = 0; x < 5; x++) b[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (var x = 0; x < 5; x++)
            {
                var t = b[(x + 4) % 5] ^ ulong.RotateLeft(b[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5) a[x + y] ^= t;
            }
            // ρ and π
            var current = a[1];
            for (var i = 0; i < 24; i++)
            {
                var j = PiLanes[i];
                var next = a[j];
                a[j] = ulong.RotateLeft(current, Rotations[i]);
                current = next;
            }
            // χ
            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++) b[x] = a[y + x];
                for (var x = 0; x < 5; x++) a[y + x] = b[x] ^ (~b[(x + 1) % 5] & b[(x + 2) % 5]);
            }
            // ι
            a[0] ^= RoundConstants[round];
        }
    }
}

/// <summary>
/// The account's address on Etherlink's EVM side, derived from its Tezos address (Tezos X
/// "accounts and aliases"): the first 20 bytes of Keccak-256 over the base58 address string,
/// which is what the EVM node's <c>tez_getTezosEthereumAddress</c> answers. Tez sent to it on the EVM
/// side are forwarded to the account on the Michelson interface.
/// </summary>
public static class EtherlinkAlias
{
    public static string AddressFor(Address account) =>
        "0x" + Convert.ToHexStringLower(Keccak.Hash256(Encoding.UTF8.GetBytes(account.Value))[..20]);
}
