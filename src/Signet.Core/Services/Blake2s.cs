using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Signet.Services;

/// <summary>
/// BLAKE2s-256 (RFC 7693) with the optional 8-byte personalization TzEL's hashes are
/// domain-separated by. Upstream compiles this in C (<c>tzel_blake2s.c</c>, -O3) because an
/// address's XMSS auth tree is about 44 million hashes; here it is managed code tuned for the
/// same job (no allocation per hash), and the leaf and node functions of that file are ported
/// beside it (<c>AuthLeaf</c>, <c>AuthNode</c>). The protocol itself stays in
/// <c>TaquitoBridge/src/tzel.js</c>; <c>ShieldedTests</c> checks this against the RFC vector and the bridge.
/// </summary>
public static class Blake2s
{
    public const int DigestLength = 32;

    /// <summary>The eight personalization bytes.</summary>
    public sealed record Personalization
    {
        public byte[] Bytes { get; }
        public static readonly Personalization None = new([]);
        private Personalization(byte[] bytes) => Bytes = bytes;

        /// <summary>Exactly eight ASCII bytes, as the spec writes them (<c>"mrklSP__"</c>); null otherwise.</summary>
        public static Personalization? From(string label)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            return bytes.Length == 8 ? new Personalization(bytes) : null;
        }
    }

    private static readonly uint[] IV =
    [
        0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au, 0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u,
    ];

    private static readonly byte[] Sigma =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    /// <summary>The 32-byte digest of <c>message</c>.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> message, Personalization? personalization = null)
    {
        var output = new byte[DigestLength];
        Hash(message, personalization?.Bytes is { Length: 8 } p ? p : default, output);
        return output;
    }

    /// <summary>The digest of <c>input</c> into <c>output</c> (32 bytes); <c>personal</c> is empty or 8 bytes.</summary>
    public static void Hash(ReadOnlySpan<byte> input, ReadOnlySpan<byte> personal, Span<byte> output)
    {
        Span<uint> h = stackalloc uint[8];
        IV.AsSpan().CopyTo(h);
        // Parameter block: digest length 32, no key, fanout 1, depth 1; the personalization is the last two words.
        h[0] ^= 0x01010020u;
        if (personal.Length == 8)
        {
            h[6] ^= BinaryPrimitives.ReadUInt32LittleEndian(personal);
            h[7] ^= BinaryPrimitives.ReadUInt32LittleEndian(personal[4..]);
        }
        Span<byte> block = stackalloc byte[64];
        ulong counter = 0;
        var offset = 0;
        while (true)
        {
            var take = Math.Min(64, input.Length - offset);
            block.Clear();
            if (take > 0) input.Slice(offset, take).CopyTo(block);
            counter += (ulong)take;
            offset += take;
            var last = offset >= input.Length;
            Compress(h, block, counter, last);
            if (last) break;
        }
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(output[(4 * i)..], h[i]);
    }

    private static void Compress(Span<uint> h, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<uint> m = stackalloc uint[16];
        for (var i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);
        ref var mr = ref MemoryMarshal.GetReference(m);
        ref var sr = ref MemoryMarshal.GetArrayDataReference(SigmaTable);
        uint v0 = h[0], v1 = h[1], v2 = h[2], v3 = h[3], v4 = h[4], v5 = h[5], v6 = h[6], v7 = h[7];
        uint v8 = 0x6A09E667u, v9 = 0xBB67AE85u, v10 = 0x3C6EF372u, v11 = 0xA54FF53Au;
        uint v12 = 0x510E527Fu ^ (uint)counter, v13 = 0x9B05688Cu ^ (uint)(counter >> 32), v14 = last ? ~0x1F83D9ABu : 0x1F83D9ABu, v15 = 0x5BE0CD19u;
        for (var r = 0; r < 160; r += 16)
        {
            v0 += v4 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 0)); v12 = BitOperations.RotateRight(v12 ^ v0, 16); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 12);
            v0 += v4 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 1)); v12 = BitOperations.RotateRight(v12 ^ v0, 8); v8 += v12; v4 = BitOperations.RotateRight(v4 ^ v8, 7);
            v1 += v5 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 2)); v13 = BitOperations.RotateRight(v13 ^ v1, 16); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 12);
            v1 += v5 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 3)); v13 = BitOperations.RotateRight(v13 ^ v1, 8); v9 += v13; v5 = BitOperations.RotateRight(v5 ^ v9, 7);
            v2 += v6 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 4)); v14 = BitOperations.RotateRight(v14 ^ v2, 16); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 12);
            v2 += v6 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 5)); v14 = BitOperations.RotateRight(v14 ^ v2, 8); v10 += v14; v6 = BitOperations.RotateRight(v6 ^ v10, 7);
            v3 += v7 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 6)); v15 = BitOperations.RotateRight(v15 ^ v3, 16); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 12);
            v3 += v7 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 7)); v15 = BitOperations.RotateRight(v15 ^ v3, 8); v11 += v15; v7 = BitOperations.RotateRight(v7 ^ v11, 7);
            v0 += v5 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 8)); v15 = BitOperations.RotateRight(v15 ^ v0, 16); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 12);
            v0 += v5 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 9)); v15 = BitOperations.RotateRight(v15 ^ v0, 8); v10 += v15; v5 = BitOperations.RotateRight(v5 ^ v10, 7);
            v1 += v6 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 10)); v12 = BitOperations.RotateRight(v12 ^ v1, 16); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 12);
            v1 += v6 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 11)); v12 = BitOperations.RotateRight(v12 ^ v1, 8); v11 += v12; v6 = BitOperations.RotateRight(v6 ^ v11, 7);
            v2 += v7 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 12)); v13 = BitOperations.RotateRight(v13 ^ v2, 16); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 12);
            v2 += v7 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 13)); v13 = BitOperations.RotateRight(v13 ^ v2, 8); v8 += v13; v7 = BitOperations.RotateRight(v7 ^ v8, 7);
            v3 += v4 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 14)); v14 = BitOperations.RotateRight(v14 ^ v3, 16); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 12);
            v3 += v4 + Unsafe.Add(ref mr, Unsafe.Add(ref sr, r + 15)); v14 = BitOperations.RotateRight(v14 ^ v3, 8); v9 += v14; v4 = BitOperations.RotateRight(v4 ^ v9, 7);
        }
        h[0] ^= v0 ^ v8;
        h[1] ^= v1 ^ v9;
        h[2] ^= v2 ^ v10;
        h[3] ^= v3 ^ v11;
        h[4] ^= v4 ^ v12;
        h[5] ^= v5 ^ v13;
        h[6] ^= v6 ^ v14;
        h[7] ^= v7 ^ v15;
    }

    /// <summary>The message schedule, as indices widened to ints so they index the words directly.</summary>
    private static readonly int[] SigmaTable = Sigma.Select(b => (int)b).ToArray();

    // ---- Auth tree (tzel_blake2s.c) --------------------------------------------------------------
    //
    // A port of `authLeaf` in TaquitoBridge/src/tzel.js: WOTS+ with w = 4 and 133 chains, the chain
    // endpoints compressed by an L-tree. Every hash is unpersonalized BLAKE2s truncated to 251 bits
    // over `pub_seed ‖ ADRS ‖ inputs`, where ADRS packs an 8-byte tag and four little-endian u32s.

    private const int Chains = 133;
    private const int WotsSteps = 3;
    private static readonly byte[] TagChain = "xmss-ch\0"u8.ToArray();
    private static readonly byte[] TagLTree = "xmss-lt\0"u8.ToArray();
    private static readonly byte[] TagTree = "xmss-tr\0"u8.ToArray();

    private static void HashTruncated(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Hash(input, default, output);
        output[31] &= 0x07;
    }

    private static void WriteAdrs(Span<byte> at, ReadOnlySpan<byte> tag, uint keyIdx, uint a, uint b, uint c)
    {
        tag.CopyTo(at);
        BinaryPrimitives.WriteUInt32LittleEndian(at[8..], keyIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(at[12..], a);
        BinaryPrimitives.WriteUInt32LittleEndian(at[16..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(at[20..], c);
        at.Slice(24, 8).Clear();
    }

    /// <summary>A domain tag felt: the label's bytes reversed into the low positions.</summary>
    internal static void FeltTag(string label, Span<byte> output)
    {
        output[..32].Clear();
        var bytes = Encoding.ASCII.GetBytes(label);
        for (var i = 0; i < bytes.Length; i++) output[i] = bytes[bytes.Length - 1 - i];
    }

    /// <summary>One XMSS leaf: the WOTS+ key at <c>keyIdx</c>, its chains compressed by the L-tree.</summary>
    public static void AuthLeaf(ReadOnlySpan<byte> askJ, ReadOnlySpan<byte> pubSeed, uint keyIdx, Span<byte> output)
    {
        // key_seed = H(tag("xmss-sk") ‖ ask_j ‖ felt(key_idx))
        Span<byte> buf = stackalloc byte[96];
        FeltTag("xmss-sk", buf);
        askJ[..32].CopyTo(buf[32..]);
        buf.Slice(64, 32).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(buf[64..], keyIdx);
        Span<byte> keySeed = stackalloc byte[32];
        HashTruncated(buf, keySeed);

        // Chain endpoints: sk_c = H(key_seed ‖ u32(c) ‖ 0…) then three chain steps.
        Span<byte> pk = stackalloc byte[Chains * 32];
        Span<byte> chainIn = stackalloc byte[64];
        keySeed.CopyTo(chainIn);
        chainIn.Slice(32, 32).Clear();
        Span<byte> scratch = stackalloc byte[128];
        pubSeed[..32].CopyTo(scratch);
        Span<byte> x = stackalloc byte[32];
        for (uint c = 0; c < Chains; c++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chainIn[32..], c);
            HashTruncated(chainIn, x);
            for (uint step = 0; step < WotsSteps; step++)
            {
                WriteAdrs(scratch[32..], TagChain, keyIdx, c, step, 0);
                x.CopyTo(scratch[64..]);
                HashTruncated(scratch[..96], x);
            }
            x.CopyTo(pk[(int)(c * 32)..]);
        }

        // L-tree: pairs are hashed, an odd one out is carried up unchanged.
        Span<byte> next = stackalloc byte[Chains * 32];
        var current = pk;
        var other = next;
        uint count = Chains, level = 0;
        while (count > 1)
        {
            uint nodeIdx = 0, written = 0;
            for (uint i = 0; i < count; i += 2)
            {
                if (i + 1 == count)
                {
                    current.Slice((int)(i * 32), 32).CopyTo(other[(int)(written * 32)..]);
                }
                else
                {
                    WriteAdrs(scratch[32..], TagLTree, keyIdx, level, nodeIdx, 0);
                    current.Slice((int)(i * 32), 64).CopyTo(scratch[64..]);
                    HashTruncated(scratch, other.Slice((int)(written * 32), 32));
                    nodeIdx++;
                }
                written++;
            }
            count = written;
            level++;
            var swap = current;
            current = other;
            other = swap;
        }
        current[..32].CopyTo(output);
    }

    /// <summary>An inner node of the auth tree.</summary>
    public static void AuthNode(ReadOnlySpan<byte> pubSeed, uint level, uint nodeIdx, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> output)
    {
        Span<byte> scratch = stackalloc byte[128];
        pubSeed[..32].CopyTo(scratch);
        WriteAdrs(scratch[32..], TagTree, 0, level, nodeIdx, 0);
        left[..32].CopyTo(scratch[64..]);
        right[..32].CopyTo(scratch[96..]);
        HashTruncated(scratch, output);
    }
}

/// <summary>Hex helpers for the values the shielded code passes around.</summary>
public static class HexBytes
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Lowercase or uppercase hex, even length; null for anything else.</summary>
    public static byte[]? Decode(string text)
    {
        if (text.Length % 2 != 0 || !text.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(text);
    }
}
