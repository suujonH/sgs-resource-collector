using Sgs.Collector.Common;

namespace Sgs.Collector.Tests;

public sealed class ContentTypesTests
{
    [Theory]
    [InlineData("h/a.json", "application/json; charset=utf-8")]
    [InlineData("h/a.jsonl", "application/x-ndjson; charset=utf-8")]
    [InlineData("h/a.js", "text/javascript; charset=utf-8")]
    [InlineData("h/a.txt", "text/plain; charset=utf-8")]
    [InlineData("h/a.atlas", "text/plain; charset=utf-8")]
    [InlineData("h/a.proto", "text/plain; charset=utf-8")]
    [InlineData("h/a.log", "text/plain; charset=utf-8")]
    [InlineData("h/a.xml", "application/xml; charset=utf-8")]
    [InlineData("h/a.html", "text/html; charset=utf-8")]
    [InlineData("h/a.webp", "image/webp")]
    [InlineData("h/a.mp4", "video/mp4")]
    [InlineData("h/a.webm", "video/webm")]
    [InlineData("h/a.mp3", "audio/mpeg")]
    [InlineData("h/a.ogg", "audio/ogg")]
    [InlineData("h/a.ttf", "font/ttf")]
    [InlineData("h/a.otf", "font/otf")]
    [InlineData("h/a.woff", "font/woff")]
    [InlineData("h/a.woff2", "font/woff2")]
    [InlineData("h/a.swf", "application/x-shockwave-flash")]
    [InlineData("h/a.sk", "application/octet-stream")]
    [InlineData("h/a.skel", "application/octet-stream")]
    [InlineData("h/a.sgs", "application/octet-stream")]
    [InlineData("h/a.part", "application/octet-stream")]
    [InlineData("h/a.bin", "application/octet-stream")]
    [InlineData("h/a.dds", "application/octet-stream")]
    [InlineData("h/a.ktx", "application/octet-stream")]
    public void Resolve_ExtensionTableWinsOverResponseContentType(string key, string expected)
    {
        Assert.Equal(expected, ContentTypes.Resolve(key, "text/html"));
        Assert.Equal(expected, ContentTypes.Resolve(key, null));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnExtension()
    {
        Assert.Equal("application/json; charset=utf-8", ContentTypes.Resolve("h/A.JSON", null));
        Assert.Equal("font/ttf", ContentTypes.Resolve("h/A.TtF", null));
    }

    [Theory]
    [InlineData("h/a.png", "image/png")]
    [InlineData("h/a.jpg", "image/jpeg")]
    [InlineData("h/a.gif", "image/gif")]
    [InlineData("h/a.svg", "image/svg+xml")]
    public void Resolve_UsesFileExtensionProviderForCommonTypes(string key, string expected)
    {
        Assert.Equal(expected, ContentTypes.Resolve(key, "application/octet-stream"));
    }

    [Fact]
    public void Resolve_UnknownExtensionUsesTrimmedResponseContentType()
    {
        Assert.Equal("application/x-custom; charset=gbk", ContentTypes.Resolve("h/a.zzz", "  application/x-custom; charset=gbk "));
        Assert.Equal("text/css", ContentTypes.Resolve("h/noext", "text/css"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToOctetStreamWhenNothingMatches(string? responseContentType)
    {
        Assert.Equal("application/octet-stream", ContentTypes.Resolve("h/a.zzz", responseContentType));
        Assert.Equal("application/octet-stream", ContentTypes.Resolve("h/noext", responseContentType));
    }

    [Fact]
    public void Resolve_OnlyLooksAtTheLastSegmentOfTheKey()
    {
        // 主机名和上级目录里的点号不能被当成扩展名
        Assert.Equal("application/octet-stream", ContentTypes.Resolve("web.sanguosha.com/res/noext", null));
        Assert.Equal("application/octet-stream", ContentTypes.Resolve("h/dir.json/noext", null));
    }

    [Fact]
    public void Resolve_PackageMembersUseTheirOwnExtension()
    {
        var packageKey = "web.sanguosha.com/220/h5_2/res/config/Config_w.sgs";

        Assert.Equal("application/octet-stream", ContentTypes.Resolve(packageKey, null));
        Assert.Equal("application/json; charset=utf-8", ContentTypes.Resolve(ResourceKeys.ForMember(packageKey, "cha_spell.json"), null));
    }
}
