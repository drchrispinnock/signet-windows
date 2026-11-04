namespace Signet.Services;

/// <summary>
/// The XMSS-style authentication tree of a TzEL address (spec § Authentication): 65536 WOTS+
/// keys (w = 4, 133 chains), each compressed by an L-tree into a leaf, the leaves hashed up
/// into the <c>auth_root</c> a payment address carries. Every signature consumes one key index and
/// needs that key's sibling path, so the leaves are built once per address and kept.
///
/// This is the one piece of the protocol done natively: about 44 million BLAKE2s hashes, which
/// the bridge's JavaScript takes minutes over, built here across every core. The leaf and node
/// functions (<c>Blake2s.AuthLeaf</c> / <c>AuthNode</c>) are a port of <c>authLeaf</c> /
/// <c>xmssTreeNode</c> in <c>TaquitoBridge/src/tzel.js</c>; <c>ShieldedTests</c> checks the two agree.
/// </summary>
public static class XMSSAuthTree
{
    public const int Depth = 16;
    public const int Size = 1 << Depth;
    public const int Chains = 133;
    public const int WotsW = 4;
    public const int FeltBytes = 32;
    /// <summary><c>leaves</c> buffers are <c>Size * FeltBytes</c> bytes.</summary>
    public const int LeavesBytes = Size * FeltBytes;

    /// <summary>A domain tag felt: the label's bytes reversed into the low positions.</summary>
    public static byte[] FeltTag(string label)
    {
        var output = new byte[FeltBytes];
        Blake2s.FeltTag(label, output);
        return output;
    }

    public static byte[] FeltOfInt(ulong n)
    {
        var output = new byte[FeltBytes];
        for (var i = 0; i < 8; i++) output[i] = (byte)(n >> (8 * i));
        return output;
    }

    /// <summary>Unpersonalized BLAKE2s truncated to 251 bits, as the key hierarchy uses.</summary>
    public static byte[] Hash(byte[] data)
    {
        var output = Blake2s.Hash(data);
        output[31] &= 0x07;
        return output;
    }

    public static byte[] HashTwo(byte[] a, byte[] b) => Hash([.. a, .. b]);

    /// <summary><c>ask_j = H(ask_base, felt(j))</c>.</summary>
    public static byte[] DeriveAsk(byte[] askBase, uint j) => HashTwo(askBase, FeltOfInt(j));

    /// <summary><c>pub_seed = H(tag("xmss-ps") ‖ ask_j)</c>.</summary>
    public static byte[] PubSeed(byte[] askJ) => Hash([.. FeltTag("xmss-ps"), .. askJ]);

    /// <summary><c>key_seed = H(tag("xmss-sk") ‖ ask_j ‖ felt(key_idx))</c>.</summary>
    public static byte[] KeySeed(byte[] askJ, uint keyIdx) => Hash([.. FeltTag("xmss-sk"), .. askJ, .. FeltOfInt(keyIdx)]);

    /// <summary>One leaf, for tests and spot checks; <c>BuildLeaves</c> does the whole tree.</summary>
    public static byte[] Leaf(byte[] askJ, byte[] pubSeed, int keyIdx)
    {
        if (askJ.Length != FeltBytes || pubSeed.Length != FeltBytes || keyIdx is < 0 or >= Size) throw new ArgumentOutOfRangeException(nameof(keyIdx));
        var output = new byte[FeltBytes];
        Blake2s.AuthLeaf(askJ, pubSeed, (uint)keyIdx, output);
        return output;
    }

    /// <summary>
    /// All 65536 leaves, concatenated, built across every core. <c>progress</c> is called from the
    /// worker threads with the number of leaves done so far (a few hundred at a time).
    /// </summary>
    public static byte[] BuildLeaves(byte[] askJ, byte[] pubSeed, Action<int>? progress = null, CancellationToken cancel = default)
    {
        if (askJ.Length != FeltBytes || pubSeed.Length != FeltBytes) throw new ArgumentException("ask_j and pub_seed are 32 bytes");
        var output = new byte[LeavesBytes];
        var workers = Math.Clamp(Environment.ProcessorCount, 1, 32);
        var done = 0;
        const int reportEvery = 512;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancel }, worker =>
        {
            var sinceReport = 0;
            for (var index = worker; index < Size; index += workers)
            {
                Blake2s.AuthLeaf(askJ, pubSeed, (uint)index, output.AsSpan(index * FeltBytes, FeltBytes));
                if (++sinceReport == reportEvery)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Invoke(Interlocked.Add(ref done, sinceReport));
                    sinceReport = 0;
                }
            }
            if (sinceReport > 0) progress?.Invoke(Interlocked.Add(ref done, sinceReport));
        });
        return output;
    }

    /// <summary>Every level of the tree, leaves first: <c>levels[0]</c> is <c>leaves</c>, <c>levels[16]</c> the root.</summary>
    public static IReadOnlyList<byte[]> Levels(byte[] pubSeed, byte[] leaves)
    {
        if (leaves.Length != LeavesBytes) throw new ArgumentException("an auth tree has 65536 leaves");
        if (pubSeed.Length != FeltBytes) throw new ArgumentException("pub_seed is 32 bytes");
        var levels = new List<byte[]> { leaves };
        for (var level = 0; level < Depth; level++)
        {
            var below = levels[level];
            var count = below.Length / FeltBytes / 2;
            var above = new byte[count * FeltBytes];
            for (var i = 0; i < count; i++)
                Blake2s.AuthNode(pubSeed, (uint)level, (uint)i, below.AsSpan(2 * i * FeltBytes, FeltBytes),
                                 below.AsSpan((2 * i + 1) * FeltBytes, FeltBytes), above.AsSpan(i * FeltBytes, FeltBytes));
            levels.Add(above);
        }
        return levels;
    }

    public static byte[] Root(IReadOnlyList<byte[]> levels) => levels[Depth];

    public static byte[] Root(byte[] pubSeed, byte[] leaves) => Root(Levels(pubSeed, leaves));

    /// <summary>The 16 sibling nodes of leaf <c>index</c>, leaf level first.</summary>
    public static IReadOnlyList<byte[]> Path(IReadOnlyList<byte[]> levels, int index)
    {
        if (index is < 0 or >= Size) throw new ArgumentOutOfRangeException(nameof(index));
        return Enumerable.Range(0, Depth).Select(level =>
        {
            var sibling = (index >> level) ^ 1;
            return levels[level].AsSpan(sibling * FeltBytes, FeltBytes).ToArray();
        }).ToList();
    }
}
