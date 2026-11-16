using Signet.Services;
using Signet.ViewModels;

namespace Signet.Models;

/// <summary>
/// What goes on a printed recovery sheet for one account: enough to bring the account back
/// with Scan QR code or Import account, and nothing that is not already in the Export sheet.
/// The secret is printed as it is stored, so a password-protected key stays protected on
/// paper and the sheet leaves a box for the password to be written in by hand.
///
/// Windows: upstream keeps this struct in <c>RecoverySheet.swift</c> beside the pages it draws;
/// here it is in Core so the tests reach it, and the pages are the app's <c>RecoverySheetPrinter</c>.
/// </summary>
public sealed class RecoverySheet
{
    public string Alias { get; }
    public Address Address { get; }
    public string? PublicKey { get; }
    /// <summary>The key the QR code carries: the octez-encrypted form when there is one, else the clear key.</summary>
    public string SecretKey { get; }
    public bool IsEncrypted { get; }
    public DateTime Date { get; }
    public string Version { get; }

    public RecoverySheet(Wallet wallet, WalletViewModel.ExportedKey key, DateTime? date = null, string version = "")
    {
        Alias = wallet.Alias;
        Address = wallet.Address;
        PublicKey = wallet.PublicKey;
        SecretKey = key.Encrypted ?? key.Clear;
        IsEncrypted = key.Encrypted is not null;
        Date = date ?? DateTime.Now;
        Version = version;
        var secret = SecretKey;
        qrMatrix = new(() => QRCode.Matrix(secret));
    }

    /// <summary>The QR code of the secret key, or <c>null</c> when the key is too long for one (a tz5 key is).</summary>
    public IReadOnlyList<bool[]>? QRMatrix() => qrMatrix.Value;
    private readonly Lazy<IReadOnlyList<bool[]>?> qrMatrix;

    /// <summary>The QR code as a picture about <c>size</c> pixels square, or <c>null</c> as for <c>QRMatrix</c>.</summary>
    public QRCode.Bitmap? QRImage(int size = 1000) => HasQRCode ? QRCode.Image(SecretKey, size) : null;

    /// <summary>
    /// False when the key is too long for one QR code (a tz5 key). The sheet then carries the
    /// key as several codes on following pages (<c>KeyParts</c>), which Scan QR code puts back
    /// together, and after them as numbered lines of text for when a code cannot be read.
    /// </summary>
    public bool HasQRCode => QRMatrix() is not null;

    /// <summary>The codes of a key too long for one; empty when one code carries it.</summary>
    public IReadOnlyList<KeyPart> KeyParts => HasQRCode ? [] : KeyPart.Split(SecretKey);

    public const int CodesPerPage = 4;

    /// <summary>The pages of codes, four to a page so each prints large enough for a webcam.</summary>
    public IReadOnlyList<IReadOnlyList<KeyPart>> CodePages => KeyParts.Chunk(CodesPerPage).Select(page => (IReadOnlyList<KeyPart>)page).ToList();

    public const int LineLength = 90;
    public const int LinesPerPage = 60;

    /// <summary>A long key cut into lines short enough to keep one's place in.</summary>
    public static IReadOnlyList<string> Lines(string key) =>
        key.Chunk(LineLength).Select(chars => new string(chars)).ToList();

    /// <summary>One page of the secret key's text.</summary>
    /// <param name="FirstLine">The number of the first line, counting from 1 across all pages.</param>
    public sealed record KeyPage(IReadOnlyList<string> Lines, int FirstLine, int Part, int Parts);

    /// <summary>The pages of key text at the end: none when one QR code carries the key.</summary>
    public IReadOnlyList<KeyPage> SecretKeyPages
    {
        get
        {
            if (HasQRCode) return [];
            var lines = Lines(SecretKey);
            var pages = lines.Chunk(LinesPerPage).ToList();
            return pages.Select((page, index) => new KeyPage(page, index * LinesPerPage + 1, index + 1, pages.Count)).ToList();
        }
    }

    public int PageCount => 1 + CodePages.Count + SecretKeyPages.Count;
}
