using System.Text;
using Sgs.Collector.Crawl;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Tests;

public sealed class DiscoveryEngineTests
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    private const string Root = "https://web.sanguosha.com/220/h5_2/";
    private const string KeyRoot = "web.sanguosha.com/220/h5_2/";

    private static readonly DiscoveryRules Rules = new(EntryUrl);
    private static readonly DiscoveryEngine Engine = new(Rules, new StaticResourceParser(Rules));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static CrawlItem Item(string relative) =>
        new(KeyRoot + relative, new Uri(Root + relative), CrawlItemKind.Archive, "test");

    [Fact]
    public void FromEntry_ReadsVersionsFromInlineScriptsAndLinksScriptSrc()
    {
        const string html = """
            <script src="libs/min/before.min.js"></script>
            <script>window.mainVersion = "1"; window.layaVersion = "L1"; window.rescVersion = "R1"; window.mainVersion = "2";</script>
            <!-- <script>window.layaVersion = "commented";</script> -->
            """;

        var result = Engine.FromEntry(Utf8(html), Rules.EntryUri);

        Assert.Equal("2", result.MainVersion);
        Assert.Equal("L1", result.LayaVersion);
        Assert.Equal("R1", result.RescVersion);
        var link = Assert.Single(result.Links);
        Assert.Equal(Root + "libs/min/before.min.js", link.Uri.AbsoluteUri);
        Assert.Equal("entry-script", link.Source);
    }

    [Fact]
    public void FromEntry_ReturnsEmptyForBinaryContent()
    {
        var result = Engine.FromEntry([0x00, 0x01, 0x02], Rules.EntryUri);

        Assert.Empty(result.Links);
        Assert.Null(result.MainVersion);
    }

    [Fact]
    public void FromResource_VersionJson_BuildsVersionedUrisAndSkipsTemplatesAndDirectConfig()
    {
        const string json = """
            {"res/a.png": -5, "libs/b.js": "9", "res/{x}.png": 1, "res/config/c.json": 2}
            """;

        var links = Engine.FromResource(Item("version.json"), Utf8(json), []);

        Assert.Equal([Root + "res/a.png?v=-5", Root + "libs/b.js?v=9"], links.Select(l => l.Uri.AbsoluteUri));
        Assert.All(links, l => Assert.Equal("version-json", l.Source));
    }

    [Fact]
    public void FromResource_DefaultResJson_UsesDefaultResSourceAndSkipsDirectConfig()
    {
        const string json = """{"resources":[{"url":"res/assets/a.png"},{"url":"res/config/x.json"}]}""";

        var links = Engine.FromResource(Item("res/default.res.json"), Utf8(json), []);

        var link = Assert.Single(links);
        Assert.Equal(Root + "res/assets/a.png", link.Uri.AbsoluteUri);
        Assert.Equal("default-res", link.Source);
    }

    [Fact]
    public void FromResource_PackageMembers_AreScannedAndConfigPackagesAreNotAdded()
    {
        var item = Item("sgsGame.sgs");
        DecodedMember[] members =
        [
            new("a.js", "a.js", Utf8("""load("res/assets/ui/x.png"); load('res/config/Config_w.sgs'); load("res/config/data.json");"""), null),
            new("broken.js", "broken.js", null, "decode failed")
        ];

        var links = Engine.FromResource(item, [1, 2, 3], members);

        var link = Assert.Single(links);
        Assert.Equal(Root + "res/assets/ui/x.png", link.Uri.AbsoluteUri);
        Assert.Equal("static-text-scan", link.Source);
    }

    [Fact]
    public void FromResource_PlainScanTarget_ScansRawContent()
    {
        var links = Engine.FromResource(Item("libs/min/before.min.js"), Utf8("""x("res/assets/y.webp?v=3")"""), []);

        Assert.Equal([Root + "res/assets/y.webp?v=3"], links.Select(l => l.Uri.AbsoluteUri));
    }

    [Fact]
    public void FromResource_IgnoresResourcesThatAreNotParsed()
    {
        var links = Engine.FromResource(Item("res/assets/a.png"), Utf8("""x("res/assets/y.png")"""), []);

        Assert.Empty(links);
    }
}
