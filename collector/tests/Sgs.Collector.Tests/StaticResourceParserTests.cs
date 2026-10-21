using Sgs.Collector.Discovery;

namespace Sgs.Collector.Tests;

public sealed class StaticResourceParserTests
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";

    private static readonly StaticResourceParser Parser = new(EntryUrl);
    private static readonly Uri Entry = new(EntryUrl);

    [Fact]
    public void ParseVersionJson_ReturnsRawNumberTextIncludingNegativeAndStringValues()
    {
        var entries = Parser.ParseVersionJson("""{"a/b.png": -2042252689, "c.js": "7", "d.json": 12, "e": null, " ": 1}""").ToArray();

        Assert.Equal(
            [new VersionEntry("a/b.png", "-2042252689"), new VersionEntry("c.js", "7"), new VersionEntry("d.json", "12"), new VersionEntry("e", "")],
            entries);
    }

    [Fact]
    public void ScanStaticResourcePaths_FindsVersionedAndNewExtensionsAndSkipsTemplates()
    {
        const string text = """
            a("res/assets/x.png?v=123"); b('res/skel/hero.skel'); c("res/video/intro.mp4");
            d("res/font/a.ttf"); e("res/assets/{id}.png"); f("data:image/png;base64,res/a.png");
            """;

        var paths = Parser.ScanStaticResourcePaths(text).ToArray();

        Assert.Equal(["res/assets/x.png?v=123", "res/skel/hero.skel", "res/video/intro.mp4", "res/font/a.ttf"], paths);
    }

    [Fact]
    public void ScanStaticResourcePaths_FindsProtocolRelativeUrls()
    {
        var paths = Parser.ScanStaticResourcePaths("""a("//web.sanguosha.com/220/h5_2/res/a.png")""").ToArray();

        Assert.Equal(["//web.sanguosha.com/220/h5_2/res/a.png"], paths);
    }

    [Fact]
    public void TryDecodeText_StripsUtf8Bom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'];

        Assert.True(Parser.TryDecodeText(bytes, out var text));
        Assert.Equal("{}", text);
    }

    [Theory]
    [InlineData("res/assets/a.png", "https://web.sanguosha.com/220/h5_2/res/assets/a.png")]
    [InlineData("sub/b.js", "https://web.sanguosha.com/220/h5_2/res/x/sub/b.js")]
    [InlineData("//cdn.example.com/c.js", "https://cdn.example.com/c.js")]
    public void TryCreateResourceUri_HandlesResPrefixRelativeAndProtocolRelative(string value, string expected)
    {
        var referer = new Uri("https://web.sanguosha.com/220/h5_2/res/x/page.js");

        Assert.True(Parser.TryCreateResourceUri(value, referer, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Fact]
    public void ParseEntry_CollectsInlineScriptsWithoutCommentedOnes()
    {
        const string html = """
            <html><head>
            <script src="libs/a.js"></script>
            <script>window.mainVersion = "1";</script>
            <!-- <script>window.mainVersion = "old";</script> -->
            <script src="  ">window.layaVersion = "2";</script>
            </head></html>
            """;

        var snapshot = Parser.ParseEntry(html, Entry);

        Assert.Equal(["https://web.sanguosha.com/220/h5_2/libs/a.js"], snapshot.EffectiveScripts.Select(u => u.AbsoluteUri));
        Assert.Contains("mainVersion = \"1\"", snapshot.InlineScriptText);
        Assert.Contains("layaVersion", snapshot.InlineScriptText);
        Assert.DoesNotContain("old", snapshot.InlineScriptText);
    }

    [Fact]
    public void FindWindowString_TakesTheLastMatchAndSupportsBothQuotes()
    {
        const string script = "window.mainVersion = \"1\";\nwindow.mainVersion='2';\nwindow.other = 'x';";

        Assert.Equal("2", StaticResourceParser.FindWindowString(script, "mainVersion"));
    }

    [Fact]
    public void FindWindowString_ReturnsNullWhenMissingOrEmpty()
    {
        Assert.Null(StaticResourceParser.FindWindowString("window.a = 'x';", "b"));
        Assert.Null(StaticResourceParser.FindWindowString("window.a = '1'; window.a = '  ';", "a"));
    }
}
