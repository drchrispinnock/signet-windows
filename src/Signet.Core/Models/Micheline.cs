using System.Globalization;

namespace Signet.Models;

/// <summary>
/// A strict reader for Micheline's binary encoding (what <c>PACK</c> produces after its <c>05</c> prefix).
///
/// Used to decide whether a dApp's "message" really is one complete Micheline expression: the
/// bytes must decode as exactly one expression with every declared length satisfied and nothing
/// left over. The bridge's <c>micheline.js</c> implements the same rules; keep them in step.
///
/// Encoding, from octez's <c>micheline_encoding</c>:
///   00 int (signed zarith)             01 string (u32 length, bytes)
///   02 sequence (u32 length, elements) 0a bytes (u32 length, bytes)
///   03 prim                             04 prim + annots
///   05 prim arg                         06 prim arg + annots
///   07 prim arg arg                     08 prim arg arg + annots
///   09 prim (u32 length, args) + annots
/// Annotations are one u32-length string. Primitive tags are not checked against octez's table.
/// </summary>
public static class MichelineBinary
{
    /// <summary>Root depth is zero. Bound recursion before descending into untrusted expressions.</summary>
    public const int MaxDepth = 128;

    /// <summary>True when <c>bytes</c> is exactly one well-formed expression.</summary>
    public static bool IsWellFormed(byte[] bytes)
    {
        try { return Expression(bytes, 0, 0) == bytes.Length; }
        catch (MalformedException) { return false; }
    }

    /// <summary>True when <c>hex</c> is <c>05</c> followed by exactly one well-formed expression.</summary>
    public static bool IsPackedExpression(string hex) =>
        Hex.Bytes(hex) is { Length: > 0 } bytes && bytes[0] == 0x05 && IsWellFormed(bytes[1..]);

    private sealed class MalformedException : Exception;

    /// <summary>Reads one expression starting at <c>pos</c> and returns the position after it.</summary>
    private static int Expression(byte[] b, int pos, int depth)
    {
        if (depth > MaxDepth || pos >= b.Length) throw new MalformedException();
        var p = pos + 1;
        switch (b[pos])
        {
            case 0x00:
                // Zarith: continuation bit 7 on every byte, at least one byte.
                if (p >= b.Length) throw new MalformedException();
                while ((b[p] & 0x80) != 0)
                {
                    p++;
                    if (p >= b.Length) throw new MalformedException();
                }
                return p + 1;
            case 0x01 or 0x0a:
            {
                var length = U32(b, p);
                p += 4;
                if (length > b.Length - p) throw new MalformedException();
                return p + (int)length;
            }
            case 0x02:
            {
                var length = U32(b, p);
                p += 4;
                return Elements(b, p, length, depth);
            }
            case 0x03: return Prim(b, p, 0, false, depth);
            case 0x04: return Prim(b, p, 0, true, depth);
            case 0x05: return Prim(b, p, 1, false, depth);
            case 0x06: return Prim(b, p, 1, true, depth);
            case 0x07: return Prim(b, p, 2, false, depth);
            case 0x08: return Prim(b, p, 2, true, depth);
            case 0x09:
            {
                if (p >= b.Length) throw new MalformedException();
                p++;
                var length = U32(b, p);
                p = Elements(b, p + 4, length, depth);
                return Annotations(b, p);
            }
            default:
                throw new MalformedException();
        }
    }

    private static int Prim(byte[] b, int pos, int args, bool annots, int depth)
    {
        if (pos >= b.Length) throw new MalformedException();
        var p = pos + 1;
        for (var i = 0; i < args; i++) p = Expression(b, p, depth + 1);
        return annots ? Annotations(b, p) : p;
    }

    /// <summary>Expressions filling exactly <c>length</c> bytes from <c>pos</c>.</summary>
    private static int Elements(byte[] b, int pos, long length, int depth)
    {
        if (length > b.Length - pos) throw new MalformedException();
        var end = pos + (int)length;
        var p = pos;
        while (p < end) p = Expression(b, p, depth + 1);
        if (p != end) throw new MalformedException();
        return end;
    }

    private static int Annotations(byte[] b, int pos)
    {
        var length = U32(b, pos);
        if (length > b.Length - pos - 4) throw new MalformedException();
        return pos + 4 + (int)length;
    }

    // Returned as long so a declared length near 2^32 cannot overflow the bounds checks.
    private static long U32(byte[] b, int pos)
    {
        if (pos + 4 > b.Length) throw new MalformedException();
        return (long)b[pos] << 24 | (long)b[pos + 1] << 16 | (long)b[pos + 2] << 8 | b[pos + 3];
    }
}

public static class Hex
{
    public static byte[]? Bytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        var output = new byte[hex.Length / 2];
        for (var i = 0; i < output.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out output[i]))
                return null;
        }
        return output;
    }
}
