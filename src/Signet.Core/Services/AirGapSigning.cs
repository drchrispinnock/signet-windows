using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Signet.Models;

namespace Signet.Services;

/// <summary>
/// Signing with AirGap Vault. When the bridge's AirGap signer needs a signature it calls here with
/// the forged operation; a panel opens over whatever sheet is open and shows the operation as
/// AirGap's transaction sign request (an animated QR code when it does not fit one), the user
/// scans it with the Vault, checks and approves it there, and holds up the Vault's signed reply to
/// the camera (or pastes a screenshot of it). The reply must answer this request and carry exactly
/// the bytes sent; the bridge then checks the signature against the account's key before anything
/// reaches a node. One request at a time.
///
/// Windows: the panel is the app's (<c>AirGapSignDialog</c>), installed as <c>Present</c> with the
/// UI thread's <c>Context</c>; without one (the tests) signing is unavailable, as under the Mac's
/// test host.
/// </summary>
public sealed class AirGapSigning
{
    public static AirGapSigning Shared { get; } = new();

    public abstract class Failure(string message) : Exception(message), IEquatable<Failure>
    {
        public sealed class Cancelled() : Failure("Signing in AirGap Vault was cancelled. Nothing was sent.");
        public sealed class Busy() : Failure("Another operation is waiting for AirGap Vault. Finish or cancel it first.");
        public sealed class Unavailable() : Failure("AirGap Vault signing is not available here.");
        public sealed class BadRequest() : Failure("Signet could not make an AirGap Vault request for this operation.");
        public sealed class DifferentOperation()
            : Failure("AirGap Vault signed other bytes than Signet sent, so its signature was not used. Nothing was sent.");

        public bool Equals(Failure? other) => other is not null && other.GetType() == GetType();
        public override bool Equals(object? obj) => Equals(obj as Failure);
        public override int GetHashCode() => GetType().GetHashCode();
    }

    /// <summary>Shows the panel for a session on the UI thread and returns what closes it.</summary>
    public Func<AirGapSignSession, Action>? Present { get; set; }
    /// <summary>The UI thread, where sessions live.</summary>
    public SynchronizationContext? Context { get; set; }

    private AirGapSignSession? active;

    /// <summary>The 64-byte signature for <c>{address, publicKey, forged}</c> (forged as hex), or an error.</summary>
    public Task<byte[]> Sign(string requestJSON)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Present is not { } present || Context is not { } context) return Task.FromException<byte[]>(new Failure.Unavailable());
        context.Post(_ =>
        {
            if (active is not null) { completion.TrySetException(new Failure.Busy()); return; }
            if (SessionFor(requestJSON) is not { } session) { completion.TrySetException(new Failure.BadRequest()); return; }
            active = session;
            Action? dismiss = null;
            session.OnFinish = (signature, error) =>
            {
                dismiss?.Invoke();
                active = null;
                if (signature is not null) completion.TrySetResult(signature);
                else completion.TrySetException(error ?? new Failure.Cancelled());
            };
            try { dismiss = present(session); }
            catch (Exception)
            {
                active = null;
                completion.TrySetException(new Failure.Unavailable());
            }
        }, null);
        return completion.Task;
    }

    private static AirGapSignSession? SessionFor(string requestJSON)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJSON);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("publicKey", out var publicKey) || publicKey.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("forged", out var forgedHex) || forgedHex.ValueKind != JsonValueKind.String
                || Hex.Bytes(forgedHex.GetString()!) is not { Length: > 0 } forged)
                return null;
            return AirGapSignSession.Create(address.GetString()!, publicKey.GetString()!, forged);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>One request to the Vault: the code to show, then the reply being read. UI thread only.</summary>
public sealed partial class AirGapSignSession : ObservableObject
{
    public enum PhaseKind { Showing, Scanning }

    public string Address { get; }
    public byte[] Forged { get; }
    public ulong Id { get; }
    /// <summary>The request as QR code texts; more than one is shown in turn.</summary>
    public IReadOnlyList<string> Frames { get; }
    [ObservableProperty] private PhaseKind phase = PhaseKind.Showing;
    [ObservableProperty] private string? note;
    public UniformResource.Decoder Decoder { get; private set; } = new();
    /// <summary>The signature, or why there is none.</summary>
    public Action<byte[]?, Exception?>? OnFinish { get; set; }
    private bool finished;

    /// <summary>Bytes per QR frame: small enough for a phone to read off a laptop screen at a glance.</summary>
    public const int FragmentSize = 200;

    private AirGapSignSession(string address, byte[] forged, ulong id, IReadOnlyList<string> frames)
    {
        Address = address;
        Forged = forged;
        Id = id;
        Frames = frames;
    }

    public static AirGapSignSession? Create(string address, string publicKey, byte[] forged, ulong? id = null)
    {
        var messageID = id ?? AirGap.NewID();
        if (AirGap.SignRequest(publicKey, forged, messageID) is not { } request) return null;
        return new AirGapSignSession(address, forged, messageID, UniformResource.Encode(AirGap.PayloadFor([request]), FragmentSize));
    }

    public bool IsCollecting => !Decoder.IsEmpty;

    /// <summary>A code read from the camera, the clipboard or an image.</summary>
    public void Receive(string text)
    {
        if (finished) return;
        switch (QRPayload.Classify(text))
        {
            case QRPayload.UniformResourceContent(var part):
                try
                {
                    if (Decoder.Receive(part) is not UniformResource.Decoder.Outcome.Complete(var payload))
                    {
                        Note = null;
                        OnPropertyChanged(nameof(Decoder));
                        return;
                    }
                    Decoder = new UniformResource.Decoder();
                    OnPropertyChanged(nameof(Decoder));
                    var messages = AirGap.Messages(payload);
                    if (messages.Any(m => m.Type == (ulong)AirGap.MessageType.TransactionSignRequest))
                    {
                        Note = "That is Signet's own request. Scan it with AirGap Vault, approve it there, then hold up the code the Vault shows.";
                        return;
                    }
                    Finish(AirGap.Signature(messages, Id, Forged), null);
                }
                catch (AirGap.Error e) when (e.Kind == AirGap.Error.Reason.DifferentOperation)
                {
                    Finish(null, new AirGapSigning.Failure.DifferentOperation());
                }
                catch (AirGap.Error e) when (e.Kind == AirGap.Error.Reason.WrongResponse)
                {
                    Note = "That AirGap code is not the Vault's reply to this request. Hold up the code the Vault shows after you approve this operation.";
                }
                catch (AirGap.Error e) when (e.Kind == AirGap.Error.Reason.UnsupportedVersion)
                {
                    Note = "That AirGap code is in a format Signet does not read. In AirGap Vault, choose QR Code V3 under QR Details.";
                }
                catch (Exception e) when (e is AirGap.Error or UniformResource.DecodeError or CBOR.DecodeError)
                {
                    Decoder = new UniformResource.Decoder();
                    OnPropertyChanged(nameof(Decoder));
                    Note = "That code could not be read as an AirGap reply. Try again.";
                }
                break;
            case QRPayload.AirGapLegacy:
                Note = "That AirGap code is in the old format. In AirGap Vault, choose QR Code V3 under QR Details.";
                break;
            default:
                Note = "That code is not AirGap Vault's signed reply.";
                break;
        }
    }

    public void Cancel() => Finish(null, new AirGapSigning.Failure.Cancelled());

    private void Finish(byte[]? signature, Exception? error)
    {
        if (finished) return;
        finished = true;
        OnFinish?.Invoke(signature, error);
    }
}
