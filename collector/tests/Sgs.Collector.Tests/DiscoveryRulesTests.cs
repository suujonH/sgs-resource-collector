using Sgs.Collector.Common;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Tests;

public sealed class DiscoveryRulesTests
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    private const string H5Root = "https://web.sanguosha.com/220/h5_2/";
    private const string KeyRoot = "web.sanguosha.com/220/h5_2/";

    private static readonly DiscoveryRules Rules = new(EntryUrl);

    // ---------- 属性 ----------

    [Fact]
    public void Constructor_DerivesTheH5RootFromTheEntryUrl()
    {
        Assert.Equal(EntryUrl, Rules.EntryUri.AbsoluteUri);
        Assert.Equal(H5Root, Rules.H5Root.AbsoluteUri);
    }

    [Fact]
    public void BootstrapKeys_AreExactlyTheEntryPageAesRescAndVersionJsonWithoutQuery()
    {
        string[] expected = [KeyRoot + "index_210000.php", KeyRoot + "libs/min/aesresc", KeyRoot + "version.json"];

        Assert.Equal(expected.Order(StringComparer.Ordinal), Rules.BootstrapKeys.Order(StringComparer.Ordinal));
        Assert.All(Rules.BootstrapKeys, key => Assert.DoesNotContain('?', key));
    }

    [Fact]
    public void BootstrapKeys_CoverEveryUrlTheBootstrapPhaseRequests()
    {
        // CrawlQueue.Register 靠这个集合识别启动条目：带版本号、时间戳的地址去掉查询参数后必须落在集合里
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_761_000_000_123);

        Assert.Contains(ResourceKeys.FromUri(Rules.EntryUri), Rules.BootstrapKeys);
        Assert.Contains(ResourceKeys.FromUri(Rules.BuildAesRescUri("2019112501")), Rules.BootstrapKeys);
        Assert.Contains(ResourceKeys.FromUri(Rules.BuildAesRescUri(null)), Rules.BootstrapKeys);
        Assert.Contains(ResourceKeys.FromUri(Rules.BuildVersionJsonUri(now)), Rules.BootstrapKeys);
    }

    [Fact]
    public void BootstrapKeys_AreComparedOrdinally()
    {
        Assert.DoesNotContain(KeyRoot + "VERSION.JSON", Rules.BootstrapKeys);
        Assert.DoesNotContain("WEB.SANGUOSHA.COM/220/h5_2/version.json", Rules.BootstrapKeys);
    }

    // ---------- 地址构造 ----------

    [Fact]
    public void BuildAesRescUri_UsesTheRescVersion()
    {
        Assert.Equal(H5Root + "libs/min/aesresc?v=2019112501", Rules.BuildAesRescUri("2019112501").AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildAesRescUri_FallsBackToVersion1Point0(string? rescVersion)
    {
        var uri = Rules.BuildAesRescUri(rescVersion);

        Assert.EndsWith("aesresc?v=1.0", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(H5Root + "libs/min/aesresc?v=1.0", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildAesRescUri_EscapesTheVersion()
    {
        Assert.Equal(H5Root + "libs/min/aesresc?v=1%200%26x", Rules.BuildAesRescUri("1 0&x").AbsoluteUri);
    }

    [Fact]
    public void BuildSeedUris_ReturnsLayaThenGamePackagesInOrderWithVersions()
    {
        var seeds = Rules.BuildSeedUris("2026101601", "2026101901");

        Assert.Equal(
            new[]
            {
                H5Root + "libs/min/laya_a.sgs?v=2026101601",
                H5Root + "libs/min/laya.sgs?v=2026101601",
                H5Root + "sgsGame_a.sgs?v=2026101901",
                H5Root + "sgsGame.sgs?v=2026101901"
            },
            seeds.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public void BuildSeedUris_HasNoQueryForAMissingVersion()
    {
        Assert.Equal(
            new[]
            {
                H5Root + "libs/min/laya_a.sgs",
                H5Root + "libs/min/laya.sgs",
                H5Root + "sgsGame_a.sgs?v=7",
                H5Root + "sgsGame.sgs?v=7"
            },
            Rules.BuildSeedUris(null, "7").Select(uri => uri.AbsoluteUri));
        Assert.Equal(
            new[]
            {
                H5Root + "libs/min/laya_a.sgs?v=3",
                H5Root + "libs/min/laya.sgs?v=3",
                H5Root + "sgsGame_a.sgs",
                H5Root + "sgsGame.sgs"
            },
            Rules.BuildSeedUris("3", "").Select(uri => uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("res/assets/baseUI/tipCommon.webp", "-2042252689", "res/assets/baseUI/tipCommon.webp?v=-2042252689")]
    [InlineData("res/runtime/pc/a.png", "1536", "res/runtime/pc/a.png?v=1536")]
    [InlineData("res/a.png", null, "res/a.png")]
    [InlineData("res/a.png", "", "res/a.png")]
    [InlineData("res/a.png", "a b&c", "res/a.png?v=a%20b%26c")]
    public void Versioned_AppendsTheEscapedVersionOnlyWhenThereIsOne(string path, string? version, string expectedRelative)
    {
        Assert.Equal(H5Root + expectedRelative, Rules.Versioned(path, version).AbsoluteUri);
    }

    [Fact]
    public void Versioned_KeepsChinesePathsRoundTrippingThroughTheResourceKey()
    {
        var uri = Rules.Versioned("res/技能/a b.png", "1");

        Assert.Equal(KeyRoot + "res/技能/a b.png", ResourceKeys.FromUri(uri));
        Assert.Equal("?v=1", uri.Query);
    }

    [Fact]
    public void BuildVersionJsonUri_AppendsTheMillisecondTimestamp()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_761_000_000_123);

        Assert.Equal(H5Root + "version.json?t=1761000000123", Rules.BuildVersionJsonUri(now).AbsoluteUri);
    }

    // ---------- InScope ----------

    [Theory]
    [InlineData("https://web.sanguosha.com/220/h5_2/index_210000.php")] // 入口页
    [InlineData("https://web.sanguosha.com/220/h5_2/index_210000.php?x=1#top")] // 路径相同即可，查询参数不参与
    [InlineData("https://web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.webp?v=-2042252689")]
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/pc/ab/c.png")]
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/PC/ab/c.png")] // 平台段不区分大小写
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/pc")] // 平台段在路径末尾
    [InlineData("http://web.sanguosha.com/220/h5_2/res/a.png")] // http 同样允许
    [InlineData("https://WEB.SanGuoSha.com/220/H5_2/res/a.png")] // 主机名和 h5 目录都不区分大小写
    public void InScope_AcceptsTheEntryPageAndH5Resources(string url)
    {
        Assert.True(Rules.InScope(new Uri(url)));
    }

    [Fact]
    public void InScope_AcceptsEveryUrlTheBootstrapAndSeedPhasesBuild()
    {
        // 种子地址要经过 CrawlQueue.TryEnqueue，如果被范围检查拒绝，整个收集就什么都发现不了
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_761_000_000_123);
        var urls = Rules.BuildSeedUris("2026101601", "2026101901")
            .Concat(Rules.BuildSeedUris(null, null))
            .Append(Rules.BuildAesRescUri("2019112501"))
            .Append(Rules.BuildAesRescUri(null))
            .Append(Rules.BuildVersionJsonUri(now))
            .Append(Rules.Versioned("res/runtime/pc/ab/c.png", "-2042252689"));

        Assert.All(urls, uri => Assert.True(Rules.InScope(uri), uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://cdn.sanguosha.com/220/h5_2/res/a.png")] // 其它域名
    [InlineData("https://web.sanguosha.com/sgsCensus/startup.php")] // 统计脚本：不在 h5 目录下
    [InlineData("https://web.sanguosha.com/220/h5_2/sgsCensus/startup.php")] // 在 h5 目录下，但 .php 是接口路径
    [InlineData("https://web.sanguosha.com/220/h5_2/other.php?a=1")]
    [InlineData("https://web.sanguosha.com/220/h5_2/page.html")]
    [InlineData("https://web.sanguosha.com/220/h5_2/page.HTM")]
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/m/ab/c.png")] // 手机端资源
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/ios/a.png")]
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/")] // 没有平台段
    [InlineData("https://web.sanguosha.com/220/h5_2/res/runtime/pcx/a.png")] // 平台段必须整段等于 pc
    [InlineData("https://web.sanguosha.com/other/res/a.png")] // 不在 h5 目录下
    [InlineData("ftp://web.sanguosha.com/220/h5_2/res/a.png")] // 不是 http / https
    public void InScope_RejectsOtherHostsApiPathsAndNonDesktopRuntime(string url)
    {
        Assert.False(Rules.InScope(new Uri(url)));
    }

    // ---------- 静态判断：旧 SgsPathRules ----------

    [Theory]
    [InlineData("https://web.sanguosha.com/a/b.php", true)]
    [InlineData("https://web.sanguosha.com/a/b.PHP?x=1", true)]
    [InlineData("https://web.sanguosha.com/a/b.html", true)]
    [InlineData("https://web.sanguosha.com/a/b.htm", true)]
    [InlineData("https://web.sanguosha.com/a/b.json", false)]
    [InlineData("https://web.sanguosha.com/a/b.php.png", false)]
    [InlineData("https://web.sanguosha.com/a/noext", false)]
    public void IsApiLikePath_LooksAtTheExtensionOfThePathOnly(string url, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsApiLikePath(new Uri(url)));
    }

    [Theory]
    [InlineData("res/config/a.json", true)]
    [InlineData("/res/config/a.json", true)]
    [InlineData("220/h5_2/res/config/a.json", true)]
    [InlineData("/220/h5_2/res/config/sub/a.JSON", true)]
    [InlineData("res\\config\\a.json", true)]
    [InlineData("config/a.json", true)]
    [InlineData("220/h5_2/config/a.json", true)]
    [InlineData("res/config/a.png", false)]
    [InlineData("res/config/Config_w.sgs", false)]
    [InlineData("res/configs/a.json", false)]
    [InlineData("res/proto/a.json", false)]
    [InlineData("other/res/config/a.json", false)] // 必须从路径开头算起
    public void IsDirectConfigJsonPath_MatchesConfigJsonFilesRelativeToTheH5Root(string path, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsDirectConfigJsonPath(path));
    }

    [Theory]
    [InlineData("res/config/Config_w.sgs", true)]
    [InlineData("res/config/Config.sgs", true)]
    [InlineData("RES/CONFIG/CONFIG_W.SGS", true)]
    [InlineData("res/config/Config_w.sgs.bak", false)]
    [InlineData("res/config/h5_global_conf_w.sgs", false)]
    [InlineData("res/config/a.json", false)]
    public void IsConfigPackagePath_MatchesTheTwoConfigPackages(string path, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsConfigPackagePath(path));
    }

    // ---------- 静态判断：按资源 key ----------

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/aesresc", true)]
    [InlineData("web.sanguosha.com/220/h5_2/LIBS/MIN/AESRESC", true)]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/aesresc.bak", false)]
    [InlineData("web.sanguosha.com/220/h5_2/libs/aesresc", false)]
    [InlineData("aesresc", false)] // 前面必须有目录分隔符
    public void IsAesResc_RequiresThePathToEndWithLibsMinAesresc(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsAesResc(key));
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/version.json", true)]
    [InlineData("web.sanguosha.com/220/h5_2/VERSION.JSON", true)]
    [InlineData("web.sanguosha.com/220/h5_2/myversion.json", false)]
    [InlineData("web.sanguosha.com/220/h5_2/version.json.bak", false)]
    public void IsVersionJson_RequiresAFileNamedVersionJson(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsVersionJson(key));
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json", true)]
    [InlineData("web.sanguosha.com/220/h5_2/RES/DEFAULT.RES.JSON", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/mydefault.res.json", false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json.bak", false)]
    public void IsDefaultResJson_RequiresAFileNamedDefaultResJson(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsDefaultResJson(key));
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/res/config/Config_w.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/proto/Proto_w.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/RES/CONFIG/Config_w.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/config/Config_w.sgs/cha_spell.json", true)] // 成员 key
    [InlineData("web.sanguosha.com/220/h5_2/res/config", false)] // 目录后面必须还有斜杠
    [InlineData("web.sanguosha.com/220/h5_2/res/configs/a.json", false)]
    [InlineData("web.sanguosha.com/220/h5_2/sgsGame_a.sgs", false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.png", false)]
    public void IsEncryptedContext_MatchesResConfigAndResProtoDirectories(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsEncryptedContext(key));
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/before.min.js", true)]
    [InlineData("web.sanguosha.com/220/h5_2/sgsGame_a.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/sgsGame.sgs/sgsGame.js", true)] // 成员 key
    [InlineData("web.sanguosha.com/220/h5_2/SGSGAME_A.SGS", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/config/Config_w.sgs/cha_spell.json", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/proto/Proto_w.sgs/a.proto", true)]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/mybefore.min.js", false)] // 文件名必须整个等于 before.min.js
    [InlineData("web.sanguosha.com/220/h5_2/libs/after.js", false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.png", false)]
    public void ShouldScanText_MatchesBeforeMinJsSgsGameAndConfigProtoContent(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.ShouldScanText(key));
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/aesresc", true)]
    [InlineData("web.sanguosha.com/220/h5_2/version.json", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json", true)]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/before.min.js", true)]
    [InlineData("web.sanguosha.com/220/h5_2/sgsGame_a.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/config/Config_w.sgs", true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/assets/baseUI/tipCommon.png", false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/runtime/pc/ab/c.atlas", false)]
    public void IsProcessedKey_IsTrueForResourcesWhoseContentIsParsed(string key, bool expected)
    {
        Assert.Equal(expected, DiscoveryRules.IsProcessedKey(key));
    }
}
