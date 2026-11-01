using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Signet.App.Services;

/// <summary>
/// Windows-only: the logos linked from upstream's asset catalogue, drawn by WinUI. Its
/// <c>SvgImageSource</c> (Direct2D) ignores CSS, so an SVG that colours its shapes through a
/// <c>&lt;style&gt;</c> block (<c>.cls-1 { fill: … }</c>, as the Etherlink logo does) comes out
/// solid black. The class fills are written onto the shapes as attributes before the SVG is
/// handed over; the file itself stays upstream's.
/// </summary>
public static partial class SvgLogo
{
    /// <summary>The SVG at <c>path</c> (relative to the app folder), rasterised at <c>pixels</c> square.</summary>
    public static SvgImageSource Load(string path, double pixels)
    {
        var source = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
        _ = SetSource(source, Path.Combine(AppContext.BaseDirectory, path));
        return source;
    }

    private static async Task SetSource(SvgImageSource source, string file)
    {
        try
        {
            var text = InlineClassFills(await File.ReadAllTextAsync(file));
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteString(text);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            await source.SetSourceAsync(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Could not load {file}: {e.Message}");
        }
    }

    /// <summary>Replaces <c>class="cls-N"</c> with the <c>fill</c> its style rule gives it; other SVGs pass unchanged.</summary>
    public static string InlineClassFills(string svg)
    {
        var fills = ClassRule().Matches(svg).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        if (fills.Count == 0) return svg;
        return ClassAttribute().Replace(svg, m => fills.TryGetValue(m.Groups[1].Value, out var fill) ? $"fill=\"{fill}\"" : m.Value);
    }

    [GeneratedRegex(@"\.([A-Za-z0-9_-]+)\s*\{\s*fill:\s*([^;}]+);?\s*\}")]
    private static partial Regex ClassRule();

    [GeneratedRegex("class=\"([A-Za-z0-9_-]+)\"")]
    private static partial Regex ClassAttribute();
}
