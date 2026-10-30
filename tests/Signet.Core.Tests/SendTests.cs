using System.Text;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.Tests;

public class SendViewModelTests
{
    internal static readonly Wallet Me = new("Main", new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), publicKey: "edpkME", keyKind: KeyKind.Unencrypted);
    internal static readonly Wallet Savings = new("Savings", new Address("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq"), publicKey: "sppkSAV", keyKind: KeyKind.Unencrypted);
    internal static readonly Wallet Alice = new("Alice", new Address("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5"));

    private static SendViewModel MakeModel(decimal? spendable = 100) =>
        new(Me, [Me, Savings, Alice], new MockChainService(), spendable, (w, _) => new SigningKey.Secret("edskSECRET", null, w.Address));

    private static Task Settle() => Task.Delay(500, TestContext.Current.CancellationToken);

    /// <summary>
    /// On Etherlink only tz1 to tz3 accounts can be sent to: contracts and tz4 are neither
    /// suggested nor accepted when typed.
    /// </summary>
    [Fact]
    public void EtherlinkSendsOnlyToImplicitAccounts()
    {
        var contract = new Wallet("Treasury", new Address("KT1Mkqf6TnMVmhAAuBJCizkzAu4Nhg6DTPmM"));
        var bls = new Wallet("BLS", new Address("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm"));
        var send = new SendViewModel(Me, [Me, Savings, Alice, contract, bls], new MockChainService(), 5,
                                     (w, _) => new SigningKey.Secret("edskSECRET", null, w.Address), implicitAccountsOnly: true);
        Assert.Equal(["Savings", "Alice"], send.Suggestions.Select(w => w.Alias));
        send.RecipientText = contract.Address.Value;
        Assert.True(send.Recipient is null && send.ErrorMessage?.Contains("tz1, tz2 or tz3") == true);
        send.RecipientText = bls.Address.Value;
        Assert.True(send.Recipient is null && send.ErrorMessage is not null);
        send.RecipientText = Alice.Address.Value;
        Assert.True(send.Recipient?.Address == Alice.Address && send.ErrorMessage is null);
        // The plain Tezos sheet still takes contracts.
        var tezos = MakeModel();
        tezos.RecipientText = contract.Address.Value;
        Assert.Equal(contract.Address, tezos.Recipient?.Address);
    }

    [Fact]
    public void SuggestsOurOtherWalletsAndFiltersByText()
    {
        var send = MakeModel();
        Assert.Equal(["Savings", "Alice"], send.Suggestions.Select(w => w.Alias));  // never the sender itself
        send.RecipientText = "ali";
        Assert.Equal(["Alice"], send.Suggestions.Select(w => w.Alias));
        send.RecipientText = "tz2";
        Assert.Equal(["Savings"], send.Suggestions.Select(w => w.Alias));
    }

    [Fact]
    public async Task RecognisesOurOwnEntriesAsVerified()
    {
        var send = MakeModel();
        send.Choose(Alice);
        await Settle();
        var recipient = send.Recipient!;
        Assert.True(recipient.IsVerified);
        Assert.Equal("Alice", recipient.DisplayName);
    }

    [Fact]
    public async Task UnknownAddressGetsProfileNameButIsNotVerified()
    {
        var send = MakeModel();
        send.RecipientText = MockChainService.CaptainStake.Value;
        await Settle();
        var recipient = send.Recipient!;
        Assert.False(recipient.IsVerified);
        Assert.Equal("Captain Stake", recipient.DisplayName);
        Assert.Equal(["captstake.tez"], recipient.Domains);
    }

    [Fact]
    public async Task ResolvesTezNames()
    {
        var send = MakeModel();
        send.RecipientText = "CaptStake.tez";
        await Settle();
        Assert.Equal(MockChainService.CaptainStake, send.Recipient?.Address);
        Assert.False(send.Recipient?.IsVerified);
    }

    [Fact]
    public async Task RejectsGarbageAmountsAndOverspend()
    {
        var send = MakeModel(spendable: 5);
        send.Choose(Alice);
        await Settle();
        send.AmountText = "abc";
        Assert.Null(send.Amount);
        Assert.False(send.CanProceed);

        send.AmountText = "10";
        await send.ProceedToConfirm();
        Assert.IsType<SendViewModel.Step.Compose>(send.CurrentStep);
        Assert.Contains("Not enough", send.ErrorMessage);

        send.UseMaximum();
        Assert.Equal(4.999m, send.Amount);
    }

    [Fact]
    public async Task ConfirmsEstimatesSendsAndConfirms()
    {
        var send = MakeModel();
        send.Choose(Alice);
        await Settle();
        send.AmountText = "1,5";  // comma decimal separator is accepted
        Assert.Equal(1.5m, send.Amount);

        await send.ProceedToConfirm();
        Assert.IsType<SendViewModel.Step.Confirm>(send.CurrentStep);
        var estimate = send.Estimate!;
        Assert.True(estimate.Burn > 0);  // Alice has never held tez in the mock
        Assert.Equal(1.5m + estimate.Fee + estimate.Burn, estimate.Total);

        await send.Send();
        var confirmed = Assert.IsType<SendViewModel.Step.Confirmed>(send.CurrentStep);
        Assert.StartsWith("oo", confirmed.Hash);
        Assert.Equal(9_000_000, confirmed.Level);
    }

    [Fact]
    public async Task RefusesToSendToItself()
    {
        var send = MakeModel();
        send.RecipientText = Me.Address.Value;
        await Settle();
        send.AmountText = "1";
        await send.ProceedToConfirm();
        Assert.IsType<SendViewModel.Step.Compose>(send.CurrentStep);
        Assert.Equal(new SendViewModel.SendError.SendingToSelf().Message, send.ErrorMessage);
    }
}

/// <summary>S14 and S15: a send carries the approved limits, and a lost reply is never retried as a new payment.</summary>
public class SendOutcomeTests
{
    /// <summary>A chain whose node loses the injection reply once and only sees the operation after a rebroadcast.</summary>
    private sealed class LossyChain : TestChainService
    {
        public List<OperationLimits> Sends { get; } = [];
        public List<SentOperation> Rebroadcasts { get; } = [];
        private bool seen;
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(100));
        public override Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits)
        {
            Sends.Add(limits);
            return Task.FromResult(new SentOperation("ooLost", Injected: false, SignedBytes: "abcd"));
        }
        public override Task<SentOperation> Rebroadcast(SentOperation sent)
        {
            Rebroadcasts.Add(sent);
            seen = true;
            return Task.FromResult(new SentOperation(sent.Hash, Injected: true, SignedBytes: sent.SignedBytes));
        }
        public override Task<int> WaitForConfirmation(string operationHash) =>
            seen ? Task.FromResult(42) : throw new ChainError.AccountNotOnChain(new Address("tz1"));
    }

    [Fact]
    public async Task LostReplyKeepsTheHashAndResendsTheSameBytes()
    {
        var chain = new LossyChain();
        var signerRequests = 0;
        var send = new SendViewModel(SendViewModelTests.Me, [SendViewModelTests.Alice], chain, 100,
                                     (w, _) => { signerRequests++; return new SigningKey.Secret("edskSECRET", null, w.Address); });
        send.Choose(SendViewModelTests.Alice);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        send.AmountText = "1";
        await send.ProceedToConfirm();
        Assert.IsType<SendViewModel.Step.Confirm>(send.CurrentStep);
        await send.Send();
        // The estimate's limits went with the operation (S14).
        Assert.Equal([new OperationLimits("1000", 0, 0)], chain.Sends);
        // The outcome is unknown: the hash is known, nothing goes back to the confirm step.
        Assert.Equal(new SendViewModel.Step.Unknown("ooLost"), send.CurrentStep);
        Assert.Contains("ooLost", send.ErrorMessage);
        Assert.Equal(1, signerRequests);
        await send.Rebroadcast();
        // The retry carries the bytes already signed, under the same hash, and signs nothing again.
        Assert.True(chain.Rebroadcasts.Select(s => s.Hash).SequenceEqual(["ooLost"]) && chain.Rebroadcasts.Select(s => s.SignedBytes).SequenceEqual(["abcd"]));
        Assert.True(chain.Sends.Count == 1 && signerRequests == 1);
        Assert.Equal(new SendViewModel.Step.Confirmed("ooLost", 42), send.CurrentStep);
    }

    private sealed class NoLimits : TestChainService
    {
        public override Task<TezBalance> TezBalanceFor(Address address) => Task.FromResult(new TezBalance(100));
        public override Task<TransferEstimate> EstimateTransfer(Wallet wallet, Address destination, decimal amount) =>
            Task.FromResult(new TransferEstimate(0.001m, 0, amount + 0.001m, 0, 0));
        public override Task<SentOperation> SendTransfer(Wallet wallet, SigningKey signer, Address destination, decimal amount, OperationLimits limits) =>
            throw new InvalidOperationException("sent without limits");
    }

    [Fact]
    public async Task RefusesToSendWithoutAnEstimate()
    {
        var send = new SendViewModel(SendViewModelTests.Me, [SendViewModelTests.Alice], new NoLimits(), 100,
                                     (w, _) => new SigningKey.Secret("edskSECRET", null, w.Address));
        send.Choose(SendViewModelTests.Alice);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        send.AmountText = "1";
        await send.ProceedToConfirm();
        await send.Send();
        Assert.IsType<SendViewModel.Step.Confirm>(send.CurrentStep);
        Assert.Equal(new SendViewModel.SendError.NotEstimated().Message, send.ErrorMessage);
    }
}

/// <summary>S20: numbers from the node or the bridge never reach a trapping conversion.</summary>
public class CheckedNumberTests
{
    [Fact]
    public void IntValueRefusesWhatIntCannotHold()
    {
        Assert.Equal(42, new JSONValue.Number(42).IntValue);
        Assert.Equal(-3, new JSONValue.Number(-3).IntValue);
        Assert.Equal(17, new JSONValue.String("17").IntValue);
        Assert.Null(new JSONValue.Number(1.5).IntValue);
        Assert.Null(new JSONValue.Number(double.NaN).IntValue);
        Assert.Null(new JSONValue.Number(double.PositiveInfinity).IntValue);
        Assert.Null(new JSONValue.Number(double.NegativeInfinity).IntValue);
        Assert.Null(new JSONValue.Number(1e300).IntValue);
        Assert.Null(new JSONValue.Number(9_007_199_254_740_994).IntValue);
        Assert.Null(new JSONValue.String("12.0").IntValue);
        Assert.Null(new JSONValue.Bool(true).IntValue);
    }

    [Fact]
    public void EstimatesWithBadFiguresAreErrorsNotZeroes()
    {
        var good = JSONValue.Parse("""{"feeMutez":"500","burnMutez":"0","gasLimit":1500,"storageLimit":0,"limits":{"feeMutez":"500","gasLimit":1500,"storageLimit":0}}""");
        var estimate = TaquitoChainService.EstimateFrom(good, 1);
        Assert.True(estimate.Fee == 0.0005m && estimate.Limits?.GasLimit == 1500 && estimate.Total == 1.0005m);
        var bad = new[]
        {
            new JSONValue.Object(new Dictionary<string, JSONValue>
            {
                ["feeMutez"] = new JSONValue.String("500"), ["burnMutez"] = new JSONValue.String("0"), ["gasLimit"] = new JSONValue.Number(double.NaN),
                ["storageLimit"] = new JSONValue.Number(0), ["limits"] = JSONValue.Parse("""{"feeMutez":"500","gasLimit":1500,"storageLimit":0}"""),
            }),
            JSONValue.Parse("""{"feeMutez":"500","burnMutez":"0","gasLimit":1500,"storageLimit":0}"""),
            JSONValue.Parse("""{"feeMutez":"500","burnMutez":"0","gasLimit":1500,"storageLimit":0,"limits":{"feeMutez":"-5","gasLimit":1500,"storageLimit":0}}"""),
            JSONValue.Parse("""{"feeMutez":"500","burnMutez":"0","gasLimit":1500,"storageLimit":0,"limits":{"feeMutez":"500","gasLimit":1e20,"storageLimit":0}}"""),
        };
        foreach (var value in bad) Assert.ThrowsAny<Exception>(() => TaquitoChainService.EstimateFrom(value));
        var info = JSONValue.Parse("""{"address":"KT1BEqzn5Wx8uJrZNvuS9DVHmLvG9td3fDLi","isGenericMultisig":true,"scriptHash":"expr","counter":"nope","threshold":"2","keys":[]}""");
        Assert.ThrowsAny<Exception>(() => BridgeMultisigService.InfoFrom(info));
    }
}

/// <summary>Part of upstream's AuditRepairTests: the operation journal (S15).</summary>
public class OperationJournalTests
{
    [Fact]
    public void JournalSurvivesReconstructionAndRefusesAReplacement()
    {
        var root = TestSupport.MakeDir("journal");
        var first = new OperationJournal(root);
        const string record = """{"hash":"synthetic-op","signedBytes":"abab","rpcURL":"https://offline.invalid","source":"synthetic-account"}""";
        first.Save(record);
        var reopened = new OperationJournal(root);
        Assert.Contains("synthetic-op", reopened.Read());
        Assert.ThrowsAny<Exception>(() => reopened.Save(record.Replace("synthetic-op", "replacement")));
        reopened.Remove("synthetic-op");
        Assert.Equal("[]", reopened.Read());
    }
}

public class TransferHelpersTests
{
    [Fact]
    public void MutezConversionTruncatesBelowOneMutez()
    {
        Assert.Equal("1500000", Mutez.FromTez(1.5m));
        Assert.Equal("1", Mutez.FromTez(0.0000019m));
        Assert.Equal(22702.455018m, Mutez.ToTez("22702455018"));
    }

    [Fact]
    public void ProfileParsingPrefersTzProfilesAlias()
    {
        var json = Encoding.UTF8.GetBytes("""
        {"alias":"TzKT Name","extras":{"profile":{"kind":"person","alias":"Captain Stake","twitter":"captstake","description":"Staking."}}}
        """);
        var profile = TzKTService.Profile(json);
        Assert.NotNull(profile);
        Assert.Equal("Captain Stake", profile.Name);
        Assert.Equal("captstake", profile.Twitter);
        Assert.Null(TzKTService.Profile(Encoding.UTF8.GetBytes("{\"address\":\"tz1x\"}")));
        Assert.Equal("Only TzKT", TzKTService.Profile(Encoding.UTF8.GetBytes("{\"alias\":\"Only TzKT\"}"))?.Name);
    }

    /// <summary>Estimation needs no secret: Captain Stake's public key is enough for the node to simulate.</summary>
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task EstimatesAMainnetTransferWithoutASecretKey()
    {
        var captain = new Wallet("Captain Stake", MockChainService.CaptainStake,
                                 publicKey: "edpku5dNSxAs5R7g91zm9mDcfYVGsM4gCniaDSPVwYPt3xtszyzgJD", keyKind: KeyKind.Unencrypted);
        var estimate = await new TaquitoChainService(Network.Mainnet)
            .EstimateTransfer(captain, new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"), 1);
        Assert.True(estimate.Fee > 0);
        Assert.True(estimate.Fee < 0.01m);
        Assert.Equal(1 + estimate.Fee + estimate.Burn, estimate.Total);
    }

    [Fact, Trait(Category.Name, Category.Network)]
    public async Task ForwardDomainLookupFindsCaptainStake()
    {
        var service = new TezosDomainsService(Network.Mainnet.TezosDomainsURL!);
        Assert.Equal(MockChainService.CaptainStake, await service.Resolve("captstake.tez"));
        Assert.Null(await service.Resolve($"this-name-should-not-exist-{Guid.NewGuid().ToString()[..8]}.tez"));
    }
}

/// <summary>
/// Not upstream (the Mac's send round trips are the tz5 and Etherlink tests): a funded tz1 on
/// Quantumnet sends through the whole Windows stack, the operation journal on disk included, and
/// the record is gone once the operation is in a block.
/// </summary>
[Collection(Quantumnet.FaucetCollection)]
public class QuantumnetSendTests
{
    [Fact, Trait(Category.Name, Category.Network)]
    public async Task SendsTezAndClearsTheJournal()
    {
        var (wallet, material, _) = await Quantumnet.FundedTz1("send-test");
        var journalDir = TestSupport.MakeDir("send-journal");
        var bridge = new TaquitoBridge { OperationJournal = new OperationJournal(journalDir) };
        var chain = new TaquitoChainService(Network.Quantumnet, bridge);
        var destination = await new KeyGenerator().Generate(AddressScheme.Tz1);
        var recipient = new Wallet("Destination", new Address(destination.Address), keyKind: KeyKind.None);

        var send = new SendViewModel(wallet, [wallet, recipient], chain, 20,
                                     (w, _) => new SigningKey.Secret(material.SecretKey, null, w.Address));
        send.Choose(recipient);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        send.AmountText = "1.5";
        await send.ProceedToConfirm();
        Assert.True(send.CurrentStep is SendViewModel.Step.Confirm, send.ErrorMessage);
        Assert.True(send.Estimate!.Burn > 0);  // a brand-new destination is allocated

        await send.Send();
        var confirmed = Assert.IsType<SendViewModel.Step.Confirmed>(send.CurrentStep);
        Assert.StartsWith("o", confirmed.Hash);
        Assert.Equal("[]", new OperationJournal(journalDir).Read());

        var received = await chain.TezBalanceFor(recipient.Address);
        Assert.Equal(1.5m, received.Spendable);
    }
}
