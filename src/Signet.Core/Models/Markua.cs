using System.Text;

namespace Signet.Models;

/// <summary>
/// Turns a chapter of the Signet book (Leanpub's Markua, the subset the book uses) into HTML for
/// Help › Getting Started: <c>{#id: …}</c> anchors, <c>#</c> headings, paragraphs, <c>-</c>/<c>*</c>/<c>1.</c>
/// lists, <c>|</c> tables, fenced code, <c>![](path)</c> images, <c>A&gt;</c>/<c>I&gt;</c>/<c>W&gt;</c>/<c>T&gt;</c>
/// asides, and inline links, code, bold and italics. Every piece of text is HTML-escaped; links to
/// a chapter that is not in this one go to the book on Leanpub.
/// </summary>
public static class Markua
{
    /// <summary>Where the book is read online; a <c>#id</c> link to another chapter goes to <c>BookURL/&lt;id&gt;</c>.</summary>
    public const string BookURL = "https://leanpub.com/read/tezossignet";

    /// <param name="imageSources">
    /// Windows: what the page's Content-Security-Policy allows pictures from. Upstream's web view reads
    /// them as <c>file:</c>; WebView2 serves them from a virtual host, which the app names here.
    /// </param>
    public static string Html(string source, string title, string imageSources = "file:")
    {
        var lines = WithoutComments(source.Replace("\r\n", "\n")).Split('\n');
        var anchors = lines.Select(Anchor).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var body = new StringBuilder();
        var paragraph = new List<string>();
        string? pendingAnchor = null;
        var index = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            body.Append($"<p>{Inline(string.Join(' ', paragraph), anchors)}</p>\n");
            paragraph.Clear();
        }

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim(' ', '\t');

            if (Attributes(line) is not null)
            {
                FlushParagraph();
                if (Anchor(line) is { } id) pendingAnchor = id;
                index++;
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                index++;
                continue;
            }
            // Fenced code: everything up to the closing fence, as is.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].Trim(' ', '\t').StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                index++;
                body.Append($"<pre><code>{Escape(string.Join('\n', code))}</code></pre>\n");
                continue;
            }
            if (Heading(trimmed) is { } heading)
            {
                FlushParagraph();
                var id = pendingAnchor is null ? "" : $" id=\"{Escape(pendingAnchor)}\"";
                pendingAnchor = null;
                body.Append($"<h{heading.Level}{id}>{Inline(heading.Text, anchors)}</h{heading.Level}>\n");
                index++;
                continue;
            }
            if (Image(trimmed) is { } image)
            {
                FlushParagraph();
                body.Append($"<figure><img src=\"{Escape(image.Path)}\" alt=\"{Escape(image.Alt)}\">"
                            + (image.Alt.Length == 0 ? "" : $"<figcaption>{Escape(image.Alt)}</figcaption>") + "</figure>\n");
                index++;
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string>();
                while (index < lines.Length && lines[index].Trim(' ', '\t').StartsWith('|'))
                {
                    rows.Add(lines[index].Trim(' ', '\t'));
                    index++;
                }
                body.Append(Table(rows, anchors));
                continue;
            }
            if (Aside(trimmed) is { } kind)
            {
                FlushParagraph();
                var text = new List<string>();
                while (index < lines.Length && AsideText(lines[index].Trim(' ', '\t'), kind) is { } rest)
                {
                    text.Add(rest);
                    index++;
                }
                body.Append($"<aside class=\"{kind}\"><p>{Inline(string.Join(' ', text), anchors)}</p></aside>\n");
                continue;
            }
            if (ListItem(trimmed) is { } first)
            {
                FlushParagraph();
                var items = new List<string>();
                while (index < lines.Length)
                {
                    var current = lines[index].Trim(' ', '\t');
                    if (ListItem(current) is { } item) items.Add(item.Text);
                    else if (current.Length > 0 && items.Count > 0 && lines[index].StartsWith(' ')) items[^1] += " " + current;  // a wrapped item
                    else break;
                    index++;
                }
                var tag = first.Ordered ? "ol" : "ul";
                body.Append($"<{tag}>" + string.Concat(items.Select(i => $"<li>{Inline(i, anchors)}</li>")) + $"</{tag}>\n");
                continue;
            }
            paragraph.Add(trimmed);
            index++;
        }
        FlushParagraph();
        return Page(title, body.ToString(), imageSources);
    }

    /// <summary>
    /// The text without <c>&lt;!-- … --&gt;</c> comments, the author's notes Leanpub does not print;
    /// an unclosed comment runs to the end.
    /// </summary>
    public static string WithoutComments(string text)
    {
        var output = new StringBuilder();
        var position = 0;
        while (text.IndexOf("<!--", position, StringComparison.Ordinal) is var start and >= 0)
        {
            output.Append(text, position, start - position);
            var end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0) return output.ToString();
            position = end + 3;
        }
        return output.Append(text, position, text.Length - position).ToString();
    }

    // ---- Blocks ----------------------------------------------------------------------------------

    /// <summary>The id in an attribute line, <c>{#id: x}</c> or <c>{id: x, sample: true}</c>.</summary>
    public static string? Anchor(string line)
    {
        if (Attributes(line) is not { } attributes) return null;
        foreach (var field in attributes.Split(','))
        {
            var parts = field.Split(':', 2).Select(p => p.Trim(' ', '\t')).ToArray();
            if (parts.Length != 2 || parts[0] is not ("id" or "#id") || parts[1].Length == 0) continue;
            return parts[1].Trim('"', '\'');
        }
        return null;
    }

    /// <summary>The inside of a Markua attribute line (<c>{…}</c> alone on its line), which is never printed.</summary>
    public static string? Attributes(string line)
    {
        var trimmed = line.Trim(' ', '\t');
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}') || trimmed.Length <= 2 || trimmed[1..].Contains('{')) return null;
        return trimmed[1..^1];
    }

    private static (int Level, string Text)? Heading(string line)
    {
        var hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes is < 1 or > 6 || line.Length <= hashes || line[hashes] != ' ') return null;
        return (hashes, line[(hashes + 1)..]);
    }

    private static (string Alt, string Path)? Image(string line)
    {
        if (!line.StartsWith("![", StringComparison.Ordinal) || !line.EndsWith(')')) return null;
        var close = line.IndexOf("](", StringComparison.Ordinal);
        if (close < 0) return null;
        var alt = line[2..close];
        var path = line[(close + 2)..^1];
        // Only pictures shipped beside the chapter: no other files, no other sites.
        if (path.Contains("..") || path.Contains(':') || path.StartsWith('/')) return null;
        return (alt, path);
    }

    private static readonly (string Marker, string Kind)[] Asides = [("A>", "aside"), ("I>", "info"), ("W>", "warning"), ("T>", "tip")];

    private static string? Aside(string line) => Asides.FirstOrDefault(a => line.StartsWith(a.Marker, StringComparison.Ordinal)).Kind;

    private static string? AsideText(string line, string kind) => Aside(line) == kind ? line[2..].Trim(' ', '\t') : null;

    private static (bool Ordered, string Text)? ListItem(string line)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) return (false, line[2..]);
        var digits = line.TakeWhile(char.IsDigit).Count();
        if (digits > 0 && line[digits..].StartsWith(". ", StringComparison.Ordinal)) return (true, line[(digits + 2)..]);
        return null;
    }

    private static string Table(List<string> rows, HashSet<string> anchors)
    {
        static string[] Cells(string row)
        {
            var inner = row;
            if (inner.StartsWith('|')) inner = inner[1..];
            if (inner.EndsWith('|')) inner = inner[..^1];
            return inner.Split('|').Select(c => c.Trim(' ', '\t')).ToArray();
        }
        static bool IsSeparator(string row) => Cells(row).All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'));
        var html = new StringBuilder("<table>");
        var header = rows.Count >= 2 && IsSeparator(rows[1]);
        for (var i = 0; i < rows.Count; i++)
        {
            if (IsSeparator(rows[i])) continue;
            var tag = header && i == 0 ? "th" : "td";
            html.Append("<tr>" + string.Concat(Cells(rows[i]).Select(c => $"<{tag}>{Inline(c, anchors)}</{tag}>")) + "</tr>");
        }
        return html.Append("</table>\n").ToString();
    }

    // ---- Inline ----------------------------------------------------------------------------------

    /// <summary>Links, code, bold and italics in a line of text, escaped.</summary>
    public static string Inline(string text, IReadOnlySet<string> anchors)
    {
        var output = new StringBuilder();
        var position = 0;
        while (text.IndexOfAny(['`', '[', '*', '_'], position) is var next and >= 0)
        {
            output.Append(Escape(text[position..next]));
            position = next;
            int end;
            if (text[position] == '`' && (end = text.IndexOf('`', position + 1)) >= 0)
            {
                output.Append($"<code>{Escape(text[(position + 1)..end])}</code>");
                position = end + 1;
            }
            else if (text[position] == '[' && text.IndexOf("](", position, StringComparison.Ordinal) is var close and >= 0
                     && (end = text.IndexOf(')', close + 2)) >= 0)
            {
                output.Append(Link(text[(position + 1)..close], text[(close + 2)..end], anchors));
                position = end + 1;
            }
            else if (text.AsSpan(position).StartsWith("**") && (end = text.IndexOf("**", position + 2, StringComparison.Ordinal)) >= 0)
            {
                output.Append($"<strong>{Inline(text[(position + 2)..end], anchors)}</strong>");
                position = end + 2;
            }
            else if (text[position] is '*' or '_' && (end = text.IndexOf(text[position], position + 1)) > position + 1
                     && !(text[position] == '_' && position > 0 && char.IsLetterOrDigit(text[position - 1])))
            {
                output.Append($"<em>{Escape(text[(position + 1)..end])}</em>");
                position = end + 1;
            }
            else
            {
                output.Append(Escape(text[position].ToString()));
                position++;
            }
        }
        return output.Append(Escape(text[position..])).ToString();
    }

    private static string Link(string label, string target, IReadOnlySet<string> anchors)
    {
        string href;
        if (target.StartsWith('#'))
        {
            var id = target[1..];
            href = anchors.Contains(id) ? $"#{id}" : $"{BookURL}/{id}";
        }
        else if (target.StartsWith("https://", StringComparison.Ordinal) || target.StartsWith("http://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
        {
            href = target;
        }
        else
        {
            return Escape(label);
        }
        return $"<a href=\"{Escape(href)}\">{Inline(label, anchors)}</a>";
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---- Page ------------------------------------------------------------------------------------

    private static string Page(string title, string body, string imageSources) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src {{imageSources}}; style-src 'unsafe-inline'">
        <title>{{Escape(title)}}</title>
        <style>
        :root { color-scheme: light dark; --text: #1d1d1f; --muted: #6e6e73; --rule: #d2d2d7; --code: #f5f5f7; --link: #0066cc; --aside: #f2f7ff; }
        @media (prefers-color-scheme: dark) { :root { --text: #f5f5f7; --muted: #a1a1a6; --rule: #424245; --code: #2c2c2e; --link: #4da3ff; --aside: #1c2633; } }
        body { font: 15px/1.55 -apple-system, system-ui, "Segoe UI", sans-serif; color: var(--text); background: Canvas; margin: 0; }
        main { max-width: 760px; margin: 0 auto; padding: 28px 32px 48px; }
        h1 { font-size: 28px; margin: 0 0 12px; } h2 { font-size: 21px; margin-top: 32px; border-bottom: 1px solid var(--rule); padding-bottom: 4px; } h3 { font-size: 17px; margin-top: 24px; }
        a { color: var(--link); text-decoration: none; } a:hover { text-decoration: underline; }
        code { font: 13px ui-monospace, Menlo, Consolas, monospace; background: var(--code); padding: 1px 4px; border-radius: 4px; }
        pre { background: var(--code); padding: 12px 14px; border-radius: 8px; overflow-x: auto; } pre code { padding: 0; background: none; }
        figure { margin: 18px 0; text-align: center; } img { max-width: 100%; border-radius: 6px; box-shadow: 0 1px 6px rgba(0,0,0,.18); }
        figcaption { color: var(--muted); font-size: 13px; margin-top: 6px; }
        table { border-collapse: collapse; margin: 14px 0; font-size: 14px; } th, td { border: 1px solid var(--rule); padding: 6px 10px; text-align: left; }
        aside { background: var(--aside); border-left: 3px solid var(--link); padding: 2px 14px; border-radius: 4px; margin: 14px 0; }
        aside.warning { border-left-color: #e8a33d; }
        </style></head>
        <body><main>
        {{body}}
        </main></body></html>
        """;
}
