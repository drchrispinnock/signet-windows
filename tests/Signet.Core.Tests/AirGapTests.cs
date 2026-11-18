using System.Text.Json;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

/// <summary>
/// AirGap Vault's QR codes: Uniform Resources, fountain codes and the IAC v3 messages. The vectors
/// were made with AirGap's own libraries (<c>@airgap/serializer</c> 0.14.0 and
/// <c>@ngraveio/bc-ur</c> 1.1.13), and AirGap's decoder reads what <c>UniformResource.Encode</c> writes.
/// </summary>
public class AirGapTests
{
    /// <summary>
    /// An account share for public key a0b1…8e9f, path m/44h/1729h/0h/0h, fingerprint f4e3d2c1,
    /// group "My Vault", message id 12345678.
    /// </summary>
    private const string ShareSingle = "UR:BYTES/HDHLCTLUAYAEAEAEAEAEAEAXJEIDJTJZIHIEGAPLDEPTMOIDTPMUVSTSHYMKPYJLIDMOOYJLISJTIENLOYJLAEFWPLGSHECKHEFTMKVYHPPTBEMTHDNYGUWZYKBDFYCXFWMEINSESWFXMSNEKEIHBWTSPFWKLETOPTWENDMYTOATAEWLWESRWMHHAEAEAEFLMOLTWN";
    /// <summary>The same share as an animated code of three fragments: parts 1 to 3 pure, 4 to 9 mixed.</summary>
    private static readonly string[] ShareParts =
    [
        "ur:bytes/1-3/lpadaxcshecyflmoltwnhdcxhdhlctluayaeaeaeaeaeaeaxjeidjtjzihiegapldeptmoidtpmuvstshymkpyjlhfpabyga",
        "ur:bytes/2-3/lpaoaxcshecyflmoltwnhdcxidmooyjlisjtienloyjlaefwplgsheckheftmkvyhpptbemthdnyguwzykbdfycxnljnbgwp",
        "ur:bytes/3-3/lpaxaxcshecyflmoltwnhdcxfwmeinseswfxmsnekeihbwtspfwkletoptwendmytoataewlwesrwmhhaeaeaeaetykovlcl",
        "ur:bytes/4-3/lpaaaxcshecyflmoltwnhdcxidmooyjlisjtienloyjlaefwplgsheckheftmkvyhpptbemthdnyguwzykbdfycxtijycprl",
        "ur:bytes/5-3/lpahaxcshecyflmoltwnhdcxfwmeinseswfxmsnekeihbwtspfwkletoptwendmytoataewlwesrwmhhaeaeaeaentjltekn",
        "ur:bytes/6-3/lpamaxcshecyflmoltwnhdcxfwmeinseswfxmsnekeihbwtspfwkletoptwendmytoataewlwesrwmhhaeaeaeaeghhpspkt",
        "ur:bytes/7-3/lpataxcshecyflmoltwnhdcxkshytsdaoldpwfamutbkbwmtkptnrkrfmuqdgertryatlfcajnsggdkkpymuwsgwbznbdsdk",
        "ur:bytes/8-3/lpayaxcshecyflmoltwnhdcxidmooyjlisjtienloyjlaefwplgsheckheftmkvyhpptbemthdnyguwzykbdfycxfwfgfwad",
        "ur:bytes/9-3/lpasaxcshecyflmoltwnhdcxfwmeinseswfxmsnekeihbwtspfwkletoptwendmytoataewlwesrwmhhaeaeaeaebshlqdsf",
    ];
    /// <summary>A sign request (id 87654321) for 40 × ab then 6c00, as AirGap Wallet writes it.</summary>
    private const string RequestSingle = "ur:bytes/hdhgctluayaeaeaeaeaeaeaxjeidjtjzihiegtpldeptmoidrepsurtphhnsmkhkmwnnhdnbhpnnmkmumuhtidonpejlneidcwoyspqzhnvloysbgwrnprldjehdknfevdtyynsnflvdemfgiseopmdsbgvedyaeaecsnncygukpaeaeaesfmtpssb";
    /// <summary>Its response: the same bytes followed by 64 × cd.</summary>
    private const string ResponseSingle = "ur:bytes/hdeectluayaeaeaeaeaeaeaxjeidjtjzihiegrpldeptmoidrepsurtpvesaghuyemfhcpndinecmecxltvydwlpaeaekovydwwflfaeaeaegsaoadmt";
    private const string PublicKeyHex = "a0b1c2d3e4f5061728394a5b6c7d8e9fa0b1c2d3e4f5061728394a5b6c7d8e9f";
    private static readonly byte[] Forged = Hex.Bytes(string.Concat(Enumerable.Repeat("ab", 40)) + "6c00")!;
    private static byte[] Signature64 => Enumerable.Repeat((byte)0xcd, 64).ToArray();

    private static byte[]? Payload(IEnumerable<string> texts)
    {
        var decoder = new UniformResource.Decoder();
        foreach (var text in texts)
        {
            if (decoder.Receive(new UniformResource.Part(text)) is UniformResource.Decoder.Outcome.Complete(var payload)) return payload;
        }
        return null;
    }

    [Fact]
    public void ReadsAVaultAccountShare()
    {
        var messages = AirGap.Messages(Payload([ShareSingle])!);
        Assert.Single(messages);
        Assert.Equal(12_345_678UL, messages[0].Id);
        var accounts = AirGap.Accounts(messages);
        Assert.Equal([new AirGap.Account("edpkurzfBZJnGNxoUrkB4xPssgKEz3QssGGnCc1RcJh1rFFyLk9CiR", "m/44h/1729h/0h/0h", "f4e3d2c1", "My Vault")], accounts);
        Assert.Equal("airgap://f4e3d2c1/44h/1729h/0h/0h", accounts[0].Locator);
        Assert.Equal(KeyKind.Airgap, KeyKindExtensions.FromLocator(accounts[0].Locator));
        Assert.True(KeyKind.Airgap.CanSign());
        Assert.False(KeyKind.Airgap.SignsMessages());
        Assert.True(KeyKind.Ledger.SignsMessages() && KeyKind.Encrypted.SignsMessages());
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 })]
    [InlineData(new[] { 3, 2, 1 })]
    [InlineData(new[] { 2, 4, 5, 6, 7, 8, 9 })]
    [InlineData(new[] { 1, 7, 9 })]
    [InlineData(new[] { 4, 5, 6, 7, 8, 9 })]
    [InlineData(new[] { 3, 4, 5, 7 })]
    [InlineData(new[] { 1, 1, 2, 2, 3 })]
    public void JoinsAnimatedFramesInAnyOrder(int[] order)
    {
        var single = Payload([ShareSingle]);
        Assert.Equal(single, Payload(order.Select(i => ShareParts[i - 1])));
    }

    [Fact]
    public void WaitsForMissingFragments()
    {
        var decoder = new UniformResource.Decoder();
        Assert.IsType<UniformResource.Decoder.Outcome.NeedMore>(decoder.Receive(new UniformResource.Part(ShareParts[0])));
        // Part 7 mixes all three fragments, so it alone cannot add one.
        Assert.IsType<UniformResource.Decoder.Outcome.NeedMore>(decoder.Receive(new UniformResource.Part(ShareParts[6])));
        Assert.Equal(1, decoder.ReceivedFragments);
        Assert.Equal(3, decoder.ExpectedParts);
    }

    [Fact]
    public void RefusesACorruptedFrame()
    {
        var text = ShareSingle[..40] + "ZO" + ShareSingle[42..];
        Assert.Throws<UniformResource.DecodeError>(() => new UniformResource.Part(text));
        Assert.Throws<UniformResource.DecodeError>(() => new UniformResource.Part("ur:crypto-psbt/" + ShareSingle[9..]));
    }

    /// <summary>The reference implementation's choices of fragments for mixed parts of a 17-fragment message.</summary>
    [Fact]
    public void ChoosesTheReferenceFragments()
    {
        var expected = new Dictionary<int, int[]>
        {
            [18] = [11], [19] = [15], [20] = [3, 12, 13, 14], [22] = [0, 1, 2, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 16], [23] = [7, 14, 16],
            [28] = [2, 3, 4, 5, 6, 11, 12, 13, 16], [29] = [3, 5, 7, 8, 9], [32] = [2, 3, 4, 5, 11, 13, 16], [35] = [2, 5, 9, 13],
            [37] = [0, 1, 6, 10, 15, 16], [40] = [4, 13], [50] = [7], [123] = [14, 16],
        };
        foreach (var (number, indexes) in expected)
            Assert.True(indexes.ToHashSet().SetEquals(UniformResource.FragmentIndexes(number, 17, 0xdead_beef)), $"part {number}");
        Assert.True(new HashSet<int> { 1, 2 }.SetEquals(UniformResource.FragmentIndexes(4, 3, 0)));
        Assert.Equal([4], UniformResource.FragmentIndexes(5, 17, 0));
    }

    [Fact]
    public void WritesTheSignRequestAirGapWalletWrites()
    {
        var edpk = AirGap.Edpk(PublicKeyHex)!;
        var request = AirGap.SignRequest(edpk, Forged, 87_654_321)!;
        var theirs = AirGap.Messages(Payload([RequestSingle])!);
        Assert.Equal([request], theirs);
        // .NET's DEFLATE happens to match pako's here, so the code is the same too.
        Assert.Equal([RequestSingle.ToUpperInvariant()], UniformResource.Encode(AirGap.PayloadFor([request]), 2000));
    }

    [Fact]
    public void SplitsALongRequestIntoFramesThatJoinAgain()
    {
        var edpk = AirGap.Edpk(PublicKeyHex)!;
        byte[] forged = [.. Enumerable.Repeat((byte)0x5a, 3000), .. Enumerable.Range(0, 255).Select(i => (byte)i)];
        var request = AirGap.SignRequest(edpk, forged, 11_112_222)!;
        var message = AirGap.PayloadFor([request]);
        var frames = UniformResource.Encode(message, 200);
        Assert.True(frames.Count > 1);
        Assert.All(frames, f => Assert.StartsWith("UR:BYTES/", f));
        Assert.Equal(message, Payload(frames.Reverse()));
        Assert.Equal([request], AirGap.Messages(message));
    }

    [Fact]
    public void ReadsTheSignedOperation()
    {
        var messages = AirGap.Messages(Payload([ResponseSingle])!);
        Assert.Equal([.. Forged, .. Signature64], AirGap.SignedOperation(messages, 87_654_321));
        var e = Assert.Throws<AirGap.Error>(() => AirGap.SignedOperation(messages, 11_111_111));
        Assert.Equal(AirGap.Error.Reason.WrongResponse, e.Kind);
    }

    [Fact]
    public void WritesStringsAsAirGapDoes()
    {
        foreach (var text in new[] { "", "My Vault", "m/44h/1729h/0h/0h", "abcd", "abc", "0xabcd", "0xabc", "0xnothex", "f4e3d2c1" })
            Assert.Equal(text, AirGap.DecodeString(AirGap.EncodeString(text)));
        Assert.Equal(new CBOR.Bytes([2, 0xf4, 0xe3, 0xd2, 0xc1]), AirGap.EncodeString("f4e3d2c1"));
        Assert.Equal(new CBOR.Text("My Vault"), AirGap.EncodeString("My Vault"));
    }

    [Fact]
    public void GzipRoundTrips()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("Signet and AirGap Vault, Signet and AirGap Vault");
        Assert.Equal(data, Gzip.Decompress(Gzip.Compress(data), 1000));
        Assert.Null(Gzip.Decompress(Gzip.Compress(data), 10));
        Assert.Null(Gzip.Decompress(data, 1000));
    }

    [Fact]
    public void ClassifiesAirGapCodes()
    {
        Assert.IsType<QRPayload.UniformResourceContent>(QRPayload.Classify(ShareSingle));
        Assert.IsType<QRPayload.UniformResourceContent>(QRPayload.Classify(ShareParts[4]));
        var body = ShareSingle["UR:BYTES/".Length..];
        var link = Assert.IsType<QRPayload.UniformResourceContent>(QRPayload.Classify($"airgap-wallet://?ur={body}"));
        Assert.Equal(new UniformResource.Part(ShareSingle), link.Part);
        Assert.Equal(new QRPayload.AirGapLegacy(), QRPayload.Classify("airgap-wallet://?d=3vQB7B6MrGQZaxCuFg4oh"));
        Assert.Equal(new QRPayload.Unknown(), QRPayload.Classify("ur:bytes/nonsense"));
        Assert.False(QRPayload.Classify(ShareSingle).IsSecret);
    }

    [Fact]
    public void SkipsOtherCoinsAndExtendedKeys()
    {
        var other = new AirGap.Message((ulong)AirGap.MessageType.AccountShareResponse, "eth", 1, []);
        Assert.Empty(AirGap.Accounts([other]));
        var messages = AirGap.Messages(Payload([ShareSingle])!);
        var payload = messages[0].Payload.ToList();
        payload[4] = new CBOR.Bool(true);
        var extended = messages[0] with { Payload = payload };
        var e = Assert.Throws<AirGap.Error>(() => AirGap.Accounts([extended]));
        Assert.Equal(AirGap.Error.Reason.Malformed, e.Kind);
    }

    [Fact]
    public void TakesTheSignatureOnlyForTheBytesSent()
    {
        var messages = AirGap.Messages(Payload([ResponseSingle])!);
        Assert.Equal(Signature64, AirGap.Signature(messages, 87_654_321, Forged));
        Assert.Equal(AirGap.Error.Reason.DifferentOperation,
            Assert.Throws<AirGap.Error>(() => AirGap.Signature(messages, 87_654_321, [.. Forged[..^1], 0x01])).Kind);
        Assert.Equal(AirGap.Error.Reason.DifferentOperation,
            Assert.Throws<AirGap.Error>(() => AirGap.Signature(messages, 87_654_321, Forged[..^1])).Kind);
        Assert.Equal(AirGap.Error.Reason.WrongResponse,
            Assert.Throws<AirGap.Error>(() => AirGap.Signature(messages, 12_121_212, Forged)).Kind);
    }

    [Fact]
    public void ASigningSessionShowsTheRequestAndReadsTheReply()
    {
        var edpk = AirGap.Edpk(PublicKeyHex)!;
        var session = AirGapSignSession.Create("tz1", edpk, Forged, 87_654_321)!;
        Assert.Equal([RequestSingle.ToUpperInvariant()], session.Frames);
        byte[]? signature = null;
        Exception? failure = null;
        var finished = false;
        session.OnFinish = (s, e) => { finished = true; signature = s; failure = e; };

        session.Receive(RequestSingle);  // our own code, seen by the camera by mistake
        Assert.False(finished);
        Assert.Contains("Signet's own request", session.Note);
        session.Receive("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        Assert.False(finished);
        session.Receive(ResponseSingle);
        Assert.Null(failure);
        Assert.Equal(Signature64, signature);
    }

    [Fact]
    public void ASessionRefusesAReplyForOtherBytes()
    {
        var edpk = AirGap.Edpk(PublicKeyHex)!;
        var session = AirGapSignSession.Create("tz1", edpk, [.. Forged, 0], 87_654_321)!;
        Exception? failure = null;
        session.OnFinish = (_, e) => failure = e;
        session.Receive(ResponseSingle);
        Assert.Equal(new AirGapSigning.Failure.DifferentOperation(), failure);
        var other = AirGapSignSession.Create("tz1", edpk, Forged, 11_111_111)!;
        var finished = false;
        other.OnFinish = (_, _) => finished = true;
        other.Receive(ResponseSingle);
        Assert.False(finished);
        Assert.Contains("not the Vault's reply", other.Note);
        other.Cancel();
        Assert.True(finished);
    }

    [Fact]
    public void AnAirGapAccountSignsThroughTheVault()
    {
        var edpk = AirGap.Edpk(PublicKeyHex)!;
        var key = new SigningKey.Airgap(edpk, new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"));
        var spec = JsonSerializer.Deserialize<Dictionary<string, string>>(key.BridgeSpec);
        Assert.Equal(new Dictionary<string, string> { ["kind"] = "airgap", ["publicKey"] = edpk, ["address"] = "tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb" }, spec);
        Assert.False(key.IsLedger);
    }
}
