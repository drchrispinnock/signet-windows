using System.Text;

namespace Signet.Services;

/// <summary>
/// Windows-only: where an exception that escapes a UI event is written (<c>crash.log</c> beside the
/// bridge log), since WinUI ends the process with 0xC000027B and no trace. Every line goes through
/// the bridge log's redaction (<see cref="TaquitoBridge.RedactedLine"/>, S11), and the file is
/// rotated to <c>crash.log.1</c> past <see cref="MaxBytes"/> (audit K02), so a pasted secret in an
/// exception message is not kept verbatim and the file cannot grow without bound.
/// </summary>
public static class CrashLog
{
    public const long MaxBytes = 1 << 20;

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(TaquitoBridge.LogFilePath)!, "crash.log");

    /// <summary>The exception as it is recorded: a timestamp line, then each line of its text redacted.</summary>
    public static string Format(Exception exception, DateTimeOffset when)
    {
        var text = new StringBuilder();
        text.Append(when.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var line in exception.ToString().Split('\n'))
            text.Append(TaquitoBridge.RedactedLine(line.TrimEnd('\r'))).Append('\n');
        return text.Append('\n').ToString();
    }

    /// <summary>Appends <c>exception</c> to <c>path</c>; never throws for I/O.</summary>
    public static void Append(Exception exception, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, Format(exception, DateTimeOffset.Now), new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
