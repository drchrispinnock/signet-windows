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
        Assert.True(request!.IsPermission);
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

    /// <summary>
    /// S12: a dApp's custom endpoint must be public HTTPS; a name match is not enough, and the
    /// wallet's own local node is never reachable through a dApp's request.
    /// </summary>
    [Fact]
    public void RefusesLocalOrInsecureCustomEndpointsFromDApps()
    {
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("http://127.0.0.1:8732")));
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("http://localhost:8732")));
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("https://10.0.0.5")));
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("https://192.168.1.2/rpc")));
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("http://rpc.example.org")));
        Assert.Null(DAppRequest.NetworkFor("custom", new Uri("https://user:pw@rpc.example.org")));
        Assert.Equal("Custom (rpc.example.org:8732/node)", DAppRequest.NetworkFor("custom", new Uri("https://rpc.example.org:8732/node"))?.Name);
        // Only the whole endpoint identifies one of our networks: same host, other path or port, is not Shadownet.
        Assert.Equal("custom", DAppRequest.NetworkFor("custom", new Uri("https://rpc.shadownet.teztnets.com/other"))?.Chain);
        Assert.Equal("custom", DAppRequest.NetworkFor("custom", new Uri("https://rpc.shadownet.teztnets.com:8443"))?.Chain);
        Assert.True(DAppRequest.SameEndpoint(new Uri("https://a.example"), new Uri("https://A.example:443/")));
        Assert.False(DAppRequest.SameEndpoint(new Uri("https://a.example"), new Uri("http://a.example")));
        Assert.False(DAppRequest.SameEndpoint(new Uri("https://a.example/x"), new Uri("https://a.example/y")));
        Assert.True(DAppRequest.SameEndpoint(null, null) && !DAppRequest.SameEndpoint(null, new Uri("https://a.example")));
    }

    /// <summary>
    /// S04: identity is the message's authenticated sender id (set by the bridge), never the
    /// self-asserted one inside the app metadata.
    /// </summary>
    [Fact]
    public void SenderIdentityIsNeverTakenFromAppMetadata()
    {
        const string json = """
        {"kind":"request","message":{"id":"req-4","type":"sign_payload_request","senderId":"authentic","appMetadata":{"senderId":"spoofed","name":"objkt.com"},
          "sourceAddress":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb","signingType":"micheline","payload":"0501000000076865792074657a"}}
        """;
        Assert.Equal("authentic", DAppRequest.Parse(json)?.App.SenderId);
        const string noSender = """
        {"kind":"request","message":{"id":"req-5","type":"sign_payload_request","appMetadata":{"senderId":"spoofed","name":"objkt.com"},
          "sourceAddress":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb","signingType":"micheline","payload":"0501000000076865792074657a"}}
        """;
        Assert.Equal("", DAppRequest.Parse(noSender)?.App.SenderId);
    }
}

/// <summary>S12: what the Settings node field accepts.</summary>
public class NodeURLPolicyTests
{
    [Fact]
    public void NodeFieldRequiresHTTPSAwayFromTheLocalMachine()
    {
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL("rpc.tzbeta.net")?.AbsoluteString());
        Assert.Equal("https://rpc.tzbeta.net", Network.NodeURL("https://rpc.tzbeta.net/")?.AbsoluteString());
        Assert.Equal("http://localhost:8732", Network.NodeURL("http://localhost:8732")?.AbsoluteString());
        Assert.NotNull(Network.NodeURL("http://127.0.0.1:8732"));
        Assert.NotNull(Network.NodeURL("http://192.168.1.20:8732"));
        Assert.Null(Network.NodeURL("http://rpc.tzbeta.net"));
        Assert.Null(Network.NodeURL("https://user:secret@rpc.tzbeta.net"));
        Assert.Null(Network.NodeURL("not a url"));
        Assert.Contains("https", Network.NodeURLProblem("http://rpc.tzbeta.net"));
        Assert.Equal("NetXdQprcVkpaWU", Network.Mainnet.ExpectedChainID);
        Assert.Equal("NetXsqzbfFenSTS", Network.Shadownet.ExpectedChainID);
        Assert.Null(Network.Weeklynet.ExpectedChainID);
    }
}

/// <summary>S08: a request is signed only under a grant for its exact peer, account, scope and network.</summary>
public class DAppGrantTests
{
    static readonly Address Source = new("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
    static readonly DAppInfo App = new("objkt.com", null, null, "peer-a");
    static DAppPermission Grant(string senderId = "peer-a", string? address = null, string networkType = "mainnet", Uri? rpc = null, string[]? scopes = null) =>
        new("acc", senderId, address ?? Source.Value, "objkt.com", null, networkType, scopes ?? ["operation_request", "sign"], null, rpc);
    static readonly DAppRequest Operation = new DAppRequest.Operation("op", App, "mainnet", null, Source, "[]");
    static readonly DAppRequest Sign = new DAppRequest.SignPayload("sig", App, Source, "micheline", "05");

    [Fact]
    public void RequiresAnExactGrant()
    {
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Operation, []));
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Operation, [Grant(senderId: "peer-b")]));
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Operation, [Grant(address: "tz1other")]));
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Operation, [Grant(scopes: ["sign"])]));
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Operation, [Grant(networkType: "shadownet")]));
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(Sign, [Grant(scopes: ["operation_request"])]));
        Assert.Equal("mainnet", DAppConnectionManager.GrantFor(Operation, [Grant(networkType: "shadownet"), Grant()]).NetworkType);
        Assert.Equal(["sign"], DAppConnectionManager.GrantFor(Sign, [Grant(scopes: ["sign"])]).Scopes);
        // A request with no authenticated sender matches nothing.
        var anonymous = new DAppRequest.SignPayload("sig", new DAppInfo("x", null, null, ""), Source, "micheline", "05");
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(anonymous, [Grant(senderId: "")]));
        // Custom networks must name the same endpoint.
        var custom = new DAppRequest.Operation("op", App, "custom", new Uri("https://rpc.example.org/"), Source, "[]");
        Assert.ThrowsAny<DAppConnectionManager.ConnectError>(() => DAppConnectionManager.GrantFor(custom, [Grant(networkType: "custom", rpc: new Uri("https://rpc.example.org:8443"))]));
        Assert.Equal("custom", DAppConnectionManager.GrantFor(custom, [Grant(networkType: "custom", rpc: new Uri("https://rpc.example.org"))]).NetworkType);
    }

    /// <summary>
    /// S08 and S17 through the manager: no grant means no signing; a signed reply the relay did
    /// not take is kept and resent, never signed again; revoking a grant drops its requests.
    /// </summary>
    [Fact]
    public async Task ManagerChecksGrantsAndKeepsUndeliveredReplies()
    {
        var material = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var wallet = new Wallet("me", new Address(material.Address), AddressScheme.Tz1, material.PublicKey, KeyKind.Unencrypted);
        var signings = 0;
        var manager = new DAppConnectionManager(new InMemoryBridgeStorage(), () => [wallet], (w, _) =>
        {
            signings++;
            return new SigningKey.Secret(material.SecretKey, null, w.Address);
        }, bridge: TaquitoBridge.Shared);
        var request = new DAppRequest.SignPayload("s-1", App, wallet.Address, "micheline", "0501000000076865792074657a");
        var @event = $$$"""
        {"kind":"request","message":{"id":"s-1","type":"sign_payload_request","senderId":"peer-a","appMetadata":{"name":"objkt.com"},
          "sourceAddress":"{{{wallet.Address.Value}}}","signingType":"micheline","payload":"0501000000076865792074657a"}}
        """;
        // No grant: refused before any signing.
        manager.PermissionsOverride = [];
        manager.Handle(@event);
        Assert.Single(manager.Pending);
        await manager.ApproveSignature(request, null);
        Assert.Equal(0, signings);
        Assert.Empty(manager.Pending);
        Assert.Contains("no permission", manager.LastError);

        // With a grant the message is signed; the SDK is not started here, so the reply cannot be
        // delivered: the request stays, marked undelivered, and a second approval resends only.
        manager.PermissionsOverride = [Grant(address: wallet.Address.Value, scopes: ["sign"])];
        manager.Handle(@event);
        await manager.ApproveSignature(request, null);
        Assert.Equal(1, signings);
        Assert.Single(manager.Pending);
        Assert.Equal(1, manager.Undelivered["s-1"].Attempts);
        await manager.ApproveSignature(request, null);
        Assert.Equal(1, signings);
        Assert.Equal(2, manager.Undelivered["s-1"].Attempts);
        await manager.ResendReply(request);
        Assert.True(signings == 1 && manager.Undelivered["s-1"].Attempts == 3);
        // Giving up keeps what was done and drops the request.
        await manager.Reject(request);
        Assert.True(manager.Pending.Count == 0 && manager.Undelivered.Count == 0);

        // Revoking the grant refuses queued requests from that peer (S08).
        manager.Handle(@event);
        Assert.Single(manager.Pending);
        await manager.Disconnect(Grant(address: wallet.Address.Value, scopes: ["sign"]));
        Assert.Empty(manager.Pending);

        // The queue is bounded (S19).
        for (var i = 0; i < DAppConnectionManager.MaxPending + 3; i++)
            manager.Handle(@event.Replace("s-1", $"s-{i + 10}"));
        Assert.Equal(DAppConnectionManager.MaxPending, manager.Pending.Count);
        Assert.Contains("Too many", manager.LastError);
    }
}

/// <summary>S11: nothing secret or private reaches the bridge log.</summary>
public class BridgeLogRedactionTests
{
    [Fact]
    public void RedactsURLsAndLongHexOrKeys()
    {
        Assert.Equal("https://relay.example/_matrix/client/r0/sync", TaquitoBridge.Redacted("https://relay.example/_matrix/client/r0/sync?access_token=SECRET&since=abc"));
        Assert.Equal("<url>", TaquitoBridge.Redacted("not a url"));
        var hex = string.Concat(Enumerable.Repeat("ab", 40));
        Assert.DoesNotContain(hex, TaquitoBridge.RedactedLine($"signed {hex} done"));
        Assert.Equal("key edsk<redacted> here", TaquitoBridge.RedactedLine("key edskRuR3azSfg5xTz6dLovR7yr7hgP9Qt6Ub8rgqJG7VTH6RhJ1vXXhfSwtxw1E2Lc3b3TvsgrnWXKVyLRQgm3Rx8cFXqQhd3 here"));
        Assert.Equal("sig edsig<redacted> ok", TaquitoBridge.RedactedLine("sig edsigtmSFeZqfyckY8zu5uX7yC3JArZrzzNwqnmGRpNgQNYu4NNo5pY3M9zfbSjtTe2kQUv7Ca8RoxwygwLyRh4FEVvdZ5Fn2KQ ok"));
        Assert.True(TaquitoBridge.RedactedLine(new string('x', 5000)).Length <= TaquitoBridge.MaxLineLength + 1);
        Assert.Equal("fetch #3 ← 200 1234 bytes", TaquitoBridge.RedactedLine("fetch #3 ← 200 1234 bytes"));
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

    /// <summary>
    /// S03: the approval sheet must show what a contract call does. Standard token calls decode
    /// into effects; anything else is flagged as opaque; parameters are always carried verbatim.
    /// </summary>
    [Fact]
    public async Task DescribesTokenCallsAndFlagsOpaqueOnes()
    {
        const string fa2Transfer = """{"kind":"transaction","destination":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton","amount":"0","parameters":{"entrypoint":"transfer","value":[{"prim":"Pair","args":[{"string":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"},[{"prim":"Pair","args":[{"string":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"},{"prim":"Pair","args":[{"int":"7"},{"int":"3"}]}]}]]}]}}""";
        const string operators = """{"kind":"transaction","destination":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton","amount":"0","parameters":{"entrypoint":"update_operators","value":[{"prim":"Left","args":[{"prim":"Pair","args":[{"string":"tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"},{"prim":"Pair","args":[{"string":"KT1F139Vd1mcqYejDnzQQkVQcQboPc4EUbqx"},{"int":"0"}]}]}]}]}}""";
        const string fa12 = """{"kind":"transaction","destination":"KT1GBZmSxmnKJXGMdMLbugPfLyUPmuLSMwKS","amount":"0","parameters":{"entrypoint":"approve","value":{"prim":"Pair","args":[{"string":"KT1F139Vd1mcqYejDnzQQkVQcQboPc4EUbqx"},{"int":"1000000"}]}}}""";
        const string unknown = """{"kind":"transaction","destination":"KT1GBZmSxmnKJXGMdMLbugPfLyUPmuLSMwKS","amount":"0","parameters":{"entrypoint":"collect","value":{"int":"1"}}}""";
        const string plain = """{"kind":"transaction","destination":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","amount":"2000000","fee":"100000000"}""";
        var described = await TaquitoBridge.Shared.Call("octezConnectDescribe", $"[{fa2Transfer},{operators},{fa12},{unknown},{plain}]");
        var list = described.ArrayValue!.Select(DAppConnectionManager.Summary).ToList();
        Assert.Equal(5, list.Count);
        Assert.Equal(["Send 3 of token 7 in KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton from tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb to tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N"], list[0].EffectList.Select(e => e.Text));
        Assert.True(list[0].EffectList[0].Warning && !list[0].Opaque);
        Assert.Equal(["Allow KT1F139Vd1mcqYejDnzQQkVQcQboPc4EUbqx to move token 0 in KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton owned by tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"], list[1].EffectList.Select(e => e.Text));
        Assert.Equal(["Allow KT1F139Vd1mcqYejDnzQQkVQcQboPc4EUbqx to spend 1000000 units of the token KT1GBZmSxmnKJXGMdMLbugPfLyUPmuLSMwKS"], list[2].EffectList.Select(e => e.Text));
        Assert.True(list[3].EffectList.Count == 0 && list[3].Opaque && list[3].Entrypoint == "collect");
        Assert.Contains("\"int\": \"1\"", list[3].Parameters);
        Assert.True(list[4].EffectList.Count == 0 && !list[4].Opaque && list[4].Amount == 2 && list[4].Parameters is null);
    }

    /// <summary>
    /// S03: fee, gas and storage come from the node's simulation, never from the dApp; a dApp
    /// asking for more fee is reported; a reveal in front is counted; totals add up.
    /// </summary>
    [Fact]
    public async Task PreparedBatchUsesTheNodesFiguresNotTheDApps()
    {
        const string ops = """[{"kind":"transaction","destination":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","amount":"2000000","fee":"100000000","gas_limit":"999999","storage_limit":"99999"},{"kind":"delegation","delegate":"tz1dCaMnnMJk76UodjewCK67ABiXWjcKj73N","fee":"100"}]""";
        const string estimates = """[{"suggestedFeeMutez":300,"gasLimit":200,"storageLimit":0,"burnFeeMutez":0},{"suggestedFeeMutez":500,"gasLimit":1500,"storageLimit":257,"burnFeeMutez":64250},{"suggestedFeeMutez":400,"gasLimit":1000,"storageLimit":0,"burnFeeMutez":0}]""";
        var r = await TaquitoBridge.Shared.Call("octezConnectBuildPrepared", ops, estimates);
        Assert.Equal(new JSONValue.String("2000000"), r["totalAmountMutez"]);
        Assert.Equal(new JSONValue.String("1200"), r["totalFeeMutez"]);        // reveal 300 + 500 + 400
        Assert.Equal(new JSONValue.String("64250"), r["totalBurnMutez"]);
        Assert.Equal(new JSONValue.String("2065450"), r["totalDebitMutez"]);
        Assert.Equal(new JSONValue.String("300"), r["reveal"]?["feeMutez"]);
        var list = r["operations"]!.ArrayValue!.Select(DAppConnectionManager.Summary).ToList();
        Assert.True(list[0].Fee == 0.0005m && list[0].RequestedFee == 100);
        Assert.True(list[1].Fee == 0.0004m && list[1].RequestedFee is null);
        using var prepared = System.Text.Json.JsonDocument.Parse(r["prepared"]!.StringValue!);
        var first = prepared.RootElement[0];
        Assert.True(first.GetProperty("fee").GetInt32() == 500 && first.GetProperty("gasLimit").GetInt32() == 1500 && first.GetProperty("storageLimit").GetInt32() == 257);
        Assert.Equal(400, prepared.RootElement[1].GetProperty("fee").GetInt32());
        // Without a reveal, counts must match exactly.
        await Assert.ThrowsAnyAsync<Exception>(() => TaquitoBridge.Shared.Call("octezConnectBuildPrepared", ops, "[]"));
        // Executing anything that was not prepared is refused before a signer is even looked at.
        await Assert.ThrowsAnyAsync<Exception>(() => TaquitoBridge.Shared.Call("octezConnectExecute", "http://127.0.0.1:1", "{}", ops));
    }

    [Fact]
    public async Task ApprovalUsesTheActualRevealAndWarnsAboutDefaultContractCalls()
    {
        const string ops = """[{"kind":"transaction","destination":"KT1RJ6PbjHpwc3M5rw5s2Nbmefwbuwbdxton","amount":"0"}]""";
        const string estimates = """[{"suggestedFeeMutez":300,"gasLimit":200,"storageLimit":0,"burnFeeMutez":0},{"suggestedFeeMutez":500,"gasLimit":1500,"storageLimit":0,"burnFeeMutez":0}]""";
        const string operation = """{"branch":"fixture","protocol":"fixture","contents":[{"kind":"reveal","fee":"999","gas_limit":"10000","storage_limit":"0"},{"kind":"transaction","fee":"500","gas_limit":"1500","storage_limit":"0"}]}""";
        var r = await TaquitoBridge.Shared.Call("octezConnectBuildPrepared", ops, estimates, operation);
        Assert.Equal(new JSONValue.String("999"), r["reveal"]?["feeMutez"]);
        Assert.Equal(new JSONValue.String("1499"), r["totalFeeMutez"]);
        Assert.Equal(new JSONValue.String("1499"), r["totalDebitMutez"]);
        using (var prepared = System.Text.Json.JsonDocument.Parse(r["prepared"]!.StringValue!))
            Assert.Equal(System.Text.Json.JsonValueKind.Object, prepared.RootElement.ValueKind);
        var summary = DAppConnectionManager.Summary(r["operations"]!.ArrayValue![0]);
        Assert.True(summary.Opaque && summary.Entrypoint == "default");
        Assert.Contains("Unit", summary.Parameters);
    }

    /// <summary>
    /// Windows-only: the manager's own bridge sends everything through <c>PublicHTTPClient</c>
    /// (the connect callback that checks every DNS answer), and the relay must still be reachable.
    /// </summary>
    [Fact(Timeout = 120_000), Trait(Category.Name, Category.Network)]
    public async Task ManagerStartsThroughThePublicOnlyBridge()
    {
        var manager = new DAppConnectionManager(new InMemoryBridgeStorage(), () => [], (_, _) => null);
        await manager.Start().WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.IsStarted, manager.LastError);
        Assert.Empty(manager.Permissions);
        await manager.Stop();
        Assert.False(manager.IsStarted);
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
