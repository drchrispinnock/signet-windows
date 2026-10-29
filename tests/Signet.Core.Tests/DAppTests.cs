using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class DAppRequestParsingTests
{
    [Fact]
    public void ParsesPermissionRequest()
    {
        const string json = """
        {"kind":"request","message":{"id":"req-1","type":"permission_request","version":"3","senderId":"abc",
          "appMetadata":{"senderId":"abc","name":"objkt.com","icon":"https://objkt.com/icon.png"},
          "network":{"type":"mainnet"},"scopes":["operation_request","sign"]}}
        """;
        var request = DAppRequest.Parse(json);
        var permission = Assert.IsType<DAppRequest.Permission>(request);
        Assert.Equal("req-1", permission.Id);
        Assert.Equal("objkt.com", permission.App.Name);
        Assert.Equal("objkt.com", permission.App.Icon?.Host);
        Assert.Equal("mainnet", permission.NetworkType);
        Assert.Null(permission.RpcURL);
        Assert.Equal(["operation_request", "sign"], permission.Scopes);
        // Upstream also checks `request.isPermission`, a view-model helper; it lands with WalletViewModel.
    }

    [Fact]
    public void ParsesOperationAndSignRequests()
    {
        const string op = """
        {"kind":"request","message":{"id":"req-2","type":"operation_request","appMetadata":{"name":"dapp"},
          "network":{"type":"custom","rpcUrl":"https://rpc.shadownet.teztnets.com"},"sourceAddress":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb",
          "operationDetails":[{"kind":"transaction","destination":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton","amount":"0","parameters":{"entrypoint":"collect","value":{"int":"1"}}}]}}
        """;
        var operation = Assert.IsType<DAppRequest.Operation>(DAppRequest.Parse(op));
        Assert.Equal("custom", operation.NetworkType);
        Assert.Equal("rpc.shadownet.teztnets.com", operation.RpcURL?.Host);
        Assert.StartsWith("tz1", operation.Source.Value);
        Assert.Contains("\"entrypoint\":\"collect\"", operation.OperationsJSON);

        const string sign = """
        {"kind":"request","message":{"id":"req-3","type":"sign_payload_request","appMetadata":{"name":"dapp"},
          "sourceAddress":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb","signingType":"micheline","payload":"0501000000076865792074657a"}}
        """;
        var signPayload = Assert.IsType<DAppRequest.SignPayload>(DAppRequest.Parse(sign));
        Assert.Equal("micheline", signPayload.SigningType);
        Assert.StartsWith("0501", signPayload.Payload);

        Assert.Null(DAppRequest.Parse("{\"kind\":\"other\"}"));
    }

    [Fact]
    public void MapsRequestNetworksOntoOurs()
    {
        Assert.Equal(Network.Mainnet, DAppRequest.NetworkFor("mainnet", null));
        Assert.Equal(Network.Shadownet, DAppRequest.NetworkFor("shadownet", null));
        Assert.Equal(Network.Ushuaianet, DAppRequest.NetworkFor("ushuaianet", null));
        Assert.Equal(Network.Shadownet, DAppRequest.NetworkFor("custom", new Uri("https://rpc.shadownet.teztnets.com")));
        Assert.Equal("custom", DAppRequest.NetworkFor("custom", new Uri("https://rpc.example.org"))?.Chain);
        Assert.Null(DAppRequest.NetworkFor("ghostnet", null));
    }
}

public class OctezConnectBridgeTests
{
    /// <summary>
    /// Round-trips through the SDK's serializer offline: the bridge must at least load the SDK
    /// and understand a pairing code.
    /// </summary>
    [Fact]
    public async Task BridgeLoadsTheSDKAndDescribesOperations()
    {
        var described = await TaquitoBridge.Shared.Call("octezConnectDescribe",
            """[{"kind":"transaction","destination":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton","amount":"1500000","parameters":{"entrypoint":"collect","value":{"int":"1"}}},{"kind":"delegation","delegate":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"}]""");
        var list = described.ArrayValue;
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Equal(new JSONValue.String("collect"), list[0]["entrypoint"]);
        Assert.Equal(new JSONValue.String("1500000"), list[0]["amountMutez"]);
        Assert.StartsWith("tz1", list[1]["delegate"]?.StringValue);
    }

    [Fact(Timeout = 120_000), Trait(Category.Name, Category.Network)]
    public async Task StartsTheWalletClientAgainstTheRelay()
    {
        var token = TestContext.Current.CancellationToken;
        TaquitoBridge.Shared.Storage = new InMemoryBridgeStorage();
        var result = await TaquitoBridge.Shared.Call("octezConnectStart", "Signet tests", "").WaitAsync(token);
        Assert.Equal(new JSONValue.Bool(true), result["started"]);
        var permissions = await TaquitoBridge.Shared.Call("octezConnectPermissions").WaitAsync(token);
        Assert.Empty(permissions.ArrayValue ?? [JSONValue.NullValue]);
        // Missing storage keys must read as the SDK's defaults (empty lists), not undefined.
        var peers = await TaquitoBridge.Shared.Call("octezConnectPeers").WaitAsync(token);
        Assert.Empty(peers.ArrayValue ?? [JSONValue.NullValue]);
    }
}

/// <summary>
/// S02: a sign_payload whose bytes start with 03 would be an operation signature, so only packed
/// Michelson (05…) of type <c>micheline</c> may reach the signer.
/// </summary>
public class DAppSignPayloadTests
{
    internal const string Forged = "03" + "8fcf233671b6a04fcf679d2a381c2544ea6c1ea29ba6157776ed8424c7ccd00b6c0002298c03ed7d454a101eb7022bc95f7e5f41ac78d0860303c8010080c2d72f0000e7670f32038107a59a2b9cfefae36ea21f5aa63c00";

    [Fact]
    public void AcceptsPackedMichelineMessages()
    {
        Assert.Null(DAppSignPayload.Refusal("micheline", "0501000000076865792074657a"));
        Assert.Null(DAppSignPayload.Refusal("micheline", "05070701000000036162630001"));
        Assert.Null(DAppSignPayload.Refusal("micheline", "0501000000076865792074657A"));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("raw")]
    [InlineData("")]
    [InlineData("MICHELINE")]
    public void RefusesOtherSigningTypes(string type)
    {
        Assert.NotNull(DAppSignPayload.Refusal(type, "0501000000076865792074657a"));
    }

    [Fact]
    public void RefusesOperationBytesWhateverTheTypeSays()
    {
        // 03 + forged transfer: the same hash the chain signs for a real transaction.
        Assert.NotNull(DAppSignPayload.Refusal("micheline", Forged));
        Assert.NotNull(DAppSignPayload.Refusal("operation", Forged));
        Assert.NotNull(DAppSignPayload.Refusal("raw", Forged));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("051")]
    [InlineData("05zz")]
    [InlineData("0x0501")]
    [InlineData("0501 00")]
    public void RefusesMalformedHex(string payload)
    {
        Assert.NotNull(DAppSignPayload.Refusal("micheline", payload));
    }

    /// <summary>The reviewer's gap: the 05 prefix alone is not enough, the rest must be one whole expression.</summary>
    [Theory]
    [InlineData("05")]                                  // prefix alone
    [InlineData("050100000007686579")]                  // string truncated: declares 7 bytes, has 3
    [InlineData("0501000000026865792074657a")]          // declared length too short: trailing bytes
    [InlineData("0501000000076865792074657a00")]        // a complete string, then a stray byte
    [InlineData("0500")]                                // zarith with no bytes
    [InlineData("050080")]                              // zarith continuation never ends
    [InlineData("0502000000040100000001")]              // sequence whose element overruns its length
    [InlineData("0507070701000000016100")]              // Pair with a missing second argument
    [InlineData("050a00000003abcd")]                    // bytes truncated
    [InlineData("0509030000000000")]                    // generic prim: annots length missing
    [InlineData("050b")]                                // unknown tag
    public void RefusesMalformedMichelineEnvelopes(string payload)
    {
        Assert.NotNull(DAppSignPayload.Refusal("micheline", payload));
        Assert.False(MichelineBinary.IsPackedExpression(payload));
    }

    [Theory]
    [InlineData("0501000000076865792074657a")]          // "hey tez"
    [InlineData("050021")]                              // int 33
    [InlineData("050080ff7f")]                          // multi-byte int
    [InlineData("050a00000002abcd")]                    // bytes
    [InlineData("05030b")]                              // Unit
    [InlineData("050707010000000161010000000162")]      // Pair "a" "b"
    public void AcceptsWellFormedMichelineEnvelopes(string payload)
    {
        Assert.Null(DAppSignPayload.Refusal("micheline", payload));
    }

    [Fact]
    public void AcceptsNestedExpressions()
    {
        // Pair (Pair "a" 1) { "b" ; 0x00 } with an annotation on the outer Pair, as a permit might pack.
        const string inner = "0707" + "0100000001" + "61" + "0001";                      // Pair "a" 1
        const string seq = "02" + "0000000c" + "0100000001" + "62" + "0a0000000100";      // { "b" ; 0x00 } (12 bytes)
        const string outer = "0807" + inner + seq + "00000002" + "2561";                // Pair ... ... %a
        Assert.True(MichelineBinary.IsPackedExpression("05" + outer));
        Assert.Null(DAppSignPayload.Refusal("micheline", "05" + outer));
        // The same expression with the annotation length claiming one byte too many.
        const string overlong = "0807" + inner + seq + "00000003" + "2561";
        Assert.False(MichelineBinary.IsPackedExpression("05" + overlong));
    }

    [Fact]
    public void RefusesOversizedPayloads()
    {
        var huge = "05" + string.Concat(Enumerable.Repeat("00", DAppSignPayload.MaxHexLength / 2));
        Assert.NotNull(DAppSignPayload.Refusal("micheline", huge));
    }

    [Fact]
    public void RequestReportsItsOwnRefusal()
    {
        static DAppRequest Request(string type, string payload)
        {
            var json = $$$"""
            {"kind":"request","message":{"id":"req-9","type":"sign_payload_request","appMetadata":{"name":"dapp"},
              "sourceAddress":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb","signingType":"{{{type}}}","payload":"{{{payload}}}"}}
            """;
            return DAppRequest.Parse(json) ?? throw new Xunit.Sdk.XunitException("did not parse");
        }
        Assert.Null(Request("micheline", "0501000000076865792074657a").SignRefusal);
        Assert.NotNull(Request("operation", "03ab").SignRefusal);
        Assert.NotNull(Request("micheline", "03ab").SignRefusal);
    }

    /// <summary>The bridge enforces the rule itself, so a C# bug cannot get an operation signed.</summary>
    [Fact]
    public async Task BridgeRefusesOperationBytesAndSignsMicheline()
    {
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var spec = new SigningKey.Secret(material.SecretKey, null, new Address(material.Address)).BridgeSpec;
        foreach (var type in new[] { "micheline", "operation", "raw" })
            await Assert.ThrowsAnyAsync<Exception>(() => TaquitoBridge.Shared.Call("octezConnectSign", spec, Forged, type));
        await Assert.ThrowsAnyAsync<Exception>(() => TaquitoBridge.Shared.Call("octezConnectSign", spec, "0501000000076865792074657a"));
        foreach (var payload in MichelineDepthTests.RejectedPayloads)
            await Assert.ThrowsAnyAsync<Exception>(() => TaquitoBridge.Shared.Call("octezConnectSign", spec, payload, "micheline"));
        foreach (var payload in MichelineDepthTests.AcceptedPayloads)
        {
            var result = await TaquitoBridge.Shared.Call("octezConnectSign", spec, payload, "micheline");
            Assert.StartsWith("edsig", result["signature"]?.StringValue);
        }
        // The bridge's own Micheline reader must agree with MichelineBinary on every vector above.
        foreach (var malformed in new[] { "05", "050100000007686579", "0501000000026865792074657a", "0501000000076865792074657a00", "0500", "050080",
                                          "0502000000040100000001", "0507070701000000016100", "050a00000003abcd", "0509030000000000", "050b" })
        {
            var error = await Record.ExceptionAsync(() => TaquitoBridge.Shared.Call("octezConnectSign", spec, malformed, "micheline"));
            Assert.True(error is not null, malformed);
        }
        foreach (var good in new[] { "050021", "050080ff7f", "050a00000002abcd", "05030b", "050707010000000161010000000162",
                                     "0508070707010000000161000102" + "0000000c" + "0100000001620a0000000100" + "000000022561" })
        {
            var r = await TaquitoBridge.Shared.Call("octezConnectSign", spec, good, "micheline");
            Assert.True(r["signature"]?.StringValue?.StartsWith("edsig") == true, good);
        }

        var signed = await TaquitoBridge.Shared.Call("octezConnectSign", spec, "0501000000076865792074657a", "micheline");
        var signature = signed["signature"]?.StringValue;
        Assert.NotNull(signature);
        Assert.StartsWith("edsig", signature);
        // Same bytes through the plain signing path give the same signature: nothing else changed.
        var plain = await TaquitoBridge.Shared.Call("signPayload", spec, "0501000000076865792074657a");
        Assert.Equal(signature, plain["signature"]?.StringValue);
    }
}

public class MichelineDepthTests
{
    public static string NestedSome(int depth) =>
        "05" + string.Concat(Enumerable.Repeat("0509", depth)) + "0000";

    public static string NestedSequence(int depth)
    {
        var expression = "0000";
        for (var i = 0; i < depth; i++)
            expression = "02" + (expression.Length / 2).ToString("x8") + expression;
        return "05" + expression;
    }

    // Generic primitive with one argument and no annotations exercises the variable-arity path.
    public static string GenericSome(string child)
    {
        var expression = child[2..];
        return "050909" + (expression.Length / 2).ToString("x8") + expression + "00000000";
    }

    public static IReadOnlyList<string> AcceptedPayloads =>
        [NestedSome(0), NestedSome(128), NestedSequence(128), GenericSome(NestedSome(127))];

    public static IReadOnlyList<string> RejectedPayloads =>
        [NestedSome(129), NestedSequence(129), GenericSome(NestedSome(128)), NestedSome(16_000)];

    [Fact]
    public void AcceptsDepthBoundary()
    {
        foreach (var payload in AcceptedPayloads)
            Assert.True(MichelineBinary.IsPackedExpression(payload));
    }

    [Fact]
    public void RejectsExcessiveDepthWithoutCrashing()
    {
        foreach (var payload in RejectedPayloads)
        {
            // The original crash reproducer fits within the signing flow's size limit.
            Assert.True(payload.Length <= 64 * 1024);
            Assert.False(MichelineBinary.IsPackedExpression(payload));
        }
    }

    [Fact]
    public void SequenceSiblingsDoNotAccumulateDepth()
    {
        var elements = new StringBuilder().Insert(0, "0000", 1_000).ToString();
        Assert.True(MichelineBinary.IsPackedExpression("0502000007d0" + elements));
    }
}
