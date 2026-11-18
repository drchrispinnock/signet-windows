using System.Security.Cryptography;
using System.Text;

namespace Signet.Models;

/// <summary>
/// Blockchain Commons' Uniform Resources (BCR-2020-005), as AirGap Vault and Wallet speak them:
/// <c>ur:&lt;type&gt;/&lt;bytewords&gt;</c> for a message that fits one QR code, and
/// <c>ur:&lt;type&gt;/&lt;n&gt;-&lt;m&gt;/&lt;bytewords&gt;</c> for the frames of an animated one, whose
/// parts after the first <c>m</c> are fountain-coded mixes of the fragments. Only what Signet needs
/// is here: the <c>bytes</c> type, minimal Bytewords, the fountain decoder, and an encoder that
/// cycles the pure fragments (which any decoder accepts).
/// </summary>
public static class UniformResource
{
    public const string Scheme = "ur:";
    /// <summary>A message longer than this is not something Signet ever needs to read.</summary>
    public const int MaximumMessageLength = 256 * 1024;
    public const int MaximumParts = 1024;

    public sealed class DecodeError(DecodeError.Reason reason) : Exception($"Uniform Resource: {reason}")
    {
        public enum Reason { NotAUR, UnsupportedType, BadBytewords, BadChecksum, BadPart, TooLarge, MismatchedParts }
        public Reason Kind { get; } = reason;
    }

    // MARK: Bytewords

    private static readonly string[] Words = MakeWords();

    private static string[] MakeWords()
    {
        const string all = "ableacidalsoapexaquaarchatomauntawayaxisbackbaldbarnbeltbetabiasbluebodybragbrewbulbbuzzcalmcashcatschefcityclawcodecolacookcostcruxcurlcuspcyandarkdatadaysdelidicedietdoordowndrawdropdrumdulldutyeacheasyechoedgeepicevenexamexiteyesfactfairfernfigsfilmfishfizzflapflewfluxfoxyfreefrogfuelfundgalagamegeargemsgiftgirlglowgoodgraygrimgurugushgyrohalfhanghardhawkheathelphighhillholyhopehornhutsicedideaidleinchinkyintoirisironitemjadejazzjoinjoltjowljudojugsjumpjunkjurykeepkenokeptkeyskickkilnkingkitekiwiknoblamblavalazyleaflegsliarlimplionlistlogoloudloveluaulucklungmainmanymathmazememomenumeowmildmintmissmonknailnavyneednewsnextnoonnotenumbobeyoboeomitonyxopenovalowlspaidpartpeckplaypluspoempoolposepuffpumapurrquadquizraceramprealredorichroadrockroofrubyruinrunsrustsafesagascarsetssilkskewslotsoapsolosongstubsurfswantacotasktaxitenttiedtimetinytoiltombtoystriptunatwinuglyundouniturgeuservastveryvetovialvibeviewvisavoidvowswallwandwarmwaspwavewaxywebswhatwhenwhizwolfworkyankyawnyellyogayurtzapszerozestzinczonezoom";
        return Enumerable.Range(0, all.Length / 4).Select(i => all.Substring(i * 4, 4)).ToArray();
    }

    /// <summary>Minimal Bytewords: each byte is the first and last letter of its word.</summary>
    private static readonly string[] Minimal = Words.Select(w => $"{w[0]}{w[^1]}").ToArray();
    private static readonly Dictionary<string, byte> MinimalValues =
        Minimal.Select((word, i) => (word, i)).ToDictionary(p => p.word, p => (byte)p.i);

    /// <summary>Bytes plus their CRC-32, as minimal Bytewords.</summary>
    public static string Bytewords(byte[] data) =>
        string.Concat(data.Concat(BigEndian(CRC32.Checksum(data))).Select(b => Minimal[b]));

    /// <summary>The bytes of minimal Bytewords whose trailing CRC-32 checks out.</summary>
    public static byte[] BytesFromBytewords(string text)
    {
        var letters = text.ToLowerInvariant();
        if (letters.Length % 2 != 0 || letters.Length < 10) throw new DecodeError(DecodeError.Reason.BadBytewords);
        var output = new byte[letters.Length / 2];
        for (var i = 0; i < letters.Length; i += 2)
        {
            if (!MinimalValues.TryGetValue(letters.Substring(i, 2), out var value)) throw new DecodeError(DecodeError.Reason.BadBytewords);
            output[i / 2] = value;
        }
        var body = output[..^4];
        if (!BigEndian(CRC32.Checksum(body)).AsSpan().SequenceEqual(output.AsSpan(output.Length - 4))) throw new DecodeError(DecodeError.Reason.BadChecksum);
        return body;
    }

    public static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    // MARK: Parts

    /// <summary>One scanned QR code of a UR.</summary>
    public sealed record Part
    {
        public string Type { get; }
        /// <summary><c>null</c> for a single-part UR, whose <c>Body</c> is then the whole message.</summary>
        public (int Number, int Count)? Sequence { get; }
        public byte[] Body { get; }

        public Part(string text)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) throw new DecodeError(DecodeError.Reason.NotAUR);
            var components = trimmed[Scheme.Length..].ToLowerInvariant().Split('/');
            if (components.Length is not (2 or 3)) throw new DecodeError(DecodeError.Reason.NotAUR);
            Type = components[0];
            if (Type != "bytes") throw new DecodeError(DecodeError.Reason.UnsupportedType);
            if (components.Length == 3)
            {
                var numbers = components[1].Split('-');
                if (numbers.Length != 2
                    || !int.TryParse(numbers[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || !int.TryParse(numbers[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                    || number < 1 || count is < 1 or > MaximumParts)
                    throw new DecodeError(DecodeError.Reason.BadPart);
                Sequence = (number, count);
            }
            if (components[^1].Length > 2 * (MaximumMessageLength + 64)) throw new DecodeError(DecodeError.Reason.TooLarge);
            Body = BytesFromBytewords(components[^1]);
        }

        public bool Equals(Part? other) =>
            other is not null && Type == other.Type && Sequence == other.Sequence && Body.AsSpan().SequenceEqual(other.Body);
        public override int GetHashCode() => HashCode.Combine(Type, Sequence, Body.Length);
    }

    /// <summary>A fountain-coded part: which fragments it mixes and their XOR.</summary>
    public sealed record FountainPart(int Number, int Count, int MessageLength, uint Checksum, byte[] Fragment)
    {
        /// <summary>The CBOR array <c>[seqNum, seqLen, messageLen, checksum, fragment]</c>.</summary>
        public static FountainPart FromCBOR(byte[] bytes)
        {
            if (CBOR.Decode(bytes) is not CBOR.Array { Items: [CBOR.Unsigned(var number), CBOR.Unsigned(var count), CBOR.Unsigned(var length), CBOR.Unsigned(var checksum), CBOR.Bytes(var fragment)] }
                || number < 1 || number > int.MaxValue || count < 1 || count > MaximumParts
                || length < 1 || length > MaximumMessageLength || checksum > uint.MaxValue
                || fragment.Length == 0 || (ulong)fragment.Length * count < length)
                throw new DecodeError(DecodeError.Reason.BadPart);
            return new FountainPart((int)number, (int)count, (int)length, (uint)checksum, fragment);
        }

        public byte[] CBORBytes => new CBOR.Array([
            new CBOR.Unsigned((ulong)Number), new CBOR.Unsigned((ulong)Count), new CBOR.Unsigned((ulong)MessageLength),
            new CBOR.Unsigned(Checksum), new CBOR.Bytes(Fragment)]).Encoded;
    }

    // MARK: Encoding

    /// <summary>
    /// The QR texts that carry <c>message</c> (raw bytes, wrapped here as a CBOR byte string),
    /// upper case for QR's alphanumeric mode: one single-part UR when it fits
    /// <c>maximumFragment</c> bytes, else the pure fragments <c>1-m … m-m</c>, to be shown in a loop.
    /// </summary>
    public static IReadOnlyList<string> Encode(byte[] message, int maximumFragment)
    {
        var cbor = new CBOR.Bytes(message).Encoded;
        if (cbor.Length <= maximumFragment) return ["UR:BYTES/" + Bytewords(cbor).ToUpperInvariant()];
        var count = (cbor.Length + maximumFragment - 1) / maximumFragment;
        var length = (cbor.Length + count - 1) / count;
        var padded = new byte[length * count];
        cbor.CopyTo(padded, 0);
        var checksum = CRC32.Checksum(cbor);
        return Enumerable.Range(1, count).Select(number =>
        {
            var fragment = padded[((number - 1) * length)..(number * length)];
            var part = new FountainPart(number, count, cbor.Length, checksum, fragment);
            return $"UR:BYTES/{number}-{count}/" + Bytewords(part.CBORBytes).ToUpperInvariant();
        }).ToList();
    }

    // MARK: Fountain codes

    /// <summary>
    /// The fragment indexes part <c>number</c> mixes: itself for the first <c>count</c>, then a
    /// degree and a shuffle drawn from Xoshiro256** seeded with SHA-256(number ‖ checksum), as the
    /// reference implementations do.
    /// </summary>
    public static IReadOnlyList<int> FragmentIndexes(int number, int count, uint checksum)
    {
        if (number <= count) return [number - 1];
        var rng = new Xoshiro256(SHA256.HashData([.. BigEndian(unchecked((uint)number)), .. BigEndian(checksum)]));
        var sampler = new AliasSampler(Enumerable.Range(1, count).Select(i => 1 / (double)i).ToArray());
        var degree = sampler.Next(rng) + 1;
        var remaining = Enumerable.Range(0, count).ToList();
        var shuffled = new List<int>();
        while (remaining.Count > 0)
        {
            var index = rng.NextInt(0, remaining.Count - 1);
            shuffled.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        return shuffled.Take(degree).ToList();
    }

    public sealed class Xoshiro256
    {
        private readonly ulong[] s;

        public Xoshiro256(byte[] seed)
        {
            s = Enumerable.Range(0, 4).Select(i => seed.Skip(i * 8).Take(8).Aggregate(0UL, (a, b) => a << 8 | b)).ToArray();
        }

        private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        public ulong Next()
        {
            var result = unchecked(Rotl(s[1] * 5, 7) * 9);
            var t = s[1] << 17;
            s[2] ^= s[0];
            s[3] ^= s[1];
            s[1] ^= s[2];
            s[0] ^= s[3];
            s[2] ^= t;
            s[3] = Rotl(s[3], 45);
            return result;
        }

        public double NextDouble() => Next() / 18_446_744_073_709_551_616.0;

        public int NextInt(int low, int high) => (int)Math.Floor(NextDouble() * (high - low + 1)) + low;
    }

    /// <summary>Vose's alias method, laid out exactly as the reference sampler builds its tables.</summary>
    public sealed class AliasSampler
    {
        private readonly double[] probabilities;
        private readonly int[] aliases;

        public AliasSampler(double[] p)
        {
            var n = p.Length;
            var sum = p.Sum();
            var scaled = p.Select(x => x * n / sum).ToArray();
            probabilities = new double[n];
            aliases = new int[n];
            var small = new List<int>();
            var large = new List<int>();
            for (var i = n - 1; i >= 0; i--)
            {
                if (scaled[i] < 1) small.Add(i); else large.Add(i);
            }
            while (small.Count > 0 && large.Count > 0)
            {
                var less = small[^1];
                var more = large[^1];
                small.RemoveAt(small.Count - 1);
                large.RemoveAt(large.Count - 1);
                probabilities[less] = scaled[less];
                aliases[less] = more;
                scaled[more] = (scaled[more] + scaled[less]) - 1;
                if (scaled[more] < 1) small.Add(more); else large.Add(more);
            }
            while (large.Count > 0) { probabilities[large[^1]] = 1; large.RemoveAt(large.Count - 1); }
            while (small.Count > 0) { probabilities[small[^1]] = 1; small.RemoveAt(small.Count - 1); }
        }

        public int Next(Xoshiro256 rng)
        {
            var column = (int)Math.Floor(rng.NextDouble() * probabilities.Length);
            return rng.NextDouble() < probabilities[column] ? column : aliases[column];
        }
    }

    /// <summary>Collects the frames of a UR in any order, mixed parts included, until the message is whole.</summary>
    public sealed class Decoder
    {
        private int count;
        private int messageLength;
        private uint checksum;
        private int fragmentLength;
        private Dictionary<int, byte[]> simple = [];
        private List<(HashSet<int> Indexes, byte[] Fragment)> mixed = [];
        private HashSet<int> seenNumbers = [];
        public byte[]? Message { get; private set; }

        public abstract record Outcome
        {
            public sealed record NeedMore : Outcome;
            /// <summary>The UR's payload: for <c>bytes</c>, the CBOR byte string's contents.</summary>
            public sealed record Complete(byte[] Payload) : Outcome
            {
                public bool Equals(Complete? other) => other is not null && Payload.AsSpan().SequenceEqual(other.Payload);
                public override int GetHashCode() => Payload.Length;
            }
        }

        public bool IsEmpty => count == 0;
        public int ExpectedParts => count;
        public int ReceivedFragments => simple.Count;
        /// <summary>0 to 1, by fragments recovered so far.</summary>
        public double Progress => count == 0 ? 0 : (double)simple.Count / count;

        private void Reset()
        {
            count = 0;
            messageLength = 0;
            checksum = 0;
            fragmentLength = 0;
            simple = [];
            mixed = [];
            seenNumbers = [];
            Message = null;
        }

        /// <summary>Takes one scanned part. A part of another message starts a new collection.</summary>
        public Outcome Receive(Part part)
        {
            if (part.Sequence is not { } sequence)
            {
                Reset();
                return new Outcome.Complete(PayloadOf(part.Body));
            }
            var fountain = FountainPart.FromCBOR(part.Body);
            if (fountain.Number != sequence.Number || fountain.Count != sequence.Count) throw new DecodeError(DecodeError.Reason.BadPart);
            if (fountain.Count != count || fountain.MessageLength != messageLength || fountain.Checksum != checksum || fountain.Fragment.Length != fragmentLength)
            {
                Reset();
                count = fountain.Count;
                messageLength = fountain.MessageLength;
                checksum = fountain.Checksum;
                fragmentLength = fountain.Fragment.Length;
            }
            if (!seenNumbers.Add(fountain.Number)) return new Outcome.NeedMore();
            var indexes = FragmentIndexes(fountain.Number, count, checksum).ToHashSet();
            var queue = new List<(HashSet<int> Indexes, byte[] Fragment)> { (indexes, fountain.Fragment) };
            while (queue.Count > 0 && Message is null)
            {
                var current = queue[0];
                queue.RemoveAt(0);
                // Reduce by every fragment already known.
                foreach (var index in current.Indexes.ToList())
                {
                    if (simple.TryGetValue(index, out var known) && current.Indexes.Count > 1)
                    {
                        current.Indexes.Remove(index);
                        current.Fragment = Xor(current.Fragment, known);
                    }
                }
                if (current.Indexes.Count == 1)
                {
                    var index = current.Indexes.First();
                    if (simple.ContainsKey(index)) continue;
                    simple[index] = current.Fragment;
                    if (simple.Count == count)
                    {
                        Finish();
                        break;
                    }
                    // A new fragment may reduce the mixed parts held back.
                    var held = new List<(HashSet<int> Indexes, byte[] Fragment)>();
                    foreach (var m in mixed)
                    {
                        if (m.Indexes.Contains(index))
                        {
                            var rest = new HashSet<int>(m.Indexes);
                            rest.Remove(index);
                            queue.Add((rest, Xor(m.Fragment, current.Fragment)));
                        }
                        else
                        {
                            held.Add(m);
                        }
                    }
                    mixed = held;
                }
                else if (current.Indexes.Count > 1 && !mixed.Any(m => m.Indexes.SetEquals(current.Indexes)))
                {
                    if (mixed.Count < 4 * MaximumParts) mixed.Add(current);
                }
            }
            if (Message is { } message) return new Outcome.Complete(PayloadOf(message));
            return new Outcome.NeedMore();
        }

        private void Finish()
        {
            var joined = Enumerable.Range(0, count).SelectMany(i => simple[i]).Take(messageLength).ToArray();
            if (CRC32.Checksum(joined) != checksum)
            {
                Reset();
                throw new DecodeError(DecodeError.Reason.BadChecksum);
            }
            Message = joined;
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var output = new byte[Math.Min(a.Length, b.Length)];
            for (var i = 0; i < output.Length; i++) output[i] = (byte)(a[i] ^ b[i]);
            return output;
        }

        private static byte[] PayloadOf(byte[] cbor) =>
            CBOR.Decode(cbor) is CBOR.Bytes(var bytes) ? bytes : throw new DecodeError(DecodeError.Reason.UnsupportedType);
    }
}

/// <summary>CRC-32 (IEEE 802.3), as Bytewords, the fountain parts and gzip use.</summary>
public static class CRC32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in bytes) crc = Table[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc ^ 0xFFFF_FFFF;
    }

    public static uint Checksum(byte[] bytes) => Checksum(bytes.AsSpan());
}

/// <summary>
/// The small part of CBOR (RFC 8949) the UR parts and AirGap's messages use: unsigned and negative
/// integers, byte and text strings, arrays, maps and the simple values. Definite lengths only;
/// depth and sizes are bounded.
/// </summary>
public abstract record CBOR
{
    public sealed record Unsigned(ulong Value) : CBOR;
    /// <summary>-1 - n.</summary>
    public sealed record Negative(ulong Value) : CBOR;
    public sealed record Bytes(byte[] Value) : CBOR
    {
        public bool Equals(Bytes? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);
        public override int GetHashCode() => Value.Length;
    }
    public sealed record Text(string Value) : CBOR;
    public sealed record Array(IReadOnlyList<CBOR> Items) : CBOR
    {
        public bool Equals(Array? other) => other is not null && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => Items.Count;
    }
    public sealed record Map(IReadOnlyList<(CBOR Key, CBOR Value)> Pairs) : CBOR
    {
        public bool Equals(Map? other) => other is not null && Pairs.SequenceEqual(other.Pairs);
        public override int GetHashCode() => Pairs.Count;
    }
    public sealed record Bool(bool Value) : CBOR;
    public sealed record Null : CBOR;
    public sealed record Undefined : CBOR;

    public sealed class DecodeError(DecodeError.Reason reason) : Exception($"CBOR: {reason}")
    {
        public enum Reason { Truncated, Unsupported, TooDeep, TooLarge, TrailingBytes }
        public Reason Kind { get; } = reason;
    }

    public const int MaximumDepth = 16;
    public const int MaximumItems = 4096;

    public static CBOR Decode(byte[] bytes)
    {
        var reader = new Reader(bytes);
        var value = reader.Read(0);
        if (reader.Offset != bytes.Length) throw new DecodeError(DecodeError.Reason.TrailingBytes);
        return value;
    }

    private sealed class Reader(byte[] bytes)
    {
        public int Offset;
        private int items;

        private byte Byte()
        {
            if (Offset >= bytes.Length) throw new DecodeError(DecodeError.Reason.Truncated);
            return bytes[Offset++];
        }

        private byte[] Take(ulong n)
        {
            if (n > (ulong)(bytes.Length - Offset)) throw new DecodeError(DecodeError.Reason.Truncated);
            var output = bytes[Offset..(Offset + (int)n)];
            Offset += (int)n;
            return output;
        }

        private ulong Argument(byte info) => info switch
        {
            <= 23 => info,
            24 => Byte(),
            25 or 26 or 27 => Take((ulong)(1 << (info - 24))).Aggregate(0UL, (a, b) => a << 8 | b),
            _ => throw new DecodeError(DecodeError.Reason.Unsupported),
        };

        public CBOR Read(int depth)
        {
            if (depth > MaximumDepth) throw new DecodeError(DecodeError.Reason.TooDeep);
            items += 1;
            if (items > MaximumItems) throw new DecodeError(DecodeError.Reason.TooLarge);
            var initial = Byte();
            var major = initial >> 5;
            var info = (byte)(initial & 0x1f);
            switch (major)
            {
                case 0: return new Unsigned(Argument(info));
                case 1: return new Negative(Argument(info));
                case 2: return new Bytes(Take(Argument(info)));
                case 3:
                    try { return new Text(new UTF8Encoding(false, true).GetString(Take(Argument(info)))); }
                    catch (DecoderFallbackException) { throw new DecodeError(DecodeError.Reason.Unsupported); }
                case 4:
                {
                    var n = Argument(info);
                    if (n > MaximumItems) throw new DecodeError(DecodeError.Reason.TooLarge);
                    var list = new List<CBOR>();
                    for (ulong i = 0; i < n; i++) list.Add(Read(depth + 1));
                    return new Array(list);
                }
                case 5:
                {
                    var n = Argument(info);
                    if (n > MaximumItems) throw new DecodeError(DecodeError.Reason.TooLarge);
                    var pairs = new List<(CBOR, CBOR)>();
                    for (ulong i = 0; i < n; i++) pairs.Add((Read(depth + 1), Read(depth + 1)));
                    return new Map(pairs);
                }
                case 7:
                    return info switch
                    {
                        20 => new Bool(false),
                        21 => new Bool(true),
                        22 => new Null(),
                        23 => new Undefined(),
                        _ => throw new DecodeError(DecodeError.Reason.Unsupported),
                    };
                default: throw new DecodeError(DecodeError.Reason.Unsupported);
            }
        }
    }

    public byte[] Encoded => this switch
    {
        Unsigned u => Head(0, u.Value),
        Negative n => Head(1, n.Value),
        Bytes b => [.. Head(2, (ulong)b.Value.Length), .. b.Value],
        Text t => [.. Head(3, (ulong)Encoding.UTF8.GetByteCount(t.Value)), .. Encoding.UTF8.GetBytes(t.Value)],
        Array a => [.. Head(4, (ulong)a.Items.Count), .. a.Items.SelectMany(i => i.Encoded)],
        Map m => [.. Head(5, (ulong)m.Pairs.Count), .. m.Pairs.SelectMany(p => p.Key.Encoded.Concat(p.Value.Encoded))],
        Bool b => [b.Value ? (byte)0xf5 : (byte)0xf4],
        Null => [0xf6],
        Undefined => [0xf7],
        _ => throw new InvalidOperationException(),
    };

    private static byte[] Head(byte major, ulong n)
    {
        var m = (byte)(major << 5);
        return n switch
        {
            <= 23 => [(byte)(m | (byte)n)],
            <= 0xff => [(byte)(m | 24), (byte)n],
            <= 0xffff => [(byte)(m | 25), (byte)(n >> 8), (byte)n],
            <= 0xffff_ffff => [(byte)(m | 26), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n],
            _ => [(byte)(m | 27), .. Enumerable.Range(0, 8).Reverse().Select(i => (byte)(n >> (8 * i)))],
        };
    }
}
