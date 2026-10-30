using System.ComponentModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signet.Models;
using Signet.Services;
using Signet.ViewModels;

namespace Signet.App.Views;

/// <summary>
/// Approves or rejects the dApp request at the front of the queue.
///
/// Windows: the sheet closes only when the request leaves the queue. Closing it any other way
/// (Reject, Esc) rejects the request, as the Mac's Reject is its cancel action; a retryable
/// failure (wrong password, Ledger not ready) or an undelivered reply keeps it open.
/// </summary>
public sealed partial class DAppRequestSheet : Sheet
{
    private const string Warning = "";
    private const string Info = "";

    private readonly WalletViewModel model;
    private readonly DAppConnectionManager dapps;
    private readonly DAppRequest request;
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly ComboBox chosenWallet = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly PasswordBox passphrase = new();
    private readonly HashSet<int> showsParameters = [];
    private IReadOnlyList<Wallet> shareable = [];
    private IReadOnlyList<DAppOperationSummary> summaries = [];
    private DAppPreparedBatch? prepared;
    private string? prepareError;
    private bool isWorking;
    /// <summary>The request left the queue by other means (revoked, stopped); closing must not reject it again.</summary>
    private bool isGone;

    public DAppRequestSheet(WalletViewModel model, DAppRequest request) : base(TitleFor(request), 520)
    {
        this.model = model;
        dapps = model.Dapps;
        this.request = request;

        Form.Children.Add(Header());
        Form.Children.Add(body);
        passphrase.PasswordChanged += (_, _) => Validate();
        chosenWallet.SelectionChanged += (_, _) => Validate();

        Primary(ApproveTitle, Approve);
        dapps.PropertyChanged += Changed;
        Closing += async (_, args) =>
        {
            if (isWorking) { args.Cancel = true; return; }
            if (args.Result == ContentDialogResult.Primary || isGone || !dapps.Pending.Contains(request)) return;
            var deferral = args.GetDeferral();
            try { await dapps.Reject(request); }
            finally { deferral.Complete(); }
        };
        Closed += (_, _) => dapps.PropertyChanged -= Changed;
        Opened += async (_, _) => await PrepareIfOperation();

        dapps.ClearMessages();
        shareable = model.Wallets.Where(w => w.PublicKey is not null && w.KeyKind.CanSign()).ToList();
        var initial = model.SelectedWallet is { PublicKey: not null } selected && selected.KeyKind.CanSign() ? selected : shareable.FirstOrDefault();
        foreach (var wallet in shareable) chosenWallet.Items.Add($"{wallet.Alias}  {wallet.Address.Shortened()}");
        if (initial is not null) chosenWallet.SelectedIndex = shareable.ToList().IndexOf(initial);
        Rebuild();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DAppConnectionManager.Current) when !ReferenceEquals(dapps.Current, request) && !isWorking:
                isGone = true;
                Hide();
                break;
            case nameof(DAppConnectionManager.Undelivered):
                Rebuild();
                break;
        }
    }

    private async Task PrepareIfOperation()
    {
        if (request is not DAppRequest.Operation operation) return;
        prepared = null;
        prepareError = null;
        summaries = await dapps.Summaries(operation.OperationsJSON);
        Rebuild();
        // Simulate the batch so the exact fee, burn and totals are known; approval waits for this.
        try { prepared = await dapps.Prepare(request); }
        catch (Exception e) { prepareError = e.Message; }
        Rebuild();
    }

    // ---- Pieces ----------------------------------------------------------------------------------

    private StackPanel Header()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new DAppIconView(request.App.Icon, 44));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = request.App.Name });
        if (request.App.Url is { } url)
            name.Children.Add(new TextBlock { Text = url.Host.Length > 0 ? url.Host : url.AbsoluteString(), Style = (Style)Application.Current.Resources["SecondaryText"] });
        row.Children.Add(name);
        return row;
    }

    private static string TitleFor(DAppRequest request) => request switch
    {
        DAppRequest.Permission => "Connection request",
        DAppRequest.Operation => "Approve operation",
        DAppRequest.SignPayload => "Sign message",
        _ => "Unsupported request",
    };

    private string ApproveTitle => request switch
    {
        DAppRequest.Permission => "Connect",
        DAppRequest.Operation => "Approve and Send",
        DAppRequest.SignPayload => "Sign",
        _ => "Dismiss",
    };

    private void Rebuild()
    {
        body.Children.Clear();
        if (dapps.Undelivered.TryGetValue(request.Id, out var reply))
        {
            Undelivered(reply);
            PrimaryButtonText = "Send reply again";
            CloseButtonText = "Dismiss";
        }
        else
        {
            RequestContent();
            PrimaryButtonText = ApproveTitle;
            CloseButtonText = "Reject";
        }
        Validate();
    }

    /// <summary>The work is done; only the reply to the dApp is outstanding (S17). Nothing can be redone here.</summary>
    private void Undelivered(DAppConnectionManager.UndeliveredReply reply)
    {
        body.Children.Add(Label(reply.Summary, "", "SystemFillColorSuccessBrush"));
        body.Children.Add(Note($"The dApp has not received the reply yet ({reply.Attempts} attempt{(reply.Attempts == 1 ? "" : "s")}). Send the reply again when the relay is back, or dismiss this and tell the dApp yourself. Nothing is signed or sent again either way."));
    }

    private void RequestContent()
    {
        switch (request)
        {
            case DAppRequest.Permission permission:
                body.Children.Add(new TextBlock { Text = "Share account" });
                body.Children.Add(chosenWallet);
                body.Children.Add(LabeledContent("Network", new TextBlock
                {
                    Text = permission.NetworkType + (permission.RpcURL is { } rpc ? $" ({rpc.Host})" : ""),
                    TextWrapping = TextWrapping.Wrap,
                }));
                body.Children.Add(LabeledContent("Permissions", new TextBlock { Text = string.Join(", ", permission.Scopes.Select(ScopeName)), TextWrapping = TextWrapping.Wrap }));
                if (DAppRequest.NetworkFor(permission.NetworkType, permission.RpcURL) is null)
                    body.Children.Add(Label("Signet has no node for this network; operations from this dApp will fail.", Warning, "SystemFillColorCautionBrush"));
                body.Children.Add(Note("The dApp will see this account's address and public key, and can ask you to approve operations and signatures. Nothing happens without your approval here."));
                break;

            case DAppRequest.Operation operation:
                body.Children.Add(SourceRow(operation.Source));
                body.Children.Add(NetworkRow(operation.NetworkType, operation.RpcURL));
                var shown = prepared?.Operations ?? summaries;
                if (shown.Count == 0)
                    body.Children.Add(LabeledContent("Operations", new ProgressRing { Width = 16, Height = 16, IsActive = true }));
                else
                    for (var i = 0; i < shown.Count; i++) body.Children.Add(OperationRow(i, shown[i]));
                CostRows();
                PassphraseField(operation.Source);
                if (prepareError is not null)
                {
                    body.Children.Add(Label(prepareError, "", "SystemFillColorCriticalBrush"));
                    body.Children.Add(Note("The node could not simulate this batch, so it cannot be approved."));
                }
                else if (prepared is null)
                {
                    body.Children.Add(Note("Simulating on the node to get the exact fee and effects…"));
                }
                else
                {
                    body.Children.Add(Note("Fee and storage are what the node's simulation says this exact batch costs. Approving signs the batch exactly as shown; the dApp's own fee, gas and storage figures are not used."));
                }
                break;

            case DAppRequest.SignPayload sign:
                body.Children.Add(SourceRow(sign.Source));
                body.Children.Add(LabeledContent("Type", new TextBlock { Text = sign.SigningType }));
                body.Children.Add(new TextBlock { Text = "Message" });
                body.Children.Add(new ScrollViewer
                {
                    MaxHeight = 140,
                    Content = new TextBlock
                    {
                        Text = DecodedPayload(sign.Payload, sign.SigningType),
                        Style = (Style)Application.Current.Resources["MonoText"],
                        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    },
                });
                PassphraseField(sign.Source);
                body.Children.Add(Note("Signing proves you control this account. Only sign messages you understand from dApps you trust. Signet signs packed Michelson messages only, never operations."));
                break;

            case DAppRequest.Unsupported unsupported:
                body.Children.Add(Note($"Signet cannot handle “{unsupported.Type}” requests yet."));
                break;
        }
    }

    private StackPanel OperationRow(int index, DAppOperationSummary op)
    {
        var panel = new StackPanel { Spacing = 4 };
        var details = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        TextBlock Line(string text, string style = "SecondaryText") =>
            new() { Text = text, Style = (Style)Application.Current.Resources[style], HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
        if (op.Amount is > 0 and var amount) details.Children.Add(new TextBlock { Text = AssetBalance.Format(amount, "tz"), HorizontalAlignment = HorizontalAlignment.Right });
        if (op.Destination is { } destination) details.Children.Add(Line(destination, "MonoText"));
        if (op.Entrypoint is { } entrypoint) details.Children.Add(Line($"entrypoint {entrypoint}", "CaptionSecondaryText"));
        if (op.Delegate is { } @delegate) details.Children.Add(Line($"to {@delegate}", "MonoText"));
        if (op.Fee is { } fee)
            details.Children.Add(Line($"fee {AssetBalance.Format(fee, "tz")}{(op.Burn is > 0 and var burn ? $" · storage {AssetBalance.Format(burn, "tz")}" : "")}", "CaptionSecondaryText"));
        panel.Children.Add(LabeledContent(Capitalized(op.Kind), details));
        foreach (var effect in op.EffectList)
            panel.Children.Add(Label(effect.Text, effect.Warning ? Warning : Info, effect.Warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"));
        if (op.Opaque)
            panel.Children.Add(Label("Signet cannot tell what this contract call does. Approve it only if you trust the dApp; the parameters are below.", Warning, "SystemFillColorCautionBrush"));
        if (op.RequestedFee is { } requested)
            panel.Children.Add(Label($"The dApp asked for a {AssetBalance.Format(requested, "tz")} fee; Signet pays the node's estimate instead.", Info));
        if (op.Parameters is { } parameters)
        {
            var expander = new Expander
            {
                Header = "Parameters",
                IsExpanded = showsParameters.Contains(index),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 160,
                    Content = new TextBlock { Text = parameters, Style = (Style)Application.Current.Resources["MonoText"], FontSize = 11, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap },
                },
            };
            expander.Expanding += (_, _) => showsParameters.Add(index);
            expander.Collapsed += (_, _) => showsParameters.Remove(index);
            panel.Children.Add(expander);
        }
        return panel;
    }

    private void CostRows()
    {
        if (prepared is { } batch)
        {
            if (batch.RevealFee is { } revealFee)
                body.Children.Add(LabeledContent("Reveal", new TextBlock { Text = $"fee {AssetBalance.Format(revealFee, "tz")} (first operation from this account)", TextWrapping = TextWrapping.Wrap }));
            body.Children.Add(LabeledContent("Fee", new TextBlock { Text = AssetBalance.Format(batch.TotalFee, "tz") }));
            if (batch.TotalBurn > 0) body.Children.Add(LabeledContent("Storage", new TextBlock { Text = AssetBalance.Format(batch.TotalBurn, "tz") }));
            body.Children.Add(LabeledContent("Total leaving the account", new TextBlock { Text = AssetBalance.Format(batch.TotalDebit, "tz"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }));
        }
        else if (prepareError is null)
        {
            body.Children.Add(LabeledContent("Fee", new ProgressRing { Width = 16, Height = 16, IsActive = true }));
        }
    }

    /// <summary>
    /// The network by name, and for a dApp-chosen endpoint the exact host, port and path it
    /// points at: a name is not a chain, so the user sees where the batch would really go (S12).
    /// </summary>
    private Grid NetworkRow(string networkType, Uri? rpcURL)
    {
        if (DAppRequest.NetworkFor(networkType, rpcURL) is { } network)
        {
            var lines = new StackPanel { Spacing = 2 };
            lines.Children.Add(new TextBlock { Text = network.Name, HorizontalAlignment = HorizontalAlignment.Right });
            if (network.Chain == "custom" || !network.IsUsingDefaultNode)
                lines.Children.Add(new TextBlock { Text = DAppRequest.EndpointDescription(network.RpcURL), Style = (Style)Application.Current.Resources["MonoText"], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            if (network.ExpectedChainID is null)
                lines.Children.Add(new TextBlock { Text = $"chain not pinned; the node is trusted to serve {network.Name}", Style = (Style)Application.Current.Resources["CaptionSecondaryText"], HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap });
            return LabeledContent("Network", lines);
        }
        var where = rpcURL is { } url ? $" at {DAppRequest.EndpointDescription(url)}" : "";
        return LabeledContent("Network", Label($"{networkType}{where} is not a network Signet will use", Warning, "SystemFillColorCautionBrush"));
    }

    private Grid SourceRow(Address source)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new AccountAvatarView(source, 22));
        if (dapps.WalletFor(source) is { } wallet)
        {
            row.Children.Add(new TextBlock { Text = wallet.Alias, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = source.Shortened(), Style = (Style)Application.Current.Resources["MonoText"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            row.Children.Add(Label($"{source.Shortened()} is not one of your accounts", Warning, "SystemFillColorCautionBrush"));
        }
        return LabeledContent("Account", row);
    }

    private void PassphraseField(Address source)
    {
        if (dapps.WalletFor(source) is { KeyKind: KeyKind.Encrypted } wallet)
        {
            body.Children.Add(new TextBlock { Text = $"Password for “{wallet.Alias}”" });
            body.Children.Add(passphrase);
        }
    }

    private Wallet? SourceWallet => request switch
    {
        DAppRequest.Operation op => dapps.WalletFor(op.Source),
        DAppRequest.SignPayload sign => dapps.WalletFor(sign.Source),
        _ => null,
    };

    private bool NeedsPassphrase => SourceWallet?.KeyKind == KeyKind.Encrypted;

    protected override void Validate()
    {
        if (dapps.Undelivered.ContainsKey(request.Id)) { IsPrimaryButtonEnabled = !isWorking; return; }
        IsPrimaryButtonEnabled = !isWorking
            && !(request.IsPermission && chosenWallet.SelectedIndex < 0)
            && !(NeedsPassphrase && passphrase.Password.Length == 0)
            && !(request.IsOperation && prepared?.RequestID != request.Id);
    }

    private string WorkingText
    {
        get
        {
            var onLedger = SourceWallet?.KeyKind == KeyKind.Ledger;
            return request switch
            {
                DAppRequest.Operation => onLedger ? "Confirm on your Ledger…" : "Signing and sending…",
                DAppRequest.SignPayload => onLedger ? "Sign on your Ledger…" : "Signing…",
                _ => "Working…",
            };
        }
    }

    private async Task Approve()
    {
        isWorking = true;
        ShowProgress(dapps.Undelivered.ContainsKey(request.Id) ? "Working…" : WorkingText);
        try
        {
            if (dapps.Undelivered.ContainsKey(request.Id))
            {
                await dapps.ResendReply(request);
            }
            else
            {
                var secret = NeedsPassphrase ? passphrase.Password : null;
                switch (request)
                {
                    case DAppRequest.Permission when chosenWallet.SelectedIndex >= 0:
                        await dapps.ApprovePermission(request, shareable[chosenWallet.SelectedIndex]);
                        break;
                    case DAppRequest.Operation when prepared is { } batch && batch.RequestID == request.Id:
                        await dapps.ApproveOperation(request, batch, secret);
                        break;
                    case DAppRequest.SignPayload:
                        await dapps.ApproveSignature(request, secret);
                        break;
                    case DAppRequest.Unsupported:
                        await dapps.Reject(request);
                        break;
                }
            }
        }
        finally
        {
            isWorking = false;
            ShowProgress(null);
        }
        // Still queued: a retryable failure or an undelivered reply. Stay up and say why.
        if (dapps.Pending.Contains(request))
        {
            KeepOpen = true;
            Rebuild();
            ShowError(dapps.LastError);
        }
        else
        {
            isGone = true;
        }
    }

    private static string ScopeName(string scope) => scope switch
    {
        "sign" => "sign messages",
        "operation_request" => "request operations",
        _ => scope,
    };

    private static string Capitalized(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    /// <summary>Micheline string payloads (0x0501 + length + UTF-8) are shown as text; anything else as hex.</summary>
    private static string DecodedPayload(string payload, string signingType)
    {
        if (signingType == "micheline" && payload.StartsWith("0501", StringComparison.Ordinal) && payload.Length > 12 &&
            Hex.Bytes(payload[12..]) is { } bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { }
        }
        return payload;
    }
}
