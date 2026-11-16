using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
using Signet.Models;
using Forms = System.Windows.Forms;

namespace Signet.App.Views;

/// <summary>
/// Prints a recovery sheet (Core's <c>RecoverySheet</c>) through the system print dialog. Signet
/// writes the sheet nowhere itself; what the dialog does with it (a printer, or a PDF through
/// Microsoft Print to PDF) is the user's choice.
///
/// Windows: upstream renders SwiftUI pages into PDF data and prints them with PDFKit. WinUI 3
/// has no XAML printing, so the pages (<c>RecoverySheetPage</c>, <c>RecoverySheetCodesPage</c>,
/// <c>RecoverySheetKeyPage</c>) are drawn with GDI+ in points, at upstream's sizes, black on
/// white whatever the app's appearance. The QR codes are filled rectangles, so they print sharply.
/// </summary>
public static class RecoverySheetPrinter
{
    /// <summary>Why printing did not happen, for the sheet to show; <c>null</c> when it printed or was cancelled.</summary>
    public static string? Print(RecoverySheet sheet, nint ownerWindow)
    {
        using var document = new PrintDocument
        {
            DocumentName = "Signet recovery sheet",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
        };
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        using var dialog = new Forms.PrintDialog { Document = document, UseEXDialog = true, AllowSomePages = false, AllowSelection = false };
        if (dialog.ShowDialog(new Owner(ownerWindow)) != Forms.DialogResult.OK) return null;

        var pages = Pages(sheet).ToList();
        var next = 0;
        document.PrintPage += (_, e) =>
        {
            if (e.Graphics is not { } graphics) return;
            // Draw in points from the paper's corner, whatever the printer's unprintable edge.
            graphics.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            graphics.PageUnit = GraphicsUnit.Point;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var paper = new SizeF(e.PageBounds.Width * 72f / 100f, e.PageBounds.Height * 72f / 100f);
            pages[next](new Painter(graphics, paper));
            next++;
            e.HasMorePages = next < pages.Count;
        };
        try
        {
            document.Print();
            return null;
        }
        catch (Exception e) when (e is InvalidPrinterException or System.ComponentModel.Win32Exception or ExternalException)
        {
            return $"The recovery sheet could not be printed: {e.Message}";
        }
    }

    /// <summary>Every page, in order: the first, the pages of codes, then the key as text.</summary>
    private static IEnumerable<Action<Painter>> Pages(RecoverySheet sheet)
    {
        yield return p => RecoverySheetPage(p, sheet);
        var number = 0;
        foreach (var parts in sheet.CodePages)
        {
            var page = ++number;
            yield return p => RecoverySheetCodesPage(p, sheet, parts, page);
        }
        foreach (var keyPage in sheet.SecretKeyPages) yield return p => RecoverySheetKeyPage(p, sheet, keyPage);
    }

    private const float Margin = 54;

    // ---- The first page ---------------------------------------------------------------------------

    /// <summary>The QR code's side on the first page, and the smallest it may shrink to on short paper.</summary>
    private const float QRSide = 250, MinimumQRSide = 160;

    /// <summary>
    /// The first page. Upstream's stack puts the instructions at the foot of the page with at least
    /// 20 points above them (<c>Spacer(minLength: 20)</c>). Windows lays the page out once without
    /// drawing; when it would not fit (US Letter is 50 points shorter than A4, and Segoe UI sets
    /// a little taller), the QR code gives up the difference, down to <c>MinimumQRSide</c>.
    /// </summary>
    private static void RecoverySheetPage(Painter p, RecoverySheet sheet)
    {
        var instructions = Instructions(sheet);
        var bottom = p.Measure(instructions, 10, FontStyle.Regular, p.Paper.Width - 2 * Margin).Height;
        var room = p.Paper.Height - Margin - bottom - 20;
        var side = QRSide;
        p.Draws = false;
        var end = RecoverySheetBody(p, sheet, side);
        p.Draws = true;
        if (end > room) side = Math.Max(MinimumQRSide, side - (end - room));
        end = RecoverySheetBody(p, sheet, side);
        p.Text(instructions, Margin, Math.Max(end + 20, p.Paper.Height - Margin - bottom), p.Paper.Width - 2 * Margin, 10);
    }

    private static string Instructions(RecoverySheet sheet) => sheet.HasQRCode
        ? $"To recover the account, open Signet, choose Scan QR code… and hold this sheet up to the camera, or choose Import account… and type the secret key{(sheet.IsEncrypted ? ", then enter the password" : "")}. octez-client accepts the same key. Keep this sheet offline and out of sight, and do not photograph or photocopy it."
        : $"To recover the account, open Signet, choose Scan QR code… and hold up each of the {sheet.KeyParts.Count} codes on the following pages, in any order; Signet says which it still needs. Or choose Import account… and type the secret key from the last pages as one unbroken line, without the line numbers or spaces{(sheet.IsEncrypted ? ". Then enter the password" : "")}. octez-client accepts the typed key. Keep these pages offline and out of sight, and do not photograph or photocopy them.";

    /// <summary>Everything on the first page above the instructions; returns where it ends.</summary>
    private static float RecoverySheetBody(Painter p, RecoverySheet sheet, float qrSide)
    {
        var hasQRCode = sheet.HasQRCode;
        var partCount = sheet.KeyParts.Count;
        var width = p.Paper.Width - 2 * Margin;
        var y = Margin;

        p.Icon(Margin, y, 64);
        var titleX = Margin + 64 + 18;
        var titleHeight = p.Text("Signet Tezos Wallet Recovery Sheet", titleX, y + 14, p.Paper.Width - Margin - titleX, 22, FontStyle.Bold);
        p.Rule(titleX, y + 14 + titleHeight + 4, p.Paper.Width - Margin, 1.5f);
        y += 64 + 22;

        y += p.Text(sheet.Alias, Margin, y, width, 17, FontStyle.Bold) + 3;
        var printed = $"Printed {sheet.Date.ToString("D", CultureInfo.CurrentCulture)}{(sheet.Version.Length == 0 ? "" : $" by Signet {sheet.Version}")}";
        y += p.Text(printed, Margin, y, width, 10);

        if (hasQRCode && sheet.QRMatrix() is { } matrix)
        {
            y += 26;
            p.QRCode(matrix, (p.Paper.Width - qrSide) / 2, y, qrSide);
            y += qrSide + 8;
            var caption = sheet.IsEncrypted ? "Secret key, protected by its password" : "Secret key";
            y += p.Text(caption, Margin, y, width, 10, align: StringAlignment.Center);
        }

        y += 26;
        y += Row(p, "Address", sheet.Address.Value, y, width) + 14;
        if (sheet.PublicKey is { } publicKey)
        {
            if (publicKey.Length > RecoverySheet.LineLength)
            {
                y += p.Text("Public key", Margin, y, width, 10, FontStyle.Bold) + 3;
                y += NumberedKeyLines(p, RecoverySheet.Lines(publicKey), 1, y) + 14;
            }
            else
            {
                y += Row(p, "Public key", publicKey, y, width) + 14;
            }
        }
        var secretTitle = sheet.IsEncrypted ? "Secret key (encrypted)" : "Secret key";
        if (hasQRCode)
            y += Row(p, secretTitle, sheet.SecretKey, y, width);
        else
            y += Row(p, secretTitle,
                     partCount > 0
                         ? $"Too long for one QR code. It follows as {partCount} codes, then in full as text; keep all {sheet.PageCount} pages together."
                         : $"Too long for QR codes. It follows in full as text; keep all {sheet.PageCount} pages together.",
                     y, width, monospaced: false);

        y += 30;
        if (sheet.IsEncrypted)
        {
            var label = p.Measure("Password", 13, FontStyle.Bold, width);
            p.Text("Password", Margin, y + (44 - label.Height) / 2, width, 13, FontStyle.Bold);
            p.Box(Margin + label.Width + 14, y, width - label.Width - 14, 44, 1.5f);
            y += 44 + 6;
            y += p.Text("Write the password in by hand; it is not printed and cannot be recovered from this sheet. For more safety keep it somewhere else.",
                        Margin, y, width, 10);
        }
        else
        {
            const string warning = "This key has no password. Anyone who sees this sheet or a copy of it controls the account.";
            var height = p.Measure(warning, 12, FontStyle.Bold, width - 20).Height;
            p.Box(Margin, y, width, height + 20, 1.5f);
            p.Text(warning, Margin + 10, y + 10, width - 20, 12, FontStyle.Bold);
            y += height + 20;
        }
        return y;
    }

    private static float Row(Painter p, string title, string value, float y, float width, bool monospaced = true)
    {
        var height = p.Text(title, Margin, y, width, 10, FontStyle.Bold) + 3;
        return height + p.Text(value, Margin, y + height, width, 12, monospaced: monospaced);
    }

    /// <summary>Key text as numbered lines, so a reader typing it back can keep their place.</summary>
    private static float NumberedKeyLines(Painter p, IReadOnlyList<string> lines, int firstLine, float y)
    {
        var top = y;
        for (var index = 0; index < lines.Count; index++)
        {
            var number = (firstLine + index).ToString("00", CultureInfo.InvariantCulture);
            var lineHeight = p.Measure(lines[index], 8, FontStyle.Regular, 1000, monospaced: true).Height;
            p.Text(number, Margin, y + 1, 14, 6.5f, monospaced: true, align: StringAlignment.Far);
            p.Text(lines[index], Margin + 22, y, 1000, 8, monospaced: true, wrap: false);
            y += lineHeight + 1.5f;
        }
        return y - top;
    }

    // ---- The following pages ----------------------------------------------------------------------

    private static float Header(Painter p, RecoverySheet sheet, int pageNumber)
    {
        var width = p.Paper.Width - 2 * Margin;
        var y = Margin;
        y += p.Text("Signet Tezos Wallet Recovery Sheet", Margin, y, width, 14, FontStyle.Bold) + 3;
        y += p.Text($"{sheet.Alias}  ·  {sheet.Address.Value}  ·  page {pageNumber} of {sheet.PageCount}", Margin, y, width, 9) + 6;
        p.Rule(Margin, y, p.Paper.Width - Margin, 1);
        return y + 14;
    }

    /// <summary>A page of the codes that together carry a key too long for one.</summary>
    private static void RecoverySheetCodesPage(Painter p, RecoverySheet sheet, IReadOnlyList<KeyPart> parts, int number)
    {
        const float codeSize = 230, across = 27, down = 26;
        var width = p.Paper.Width - 2 * Margin;
        var y = Header(p, sheet, 1 + number);
        y += p.Text($"{(sheet.IsEncrypted ? "Secret key (encrypted)" : "Secret key")} as QR codes. Scan every part with Signet's Scan QR code, in any order.",
                    Margin, y, width, 10, FontStyle.Bold) + 18;
        for (var start = 0; start < parts.Count; start += 2)
        {
            var rowHeight = 0f;
            for (var column = 0; column < 2 && start + column < parts.Count; column++)
            {
                var part = parts[start + column];
                var x = Margin + column * (codeSize + across);
                if (Signet.Services.QRCode.Matrix(part.Text, QRCoder.QRCodeGenerator.ECCLevel.L) is { } matrix) p.QRCode(matrix, x, y, codeSize);
                var caption = p.Text($"Part {part.Index} of {part.Count}", x, y + codeSize + 6, codeSize, 11, FontStyle.Bold, align: StringAlignment.Center);
                rowHeight = Math.Max(rowHeight, codeSize + 6 + caption);
            }
            y += rowHeight + down;
        }
    }

    /// <summary>A following page of a sheet whose key is too long for a QR code: part of the secret key as text.</summary>
    private static void RecoverySheetKeyPage(Painter p, RecoverySheet sheet, RecoverySheet.KeyPage page)
    {
        var width = p.Paper.Width - 2 * Margin;
        var y = Header(p, sheet, 1 + sheet.CodePages.Count + page.Part);
        y += p.Text($"{(sheet.IsEncrypted ? "Secret key (encrypted)" : "Secret key")}, lines {page.FirstLine} to {page.FirstLine + page.Lines.Count - 1}",
                    Margin, y, width, 10, FontStyle.Bold) + 6;
        NumberedKeyLines(p, page.Lines, page.FirstLine, y);
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    /// <summary>GDI+ in points, with the few marks the pages are made of.</summary>
    private sealed class Painter(Graphics graphics, SizeF paper)
    {
        public SizeF Paper { get; } = paper;

        /// <summary>Off while a page is only being laid out: every mark is measured, nothing is drawn.</summary>
        public bool Draws { get; set; } = true;

        private static Font Font(float size, FontStyle style, bool monospaced) =>
            new(monospaced ? "Consolas" : "Segoe UI", size, style, GraphicsUnit.Point);

        public SizeF Measure(string text, float size, FontStyle style, float width, bool monospaced = false)
        {
            using var font = Font(size, style, monospaced);
            return graphics.MeasureString(text, font, new SizeF(width, 10000));
        }

        /// <summary>Draws wrapped text in a column <c>width</c> wide and returns its height.</summary>
        public float Text(string text, float x, float y, float width, float size, FontStyle style = FontStyle.Regular,
                          bool monospaced = false, StringAlignment align = StringAlignment.Near, bool wrap = true)
        {
            using var font = Font(size, style, monospaced);
            using var format = new StringFormat { Alignment = align };
            if (!wrap) format.FormatFlags |= StringFormatFlags.NoWrap;
            var measured = graphics.MeasureString(text, font, new SizeF(width, 10000), format);
            if (Draws) graphics.DrawString(text, font, Brushes.Black, new RectangleF(x, y, width, measured.Height + 1), format);
            return measured.Height;
        }

        public void Rule(float x1, float y, float x2, float thickness)
        {
            if (Draws) graphics.FillRectangle(Brushes.Black, x1, y, x2 - x1, thickness);
        }

        public void Box(float x, float y, float width, float height, float thickness)
        {
            if (!Draws) return;
            using var pen = new Pen(Color.Black, thickness) { Alignment = PenAlignment.Inset };
            graphics.DrawRectangle(pen, x, y, width, height);
        }

        /// <summary>The app icon, from the same artwork as the welcome screen.</summary>
        public void Icon(float x, float y, float side)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Signet.png");
            if (!Draws || !File.Exists(path)) return;
            using var image = System.Drawing.Image.FromFile(path);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, x, y, side, side);
        }

        /// <summary>A code as filled squares, a dark run of modules at a time so nothing shows between them.</summary>
        public void QRCode(IReadOnlyList<bool[]> matrix, float x, float y, float side)
        {
            if (!Draws) return;
            var count = matrix.Count;
            var module = side / count;
            var smoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.FillRectangle(Brushes.White, x, y, side, side);
            for (var row = 0; row < count; row++)
            {
                var columns = matrix[row];
                for (var column = 0; column < columns.Length;)
                {
                    if (!columns[column]) { column++; continue; }
                    var start = column;
                    while (column < columns.Length && columns[column]) column++;
                    graphics.FillRectangle(Brushes.Black, x + start * module, y + row * module, (column - start) * module, module);
                }
            }
            graphics.SmoothingMode = smoothing;
        }
    }

    /// <summary>The app's window as the print dialog's owner.</summary>
    private sealed class Owner(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
