using System.Text.RegularExpressions;
using Signet.Models;

namespace Signet.Tests;

/// <summary>Help › Getting Started: the book chapter rendered from Markua, and bundled with the app.</summary>
public class GettingStartedTests
{
    [Fact]
    public void RendersTheBlocksTheBookUses()
    {
        const string source = """
            {#id: 110-starting_1_Getting}
            # Getting started

            Some **bold**, *italic*, `code` and a [site](https://signetwallet.org).
            A second line of the same paragraph.

            | Platform | Repository |
            |----------|------------|
            | [Mac](https://github.com/drchrispinnock/signet) | x |

            ```
            sudo apt install <signet>
            ```

            ![The disclaimer](110/Disclaimer.png)

            - one
            - two

            1. first
            2. second

            I> A note for the reader.
            """;
        var html = Markua.Html(source, "Getting Started");
        Assert.Contains("""<h1 id="110-starting_1_Getting">Getting started</h1>""", html);
        Assert.Contains("<p>Some <strong>bold</strong>, <em>italic</em>, <code>code</code> and a <a href=\"https://signetwallet.org\">site</a>. A second line of the same paragraph.</p>", html);
        Assert.Contains("<tr><th>Platform</th><th>Repository</th></tr><tr><td><a href=\"https://github.com/drchrispinnock/signet\">Mac</a></td><td>x</td></tr>", html);
        Assert.Contains("<pre><code>sudo apt install &lt;signet&gt;</code></pre>", html);
        Assert.Contains("""<img src="110/Disclaimer.png" alt="The disclaimer">""", html);
        Assert.True(html.Contains("<ul><li>one</li><li>two</li></ul>") && html.Contains("<ol><li>first</li><li>second</li></ol>"));
        Assert.Contains("""<aside class="info"><p>A note for the reader.</p></aside>""", html);
    }

    [Fact]
    public void ReadsBothAnchorFormsAndHidesAttributeLines()
    {
        var html = Markua.Html("{id: ID110-starting_1_Getting, sample: true}\n# Getting started\n\n{width: 80%}\nText", "t");
        Assert.Contains("""<h1 id="ID110-starting_1_Getting">Getting started</h1>""", html);
        // The page's stylesheet has braces of its own; the attribute lines must not appear.
        Assert.True(!html.Contains("{id:") && !html.Contains("{width") && html.Contains("<p>Text</p>"));
        Assert.Equal("110-starting_1_Getting", Markua.Anchor("{#id: 110-starting_1_Getting}"));
    }

    [Fact]
    public void LinksWithinTheChapterStayAndOthersGoToTheBook()
    {
        const string source = "{#id: here}\n## Here\n\nSee [here](#here), [dApps](#170-dapps_1_Distribute) and [bad](javascript:alert(1)).";
        var html = Markua.Html(source, "t");
        Assert.Contains("""<a href="#here">here</a>""", html);
        Assert.Contains("""<a href="https://leanpub.com/read/tezossignet/170-dapps_1_Distribute">dApps</a>""", html);
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void HidesAuthorNotesAndEscapesEverything()
    {
        var html = Markua.Html("Before <!-- XXX a note\nover two lines --> after <script>alert(1)</script> my_snake_case_name", "t");
        Assert.True(!html.Contains("XXX") && !html.Contains("<script>"));
        Assert.Contains("Before  after &lt;script&gt;alert(1)&lt;/script&gt; my_snake_case_name", html);
        // Only pictures beside the chapter.
        Assert.DoesNotContain("<img", Markua.Html("![](https://example.com/x.png)\n\n![](../../x.png)", "t"));
    }

    /// <summary>
    /// Windows: the app links the chapter and its pictures from the submodule (<c>Signet.App.csproj</c>),
    /// so this checks the submodule's copy carries every picture the chapter shows.
    /// </summary>
    [Fact]
    public void TheAppBundlesTheChapterAndItsPictures()
    {
        var manuscript = Path.Combine(TestSupport.RepositoryRoot, "upstream", "manuscript");
        var html = Markua.Html(File.ReadAllText(Path.Combine(manuscript, "110-starting.md")), "Getting Started");
        Assert.Contains("<h1 id=\"ID110-starting_1_Getting\">Getting started</h1>", html, StringComparison.OrdinalIgnoreCase);
        var pictures = Regex.Matches(html, "<img src=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(pictures.Count >= 10);
        foreach (var picture in pictures)
            Assert.True(File.Exists(Path.Combine(manuscript, "resources", picture)), picture);
        var project = File.ReadAllText(Path.Combine(TestSupport.RepositoryRoot, "src", "Signet.App", "Signet.App.csproj"));
        Assert.Contains(@"upstream\manuscript\resources\110\**", project);
    }
}
